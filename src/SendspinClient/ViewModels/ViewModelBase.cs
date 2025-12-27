using CommunityToolkit.Mvvm.ComponentModel;

namespace SendspinClient.ViewModels;

/// <summary>
/// Base class for all ViewModels providing common functionality.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    private CancellationTokenSource? _errorClearCts;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Clears any error message.
    /// </summary>
    protected void ClearError()
    {
        _errorClearCts?.Cancel();
        _errorClearCts?.Dispose();
        _errorClearCts = null;
        ErrorMessage = null;
    }

    /// <summary>
    /// Sets an error message that auto-clears after a timeout.
    /// </summary>
    /// <param name="message">The error message to display.</param>
    /// <param name="autoClearSeconds">Seconds before auto-clearing (default 8). Set to 0 to disable auto-clear.</param>
    protected void SetError(string message, int autoClearSeconds = 8)
    {
        // Cancel any pending auto-clear
        _errorClearCts?.Cancel();
        _errorClearCts?.Dispose();

        ErrorMessage = message;

        if (autoClearSeconds > 0)
        {
            _errorClearCts = new CancellationTokenSource();
            var token = _errorClearCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(autoClearSeconds), token);
                    if (!token.IsCancellationRequested)
                    {
                        // Must update on UI thread
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            if (ErrorMessage == message) // Only clear if it's still the same error
                            {
                                ErrorMessage = null;
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when error is cleared or replaced
                }
            });
        }
    }
}
