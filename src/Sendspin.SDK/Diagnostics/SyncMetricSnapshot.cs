// <copyright file="SyncMetricSnapshot.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Audio;

namespace Sendspin.SDK.Diagnostics;

/// <summary>
/// Immutable record of sync metrics captured at a specific point in time.
/// Used for diagnostic audio recording with embedded markers.
/// </summary>
/// <remarks>
/// Each snapshot represents the sync state at the moment audio samples were captured.
/// The <see cref="SamplePosition"/> allows correlation between audio waveform position
/// and the metrics at that moment.
/// </remarks>
public readonly record struct SyncMetricSnapshot
{
    /// <summary>
    /// Gets the local timestamp in microseconds when this snapshot was captured.
    /// Uses <see cref="Synchronization.HighPrecisionTimer"/> for accuracy.
    /// </summary>
    public long TimestampMicroseconds { get; init; }

    /// <summary>
    /// Gets the cumulative sample position in the ring buffer when this snapshot was captured.
    /// Used to correlate markers with audio waveform positions.
    /// </summary>
    public long SamplePosition { get; init; }

    /// <summary>
    /// Gets the raw (unsmoothed) sync error in microseconds at capture time.
    /// Positive = behind (need to speed up), negative = ahead (need to slow down).
    /// </summary>
    public long RawSyncErrorMicroseconds { get; init; }

    /// <summary>
    /// Gets the EMA-smoothed sync error in microseconds at capture time.
    /// This is the value used for correction decisions.
    /// </summary>
    public long SmoothedSyncErrorMicroseconds { get; init; }

    /// <summary>
    /// Gets the current sync correction mode.
    /// </summary>
    public SyncCorrectionMode CorrectionMode { get; init; }

    /// <summary>
    /// Gets the current playback rate (1.0 = normal speed).
    /// Values > 1.0 indicate speedup, < 1.0 indicate slowdown.
    /// </summary>
    public double PlaybackRate { get; init; }

    /// <summary>
    /// Gets the current buffer depth in milliseconds.
    /// </summary>
    public double BufferDepthMs { get; init; }

    /// <summary>
    /// Formats a short label suitable for WAV cue markers.
    /// </summary>
    /// <returns>A compact string like "ERR:+2.35ms RATE:1.02x RSMP".</returns>
    public string FormatShortLabel()
    {
        var modeAbbrev = CorrectionMode switch
        {
            SyncCorrectionMode.Resampling => "RSMP",
            SyncCorrectionMode.Dropping => "DROP",
            SyncCorrectionMode.Inserting => "INS",
            _ => "OK",
        };

        var errorMs = SmoothedSyncErrorMicroseconds / 1000.0;
        return $"ERR:{errorMs:+0.00;-0.00}ms RATE:{PlaybackRate:F3}x {modeAbbrev}";
    }

    /// <summary>
    /// Formats detailed metrics suitable for WAV note chunks or log output.
    /// </summary>
    /// <returns>A multi-line string with all metric details.</returns>
    public string FormatDetailedNote()
    {
        return $"Raw Error: {RawSyncErrorMicroseconds} us\n" +
               $"Smoothed Error: {SmoothedSyncErrorMicroseconds} us\n" +
               $"Playback Rate: {PlaybackRate:F4}x\n" +
               $"Correction Mode: {CorrectionMode}\n" +
               $"Buffer Depth: {BufferDepthMs:F1} ms\n" +
               $"Timestamp: {TimestampMicroseconds} us\n" +
               $"Sample Position: {SamplePosition}";
    }

    /// <summary>
    /// Formats as a tab-separated line for Audacity label import.
    /// </summary>
    /// <param name="startTimeSeconds">Start time in seconds (waveform position).</param>
    /// <returns>A line like "0.100000\t0.100000\tERR:+1.20ms RATE:1.002x RSMP".</returns>
    public string FormatAudacityLabel(double startTimeSeconds)
    {
        // Audacity label format: start_time\tend_time\tlabel
        // Point labels have same start and end time
        return $"{startTimeSeconds:F6}\t{startTimeSeconds:F6}\t{FormatShortLabel()}";
    }
}
