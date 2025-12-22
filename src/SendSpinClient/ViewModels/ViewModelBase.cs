using CommunityToolkit.Mvvm.ComponentModel;

namespace SendSpinClient.ViewModels;

/// <summary>
/// Base class for all ViewModels providing common functionality.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Clears any error message.
    /// </summary>
    protected void ClearError() => ErrorMessage = null;

    /// <summary>
    /// Sets an error message.
    /// </summary>
    protected void SetError(string message) => ErrorMessage = message;
}
