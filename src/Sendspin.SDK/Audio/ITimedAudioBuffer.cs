// <copyright file="ITimedAudioBuffer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Models;

namespace Sendspin.SDK.Audio;

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
    /// Gets or sets the output latency in microseconds (informational).
    /// This is the delay between when samples are read from the buffer
    /// and when they actually play through the speakers (WASAPI buffer latency).
    /// </summary>
    /// <remarks>
    /// This value is stored for diagnostic/logging purposes but is NOT used in sync
    /// error calculation. The sync error tracks the rate at which we consume samples
    /// relative to wall clock time, which is independent of the constant output latency offset.
    /// </remarks>
    long OutputLatencyMicroseconds { get; set; }

    /// <summary>
    /// Gets the target playback rate for smooth sync correction via resampling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Values: 1.0 = normal speed, &gt;1.0 = speed up (behind), &lt;1.0 = slow down (ahead).
    /// </para>
    /// <para>
    /// Range is typically 0.96-1.04 (±4%), but most corrections use 0.98-1.02 (±2%).
    /// This is imperceptible to human ears unlike discrete sample dropping.
    /// </para>
    /// </remarks>
    double TargetPlaybackRate { get; }

    /// <summary>
    /// Event raised when target playback rate changes.
    /// Subscribers should update their resampler ratio accordingly.
    /// </summary>
    event Action<double>? TargetPlaybackRateChanged;

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

    /// <summary>
    /// Gets the target playback rate for resampling-based sync correction.
    /// </summary>
    /// <remarks>
    /// 1.0 = normal, &gt;1.0 = speed up, &lt;1.0 = slow down.
    /// Only meaningful when <see cref="CurrentCorrectionMode"/> is <see cref="SyncCorrectionMode.Resampling"/>.
    /// </remarks>
    public double TargetPlaybackRate { get; init; } = 1.0;

    /// <summary>
    /// Gets the total samples read since playback started (for sync debugging).
    /// </summary>
    public long SamplesReadSinceStart { get; init; }

    /// <summary>
    /// Gets the total samples output since playback started (for sync debugging).
    /// </summary>
    public long SamplesOutputSinceStart { get; init; }

    /// <summary>
    /// Gets elapsed time since playback started in milliseconds (for sync debugging).
    /// </summary>
    public double ElapsedSinceStartMs { get; init; }
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
    /// Using playback rate adjustment via resampling (smooth, imperceptible correction).
    /// This is the preferred mode for small errors (2-15ms).
    /// </summary>
    Resampling,

    /// <summary>
    /// Dropping samples to catch up (playing too slow).
    /// Used for larger errors (&gt;15ms) that need faster correction.
    /// </summary>
    Dropping,

    /// <summary>
    /// Inserting samples to slow down (playing too fast).
    /// Used for larger errors (&gt;15ms) that need faster correction.
    /// </summary>
    Inserting,
}
