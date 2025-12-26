// <copyright file="ITimedAudioBuffer.cs" company="SendSpin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using SendSpinClient.Core.Models;

namespace SendSpinClient.Core.Audio;

/// <summary>
/// Thread-safe circular buffer for timestamped PCM audio.
/// Handles jitter compensation and timed release of audio samples.
/// </summary>
public interface ITimedAudioBuffer : IDisposable
{
    /// <summary>
    /// Gets the audio format for samples in the buffer.
    /// </summary>
    AudioFormat Format { get; }

    /// <summary>
    /// Gets the current buffer fill level in milliseconds.
    /// </summary>
    double BufferedMilliseconds { get; }

    /// <summary>
    /// Gets or sets the target buffer level in milliseconds (for jitter compensation).
    /// </summary>
    double TargetBufferMilliseconds { get; set; }

    /// <summary>
    /// Gets whether the buffer has enough data to start playback.
    /// </summary>
    bool IsReadyForPlayback { get; }

    /// <summary>
    /// Adds decoded audio samples with their target playback timestamp.
    /// Called from decoder thread.
    /// </summary>
    /// <param name="samples">Interleaved float PCM samples.</param>
    /// <param name="serverTimestamp">Server timestamp (microseconds) when audio should play.</param>
    void Write(ReadOnlySpan<float> samples, long serverTimestamp);

    /// <summary>
    /// Reads samples that are ready for playback at the current time.
    /// Called from audio output thread.
    /// </summary>
    /// <param name="buffer">Buffer to fill with samples.</param>
    /// <param name="currentLocalTime">Current local time in microseconds.</param>
    /// <returns>Number of samples written.</returns>
    int Read(Span<float> buffer, long currentLocalTime);

    /// <summary>
    /// Clears all buffered audio (for seek/stream clear).
    /// </summary>
    void Clear();

    /// <summary>
    /// Gets buffer statistics for monitoring.
    /// </summary>
    /// <returns>Current buffer statistics.</returns>
    AudioBufferStats GetStats();
}

/// <summary>
/// Statistics for audio buffer monitoring.
/// </summary>
public record AudioBufferStats
{
    /// <summary>
    /// Gets the current buffered time in milliseconds.
    /// </summary>
    public double BufferedMs { get; init; }

    /// <summary>
    /// Gets the target buffer time in milliseconds.
    /// </summary>
    public double TargetMs { get; init; }

    /// <summary>
    /// Gets the number of underrun events (buffer empty when reading).
    /// </summary>
    public long UnderrunCount { get; init; }

    /// <summary>
    /// Gets the number of overrun events (buffer full when writing).
    /// </summary>
    public long OverrunCount { get; init; }

    /// <summary>
    /// Gets the number of samples dropped due to overflow.
    /// </summary>
    public long DroppedSamples { get; init; }

    /// <summary>
    /// Gets the total samples written to the buffer.
    /// </summary>
    public long TotalSamplesWritten { get; init; }

    /// <summary>
    /// Gets the total samples read from the buffer.
    /// </summary>
    public long TotalSamplesRead { get; init; }

    /// <summary>
    /// Gets the current sync error in microseconds.
    /// Positive = playing late (behind schedule), Negative = playing early (ahead of schedule).
    /// </summary>
    /// <remarks>
    /// This is the difference between where playback SHOULD be based on elapsed time
    /// versus where it actually IS based on samples output. Used to detect drift
    /// that may require correction via sample drop/insert.
    /// </remarks>
    public long SyncErrorMicroseconds { get; init; }

    /// <summary>
    /// Gets the sync error in milliseconds (convenience property).
    /// </summary>
    public double SyncErrorMs => SyncErrorMicroseconds / 1000.0;

    /// <summary>
    /// Gets whether playback is currently active.
    /// </summary>
    public bool IsPlaybackActive { get; init; }

    /// <summary>
    /// Gets the number of samples dropped for sync correction (to speed up playback).
    /// </summary>
    public long SamplesDroppedForSync { get; init; }

    /// <summary>
    /// Gets the number of samples inserted for sync correction (to slow down playback).
    /// </summary>
    public long SamplesInsertedForSync { get; init; }

    /// <summary>
    /// Gets the current sync correction mode.
    /// </summary>
    public SyncCorrectionMode CurrentCorrectionMode { get; init; }
}

/// <summary>
/// Indicates the current sync correction mode.
/// </summary>
public enum SyncCorrectionMode
{
    /// <summary>
    /// No correction needed - sync error within deadband.
    /// </summary>
    None,

    /// <summary>
    /// Dropping samples to catch up (playing too slow).
    /// </summary>
    Dropping,

    /// <summary>
    /// Inserting samples to slow down (playing too fast).
    /// </summary>
    Inserting,
}
