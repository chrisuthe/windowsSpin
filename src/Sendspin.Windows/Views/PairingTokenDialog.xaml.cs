using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Sendspin.SDK.Client;

namespace Sendspin.Windows.Views;

/// <summary>
/// Dialog that shows this player's pairing token so it can be pasted into a server's
/// pairing prompt (e.g. Music Assistant). The token contains the Pairing PSK, so it
/// must never be logged or written anywhere other than this dialog and the clipboard.
/// </summary>
public partial class PairingTokenDialog : Window
{
    private readonly SendspinHostService _hostService;
    private readonly DispatcherTimer _copyFeedbackTimer;

    public PairingTokenDialog(SendspinHostService hostService)
    {
        InitializeComponent();
        _hostService = hostService;

        _copyFeedbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _copyFeedbackTimer.Tick += OnCopyFeedbackTimerTick;

        // A server can replace the pairing config (management/set-pairing-config or
        // management/remove-record) while the dialog is open, making the displayed
        // token stale. Unsubscribed when the dialog closes.
        _hostService.PairingConfigChanged += OnPairingConfigChanged;
        Closed += OnDialogClosed;

        LoadToken();
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        _hostService.PairingConfigChanged -= OnPairingConfigChanged;
        _copyFeedbackTimer.Stop();
    }

    /// <summary>
    /// Shows the stored token, generating and persisting a Pairing PSK on first use.
    /// Idempotent until the PSK is replaced.
    /// </summary>
    private void LoadToken()
    {
        try
        {
            TokenTextBox.Text = _hostService.EnsurePairingPsk();
        }
        catch (InvalidOperationException ex)
        {
            TokenTextBox.Text = string.Empty;
            CopyButton.IsEnabled = false;
            RegenerateButton.IsEnabled = false;
            ShowNotice($"No pairing token is available: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles a server-side pairing config change. The SDK raises this on a
    /// connection's receive thread, so all UI work is marshalled through the
    /// dispatcher (the same pattern MainViewModel uses for its SDK events).
    /// </summary>
    private void OnPairingConfigChanged(object? sender, PairingConfigChangedEventArgs e)
    {
        if (!e.PairingPskReplaced)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            LoadToken();
            ShowNotice("The server changed this player's pairing configuration. " +
                       "The token above has been refreshed; any token copied earlier no longer works.");
        });
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TokenTextBox.Text);
        }
        catch (ExternalException)
        {
            // Another process is holding the clipboard; the token stays selectable by hand.
            ShowNotice("Could not access the clipboard. Select the token above and copy it manually (Ctrl+C).");
            return;
        }

        ClearNotice();
        CopyButton.Content = "Copied!";
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    private void OnCopyFeedbackTimerTick(object? sender, EventArgs e)
    {
        _copyFeedbackTimer.Stop();
        CopyButton.Content = "Copy";
    }

    private void RegenerateButton_Click(object sender, RoutedEventArgs e)
    {
        const string warning =
            "Regenerating immediately invalidates the current token. Any server that was " +
            "given it - including one that is pairing right now - will fail to pair until " +
            "it gets the new token.\n\nRegenerate the pairing token?";

        var confirm = MessageBox.Show(
            this,
            warning,
            "Regenerate Pairing Token",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            TokenTextBox.Text = _hostService.RotatePairingPsk();
            ShowNotice("A new token was generated. The previous token no longer works.");
        }
        catch (InvalidOperationException ex)
        {
            ShowNotice($"Could not regenerate the token: {ex.Message}");
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowNotice(string message)
    {
        NoticeText.Text = message;
        NoticeText.Visibility = Visibility.Visible;
    }

    private void ClearNotice()
    {
        NoticeText.Text = string.Empty;
        NoticeText.Visibility = Visibility.Collapsed;
    }
}
