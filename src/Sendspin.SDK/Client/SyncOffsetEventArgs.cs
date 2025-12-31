namespace Sendspin.SDK.Client;

/// <summary>
/// Event args for sync offset applied from GroupSync calibration.
/// </summary>
public sealed class SyncOffsetEventArgs : EventArgs
{
    /// <summary>
    /// The player ID that the offset was applied to.
    /// </summary>
    public string PlayerId { get; }

    /// <summary>
    /// The offset in milliseconds that was applied.
    /// Positive = delay playback, Negative = advance playback.
    /// </summary>
    public double OffsetMs { get; }

    /// <summary>
    /// The source of the calibration (e.g., "groupsync", "manual").
    /// </summary>
    public string? Source { get; }

    public SyncOffsetEventArgs(string playerId, double offsetMs, string? source = null)
    {
        PlayerId = playerId;
        OffsetMs = offsetMs;
        Source = source;
    }
}
