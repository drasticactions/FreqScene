using Foundation;
using FreqScene.Remote.Client;
using ProjectMDotNet;
using UIKit;

namespace FreqScene.iOS;

public sealed class VisualizerViewController : UIViewController
{
    private readonly Uri? _serverAddress;
    private readonly string? _serverBonjourName;
    private ProjectMGLView? _glView;
    private SyntheticAudioSource? _audio;
    private RemoteVisualizerSession? _session;
    private UILabel? _statusBadge;

    public VisualizerViewController()
    {
    }

    public VisualizerViewController(Uri serverAddress, string serverBonjourName)
    {
        _serverAddress = serverAddress;
        _serverBonjourName = serverBonjourName;
        ModalPresentationStyle = UIModalPresentationStyle.FullScreen;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.Black;

        _glView = new ProjectMGLView(View.Bounds)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
        };
        View.AddSubview(_glView);

        if (_serverAddress is not null)
        {
            _statusBadge = new UILabel
            {
                Text = "connecting…",
                TextColor = UIColor.White,
                BackgroundColor = UIColor.FromRGBA(0, 0, 0, 160),
                Font = UIFont.SystemFontOfSize(14),
                TextAlignment = UITextAlignment.Center,
                Frame = new CoreGraphics.CGRect(View.Bounds.Width - 180, 40, 160, 28),
                AutoresizingMask = UIViewAutoresizing.FlexibleLeftMargin | UIViewAutoresizing.FlexibleBottomMargin,
            };
            _statusBadge.Layer.CornerRadius = 6;
            _statusBadge.Layer.MasksToBounds = true;
            View.AddSubview(_statusBadge);
        }

        AddDismissGesture();
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);

        UIApplication.SharedApplication.IdleTimerDisabled = true;

        if (_serverAddress is { } address)
        {
            StartRemote(address);
        }
        else
        {
            StartSynthetic();
        }
    }

    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);
        UIApplication.SharedApplication.IdleTimerDisabled = false;
        StopSynthetic();
        if (_session is { } session)
        {
            _session = null;
            _ = session.DisposeAsync();
        }
    }

    private void StartRemote(Uri address)
    {
        if (_session is not null || _glView is not { } glView)
        {
            return;
        }

        var cacheDir = Path.Combine(
            NSSearchPath.GetDirectories(NSSearchPathDirectory.CachesDirectory, NSSearchPathDomain.User)[0],
            "presets");
        var bonjourName = _serverBonjourName;
        var session = new RemoteVisualizerSession(
            address,
            UIDevice.CurrentDevice.Name,
            UIDevice.CurrentDevice.Model,
            new PresetCache(cacheDir),
            bonjourName is null ? null : ct => BonjourResolver.ResolveByNameAsync(bonjourName, ct));

        // PcmBuffer is thread-safe; PCM flows straight in from the hub thread.
        session.PcmReceived += samples => _glView?.AddPcm(samples, AudioChannels.Stereo);
        session.PresetReceived += (content, hardCut) =>
            InvokeOnMainThread(() => _glView?.LoadPresetData(content, smoothTransition: !hardCut));
        session.StateChanged += state => InvokeOnMainThread(() => ApplySessionState(state));
        session.StatusChanged += message => Console.WriteLine($"[remote] {message}");

        _session = session;
        StartSynthetic();
        session.Start();
    }

    private void ApplySessionState(RemoteSessionState state)
    {
        switch (state)
        {
            case RemoteSessionState.Connected:
                StopSynthetic();
                if (_statusBadge is not null)
                {
                    _statusBadge.Hidden = true;
                }

                break;
            case RemoteSessionState.Connecting:
            case RemoteSessionState.Reconnecting:
                StartSynthetic();
                if (_statusBadge is not null)
                {
                    _statusBadge.Hidden = false;
                    _statusBadge.Text = state == RemoteSessionState.Connecting ? "connecting…" : "reconnecting…";
                }

                break;
            case RemoteSessionState.Stopped:
                StartSynthetic();
                break;
        }
    }

    private void StartSynthetic()
    {
        if (_audio is null && _glView is { } glView)
        {
            _audio = new SyntheticAudioSource(chunk => glView.AddPcm(chunk, AudioChannels.Stereo));
            _audio.Start();
        }
    }

    private void StopSynthetic()
    {
        _audio?.Dispose();
        _audio = null;
    }

    private void AddDismissGesture()
    {
#if TVOS
        var menuTap = new UITapGestureRecognizer(() => DismissIfPresented())
        {
            AllowedPressTypes = [NSNumber.FromNInt((nint)UIPressType.Menu)],
        };
        View!.AddGestureRecognizer(menuTap);
#else
        var doubleTap = new UITapGestureRecognizer(() => DismissIfPresented())
        {
            NumberOfTapsRequired = 2,
        };
        View!.AddGestureRecognizer(doubleTap);
#endif
    }

    private void DismissIfPresented()
    {
        if (PresentingViewController is not null)
        {
            DismissViewController(true, null);
        }
    }

#if IOS
    public override bool PrefersStatusBarHidden() => true;
#endif
}
