using System.Windows;

namespace SendspinClient.Views;

/// <summary>
/// Dialog shown at startup when verbose logging is detected.
/// Allows user to either leave logging as-is or disable it immediately.
/// </summary>
public partial class LoggingWarningDialog : Window
{
    /// <summary>
    /// Gets whether the user chose to disable logging.
    /// </summary>
    public bool DisableLogging { get; private set; }

    public LoggingWarningDialog()
    {
        InitializeComponent();
    }

    private void LeaveButton_Click(object sender, RoutedEventArgs e)
    {
        DisableLogging = false;
        DialogResult = true;
        Close();
    }

    private void DisableButton_Click(object sender, RoutedEventArgs e)
    {
        DisableLogging = true;
        DialogResult = true;
        Close();
    }
}
