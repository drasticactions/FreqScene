using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FreqScene.Kiosk;

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
        var connectOption = new Option<string?>("--connect")
        {
            Description = "Mirror a remote FreqScene host: host[:port] or an mDNS server name (see --list-servers).",
        };
        var listServersOption = new Option<bool>("--list-servers")
        {
            Description = "List FreqScene servers discovered on the local network.",
        };
        var listOutputsOption = new Option<bool>("--list-outputs")
        {
            Description = "List available displays and modes.",
        };
        var listAudioOption = new Option<bool>("--list-audio")
        {
            Description = "List available audio sources.",
        };
        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Show verbose diagnostic output.",
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
            noRemoteOption, portOption, pairOption, connectOption,
            listOutputsOption, listAudioOption, listServersOption,
            verboseOption, presetsArgument,
        };

        root.SetAction(parseResult => Run(new KioskOptions(
            parseResult.GetValue(outputOption),
            parseResult.GetValue(modeOption),
            parseResult.GetValue(backendOption)!,
            parseResult.GetValue(audioOption),
            parseResult.GetValue(configDirOption),
            parseResult.GetValue(noRemoteOption),
            parseResult.GetValue(portOption),
            parseResult.GetValue(pairOption),
            parseResult.GetValue(connectOption),
            parseResult.GetValue(listOutputsOption),
            parseResult.GetValue(listAudioOption),
            parseResult.GetValue(listServersOption),
            parseResult.GetValue(verboseOption),
            parseResult.GetValue(presetsArgument) ?? [])));

        return root.Parse(args).Invoke();
    }

    private sealed record KioskOptions(
        string? Output,
        string? Mode,
        string Backend,
        string? Audio,
        string? ConfigDir,
        bool NoRemote,
        int? Port,
        bool Pair,
        string? Connect,
        bool ListOutputs,
        bool ListAudio,
        bool ListServers,
        bool Verbose,
        string[] Presets);

    private static int Run(KioskOptions options)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("freqscene-kiosk only runs on Linux.");
            return 1;
        }

        Trace.Listeners.Add(new KioskTraceListener(options.Verbose));

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

        if (options.ListServers)
        {
            return PrintServers();
        }

        if (options.Connect is not null && (options.Port is not null || options.Pair))
        {
            Console.Error.WriteLine("--connect cannot be combined with --port or --pair.");
            return 1;
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

        if (options.Connect is not null)
        {
            if (options.Audio is not null)
            {
                Console.WriteLine("[audio] --audio is ignored while mirroring a remote host");
            }

            if (options.Presets.Length > 0)
            {
                Console.WriteLine("[preset] preset arguments are ignored while mirroring a remote host");
            }
        }
        else if (options.Audio is { } audio && !SelectAudio(coordinator, audio))
        {
            coordinator.Dispose();
            return 1;
        }

        RemoteServerManager? remote = null;
        if (!options.NoRemote && options.Connect is null)
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

        RemoteClientManager? client = null;
        Remote.Server.MdnsBrowser? mdns = null;
        if (options.Connect is { } target)
        {
            client = new RemoteClientManager(coordinator, options.ConfigDir, deviceModel: "Kiosk");
            client.StatusChanged += message => Console.WriteLine($"[client] {message}");
            Remote.Client.RemoteSessionState? lastState = null;
            var currentClient = client;
            client.StateChanged += () => dispatcher.Post(() =>
            {
                if (currentClient.State is { } state && state != lastState)
                {
                    lastState = state;
                    Console.WriteLine($"[client] {DescribeState(currentClient, state)}");
                }
            });
            client.PairingRequired += () => dispatcher.Post(() => PromptPairing(currentClient));
            if (TryParseAddress(target) is null)
            {
                mdns = new Remote.Server.MdnsBrowser();
            }
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
        else if (client is not null)
        {
            Console.WriteLine("keys: [q]uit");
        }

        if (client is not null && options.Connect is { } connectTarget)
        {
            var connectClient = client;
            _ = Task.Run(() => ConnectClientAsync(connectClient, mdns, connectTarget, shutdown.Token));
        }

        var keys = new ConsoleKeyReader();
        dispatcher.Run(shutdown.Token, () => HandleKeys(keys, coordinator, remote, client, shutdown));

        coordinator.DetachControl(host);
        host.Dispose();
        remote?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        client?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        mdns?.Dispose();
        coordinator.Dispose();
        return exitCode;
    }

    private static void HandleKeys(
        ConsoleKeyReader keys,
        VisualizerCoordinator coordinator,
        RemoteServerManager? remote,
        RemoteClientManager? client,
        CancellationTokenSource shutdown)
    {
        switch (keys.TryRead())
        {
            case 'q':
                shutdown.Cancel();
                break;

            case 'n' when client is null:
                coordinator.NextPreset();
                break;

            case 'b' when client is null:
                coordinator.PreviousPreset();
                break;

            case 'p' when remote is not null:
                PrintPairingPin(remote);
                break;
        }
    }

    private static async Task ConnectClientAsync(
        RemoteClientManager client,
        Remote.Server.MdnsBrowser? mdns,
        string target,
        CancellationToken ct)
    {
        try
        {
            Uri address;
            Func<CancellationToken, Task<Uri?>>? rediscover = null;
            if (TryParseAddress(target) is { } parsed)
            {
                var uriHost = Uri.CheckHostName(parsed.Host) == UriHostNameType.IPv6 ? $"[{parsed.Host}]" : parsed.Host;
                address = new Uri($"http://{uriHost}:{parsed.Port}");
            }
            else
            {
                Console.WriteLine($"[client] looking for “{target}” on the local network…");
                Uri? resolved = null;
                for (var i = 0; i < 20 && resolved is null && !ct.IsCancellationRequested; i++)
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    resolved = mdns!.Resolve(target);
                }

                rediscover = _ => Task.FromResult(mdns!.Resolve(target));
                if (resolved is null)
                {
                    Console.WriteLine($"[client] “{target}” not found via mDNS yet; also trying it as a hostname");
                    address = new Uri($"http://{target}:{Remote.RemoteProtocol.DefaultPort}");
                }
                else
                {
                    address = resolved;
                }
            }

            await client.ConnectAsync(address, target, rediscover).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[client] connect failed: {ex.Message}");
        }
    }

    /// <summary>Splits host[:port] / [v6]:port / IP-literal targets; null means an mDNS instance name.</summary>
    private static (string Host, int Port)? TryParseAddress(string target)
    {
        if (target.StartsWith('['))
        {
            var end = target.IndexOf(']');
            if (end < 0)
            {
                return null;
            }

            var host = target[1..end];
            var rest = target[(end + 1)..];
            if (rest.Length == 0)
            {
                return (host, Remote.RemoteProtocol.DefaultPort);
            }

            return rest[0] == ':' && int.TryParse(rest[1..], out var bracketPort) ? (host, bracketPort) : null;
        }

        if (System.Net.IPAddress.TryParse(target, out _))
        {
            // Bare IP literals (including IPv6, whose colons are not a port separator) use the default port.
            return (target, Remote.RemoteProtocol.DefaultPort);
        }

        var colon = target.IndexOf(':');
        if (colon >= 0)
        {
            return colon == target.LastIndexOf(':') && int.TryParse(target[(colon + 1)..], out var port)
                ? (target[..colon], port)
                : null;
        }

        return target.Contains('.') ? (target, Remote.RemoteProtocol.DefaultPort) : null;
    }

    private static void PromptPairing(RemoteClientManager client)
    {
        if (Console.IsInputRedirected)
        {
            Console.WriteLine(
                "[client] pairing required — run once from an interactive terminal to enter the PIN; the pairing persists for later runs");
            return;
        }

        while (true)
        {
            Console.Write($"[client] pairing PIN for “{client.HostName}”: ");
            var pin = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(pin))
            {
                Console.WriteLine("[client] pairing skipped; not mirroring");
                return;
            }

            try
            {
                client.PairAsync(pin).GetAwaiter().GetResult();
                return;
            }
            catch (Remote.Client.PairingException ex)
            {
                Console.WriteLine($"[client] {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[client] pairing failed: {ex.Message}");
            }
        }
    }

    private static string DescribeState(RemoteClientManager client, Remote.Client.RemoteSessionState state) =>
        state switch
        {
            Remote.Client.RemoteSessionState.Connecting => $"connecting to “{client.HostName}”…",
            Remote.Client.RemoteSessionState.Connected => $"mirroring “{client.HostName}”",
            Remote.Client.RemoteSessionState.Reconnecting => $"reconnecting to “{client.HostName}”…",
            Remote.Client.RemoteSessionState.PairingRequired => "pairing required",
            _ => "stopped",
        };

    private static int PrintServers()
    {
        using var browser = new Remote.Server.MdnsBrowser();
        Console.WriteLine("looking for FreqScene servers…");
        Thread.Sleep(TimeSpan.FromSeconds(3));
        var servers = browser.Servers;
        if (servers.Count == 0)
        {
            Console.WriteLine("no servers found.");
            return 0;
        }

        foreach (var server in servers)
        {
            Console.WriteLine(
                $"  {server.InstanceName}  {server.Address}:{server.Port}  v{server.ProtocolVersion}{(server.IsCompatible ? "" : " (incompatible)")}");
        }

        return 0;
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
        Console.WriteLine("DRM connectors:");
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
            Console.WriteLine("Wayland outputs:");
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
