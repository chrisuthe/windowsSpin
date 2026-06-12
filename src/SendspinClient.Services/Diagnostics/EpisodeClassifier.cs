// <copyright file="EpisodeClassifier.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Globalization;

namespace SendspinClient.Services.Diagnostics;

/// <summary>The root-cause label assigned to a sync-trouble episode.</summary>
public enum SyncHealthVerdict
{
    /// <summary>Network starvation: buffer collapsed or underruns occurred.</summary>
    NetworkStarvation,

    /// <summary>Clock sync instability: Kalman filter oscillating or RTT jitter high.</summary>
    ClockSyncInstability,

    /// <summary>Device clock skew: steady one-directional drift consistent with hardware clock error.</summary>
    DeviceClockSkew,

    /// <summary>Local timing: audio callback gaps with healthy network and buffer.</summary>
    LocalTiming,

    /// <summary>Unknown: no rule matched; evidence carried for future tuning.</summary>
    Unknown,
}

/// <summary>Classification output: verdict plus the evidence values that fired the rule.</summary>
public sealed record SyncHealthClassification
{
    /// <summary>Gets the root-cause verdict for the episode.</summary>
    required public SyncHealthVerdict Verdict { get; init; }

    /// <summary>Gets a human-readable summary of the values that triggered the verdict.</summary>
    required public string Evidence { get; init; }

    /// <summary>Gets the estimated clock skew in parts-per-million (set only for <see cref="SyncHealthVerdict.DeviceClockSkew"/>).</summary>
    public double? EstimatedSkewPpm { get; init; }
}

/// <summary>
/// Deterministic, ordered classification rules for sync-trouble episodes.
/// First match wins. Thresholds are tuning constants — adjust from real-world
/// sync-health.log evidence, not speculation.
/// </summary>
public static class EpisodeClassifier
{
    // Rule thresholds (see design spec for rationale)
    internal const double NetworkBufferFraction = 0.4;     // buffer below 40% of target
    internal const double NetworkChunkGapMs = 1000;    // >1000 ms = ingest stalled (rule 1)
    internal const double HealthyBufferFraction = 0.6;     // buffer at/above 60% of target
    internal const double HealthyChunkGapMs = 500;     // <500 ms = healthy enough for skew/local rules; the 500-1000 ms zone is intentionally ambiguous and falls through to Unknown
    internal const double RttJitterThresholdMs = 5.0;
    internal const double OffsetTravelThresholdMs = 5.0;
    internal const int MinDirectionFlips = 2;
    internal const double SkewMinDurationSeconds = 10.0;
    internal const double LocalMaxCallbackGapMs = 100;

    /// <summary>Classifies a closed episode into a root-cause verdict with supporting evidence.</summary>
    /// <param name="e">The closed episode record to classify.</param>
    /// <returns>A <see cref="SyncHealthClassification"/> containing the verdict and evidence string.</returns>
    public static SyncHealthClassification Classify(EpisodeRecord e)
    {
        var ci = CultureInfo.InvariantCulture;

        // Rule 1: network starvation
        var bufferCollapsed = e.MinBufferedMs < NetworkBufferFraction * e.TargetMs;
        var ingestStalled = e.MaxChunkGapMs > NetworkChunkGapMs || e.MaxChunkAgeMs > NetworkChunkGapMs;
        if (e.Underruns > 0 || (bufferCollapsed && ingestStalled))
        {
            return new SyncHealthClassification
            {
                Verdict = SyncHealthVerdict.NetworkStarvation,
                Evidence = string.Create(ci,
                    $"underruns={e.Underruns} minBuffer={e.MinBufferedMs:F0}ms/{e.TargetMs:F0}ms preRollMinBuffer={e.PreRollMinBufferedMs:F0}ms maxChunkGap={e.MaxChunkGapMs:F0}ms maxChunkAge={e.MaxChunkAgeMs:F0}ms bytesPerSec={e.BytesPerSecond:F0}"),
            };
        }

        // Rule 2: clock-sync instability
        var clockUnstable = e.MaxRttJitterMs > RttJitterThresholdMs
            || e.AdaptiveForgettingTriggers > 0
            || e.OffsetTravelMs > OffsetTravelThresholdMs;
        if (e.DirectionFlips >= MinDirectionFlips && clockUnstable)
        {
            return new SyncHealthClassification
            {
                Verdict = SyncHealthVerdict.ClockSyncInstability,
                Evidence = string.Create(ci,
                    $"dirFlips={e.DirectionFlips} rttJitter={e.MaxRttJitterMs:F1}ms offsetTravel={e.OffsetTravelMs:F1}ms forgettingTriggers={e.AdaptiveForgettingTriggers}"),
            };
        }

        // Rule 3: device clock skew
        var oneDirectional = (e.Drops > 0) ^ (e.Inserts > 0);
        var bufferHealthy = e.MinBufferedMs >= HealthyBufferFraction * e.TargetMs;
        var networkHealthy = e.MaxChunkGapMs < HealthyChunkGapMs && e.MaxRttJitterMs <= RttJitterThresholdMs;
        if (oneDirectional && bufferHealthy && networkHealthy && e.DurationSeconds >= SkewMinDurationSeconds)
        {
            // Counters are in samples (channels included): ppm = samples/s ÷ (rate × channels) × 1e6
            var samplesPerSecond = (e.Drops > 0 ? e.Drops : -e.Inserts) / e.DurationSeconds;
            var ppm = samplesPerSecond / (e.SampleRate * (double)e.Channels) * 1_000_000.0;
            return new SyncHealthClassification
            {
                Verdict = SyncHealthVerdict.DeviceClockSkew,
                EstimatedSkewPpm = ppm,
                Evidence = string.Create(ci,
                    $"direction={(e.Drops > 0 ? "drop" : "insert")} estSkew={ppm:F0}ppm minBuffer={e.MinBufferedMs:F0}ms duration={e.DurationSeconds:F1}s"),
            };
        }

        // Rule 4: local timing / CPU
        if ((e.CallbackGaps > 0 || e.MaxCallbackGapMs > LocalMaxCallbackGapMs)
            && bufferHealthy
            && e.MaxChunkGapMs < HealthyChunkGapMs)
        {
            return new SyncHealthClassification
            {
                Verdict = SyncHealthVerdict.LocalTiming,
                Evidence = string.Create(ci,
                    $"cbGaps={e.CallbackGaps} maxCbGap={e.MaxCallbackGapMs:F0}ms minBuffer={e.MinBufferedMs:F0}ms"),
            };
        }

        // Rule 5: unknown — carry full aggregates so the rule set can be tuned
        return new SyncHealthClassification
        {
            Verdict = SyncHealthVerdict.Unknown,
            Evidence = string.Create(ci,
                $"drops={e.Drops} inserts={e.Inserts} underruns={e.Underruns} reanchors={e.Reanchors} maxSyncErr={e.MaxAbsSyncErrorMs:F1}ms minBuffer={e.MinBufferedMs:F0}ms maxChunkGap={e.MaxChunkGapMs:F0}ms rttJitter={e.MaxRttJitterMs:F1}ms dirFlips={e.DirectionFlips} cbGaps={e.CallbackGaps} rateSaturated={e.RateSaturated}"),
        };
    }
}
