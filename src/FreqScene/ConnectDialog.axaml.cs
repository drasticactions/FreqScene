using Avalonia.Controls;
using Avalonia.Interactivity;
using FreqScene.Remote;

namespace FreqScene;

public partial class ConnectDialog : Window
{
    public ConnectDialog()
    {
        InitializeComponent();
    }

    public ConnectDialog(string? lastAddress)
    {
        InitializeComponent();

        var (host, port) = Parse(lastAddress);
        HostBox.Text = host;
        PortBox.Text = port.ToString();
    }

    public event Action<string, int>? ConnectRequested;

    private static (string Host, int Port) Parse(string? address)
    {
        if (!string.IsNullOrWhiteSpace(address))
        {
            var separator = address.LastIndexOf(':');
            if (separator > 0 && int.TryParse(address[(separator + 1)..], out var port))
            {
                return (address[..separator], port);
            }

            return (address, RemoteProtocol.DefaultPort);
        }

        return (string.Empty, RemoteProtocol.DefaultPort);
    }

    private void OnConnect(object? sender, RoutedEventArgs e)
    {
        var host = HostBox.Text?.Trim();
        if (string.IsNullOrEmpty(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            ShowError("Enter a valid host name or IP address.");
            return;
        }

        if (!int.TryParse(PortBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
        {
            ShowError("Enter a port between 1 and 65535.");
            return;
        }

        ConnectRequested?.Invoke(host, port);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
