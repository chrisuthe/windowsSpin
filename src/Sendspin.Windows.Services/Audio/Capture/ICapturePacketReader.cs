namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// The three <c>IAudioCaptureClient</c> calls the capture loop makes, so NAudio's wrapper and
/// the process-loopback interop can share one loop.
/// </summary>
public interface ICapturePacketReader
{
    /// <summary>Frames in the next packet, or 0 when none is waiting.</summary>
    /// <returns>The frame count.</returns>
    int GetNextPacketSize();

    /// <summary>Locks the next packet. Must be followed by <see cref="ReleaseBuffer"/>.</summary>
    /// <param name="frames">Frames in the packet.</param>
    /// <param name="silent">True when Windows marked the packet silent; its contents are then undefined.</param>
    /// <param name="qpcPosition">Capture time of the first frame in 100-ns QPC units, or 0 when unknown.</param>
    /// <returns>The packet's data.</returns>
    IntPtr GetBuffer(out int frames, out bool silent, out long qpcPosition);

    /// <summary>Releases the packet returned by <see cref="GetBuffer"/>.</summary>
    /// <param name="frames">The frame count <see cref="GetBuffer"/> reported.</param>
    void ReleaseBuffer(int frames);
}
