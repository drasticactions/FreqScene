using Android.Content;
using Android.Graphics;
using Android.Provider;
using Android.Util;
using Android.Views;
using Android.Widget;
using FreqScene.Remote.Client;
using ProjectMDotNet;

namespace FreqScene.Android;

[Activity(
    Label = "FreqScene",
    Theme = "@android:style/Theme.Material.NoActionBar.Fullscreen",
    ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation
        | global::Android.Content.PM.ConfigChanges.ScreenSize
        | global::Android.Content.PM.ConfigChanges.ScreenLayout
        | global::Android.Content.PM.ConfigChanges.UiMode)]
public sealed class VisualizerActivity : Activity
{
    public const string ExtraAddress = "address";
    public const string ExtraServiceName = "serviceName";

    private ProjectMGLView? _glView;
    private SyntheticAudioSource? _audio;
    private RemoteVisualizerSession? _session;
    private NsdServerBrowser? _resolver;
    private TextView? _statusBadge;
    private GestureDetector? _gestures;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window!.AddFlags(WindowManagerFlags.KeepScreenOn);

        var root = new FrameLayout(this);
        _glView = new ProjectMGLView(this);
        root.AddView(_glView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _statusBadge = new TextView(this) { Text = "connecting…", TextSize = 14 };
        _statusBadge.SetTextColor(Color.White);
        _statusBadge.SetBackgroundColor(Color.Argb(160, 0, 0, 0));
        _statusBadge.SetPadding(24, 8, 24, 8);
        root.AddView(_statusBadge, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Top | GravityFlags.End)
        {
            TopMargin = 48,
            RightMargin = 48,
        });

        SetContentView(root);

        // Double tap dismisses, parity with iOS; Back (and thus the TV remote)
        // works via the default activity behavior.
        _gestures = new GestureDetector(this, new DoubleTapListener(Finish));

        var addressText = Intent?.GetStringExtra(ExtraAddress);
        if (addressText is not null && Uri.TryCreate(addressText, UriKind.Absolute, out var address))
        {
            StartRemote(address, Intent?.GetStringExtra(ExtraServiceName));
        }
        else
        {
            _statusBadge.Visibility = ViewStates.Gone;
            StartSynthetic();
        }
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
        {
            Window!.DecorView.SystemUiFlags =
                SystemUiFlags.ImmersiveSticky | SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation
                | SystemUiFlags.LayoutStable | SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutHideNavigation;
        }
    }

    public override bool OnTouchEvent(MotionEvent? e) =>
        (e is not null && _gestures?.OnTouchEvent(e) == true) || base.OnTouchEvent(e);

    protected override void OnResume()
    {
        base.OnResume();
        _glView?.OnResume();
        if (_session is null or { State: not RemoteSessionState.Connected })
        {
            StartSynthetic();
        }
    }

    protected override void OnPause()
    {
        base.OnPause();
        _glView?.OnPause();
        StopSynthetic();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        StopSynthetic();
        if (_session is { } session)
        {
            _session = null;
            _ = session.DisposeAsync();
        }

        _resolver?.Dispose();
        _resolver = null;
    }

    private void StartRemote(Uri address, string? serviceName)
    {
        if (_session is not null || _glView is not { } glView)
        {
            return;
        }

        var cacheDir = System.IO.Path.Combine(CacheDir!.AbsolutePath, "presets");
        _resolver = serviceName is null ? null : new NsdServerBrowser(this);
        var resolver = _resolver;
        var session = new RemoteVisualizerSession(
            address,
            ClientName(),
            global::Android.OS.Build.Model ?? "Android",
            new PresetCache(cacheDir),
            serviceName is null || resolver is null
                ? null
                : ct => resolver.ResolveAsync(serviceName, ct));

        // PcmBuffer is thread-safe; PCM flows straight in from the hub thread.
        session.PcmReceived += samples => _glView?.AddPcm(samples, AudioChannels.Stereo);
        // LoadPresetData queues onto the GL thread itself; no UI-thread hop needed.
        session.PresetReceived += (content, hardCut) => _glView?.LoadPresetData(content, smoothTransition: !hardCut);
        session.StateChanged += state => RunOnUiThread(() => ApplySessionState(state));
        session.StatusChanged += message => Log.Info("FreqScene", $"[remote] {message}");

        _session = session;
        StartSynthetic();
        session.Start();
    }

    private string ClientName()
    {
        try
        {
            var name = Settings.Global.GetString(ContentResolver, "device_name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch (Exception)
        {
            // Some builds gate the setting; the model name is a fine fallback.
        }

        return global::Android.OS.Build.Model ?? "Android";
    }

    private void ApplySessionState(RemoteSessionState state)
    {
        switch (state)
        {
            case RemoteSessionState.Connected:
                StopSynthetic();
                if (_statusBadge is not null)
                {
                    _statusBadge.Visibility = ViewStates.Gone;
                }

                break;
            case RemoteSessionState.Connecting:
            case RemoteSessionState.Reconnecting:
                StartSynthetic();
                if (_statusBadge is not null)
                {
                    _statusBadge.Visibility = ViewStates.Visible;
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

    private sealed class DoubleTapListener(Action onDoubleTap) : GestureDetector.SimpleOnGestureListener
    {
        public override bool OnDoubleTap(MotionEvent e)
        {
            onDoubleTap();
            return true;
        }
    }
}
