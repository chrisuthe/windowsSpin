// <copyright file="EpisodeDetectorTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using SendspinClient.Services.Diagnostics;
using Xunit;

namespace SendspinClient.Services.Tests.Diagnostics;

public class EpisodeDetectorTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    private static EpisodeDetector NewDetector() => new(
        deadbandMs: 2.0, maxSpeedCorrection: 0.02, clock: () => FixedNow);

    /// <summary>Builds a healthy baseline sample at the given time with overridable fields.</summary>
    private static SyncHealthSample Sample(
        long tMs,
        double syncErrMs = 0.0,
        long drops = 0,
        long inserts = 0,
        long underruns = 0,
        long reanchors = 0,
        long cbGaps = 0,
        double rate = 1.0,
        double bufferedMs = 250,
        long totalWritten = 1_000_000) => new()
    {
        TimestampMs = tMs,
        SmoothedSyncErrorMs = syncErrMs,
        SamplesDroppedForSync = drops,
        SamplesInsertedForSync = inserts,
        UnderrunCount = underruns,
        ReanchorCount = reanchors,
        CallbackGapCount = cbGaps,
        TargetPlaybackRate = rate,
        BufferedMs = bufferedMs,
        TargetMs = 250,
        TotalSamplesWritten = totalWritten,
        SampleRate = 48000,
        Channels = 2,
    };

    [Fact]
    public void NoTriggers_NoEpisode()
    {
        var d = NewDetector();
        for (long t = 0; t < 10_000; t += 100)
        {
            Assert.Null(d.Observe(Sample(t)));
        }
    }

    // One test per audibility trigger from the spec.
    [Theory]
    [InlineData("drops")]
    [InlineData("inserts")]
    [InlineData("underruns")]
    [InlineData("reanchors")]
    [InlineData("cbGaps")]
    [InlineData("syncErr")]
    [InlineData("rateSaturated")]
    public void EachTrigger_OpensEpisode_AndClosesAfterQuietPeriod(string trigger)
    {
        var d = NewDetector();
        d.Observe(Sample(0));

        // Trigger tick at t=100
        var s = trigger switch
        {
            "drops" => Sample(100, drops: 480),
            "inserts" => Sample(100, inserts: 480),
            "underruns" => Sample(100, underruns: 1),
            "reanchors" => Sample(100, reanchors: 1),
            "cbGaps" => Sample(100, cbGaps: 1),
            "syncErr" => Sample(100, syncErrMs: 2.5),       // > 2.0 deadband
            "rateSaturated" => Sample(100, rate: 1.019),    // ≥ 0.9 * 0.02 away from 1.0
            _ => throw new InvalidOperationException(),
        };
        Assert.Null(d.Observe(s)); // opens, not yet closed

        // Quiet samples after the trigger; carry counters forward so no new deltas fire
        var quiet = trigger switch
        {
            "drops" => Sample(0, drops: 480),
            "inserts" => Sample(0, inserts: 480),
            "underruns" => Sample(0, underruns: 1),
            "reanchors" => Sample(0, reanchors: 1),
            "cbGaps" => Sample(0, cbGaps: 1),
            _ => Sample(0),
        };

        EpisodeRecord? closed = null;
        for (long t = 200; t <= 3_400 && closed is null; t += 100)
        {
            closed = d.Observe(quiet with { TimestampMs = t });
        }

        Assert.NotNull(closed);
        Assert.Equal(FixedNow, closed!.StartedAt);
    }

    [Fact]
    public void EpisodeAggregates_AreCorrect()
    {
        var d = NewDetector();
        d.Observe(Sample(0));
        d.Observe(Sample(100, drops: 1000, bufferedMs: 100, syncErrMs: 15));
        d.Observe(Sample(200, drops: 2500, bufferedMs: 40, syncErrMs: -25));
        d.Observe(Sample(300, drops: 2500, inserts: 200, bufferedMs: 80)); // direction flip

        EpisodeRecord? closed = null;
        for (long t = 400; t <= 3_600 && closed is null; t += 100)
        {
            closed = d.Observe(Sample(t, drops: 2500, inserts: 200));
        }

        Assert.NotNull(closed);
        Assert.Equal(2500, closed!.Drops);
        Assert.Equal(200, closed.Inserts);
        Assert.Equal(40, closed.MinBufferedMs);
        Assert.Equal(25, closed.MaxAbsSyncErrorMs);
        Assert.Equal(1, closed.DirectionFlips);
    }

    [Fact]
    public void PreRoll_CapturesBufferDipBeforeTrigger()
    {
        var d = NewDetector();
        d.Observe(Sample(0, bufferedMs: 250));
        d.Observe(Sample(100, bufferedMs: 30));   // dip BEFORE any trigger
        d.Observe(Sample(200, bufferedMs: 200));
        d.Observe(Sample(300, drops: 500));       // trigger

        EpisodeRecord? closed = null;
        for (long t = 400; t <= 3_600 && closed is null; t += 100)
        {
            closed = d.Observe(Sample(t, drops: 500));
        }

        Assert.NotNull(closed);
        Assert.Equal(30, closed!.PreRollMinBufferedMs);
    }

    [Fact]
    public void HardCap_ClosesAt120Seconds_EvenWhileTriggering()
    {
        var d = NewDetector();
        d.Observe(Sample(0));
        EpisodeRecord? closed = null;
        long drops = 0;
        // Continuous dropping: every tick is a trigger
        for (long t = 100; t <= 130_000 && closed is null; t += 100)
        {
            drops += 10;
            closed = d.Observe(Sample(t, drops: drops));
        }

        Assert.NotNull(closed);
        Assert.True(closed!.DurationSeconds >= 119 && closed.DurationSeconds <= 121,
            $"expected ~120s, got {closed.DurationSeconds}");
    }

    [Fact]
    public void CounterReset_ResetsDetector_WithoutEmittingEpisode()
    {
        var d = NewDetector();
        d.Observe(Sample(0, drops: 5000, totalWritten: 2_000_000));
        // Pipeline restart: counters all go backwards
        var result = d.Observe(Sample(100, drops: 0, totalWritten: 10_000));
        Assert.Null(result);
        // And the backwards jump did not open an episode: quiet ticks produce nothing
        for (long t = 200; t <= 4_000; t += 100)
        {
            Assert.Null(d.Observe(Sample(t, totalWritten: 10_000 + t)));
        }
    }

    [Fact]
    public void SecondEpisode_OpensAfterFirstCloses()
    {
        var d = NewDetector();
        d.Observe(Sample(0));
        d.Observe(Sample(100, drops: 500)); // first trigger

        // Quiet until first episode closes
        EpisodeRecord? first = null;
        long t = 200;
        for (; t <= 4_000 && first is null; t += 100)
        {
            first = d.Observe(Sample(t, drops: 500));
        }

        Assert.NotNull(first);

        // Immediately trigger again on the next tick
        Assert.Null(d.Observe(Sample(t, drops: 900))); // opens second episode

        EpisodeRecord? second = null;
        long end = t + 4_000;
        for (t += 100; t <= end && second is null; t += 100)
        {
            second = d.Observe(Sample(t, drops: 900));
        }

        Assert.NotNull(second);
        Assert.Equal(400, second!.Drops); // 900 - 500 baseline
    }

    [Theory]
    [InlineData(0.0, 0.02)]
    [InlineData(-1.0, 0.02)]
    [InlineData(2.0, 0.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(2.0, 1.5)]
    public void InvalidConstructorArgs_Throw(double deadband, double maxCorrection)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EpisodeDetector(deadband, maxCorrection));
    }
}
