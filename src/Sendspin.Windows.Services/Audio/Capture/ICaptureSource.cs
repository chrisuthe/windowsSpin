using Sendspin.SDK.Audio.Source;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// One thing <c>WindowsAudioCaptureDevice</c> can capture from: generated silence, desktop
/// loopback, or a recording endpoint.
/// </summary>
public interface ICaptureSource : IAsyncDisposable
{
    /// <summary>
    /// Raised for each captured buffer, in <see cref="CaptureFormat"/>, with the capture time of
    /// its first frame in the <c>HighPrecisionTimer</c> microsecond domain.
    /// </summary>
    event EventHandler<CapturedAudio>? FramesCaptured;

    /// <summary>Raised once if capture fails after it started; no frames follow.</summary>
    event EventHandler<Exception>? Faulted;

    /// <summary>Starts capturing. Throws if the source cannot be opened.</summary>
    /// <param name="cancellationToken">Unused by current sources; part of the start contract.</param>
    /// <returns>A task that completes once capture is running.</returns>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops capturing. Idempotent.</summary>
    /// <returns>A task that completes once no more frames will be raised.</returns>
    Task StopAsync();
}
