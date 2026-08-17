using System.Windows;

namespace Sendspin.Windows.Views;

/// <summary>
/// Modal dialog that displays the dynamic pairing pairing code derived for the current pairing
/// attempt so the operator can type it into the server (e.g. Music Assistant). The pairing code
/// is the pairing secret while the attempt is live, so - like the pairing token - it
/// must never be logged or written anywhere other than this dialog. Created, updated,
/// and closed exclusively on the dispatcher thread by <see cref="PairingCodePresenter"/>.
/// </summary>
public partial class PairingCodeDialog : Window
{
    public PairingCodeDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows <paramref name="pin"/>. A pairing retry derives a fresh pairing code (each attempt
    /// carries new nonces), so on retry this is called again on the open dialog rather
    /// than opening a second window; a notice then tells the operator the old pairing code is dead.
    /// </summary>
    public void UpdatePairingCode(string pairingCode)
    {
        var replaced = !string.IsNullOrEmpty(PairingCodeText.Text) && PairingCodeText.Text != pairingCode;
        PairingCodeText.Text = pairingCode;

        if (replaced)
        {
            NoticeText.Text = "The server started a new pairing attempt. Enter the new pairing code above; " +
                              "the previous pairing code no longer works.";
            NoticeText.Visibility = Visibility.Visible;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
