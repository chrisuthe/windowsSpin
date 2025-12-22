using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace SendSpinClient;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Subscribe to window events for system tray behavior
        Closing += OnWindowClosing;
        StateChanged += OnWindowStateChanged;
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
