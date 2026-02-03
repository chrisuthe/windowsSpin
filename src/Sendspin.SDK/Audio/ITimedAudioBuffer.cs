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
    /// Gets the sync correction options used by this buffer.
    /// </summary>
    /// <remarks>
    /// Returns a clone of the options to prevent modification after construction.
    /// </remarks>
    SyncCorrectionOptions SyncOptions { get; }

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
    /// and when they actually play through the speakers (audio output buffer latency).
    /// </summary>
    /// <remarks>
    /// This value is stored for diagnostic/logging purposes. For sync error compensation,
    /// use <see cref="CalibratedStartupLatencyMicroseconds"/> which is specifically for
    /// push-model backends that pre-fill their output buffer.
    /// </remarks>
    long OutputLatencyMicroseconds { get; set; }

    /// <summary>
    /// Gets or sets the calibrated startup latency in microseconds for push-model audio backends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This value compensates for audio pre-filled in the output buffer on push-model
    /// backends (like ALSA) where the application must fill the buffer before playback starts.
    /// Without compensation, this prefill causes a constant negative sync error.
    /// </para>
    /// <para>
    /// Set this value only when the audio player has measured/calibrated the actual startup
    /// latency. Pull-model backends (like WASAPI) should leave this at 0.
    /// </para>
    /// <para>
    /// Formula: syncError = elapsed - samplesReadTime + CalibratedStartupLatencyMicroseconds
    /// </para>
    /// </remarks>
    long CalibratedStartupLatencyMicroseconds { get; set; }

    /// <summary>
    /// Gets or sets a descriptive name for the timing source used for sync calculations.
    /// </summary>
    /// <remarks>
    /// Set by the pipeline to indicate which timing source is providing timestamps:
    /// "audio-clock" for hardware audio clock, "monotonic" for MonotonicTimer, "wall-clock" for raw timer.
    /// Included in sync correction diagnostic logs to help identify timing-related issues.
    /// </remarks>
    string? TimingSourceName { get; set; }

    /// <summary>
    /// Gets the current raw sync error in microseconds.
    /// Positive = behind (need to speed up/drop), Negative = ahead (need to slow down/insert).
    /// </summary>
    /// <remarks>
    /// This is the unsmoothed sync error, updated on every Read/ReadRaw call.
    /// For correction decisions, consider using <see cref="SmoothedSyncErrorMicroseconds"/>.
    /// </remarks>
    long SyncErrorMicroseconds { get; }

    /// <summary>
    /// Gets the smoothed sync error in microseconds (EMA-filtered).
    /// Positive = behind (need to speed up/drop), Negative = ahead (need to slow down/insert).
    /// </summary>
    /// <remarks>
    /// This is filtered with an exponential moving average to reduce jitter.
    /// Use this value for correction decisions to avoid reacting to transient noise.
    /// </remarks>
    double SmoothedSyncErrorMicroseconds { get; }

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
    [Obsolete("Use SyncErrorMicroseconds with external ISyncCorrectionProvider instead. SDK no longer calculates correction rate.")]
    double TargetPlaybackRate { get; }

    /// <summary>
    /// Event raised when target playback rate changes.
    /// Subscribers should update their resampler ratio accordingly.
    /// </summary>
    [Obsolete("Use SyncErrorMicroseconds with external ISyncCorrectionProvider instead. SDK no longer calculates correction rate.")]
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
    /// Called from audio output thread. Applies internal sync correction (drop/insert).
    /// </summary>
    /// <param name="buffer">Buffer to fill with samples.</param>
    /// <param name="currentLocalTime">Current local time in microseconds.</param>
    /// <returns>Number of samples written.</returns>
    /// <remarks>
    /// This method applies internal sync correction. For external correction control,
    /// use <see cref="ReadRaw"/> instead and apply correction in the caller.
    /// </remarks>
    [Obsolete("Use ReadRaw() with external ISyncCorrectionProvider for correction control. This method applies internal correction.")]
    int Read(Span<float> buffer, long currentLocalTime);

    /// <summary>
    /// Reads samples without applying internal sync correction.
    /// Use this with an external <see cref="ISyncCorrectionProvider"/> for correction control.
    /// </summary>
    /// <param name="buffer">Buffer to fill with samples.</param>
    /// <param name="currentLocalTime">Current local time in microseconds.</param>
    /// <returns>Number of samples written (always matches samples read from buffer).</returns>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="Read"/>, this method does NOT apply drop/insert correction.
    /// It still calculates and updates <see cref="SyncErrorMicroseconds"/> and
    /// <see cref="SmoothedSyncErrorMicroseconds"/>.
    /// </para>
    /// <para>
    /// The caller is responsible for:
    /// 1. Reading sync error from this buffer
    /// 2. Calculating correction strategy (via ISyncCorrectionProvider)
    /// 3. Applying correction (drop/insert/resampling) externally
    /// 4. Calling <see cref="NotifyExternalCorrection"/> to update tracking
    /// </para>
    /// </remarks>
    int ReadRaw(Span<float> buffer, long currentLocalTime);

    /// <summary>
    /// Notifies the buffer that external sync correction was applied.
    /// Call this after applying drop/insert correction externally.
    /// </summary>
    /// <param name="samplesDropped">Number of samples dropped (consumed but not output). Must be non-negative.</param>
    /// <param name="samplesInserted">Number of samples inserted (output without consuming). Must be non-negative.</param>
    /// <remarks>
    /// <para>
    /// This updates internal tracking so <see cref="SyncErrorMicroseconds"/> remains accurate.
    /// </para>
    /// <para>
    /// When dropping: samplesRead cursor advances by droppedCount (we consumed more than output).
    /// When inserting: samplesRead cursor is reduced by insertedCount because <see cref="ReadRaw"/>
    /// already counted the full read, but inserted samples were duplicated output, not new consumption.
    /// </para>
    /// <para>
    /// <b>Contract:</b> Either <paramref name="samplesDropped"/> OR <paramref name="samplesInserted"/>
    /// should be non-zero, but not both simultaneously. Dropping and inserting in the same correction
    /// cycle is logically invalid. The <see cref="SyncCorrectionCalculator"/> enforces this by design.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="samplesDropped"/> or <paramref name="samplesInserted"/> is negative.
    /// </exception>
    void NotifyExternalCorrection(int samplesDropped, int samplesInserted);

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
    /// Gets the current raw sync error in microseconds.
    /// Positive = playing late (behind schedule), Negative = playing early (ahead of schedule).
    /// </summary>
    /// <remarks>
    /// This is the difference between where playback SHOULD be based on elapsed time
    /// versus where it actually IS based on samples output. Used to detect drift
    /// that may require correction via sample drop/insert.
    /// </remarks>
    public long SyncErrorMicroseconds { get; init; }

    /// <summary>
    /// Gets the smoothed sync error in microseconds (EMA-filtered).
    /// </summary>
    /// <remarks>
    /// This is the value used for correction decisions, filtered to reduce jitter.
    /// </remarks>
    public double SmoothedSyncErrorMicroseconds { get; init; }

    /// <summary>
    /// Gets the raw sync error in milliseconds (convenience property).
    /// </summary>
    public double SyncErrorMs => SyncErrorMicroseconds / 1000.0;

    /// <summary>
    /// Gets the smoothed sync error in milliseconds (convenience property).
    /// </summary>
    public double SmoothedSyncErrorMs => SmoothedSyncErrorMicroseconds / 1000.0;

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
