using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FreqScene;

public partial class PairPinDialog : Window
{
    public PairPinDialog()
    {
        InitializeComponent();
    }

    public PairPinDialog(string? hostName)
    {
        InitializeComponent();
        PromptText.Text = hostName is null
            ? "The server requires pairing. Enter the PIN shown in FreqScene on the server."
            : $"“{hostName}” requires pairing. Enter the PIN shown in FreqScene on the server.";
    }

    /// <summary>The user submitted a PIN; the handler pairs and either closes the dialog or shows an error.</summary>
    public event Action<string>? PairRequested;

    private void OnPair(object? sender, RoutedEventArgs e)
    {
        var pin = PinBox.Text?.Trim() ?? "";
        if (pin.Length == 0)
        {
            ShowError("Enter the PIN shown on the server.");
            return;
        }

        PairButton.IsEnabled = false;
        ErrorText.IsVisible = false;
        PairRequested?.Invoke(pin);
    }

    public void ShowError(string message)
    {
        PairButton.IsEnabled = true;
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
