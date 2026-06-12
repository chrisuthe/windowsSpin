// <copyright file="EpisodeRecord.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// Aggregated facts about one closed sync-trouble episode, ready for classification and logging.
/// </summary>
public sealed record EpisodeRecord
{
    /// <summary>Gets the timestamp when the episode started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Gets the duration of the episode in seconds.</summary>
    public double DurationSeconds { get; init; }

    // Counter deltas accumulated over the episode

    /// <summary>Gets the total count of samples dropped for sync during the episode.</summary>
    public long Drops { get; init; }

    /// <summary>Gets the total count of samples inserted for sync during the episode.</summary>
    public long Inserts { get; init; }

    /// <summary>Gets the total count of audio underruns during the episode.</summary>
    public long Underruns { get; init; }

    /// <summary>Gets the total count of buffer re-anchors during the episode.</summary>
    public long Reanchors { get; init; }

    /// <summary>Gets the total count of audio callback gaps during the episode.</summary>
    public long CallbackGaps { get; init; }

    /// <summary>Gets the total count of adaptive forgetting triggers during the episode.</summary>
    public int AdaptiveForgettingTriggers { get; init; }

    // Extremes observed during the episode

    /// <summary>Gets the maximum absolute sync error observed during the episode in milliseconds.</summary>
    public double MaxAbsSyncErrorMs { get; init; }

    /// <summary>Gets the minimum buffered audio depth observed during the episode in milliseconds.</summary>
    public double MinBufferedMs { get; init; }

    /// <summary>Gets the target buffer depth in milliseconds.</summary>
    public double TargetMs { get; init; }

    /// <summary>Gets the maximum gap between consecutive audio chunks in milliseconds.</summary>
    public double MaxChunkGapMs { get; init; }

    /// <summary>Gets the maximum age of an audio chunk in milliseconds.</summary>
    public double MaxChunkAgeMs { get; init; }

    /// <summary>Gets the maximum jitter in round-trip time measurements in milliseconds.</summary>
    public double MaxRttJitterMs { get; init; }

    /// <summary>Gets the maximum gap duration in the audio read callback in milliseconds.</summary>
    public double MaxCallbackGapMs { get; init; }

    /// <summary>Gets total absolute movement of the clock offset during the episode (sum of |Δoffset|).</summary>
    public double OffsetTravelMs { get; init; }

    /// <summary>Gets the number of times correction direction changed (dropping↔inserting) during the episode.</summary>
    public int DirectionFlips { get; init; }

    /// <summary>Gets whether playback rate hit ≥90% of max correction at any point.</summary>
    public bool RateSaturated { get; init; }

    /// <summary>Gets average network ingest rate over the episode in bytes/second.</summary>
    public double BytesPerSecond { get; init; }

    // Pre-roll (up to 10 s before the episode opened)

    /// <summary>Gets the minimum buffered audio depth observed during the pre-roll period in milliseconds.</summary>
    public double PreRollMinBufferedMs { get; init; }

    /// <summary>Gets the maximum gap between consecutive audio chunks during the pre-roll period in milliseconds.</summary>
    public double PreRollMaxChunkGapMs { get; init; }

    // Format context for skew ppm math

    /// <summary>Gets the audio sample rate in Hz.</summary>
    public int SampleRate { get; init; }

    /// <summary>Gets the number of audio channels.</summary>
    public int Channels { get; init; }
}
