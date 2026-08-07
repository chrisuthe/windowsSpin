using System.Windows;

namespace Sendspin.Windows.Views;

/// <summary>
/// Modal dialog that displays the dynamic pairing PIN derived for the current pairing
/// attempt so the operator can type it into the server (e.g. Music Assistant). The PIN
/// is the pairing secret while the attempt is live, so - like the pairing token - it
/// must never be logged or written anywhere other than this dialog. Created, updated,
/// and closed exclusively on the dispatcher thread by <see cref="PinPairingPresenter"/>.
/// </summary>
public partial class PinPairingDialog : Window
{
    public PinPairingDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows <paramref name="pin"/>. A pairing retry derives a fresh PIN (each attempt
    /// carries new nonces), so on retry this is called again on the open dialog rather
    /// than opening a second window; a notice then tells the operator the old PIN is dead.
    /// </summary>
    public void UpdatePin(string pin)
    {
        var replaced = !string.IsNullOrEmpty(PinText.Text) && PinText.Text != pin;
        PinText.Text = pin;

        if (replaced)
        {
            NoticeText.Text = "The server started a new pairing attempt. Enter the new PIN above; " +
                              "the previous PIN no longer works.";
            NoticeText.Visibility = Visibility.Visible;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
