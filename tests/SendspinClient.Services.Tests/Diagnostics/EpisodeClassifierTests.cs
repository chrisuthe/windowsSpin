// <copyright file="EpisodeClassifierTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using SendspinClient.Services.Diagnostics;
using Xunit;

namespace SendspinClient.Services.Tests.Diagnostics;

public class EpisodeClassifierTests
{
    /// <summary>A baseline episode that matches NO rule (healthy everything except it exists).</summary>
    private static EpisodeRecord Baseline() => new()
    {
        StartedAt = DateTimeOffset.UnixEpoch,
        DurationSeconds = 5,
        TargetMs = 250,
        MinBufferedMs = 250,
        PreRollMinBufferedMs = 250,
        SampleRate = 48000,
        Channels = 2,
    };

    [Fact]
    public void Underruns_ClassifyAsNetworkStarvation()
    {
        var r = EpisodeClassifier.Classify(Baseline() with { Underruns = 2 });
        Assert.Equal(SyncHealthVerdict.NetworkStarvation, r.Verdict);
    }

    [Fact]
    public void BufferCollapse_WithChunkGap_ClassifiesAsNetworkStarvation()
    {
        var r = EpisodeClassifier.Classify(Baseline() with
        {
            MinBufferedMs = 50,      // < 40% of 250
            MaxChunkGapMs = 1500,    // > 1000
            Drops = 10_000,
        });
        Assert.Equal(SyncHealthVerdict.NetworkStarvation, r.Verdict);
        Assert.Contains("50", r.Evidence); // evidence carries the numbers
    }

    [Fact]
    public void DirectionFlips_WithRttJitter_ClassifyAsClockSyncInstability()
    {
        var r = EpisodeClassifier.Classify(Baseline() with
        {
            DirectionFlips = 3,
            MaxRttJitterMs = 8.0,
            Drops = 500,
            Inserts = 400,
        });
        Assert.Equal(SyncHealthVerdict.ClockSyncInstability, r.Verdict);
    }

    [Fact]
    public void SteadyOneDirectionalDrops_HealthyBufferAndNetwork_ClassifyAsDeviceClockSkew_WithPpm()
    {
        var r = EpisodeClassifier.Classify(Baseline() with
        {
            DurationSeconds = 30,
            Drops = 288_000,
            MinBufferedMs = 200,     // ≥ 60% of target
            MaxChunkGapMs = 100,
            MaxRttJitterMs = 1.0,
        });
        Assert.Equal(SyncHealthVerdict.DeviceClockSkew, r.Verdict);
        Assert.NotNull(r.EstimatedSkewPpm);
        // 288000 samples / 30 s = 9600/s; ÷ (48000 Hz × 2 ch) = 0.1 → 100,000 ppm (synthetic value; the MATH is what we assert)
        Assert.Equal(100_000, r.EstimatedSkewPpm!.Value, precision: 0);
    }

    [Fact]
    public void InsertsProduceNegativePpm()
    {
        var r = EpisodeClassifier.Classify(Baseline() with
        {
            DurationSeconds = 30,
            Inserts = 2_880,         // 96/s ÷ 96000 = 0.001 → 1000 ppm, negative direction
            MinBufferedMs = 200,
            MaxChunkGapMs = 100,
            MaxRttJitterMs = 1.0,
        });
        Assert.Equal(SyncHealthVerdict.DeviceClockSkew, r.Verdict);
        Assert.Equal(-1000, r.EstimatedSkewPpm!.Value, precision: 0);
    }

    [Fact]
    public void ShortSkewEpisode_FallsThroughToUnknown()
    {
        var r = EpisodeClassifier.Classify(Baseline() with
        {
            DurationSeconds = 5,     // < 10 s sustain requirement
            Drops = 1000,
            MinBufferedMs = 200,
            MaxChunkGapMs = 100,
        });
        Assert.Equal(SyncHealthVerdict.Unknown, r.Verdict);
    }

    [Fact]
    public void CallbackGaps_HealthyBufferAndNetwork_ClassifyAsLocalTiming()
    {
        var r = EpisodeClassifier.Classify(Baseline() with
        {
            CallbackGaps = 4,
            MaxCallbackGapMs = 180,
            MinBufferedMs = 240,
            MaxChunkGapMs = 50,
        });
        Assert.Equal(SyncHealthVerdict.LocalTiming, r.Verdict);
    }

    [Fact]
    public void RuleOrder_NetworkWinsOverLocalTiming()
    {
        // Underruns + callback gaps: network rule fires first.
        var r = EpisodeClassifier.Classify(Baseline() with { Underruns = 1, CallbackGaps = 5 });
        Assert.Equal(SyncHealthVerdict.NetworkStarvation, r.Verdict);
    }

    [Fact]
    public void NothingMatches_IsUnknown_WithEvidence()
    {
        var r = EpisodeClassifier.Classify(Baseline() with { MaxAbsSyncErrorMs = 3.2 });
        Assert.Equal(SyncHealthVerdict.Unknown, r.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(r.Evidence));
    }
}
