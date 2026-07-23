using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FreqScene.Remote.Server;

namespace FreqScene;

public partial class PairingWindow : Window
{
    private readonly RemoteServerManager? _manager;
    private readonly DispatcherTimer _timer;
    private bool _paired;

    public PairingWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer();
    }

    public PairingWindow(RemoteServerManager manager)
    {
        InitializeComponent();
        _manager = manager;
        _manager.Pairing.DevicePaired += OnDevicePaired;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateCountdown();
        _timer.Start();
        StartNewPin();
    }

    private void StartNewPin()
    {
        if (_manager is null)
        {
            return;
        }

        _paired = false;
        var pin = _manager.Pairing.BeginPairing();
        PinText.Text = $"{pin[..3]} {pin[3..]}";
        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        if (_manager is null || _paired)
        {
            return;
        }

        if (_manager.Pairing.ActivePin is null)
        {
            StatusText.Text = "PIN expired — click New PIN.";
            return;
        }

        var remaining = _manager.Pairing.PinDeadlineUtc - DateTime.UtcNow;
        StatusText.Text = $"Waiting for a device… PIN valid for {remaining:m\\:ss}.";
    }

    private void OnDevicePaired(PairedDevice device) => Dispatcher.UIThread.Post(() =>
    {
        _paired = true;
        StatusText.Text = $"Paired “{device.Name}” ✔";
    });

    private void OnNewPin(object? sender, RoutedEventArgs e) => StartNewPin();

    private void OnDone(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (_manager is not null)
        {
            _manager.Pairing.DevicePaired -= OnDevicePaired;
            _manager.Pairing.CancelPairing();
        }

        base.OnClosed(e);
    }
}
