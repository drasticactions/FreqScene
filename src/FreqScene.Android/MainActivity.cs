using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using FreqScene.Remote;

namespace FreqScene.Android;

[Activity(
    Label = "FreqScene",
    Exported = true,
    Theme = "@android:style/Theme.Material.NoActionBar",
    LaunchMode = global::Android.Content.PM.LaunchMode.SingleTop,
    ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation
        | global::Android.Content.PM.ConfigChanges.ScreenSize
        | global::Android.Content.PM.ConfigChanges.ScreenLayout
        | global::Android.Content.PM.ConfigChanges.UiMode)]
[IntentFilter(
    [Intent.ActionMain],
    Categories = [Intent.CategoryLauncher, Intent.CategoryLeanbackLauncher])]
public sealed class MainActivity : Activity
{
    private const string PrefsName = "FreqScene";
    private const string LastServerKey = "lastServerName";
    private const string LastAddressKey = "lastManualAddress";
    private const string ConnectByAddressRow = "Connect by address…";
    private const string StandaloneRow = "Standalone Mode";

    private NsdServerBrowser? _browser;
    private ListView? _list;
    private ISharedPreferences? _prefs;
    private bool _autoJoinArmed = true;
    private bool _joining;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _prefs = GetSharedPreferences(PrefsName, FileCreationMode.Private);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Color.Black);

        var title = new TextView(this)
        {
            Text = "FreqScene",
            TextSize = 34,
            Gravity = GravityFlags.CenterHorizontal,
        };
        title.SetTextColor(Color.White);
        title.SetTypeface(null, TypefaceStyle.Bold);
        title.SetPadding(0, 60, 0, 30);
        root.AddView(title);

        _list = new ListView(this);
        _list.ItemClick += OnRowClick;
        root.AddView(_list, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        SetContentView(root);

        _browser = new NsdServerBrowser(this);
        _browser.ServicesChanged += () =>
        {
            RefreshList();
            TryAutoJoin();
        };
        RefreshList();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _joining = false;
        _browser?.StartDiscovery();
    }

    protected override void OnPause()
    {
        base.OnPause();
        _browser?.StopDiscovery();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        _browser?.Dispose();
        _browser = null;
    }

    private void RefreshList()
    {
        if (_list is not { } list || _browser is not { } browser)
        {
            return;
        }

        var rows = browser.Services
            .Select(name => $"{name} — FreqScene server")
            .Append(ConnectByAddressRow)
            .Append(StandaloneRow)
            .ToList();
        list.Adapter = new ArrayAdapter<string>(
            this, global::Android.Resource.Layout.SimpleListItem1, rows);
    }

    private async void OnRowClick(object? sender, AdapterView.ItemClickEventArgs e)
    {
        if (_browser is not { } browser)
        {
            return;
        }

        _autoJoinArmed = false;
        if (e.Position < browser.Services.Count)
        {
            await JoinAsync(browser.Services[e.Position]);
        }
        else if (e.Position == browser.Services.Count)
        {
            PromptForAddress();
        }
        else
        {
            StartVisualizer(address: null, serviceName: null);
        }
    }

    private async void TryAutoJoin()
    {
        if (!_autoJoinArmed
            || _browser is not { } browser
            || browser.Services.Count != 1
            || _prefs?.GetString(LastServerKey, null) != browser.Services[0])
        {
            return;
        }

        _autoJoinArmed = false;
        var candidate = browser.Services[0];
        await Task.Delay(1500);
        if (!_joining && !IsFinishing && browser.Services.Count == 1 && browser.Services[0] == candidate)
        {
            await JoinAsync(candidate);
        }
    }

    private async Task JoinAsync(string serviceName)
    {
        if (_joining || _browser is not { } browser)
        {
            return;
        }

        _joining = true;
        var address = await browser.ResolveAsync(serviceName);
        if (address is null)
        {
            _joining = false;
            Toast.MakeText(this, $"Could not resolve “{serviceName}”", ToastLength.Short)?.Show();
            return;
        }

        _prefs?.Edit()?.PutString(LastServerKey, serviceName)?.Apply();
        StartVisualizer(address, serviceName);
    }

    private void PromptForAddress()
    {
        var input = new EditText(this)
        {
            Hint = $"192.168.1.10:{RemoteProtocol.DefaultPort}",
            Text = _prefs?.GetString(LastAddressKey, string.Empty),
        };

        new AlertDialog.Builder(this)
            .SetTitle("Connect to server")!
            .SetView(input)!
            .SetPositiveButton("Connect", (_, _) =>
            {
                var text = input.Text ?? string.Empty;
                if (ParseAddress(text) is not { } address)
                {
                    Toast.MakeText(this, "Invalid address", ToastLength.Short)?.Show();
                    return;
                }

                _prefs?.Edit()?.PutString(LastAddressKey, text.Trim())?.Apply();
                StartVisualizer(address, serviceName: null);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    private static Uri? ParseAddress(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!text.Contains("://"))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            return null;
        }

        return uri.IsDefaultPort ? new UriBuilder(uri) { Port = RemoteProtocol.DefaultPort }.Uri : uri;
    }

    private void StartVisualizer(Uri? address, string? serviceName)
    {
        var intent = new Intent(this, typeof(VisualizerActivity));
        if (address is not null)
        {
            intent.PutExtra(VisualizerActivity.ExtraAddress, address.ToString());
        }

        if (serviceName is not null)
        {
            intent.PutExtra(VisualizerActivity.ExtraServiceName, serviceName);
        }

        StartActivity(intent);
    }
}
