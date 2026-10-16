namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>Capture from an input failed; the device has already fallen back to silence.</summary>
/// <param name="inputId">The input that failed.</param>
/// <param name="exception">Why.</param>
public sealed class CaptureFaultedEventArgs(string inputId, Exception exception) : EventArgs
{
    /// <summary>Gets the input that failed.</summary>
    public string InputId { get; } = inputId;

    /// <summary>Gets the failure.</summary>
    public Exception Exception { get; } = exception;
}
