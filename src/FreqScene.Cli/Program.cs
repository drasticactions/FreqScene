using System.CommandLine;
using System.Runtime.InteropServices;

namespace FreqScene.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        var outputOption = new Option<string?>("--output")
        {
            Description = "The display to use.",
        };
        var modeOption = new Option<string?>("--mode")
        {
            Description = "Video mode for the DRM backend. Examples are 1920x1080 or 1920x1080@60.",
        };
        var backendOption = new Option<string>("--backend")
        {
            Description = "Rendering backend.",
            DefaultValueFactory = _ => "auto",
        };
        backendOption.AcceptOnlyFromAmong("auto", "drm", "wayland");
        var audioOption = new Option<string?>("--audio")
        {
            Description = "Audio source: 'synthetic' or a capture device name (see --list-audio).",
        };
        var configDirOption = new Option<string?>("--config-dir")
        {
            Description = "Use this directory for playlist/settings instead of the shared app data.",
        };
        var noRemoteOption = new Option<bool>("--no-remote")
        {
            Description = "Do not start the remote-control server.",
        };
        var portOption = new Option<int?>("--port")
        {
            Description = "Port for the remote-control server.",
        };
        var pairOption = new Option<bool>("--pair")
        {
            Description = "Print a pairing PIN at startup so a remote client can pair immediately.",
        };
        var listOutputsOption = new Option<bool>("--list-outputs")
        {
            Description = "List available displays and modes, then exit.",
        };
        var listAudioOption = new Option<bool>("--list-audio")
        {
            Description = "List available audio sources, then exit.",
        };
        var presetsArgument = new Argument<string[]>("presets")
        {
            Description = "Preset files or folders to add to the playlist.",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var root = new RootCommand(
            "FreqScene headless visualizer")
        {
            outputOption, modeOption, backendOption, audioOption, configDirOption,
            noRemoteOption, portOption, pairOption, listOutputsOption, listAudioOption,
            presetsArgument,
        };

        root.SetAction(parseResult => Run(new CliOptions(
            parseResult.GetValue(outputOption),
            parseResult.GetValue(modeOption),
            parseResult.GetValue(backendOption)!,
            parseResult.GetValue(audioOption),
            parseResult.GetValue(configDirOption),
            parseResult.GetValue(noRemoteOption),
            parseResult.GetValue(portOption),
            parseResult.GetValue(pairOption),
            parseResult.GetValue(listOutputsOption),
            parseResult.GetValue(listAudioOption),
            parseResult.GetValue(presetsArgument) ?? [])));

        return root.Parse(args).Invoke();
    }

    private sealed record CliOptions(
        string? Output,
        string? Mode,
        string Backend,
        string? Audio,
        string? ConfigDir,
        bool NoRemote,
        int? Port,
        bool Pair,
        bool ListOutputs,
        bool ListAudio,
        string[] Presets);

    private static int Run(CliOptions options)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("freqscene-cli only runs on Linux.");
            return 1;
        }

        if (options.ListOutputs)
        {
            return PrintOutputs();
        }

        if (options.ListAudio)
        {
            Console.WriteLine(VisualizerCoordinator.SyntheticSourceName);
            foreach (var device in OpenAlCapture.GetCaptureDevices())
            {
                Console.WriteLine(device);
            }

            return 0;
        }

        if (options.ConfigDir is { } configDir)
        {
            PlaylistStore.OverrideDirectory(configDir);
            SettingsStore.OverrideDirectory(configDir);
        }

        var useWayland = options.Backend switch
        {
            "wayland" => true,
            "drm" => false,
            _ => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")),
        };

        var settings = SettingsStore.Load();
        settings.RenderScalePercent = QualityOptions.NormalizeRenderScale(settings.RenderScalePercent);
        settings.FrameRateCap = QualityOptions.NormalizeFrameRate(settings.FrameRateCap);
        if (options.Port is { } port)
        {
            settings.RemotePort = port;
        }

        var dispatcher = new MainThreadDispatcher();
        using var shutdown = new CancellationTokenSource();
        var exitCode = 0;

        var coordinator = new VisualizerCoordinator(options.Presets) { UiDispatcher = dispatcher };
        coordinator.RenderScalePercent = settings.RenderScalePercent;
        coordinator.FrameRateCap = settings.FrameRateCap;
        coordinator.WallpaperTransparency = false;
        coordinator.StatusChanged += message => Console.WriteLine($"[preset] {message}");

        if (options.Audio is { } audio && !SelectAudio(coordinator, audio))
        {
            coordinator.Dispose();
            return 1;
        }

        RemoteServerManager? remote = null;
        if (!options.NoRemote)
        {
            remote = new RemoteServerManager(coordinator, settings) { ForceEnabled = true };
            remote.StatusChanged += message => Console.WriteLine($"[remote] {message}");
            remote.ClientsChanged += () => dispatcher.Post(
                () => Console.WriteLine($"[remote] {remote.ClientCount} client(s) connected"));
            remote.Pairing.DevicePaired += device => Console.WriteLine($"[remote] paired: {device.Name}");
            _ = remote.ApplyAsync();
            if (options.Pair)
            {
                PrintPairingPin(remote);
            }
        }
        else if (options.Pair)
        {
            Console.Error.WriteLine("--pair does nothing with --no-remote.");
        }

        var host = new LinuxVisualizerHost(
            useWayland
                ? () => new LinuxWaylandSession(DisplayMode.Window, options.Output, fullscreen: true)
                : () => new LinuxKmsSession(options.Output, options.Mode),
            transparent: false,
            dispatcher);
        host.RenderScale = settings.RenderScalePercent / 100.0;
        coordinator.RenderScaleChanged += percent => host.RenderScale = percent / 100.0;
        host.InitializationFailed += (_, ex) =>
        {
            Console.Error.WriteLine($"visualizer failed to start: {ex.Message}");
            exitCode = 1;
            shutdown.Cancel();
        };

        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            shutdown.Cancel();
        });
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            shutdown.Cancel();
        });

        coordinator.AttachControl(host);
        host.Start();
        Console.WriteLine(useWayland
            ? "rendering into the Wayland session (Ctrl+C to quit)"
            : "rendering via DRM/KMS (Ctrl+C to quit)");
        if (remote is not null)
        {
            Console.WriteLine("keys: [p]air PIN, [n]ext preset, [b]ack, [q]uit");
        }

        var keys = new ConsoleKeyReader();
        dispatcher.Run(shutdown.Token, () => HandleKeys(keys, coordinator, remote, shutdown));

        coordinator.DetachControl(host);
        host.Dispose();
        remote?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        coordinator.Dispose();
        return exitCode;
    }

    private static void HandleKeys(
        ConsoleKeyReader keys,
        VisualizerCoordinator coordinator,
        RemoteServerManager? remote,
        CancellationTokenSource shutdown)
    {
        switch (keys.TryRead())
        {
            case 'q':
                shutdown.Cancel();
                break;

            case 'n':
                coordinator.NextPreset();
                break;

            case 'b':
                coordinator.PreviousPreset();
                break;

            case 'p' when remote is not null:
                PrintPairingPin(remote);
                break;
        }
    }

    private static void PrintPairingPin(RemoteServerManager remote)
    {
        var pin = remote.Pairing.BeginPairing();
        var deadline = Remote.Server.PairingManager.PinLifetime.TotalMinutes;
        Console.WriteLine($"[remote] pairing PIN: {pin} (valid {deadline:0} minutes)");
    }

    private static bool SelectAudio(VisualizerCoordinator coordinator, string requested)
    {
        var name = string.Equals(requested, "synthetic", StringComparison.OrdinalIgnoreCase)
            ? VisualizerCoordinator.SyntheticSourceName
            : coordinator.AudioSources.FirstOrDefault(
                s => string.Equals(s, requested, StringComparison.OrdinalIgnoreCase));
        if (name is null || !coordinator.SelectAudioSource(name))
        {
            Console.Error.WriteLine($"audio source '{requested}' is not available; choose one of:");
            foreach (var source in coordinator.AudioSources)
            {
                Console.Error.WriteLine($"  {source}");
            }

            return false;
        }

        Console.WriteLine($"[audio] {name}");
        return true;
    }

    private static int PrintOutputs()
    {
        var drmOutputs = LinuxKmsSession.ListOutputs();
        Console.WriteLine("DRM connectors (for --backend drm):");
        if (drmOutputs.Count == 0)
        {
            Console.WriteLine("  none found (no /dev/dri access?)");
        }

        foreach (var output in drmOutputs)
        {
            Console.WriteLine($"  {output.Name} [{output.DevicePath}] {(output.Connected ? "connected" : "disconnected")}");
            if (output.Connected)
            {
                Console.WriteLine($"    modes: {string.Join(' ', output.Modes)}   (* = preferred)");
            }
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Console.WriteLine("Wayland outputs (for --backend wayland):");
            foreach (var output in LinuxWaylandSession.ListOutputs())
            {
                Console.WriteLine($"  {output.Key}: {output.Label}");
            }
        }

        return 0;
    }

    private sealed class ConsoleKeyReader
    {
        private bool _usable = true;

        public char? TryRead()
        {
            if (!_usable)
            {
                return null;
            }

            try
            {
                if (!Console.KeyAvailable)
                {
                    return null;
                }

                return char.ToLowerInvariant(Console.ReadKey(intercept: true).KeyChar);
            }
            catch (InvalidOperationException)
            {
                _usable = false;
                return null;
            }
        }
    }
}
