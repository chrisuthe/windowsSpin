using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Sendspin.SDK.Discovery;
using SendspinClient.ViewModels;

namespace SendspinClient;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const double DefaultWidth = 400;
    private const double DefaultHeight = 780;

    public MainWindow()
    {
        InitializeComponent();

        // Subscribe to window events for system tray behavior
        Closing += OnWindowClosing;
        StateChanged += OnWindowStateChanged;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Subscribe to ViewModel property changes to reset window size when settings close.
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is INotifyPropertyChanged newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSettingsOpen))
        {
            if (sender is MainViewModel vm && !vm.IsSettingsOpen)
            {
                // Reset to default size when settings panel closes
                Width = DefaultWidth;
                Height = DefaultHeight;
            }
        }
    }

    /// <summary>
    /// Handles window close button - hides to tray instead of closing.
    /// The app continues running in the system tray.
    /// </summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // Don't actually close - hide to system tray instead
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Handles window state changes - Shift+Minimize hides to tray.
    /// Regular minimize goes to taskbar as normal.
    /// </summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            // If Shift is held, minimize to tray instead of taskbar
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                Hide();
                WindowState = WindowState.Normal; // Reset state so it shows normally when restored
            }
        }
    }

    /// <summary>
    /// Handles clicks on server cards in the welcome view.
    /// Opens the auto-connect dialog for the selected server.
    /// </summary>
    private void ServerCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is DiscoveredServer server)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SelectServerCommand.Execute(server);
            }
        }
    }

    /// <summary>
    /// Validates that only numeric input (including negative sign) is entered in TextBox.
    /// </summary>
    private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Allow digits and minus sign (for negative numbers)
        foreach (char c in e.Text)
        {
            if (!char.IsDigit(c) && c != '-')
            {
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>
    /// Prevents pasting non-numeric content into TextBox.
    /// </summary>
    private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            string text = (string)e.DataObject.GetData(typeof(string));
            if (!int.TryParse(text, out _))
            {
                e.CancelCommand();
            }
        }
        else
        {
            e.CancelCommand();
        }
    }
}
