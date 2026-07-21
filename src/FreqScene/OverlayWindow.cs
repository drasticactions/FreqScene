using Avalonia.Controls;
using Avalonia.Media;
using ProjectMDotNet.Avalonia;

namespace FreqScene;

public class OverlayWindow : Window
{
    private readonly VisualizerCoordinator _coordinator;
    private readonly ProjectMControl _visualizer;
    private bool _wallpaper;

    public OverlayWindow(VisualizerCoordinator coordinator, bool wallpaper)
    {
        _coordinator = coordinator;
        _wallpaper = wallpaper;

        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        Focusable = false;

        _visualizer = new ProjectMControl
        {
            TransparentBackground = true,
            IsHitTestVisible = false,
        };
        var scaleHost = new RenderScaleHost
        {
            Child = _visualizer,
            RenderScale = coordinator.RenderScalePercent / 100.0,
            IsHitTestVisible = false,
        };
        Content = scaleHost;
        Action<int> onRenderScaleChanged = percent => scaleHost.RenderScale = percent / 100.0;
        coordinator.RenderScaleChanged += onRenderScaleChanged;
        ApplyBackgroundMode();
        coordinator.AttachControl(_visualizer);

        _visualizer.InitializationFailed += (_, ex) =>
            System.Diagnostics.Trace.WriteLine($"[overlay] visualizer init failed: {ex}");
        _visualizer.InstanceCreated += (_, _) =>
            System.Diagnostics.Trace.WriteLine("[overlay] visualizer instance created");

        Opened += (_, _) =>
        {
            MacOverlay.ConfigureOverlay(this, _wallpaper);
            WindowsOverlay.ConfigureOverlay(this, _wallpaper);

            // Needs the realised HWND, so it cannot happen in the constructor.
            ApplyBackgroundMode();
            FitPrimaryScreen();
            System.Diagnostics.Trace.WriteLine(
                $"[overlay] opened at {Position} size {Width}x{Height} transparency={ActualTransparencyLevel}");
        };
        Closed += (_, _) =>
        {
            _coordinator.RenderScaleChanged -= onRenderScaleChanged;
            _coordinator.DetachControl(_visualizer);
            if (_wallpaper)
            {
                // Vacating the shell's wallpaper host leaves it blank otherwise.
                WindowsOverlay.RefreshDesktop();
            }
        };
    }

    /// <summary>Switches between overlay-above and wallpaper-behind stacking.</summary>
    public void SetWallpaperMode(bool wallpaper)
    {
        _wallpaper = wallpaper;
        MacOverlay.SetLevel(this, wallpaper);
        WindowsOverlay.SetStacking(this, wallpaper);
        ApplyBackgroundMode();
    }

    /// <summary>
    /// Chooses how the visualization's black background disappears.
    /// <para>
    /// Windows renders the overlay through WGL, which leaves the window opaque
    /// to the compositor, so the GL-side transparency pass would only darken
    /// the image — black is keyed out at the window level instead. Wallpaper
    /// mode keys out nothing: it replaces the wallpaper, so there is nothing
    /// behind it worth revealing.
    /// </para>
    /// <para>
    /// Elsewhere the window itself is transparent, and the GL compositor's
    /// brightness-derived alpha gives a soft edge over the desktop.
    /// </para>
    /// </summary>
    private void ApplyBackgroundMode()
    {
        if (OperatingSystem.IsWindows())
        {
            _visualizer.TransparentBackground = false;
            Background = Brushes.Black;
            WindowsOverlay.SetBlackKeyedOut(this, !_wallpaper);
            return;
        }

        _visualizer.TransparentBackground = true;
        Background = Brushes.Transparent;
    }

    private void FitPrimaryScreen()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;

        if (OperatingSystem.IsMacOS())
        {
            // AppKit clamps managed positioning below the menu bar.
            MacOverlay.SetFullScreenFrame(this);
        }
        else if (OperatingSystem.IsWindows())
        {
            // Coordinates are relative to the wallpaper host once reparented.
            WindowsOverlay.ApplyBounds(this);
        }
        else
        {
            Position = screen.Bounds.Position;
        }
    }
}
