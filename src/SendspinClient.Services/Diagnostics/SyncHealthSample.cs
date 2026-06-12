// <copyright file="SyncHealthSample.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// One 10 Hz snapshot of every signal the sync-health monitor watches.
/// Built by SyncHealthMonitor from SDK stats; consumed by EpisodeDetector.
/// </summary>
public readonly record struct SyncHealthSample
{
    /// <summary>Gets the monotonic capture time in milliseconds (Environment.TickCount64).</summary>
    public long TimestampMs { get; init; }

    // --- Buffer / correction (AudioBufferStats) ---

    /// <summary>Gets the smoothed sync error in milliseconds.</summary>
    public double SmoothedSyncErrorMs { get; init; }

    /// <summary>Gets the current buffered audio in milliseconds.</summary>
    public double BufferedMs { get; init; }

    /// <summary>Gets the target buffer depth in milliseconds.</summary>
    public double TargetMs { get; init; }

    /// <summary>Gets the count of audio underruns.</summary>
    public long UnderrunCount { get; init; }

    /// <summary>Gets the count of samples dropped for sync correction.</summary>
    public long SamplesDroppedForSync { get; init; }

    /// <summary>Gets the count of samples inserted for sync correction.</summary>
    public long SamplesInsertedForSync { get; init; }

    /// <summary>Gets the count of times the buffer was re-anchored.</summary>
    public long ReanchorCount { get; init; }

    /// <summary>Gets the target playback rate for smooth sync correction.</summary>
    public double TargetPlaybackRate { get; init; }

    /// <summary>Gets the total samples written to the buffer since the pipeline started.</summary>
    public long TotalSamplesWritten { get; init; }

    // --- Network ingest (AudioBufferStats, 9.0.2) ---

    /// <summary>Gets the age of the last received audio chunk in milliseconds.</summary>
    public double LastChunkAgeMs { get; init; }

    /// <summary>Gets the maximum gap between consecutive audio chunks in milliseconds.</summary>
    public double MaxChunkGapMs { get; init; }

    /// <summary>Gets the jitter in chunk arrival timing in milliseconds.</summary>
    public double ChunkJitterMs { get; init; }

    /// <summary>Gets the total bytes of audio data received.</summary>
    public long BytesReceived { get; init; }

    // --- Clock sync (ClockSyncStatus) ---

    /// <summary>Gets the current clock offset from server in milliseconds.</summary>
    public double OffsetMs { get; init; }

    /// <summary>Gets the jitter in round-trip time measurements in milliseconds.</summary>
    public double RttJitterMs { get; init; }

    /// <summary>Gets the count of times adaptive forgetting was triggered.</summary>
    public int AdaptiveForgettingTriggerCount { get; init; }

    // --- Local audio thread (ReadCallbackGapTracker) ---

    /// <summary>Gets the count of gaps in the audio read callback.</summary>
    public long CallbackGapCount { get; init; }

    /// <summary>Gets the maximum gap duration in the audio read callback in milliseconds.</summary>
    public double MaxCallbackGapMs { get; init; }

    // --- Format context ---

    /// <summary>Gets the audio sample rate in Hz.</summary>
    public int SampleRate { get; init; }

    /// <summary>Gets the number of audio channels.</summary>
    public int Channels { get; init; }
}
