using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using SendspinClient.ViewModels;

namespace SendspinClient;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const double DefaultWidth = 400;
    private const double DefaultHeight = 630;

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
}
