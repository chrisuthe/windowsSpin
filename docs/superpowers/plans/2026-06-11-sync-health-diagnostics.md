# Sync Health Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect, classify, and log "episodes" of audio sync trouble so issue #33-style stutters arrive with an attached root-cause diagnosis (`sync-health.log`) instead of raw counters.

**Architecture:** A singleton `SyncHealthMonitor` samples `IAudioPipeline.BufferStats` + `IClockSynchronizer.GetStatus()` at 10 Hz into an episode-detecting state machine. Closed episodes pass through a pure rule-based classifier (network starvation / clock-sync instability / device clock skew / local timing / unknown) and are appended to an always-on, 1 MB-capped `sync-health.log`. The Stats window shows the latest verdict. Spec: `docs/superpowers/specs/2026-06-11-sync-health-diagnostics-design.md`.

**Tech Stack:** .NET 10, WPF, Sendspin.SDK 9.0.2 (diagnostic signals), xUnit 2.9.2 (existing `SendspinClient.Services.Tests` project, builds against stable NuGet SDK).

**Conventions reminders:** private fields `_camelCase`; structured logging; XML docs on public APIs; every file starts with the standard 3-line copyright header (copy from any existing file in the project). Run all commands from the repo root `c:\CodeProjects\windowsSpin`.

---

### Task 1: SDK bump to 9.0.2

**Files:**
- Modify: `src/SendspinClient/SendspinClient.csproj` (line ~66)
- Modify: `src/SendspinClient.Services/SendspinClient.Services.csproj` (line ~26)

- [ ] **Step 1: Bump both PackageReference entries**

In both files change:
```xml
<PackageReference Include="Sendspin.SDK" Version="9.0.1" />
```
to:
```xml
<PackageReference Include="Sendspin.SDK" Version="9.0.2" />
```

- [ ] **Step 2: Restore and build**

Run: `dotnet build SendspinClient.sln`
Expected: `0 Error(s)`. If restore fails with NU1102 (version not found), STOP — 9.0.2 isn't on NuGet yet; tell the user.

- [ ] **Step 3: Verify the new 9.0.2 signals exist**

Grep the package's XML doc file for the expected signal names:

Run: `Get-ChildItem "$env:USERPROFILE\.nuget\packages\sendspin.sdk\9.0.2\lib" -Recurse -Filter *.xml | Select-Object -First 1 | Get-Content | Select-String "ChunkJitterMs|RttJitterMicroseconds|ReanchorCount" | Select-Object -First 5`
Expected: matches for all three names. If names differ from the spec (e.g. `ChunkJitterMilliseconds`), note the actual names — Task 7's sampler mapping must use them.

- [ ] **Step 4: Commit**

```bash
git add src/SendspinClient/SendspinClient.csproj src/SendspinClient.Services/SendspinClient.Services.csproj
git commit -m "chore: bump Sendspin.SDK to 9.0.2 for diagnostic signals"
```

---

### Task 2: SyncHealthSample + EpisodeRecord data types

Plain data, no logic — no tests needed beyond compilation.

**Files:**
- Create: `src/SendspinClient.Services/Diagnostics/SyncHealthSample.cs`
- Create: `src/SendspinClient.Services/Diagnostics/EpisodeRecord.cs`

- [ ] **Step 1: Create SyncHealthSample.cs**

```csharp
namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// One 10 Hz snapshot of every signal the sync-health monitor watches.
/// Built by <see cref="SyncHealthMonitor"/> from SDK stats; consumed by <see cref="EpisodeDetector"/>.
/// </summary>
public readonly record struct SyncHealthSample
{
    /// <summary>Gets the monotonic capture time in milliseconds (Environment.TickCount64).</summary>
    public long TimestampMs { get; init; }

    // --- Buffer / correction (AudioBufferStats) ---
    public double SmoothedSyncErrorMs { get; init; }
    public double BufferedMs { get; init; }
    public double TargetMs { get; init; }
    public long UnderrunCount { get; init; }
    public long SamplesDroppedForSync { get; init; }
    public long SamplesInsertedForSync { get; init; }
    public long ReanchorCount { get; init; }
    public double TargetPlaybackRate { get; init; }
    public long TotalSamplesWritten { get; init; }

    // --- Network ingest (AudioBufferStats, 9.0.2) ---
    public double LastChunkAgeMs { get; init; }
    public double MaxChunkGapMs { get; init; }
    public double ChunkJitterMs { get; init; }
    public long BytesReceived { get; init; }

    // --- Clock sync (ClockSyncStatus) ---
    public double OffsetMs { get; init; }
    public double RttJitterMs { get; init; }
    public int AdaptiveForgettingTriggerCount { get; init; }

    // --- Local audio thread (ReadCallbackGapTracker) ---
    public long CallbackGapCount { get; init; }
    public double MaxCallbackGapMs { get; init; }

    // --- Format context ---
    public int SampleRate { get; init; }
    public int Channels { get; init; }
}
```

- [ ] **Step 2: Create EpisodeRecord.cs**

```csharp
namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// Aggregated facts about one closed sync-trouble episode, ready for classification and logging.
/// </summary>
public sealed record EpisodeRecord
{
    public DateTimeOffset StartedAt { get; init; }
    public double DurationSeconds { get; init; }

    // Counter deltas accumulated over the episode
    public long Drops { get; init; }
    public long Inserts { get; init; }
    public long Underruns { get; init; }
    public long Reanchors { get; init; }
    public long CallbackGaps { get; init; }
    public int AdaptiveForgettingTriggers { get; init; }

    // Extremes observed during the episode
    public double MaxAbsSyncErrorMs { get; init; }
    public double MinBufferedMs { get; init; }
    public double TargetMs { get; init; }
    public double MaxChunkGapMs { get; init; }
    public double MaxChunkAgeMs { get; init; }
    public double MaxRttJitterMs { get; init; }
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
    public double PreRollMinBufferedMs { get; init; }
    public double PreRollMaxChunkGapMs { get; init; }

    // Format context for skew ppm math
    public int SampleRate { get; init; }
    public int Channels { get; init; }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/SendspinClient.Services/SendspinClient.Services.csproj`
Expected: `0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add src/SendspinClient.Services/Diagnostics/
git commit -m "feat: sync health sample and episode record types"
```

---

### Task 3: EpisodeDetector (TDD)

State machine: Quiet → Active on any audibility trigger; closes after 3 quiet seconds or 120 s hard cap; emits `EpisodeRecord`. Owns a 100-entry pre-roll ring.

**Files:**
- Create: `src/SendspinClient.Services/Diagnostics/EpisodeDetector.cs`
- Test: `tests/SendspinClient.Services.Tests/Diagnostics/EpisodeDetectorTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SendspinClient.Services.Tests/Diagnostics/EpisodeDetectorTests.cs
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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj --filter "FullyQualifiedName~EpisodeDetectorTests"`
Expected: compile error — `EpisodeDetector` does not exist.

- [ ] **Step 3: Implement EpisodeDetector**

```csharp
// src/SendspinClient.Services/Diagnostics/EpisodeDetector.cs
namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// State machine that turns a stream of <see cref="SyncHealthSample"/> into closed
/// <see cref="EpisodeRecord"/>s. An episode opens on any audibility trigger and closes
/// after <see cref="QuietCloseSeconds"/> with no triggers, or at the <see cref="HardCapSeconds"/> cap.
/// </summary>
/// <remarks>
/// Single-threaded by contract: <see cref="Observe"/> is only called from the monitor's timer tick.
/// Triggers are audibility-anchored (see design spec): correction activity, deadband crossings,
/// callback gaps, and saturated correction rate.
/// </remarks>
public sealed class EpisodeDetector
{
    internal const double QuietCloseSeconds = 3.0;
    internal const double HardCapSeconds = 120.0;
    private const int PreRollCapacity = 100; // 10 s at 10 Hz

    private readonly double _deadbandMs;
    private readonly double _maxSpeedCorrection;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SyncHealthSample[] _preRoll = new SyncHealthSample[PreRollCapacity];
    private int _preRollCount;
    private int _preRollHead;

    private bool _hasPrev;
    private SyncHealthSample _prev;

    private bool _active;
    private Accumulator _acc;
    private long _lastTriggerMs;

    public EpisodeDetector(double deadbandMs, double maxSpeedCorrection, Func<DateTimeOffset>? clock = null)
    {
        _deadbandMs = deadbandMs;
        _maxSpeedCorrection = maxSpeedCorrection;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// Feeds one sample; returns a closed episode record, or null.
    /// </summary>
    public EpisodeRecord? Observe(in SyncHealthSample s)
    {
        // Pipeline restart: monotonic counters went backwards. Reset everything, emit nothing.
        if (_hasPrev && s.TotalSamplesWritten < _prev.TotalSamplesWritten)
        {
            Reset();
        }

        if (!_hasPrev)
        {
            _hasPrev = true;
            _prev = s;
            PushPreRoll(s);
            return null;
        }

        var triggered = IsTrigger(in s, in _prev);
        EpisodeRecord? closed = null;

        if (!_active && triggered)
        {
            _active = true;
            _acc = Accumulator.Open(in _prev, in s, _clock(), ComputePreRollMins());
            _lastTriggerMs = s.TimestampMs;
        }
        else if (_active)
        {
            _acc.Accumulate(in s, in _prev, _deadbandMs, _maxSpeedCorrection);
            if (triggered)
            {
                _lastTriggerMs = s.TimestampMs;
            }

            var quietSeconds = (s.TimestampMs - _lastTriggerMs) / 1000.0;
            var durationSeconds = (s.TimestampMs - _acc.StartTimestampMs) / 1000.0;
            if (quietSeconds >= QuietCloseSeconds || durationSeconds >= HardCapSeconds)
            {
                closed = _acc.Build(durationSeconds);
                _active = false;
            }
        }

        PushPreRoll(s);
        _prev = s;
        return closed;
    }

    private bool IsTrigger(in SyncHealthSample s, in SyncHealthSample prev) =>
        s.SamplesDroppedForSync > prev.SamplesDroppedForSync
        || s.SamplesInsertedForSync > prev.SamplesInsertedForSync
        || s.UnderrunCount > prev.UnderrunCount
        || s.ReanchorCount > prev.ReanchorCount
        || s.CallbackGapCount > prev.CallbackGapCount
        || Math.Abs(s.SmoothedSyncErrorMs) > _deadbandMs
        || Math.Abs(s.TargetPlaybackRate - 1.0) >= 0.9 * _maxSpeedCorrection;

    private void Reset()
    {
        _hasPrev = false;
        _active = false;
        _preRollCount = 0;
        _preRollHead = 0;
    }

    private void PushPreRoll(in SyncHealthSample s)
    {
        _preRoll[_preRollHead] = s;
        _preRollHead = (_preRollHead + 1) % PreRollCapacity;
        if (_preRollCount < PreRollCapacity)
        {
            _preRollCount++;
        }
    }

    private (double MinBufferedMs, double MaxChunkGapMs) ComputePreRollMins()
    {
        var minBuffered = double.MaxValue;
        var maxGap = 0.0;
        for (var i = 0; i < _preRollCount; i++)
        {
            ref readonly var s = ref _preRoll[i];
            if (s.BufferedMs < minBuffered) minBuffered = s.BufferedMs;
            if (s.MaxChunkGapMs > maxGap) maxGap = s.MaxChunkGapMs;
        }

        return (minBuffered == double.MaxValue ? 0 : minBuffered, maxGap);
    }

    /// <summary>Mutable episode-in-progress aggregation.</summary>
    private struct Accumulator
    {
        public long StartTimestampMs;
        private DateTimeOffset _startedAt;
        private SyncHealthSample _baseline;     // prev sample at open: counter baseline
        private double _preRollMinBufferedMs;
        private double _preRollMaxChunkGapMs;
        private double _maxAbsSyncErrorMs;
        private double _minBufferedMs;
        private double _targetMs;
        private double _maxChunkGapMs;
        private double _maxChunkAgeMs;
        private double _maxRttJitterMs;
        private double _maxCallbackGapMs;
        private double _offsetTravelMs;
        private double _lastOffsetMs;
        private int _directionFlips;
        private int _lastDirection;             // +1 dropping, -1 inserting, 0 none yet
        private bool _rateSaturated;
        private long _endDrops, _endInserts, _endUnderruns, _endReanchors, _endCallbackGaps;
        private int _endForgetting;
        private long _bytesAtOpen, _bytesAtEnd;
        private int _sampleRate, _channels;

        public static Accumulator Open(
            in SyncHealthSample baseline,
            in SyncHealthSample first,
            DateTimeOffset startedAt,
            (double MinBufferedMs, double MaxChunkGapMs) preRoll)
        {
            var acc = new Accumulator
            {
                StartTimestampMs = first.TimestampMs,
                _startedAt = startedAt,
                _baseline = baseline,
                _preRollMinBufferedMs = preRoll.MinBufferedMs,
                _preRollMaxChunkGapMs = preRoll.MaxChunkGapMs,
                _minBufferedMs = double.MaxValue,
                _lastOffsetMs = baseline.OffsetMs,
                _bytesAtOpen = baseline.BytesReceived,
                _sampleRate = first.SampleRate,
                _channels = first.Channels,
            };
            acc.Accumulate(in first, in baseline, deadbandMs: 0, maxSpeedCorrection: 1);
            return acc;
        }

        public void Accumulate(in SyncHealthSample s, in SyncHealthSample prev, double deadbandMs, double maxSpeedCorrection)
        {
            _maxAbsSyncErrorMs = Math.Max(_maxAbsSyncErrorMs, Math.Abs(s.SmoothedSyncErrorMs));
            _minBufferedMs = Math.Min(_minBufferedMs, s.BufferedMs);
            _targetMs = s.TargetMs;
            _maxChunkGapMs = Math.Max(_maxChunkGapMs, s.MaxChunkGapMs);
            _maxChunkAgeMs = Math.Max(_maxChunkAgeMs, s.LastChunkAgeMs);
            _maxRttJitterMs = Math.Max(_maxRttJitterMs, s.RttJitterMs);
            _maxCallbackGapMs = Math.Max(_maxCallbackGapMs, s.MaxCallbackGapMs);
            _offsetTravelMs += Math.Abs(s.OffsetMs - _lastOffsetMs);
            _lastOffsetMs = s.OffsetMs;
            if (Math.Abs(s.TargetPlaybackRate - 1.0) >= 0.9 * maxSpeedCorrection && maxSpeedCorrection < 1)
            {
                _rateSaturated = true;
            }

            var dropped = s.SamplesDroppedForSync > prev.SamplesDroppedForSync;
            var inserted = s.SamplesInsertedForSync > prev.SamplesInsertedForSync;
            var direction = dropped ? 1 : inserted ? -1 : 0;
            if (direction != 0)
            {
                if (_lastDirection != 0 && direction != _lastDirection)
                {
                    _directionFlips++;
                }

                _lastDirection = direction;
            }

            _endDrops = s.SamplesDroppedForSync;
            _endInserts = s.SamplesInsertedForSync;
            _endUnderruns = s.UnderrunCount;
            _endReanchors = s.ReanchorCount;
            _endCallbackGaps = s.CallbackGapCount;
            _endForgetting = s.AdaptiveForgettingTriggerCount;
            _bytesAtEnd = s.BytesReceived;
        }

        public readonly EpisodeRecord Build(double durationSeconds) => new()
        {
            StartedAt = _startedAt,
            DurationSeconds = durationSeconds,
            Drops = _endDrops - _baseline.SamplesDroppedForSync,
            Inserts = _endInserts - _baseline.SamplesInsertedForSync,
            Underruns = _endUnderruns - _baseline.UnderrunCount,
            Reanchors = _endReanchors - _baseline.ReanchorCount,
            CallbackGaps = _endCallbackGaps - _baseline.CallbackGapCount,
            AdaptiveForgettingTriggers = _endForgetting - _baseline.AdaptiveForgettingTriggerCount,
            MaxAbsSyncErrorMs = _maxAbsSyncErrorMs,
            MinBufferedMs = _minBufferedMs == double.MaxValue ? 0 : _minBufferedMs,
            TargetMs = _targetMs,
            MaxChunkGapMs = _maxChunkGapMs,
            MaxChunkAgeMs = _maxChunkAgeMs,
            MaxRttJitterMs = _maxRttJitterMs,
            MaxCallbackGapMs = _maxCallbackGapMs,
            OffsetTravelMs = _offsetTravelMs,
            DirectionFlips = _directionFlips,
            RateSaturated = _rateSaturated,
            BytesPerSecond = durationSeconds > 0 ? (_bytesAtEnd - _bytesAtOpen) / durationSeconds : 0,
            PreRollMinBufferedMs = _preRollMinBufferedMs,
            PreRollMaxChunkGapMs = _preRollMaxChunkGapMs,
            SampleRate = _sampleRate,
            Channels = _channels,
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj --filter "FullyQualifiedName~EpisodeDetectorTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SendspinClient.Services/Diagnostics/EpisodeDetector.cs tests/SendspinClient.Services.Tests/Diagnostics/EpisodeDetectorTests.cs
git commit -m "feat: episode detector state machine for sync health"
```

---

### Task 4: EpisodeClassifier (TDD)

Pure ordered rules; first match wins; classification only labels (every episode is logged regardless).

**Files:**
- Create: `src/SendspinClient.Services/Diagnostics/EpisodeClassifier.cs`
- Test: `tests/SendspinClient.Services.Tests/Diagnostics/EpisodeClassifierTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SendspinClient.Services.Tests/Diagnostics/EpisodeClassifierTests.cs
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
            Drops = 288_000,         // 9600 samples/s ÷ (48000*2) = 100 ppm... see formula below
            MinBufferedMs = 200,     // ≥ 60% of target
            MaxChunkGapMs = 100,
            MaxRttJitterMs = 1.0,
        });
        Assert.Equal(SyncHealthVerdict.DeviceClockSkew, r.Verdict);
        Assert.NotNull(r.EstimatedSkewPpm);
        // 288000 samples / 30 s = 9600/s; ÷ (48000 Hz * 2 ch) = 0.1 = 100000 ppm? No:
        // 9600 / 96000 = 0.1 → 100_000 ppm is implausible audio but the MATH is what we assert:
        Assert.Equal(100_000, r.EstimatedSkewPpm!.Value, precision: 0);
    }

    [Fact]
    public void InsertsProduceNegativePpm()
    {
        var r = EpisodeClassifier.Classify(Baseline() with
        {
            DurationSeconds = 30,
            Inserts = 2_880,         // 96/s ÷ 96000 = 1000 ppm, negative direction
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj --filter "FullyQualifiedName~EpisodeClassifierTests"`
Expected: compile error — `EpisodeClassifier` does not exist.

- [ ] **Step 3: Implement EpisodeClassifier**

```csharp
// src/SendspinClient.Services/Diagnostics/EpisodeClassifier.cs
using System.Globalization;

namespace SendspinClient.Services.Diagnostics;

/// <summary>The root-cause label assigned to a sync-trouble episode.</summary>
public enum SyncHealthVerdict
{
    NetworkStarvation,
    ClockSyncInstability,
    DeviceClockSkew,
    LocalTiming,
    Unknown,
}

/// <summary>Classification output: verdict plus the evidence values that fired the rule.</summary>
public sealed record SyncHealthClassification
{
    required public SyncHealthVerdict Verdict { get; init; }
    required public string Evidence { get; init; }
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
    internal const double NetworkChunkGapMs = 1000;
    internal const double HealthyBufferFraction = 0.6;     // buffer at/above 60% of target
    internal const double HealthyChunkGapMs = 500;
    internal const double RttJitterThresholdMs = 5.0;
    internal const double OffsetTravelThresholdMs = 5.0;
    internal const int MinDirectionFlips = 2;
    internal const double SkewMinDurationSeconds = 10.0;
    internal const double LocalMaxCallbackGapMs = 100;

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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj --filter "FullyQualifiedName~EpisodeClassifierTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SendspinClient.Services/Diagnostics/EpisodeClassifier.cs tests/SendspinClient.Services.Tests/Diagnostics/EpisodeClassifierTests.cs
git commit -m "feat: deterministic episode classifier with evidence"
```

---

### Task 5: SyncHealthLog (TDD)

Always-on append writer with 1 MB cap and single rotation. Directory is injected (the WPF app passes `AppPaths.LogDirectory`; Services must not reference the WPF project).

**Files:**
- Create: `src/SendspinClient.Services/Diagnostics/SyncHealthLog.cs`
- Test: `tests/SendspinClient.Services.Tests/Diagnostics/SyncHealthLogTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SendspinClient.Services.Tests/Diagnostics/SyncHealthLogTests.cs
using SendspinClient.Services.Diagnostics;
using Xunit;

namespace SendspinClient.Services.Tests.Diagnostics;

public sealed class SyncHealthLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sendspin-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static EpisodeRecord SampleEpisode() => new()
    {
        StartedAt = new DateTimeOffset(2026, 6, 11, 21, 14, 32, TimeSpan.Zero),
        DurationSeconds = 8.2,
        Drops = 18234,
        Underruns = 3,
        MaxAbsSyncErrorMs = 142.1,
        MinBufferedMs = 12,
        TargetMs = 250,
        MaxChunkGapMs = 2310,
        MaxChunkAgeMs = 2280,
        PreRollMinBufferedMs = 12,
        SampleRate = 48000,
        Channels = 2,
    };

    [Fact]
    public void WriteSessionHeader_CreatesFileWithHeader()
    {
        var log = new SyncHealthLog(_dir);
        log.WriteSessionHeader("app=3.4.0 sdk=9.0.2 device=Speakers format=48000Hz/2ch target=250ms");

        var content = File.ReadAllText(Path.Combine(_dir, "sync-health.log"));
        Assert.Contains("SESSION", content);
        Assert.Contains("sdk=9.0.2", content);
    }

    [Fact]
    public void WriteEpisode_ContainsVerdictAndKeyValues()
    {
        var log = new SyncHealthLog(_dir);
        var classification = EpisodeClassifier.Classify(SampleEpisode());
        log.WriteEpisode(SampleEpisode(), classification);

        var content = File.ReadAllText(Path.Combine(_dir, "sync-health.log"));
        Assert.Contains("EPISODE verdict=network-starvation", content);
        Assert.Contains("duration=8.2s", content);
        Assert.Contains("drops=18234", content);
    }

    [Fact]
    public void ExceedingCap_RotatesToBackupFile()
    {
        var log = new SyncHealthLog(_dir, maxBytes: 2048); // tiny cap for test
        var classification = EpisodeClassifier.Classify(SampleEpisode());
        for (var i = 0; i < 50; i++)
        {
            log.WriteEpisode(SampleEpisode(), classification);
        }

        Assert.True(File.Exists(Path.Combine(_dir, "sync-health.1.log")), "backup should exist");
        var mainSize = new FileInfo(Path.Combine(_dir, "sync-health.log")).Length;
        Assert.True(mainSize <= 2048 + 1024, $"main log should stay near cap, was {mainSize}");
    }

    [Fact]
    public void WriteFailure_DoesNotThrow()
    {
        // Point at an un-creatable path (file used as directory)
        var filePath = Path.Combine(Path.GetTempPath(), "sendspin-tests-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(filePath, "block");
        var log = new SyncHealthLog(Path.Combine(filePath, "nested"));
        var ex = Record.Exception(() => log.WriteSessionHeader("header"));
        Assert.Null(ex);
        File.Delete(filePath);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj --filter "FullyQualifiedName~SyncHealthLogTests"`
Expected: compile error — `SyncHealthLog` does not exist.

- [ ] **Step 3: Implement SyncHealthLog**

```csharp
// src/SendspinClient.Services/Diagnostics/SyncHealthLog.cs
using System.Globalization;
using System.Text;

namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// Always-on, size-capped writer for sync-health episode records.
/// Independent of the normal logging pipeline so episode evidence survives default config
/// (EnableFileLogging=false). Never throws: diagnostics must not break playback.
/// </summary>
/// <remarks>
/// Writes happen on the monitor's timer thread (background) — never the audio or UI thread.
/// Cap is checked before each write; on overflow the file rotates to sync-health.1.log
/// (one backup kept, ≤ 2× cap on disk).
/// </remarks>
public sealed class SyncHealthLog
{
    private const string FileName = "sync-health.log";
    private const string BackupFileName = "sync-health.1.log";
    private const int DefaultMaxBytes = 1024 * 1024; // 1 MB

    private readonly string _directory;
    private readonly int _maxBytes;
    private readonly object _writeLock = new();

    public SyncHealthLog(string directory, int maxBytes = DefaultMaxBytes)
    {
        _directory = directory;
        _maxBytes = maxBytes;
    }

    /// <summary>Writes the session context line (app/SDK versions, device, format, buffer config).</summary>
    public void WriteSessionHeader(string context)
    {
        Append($"[{Timestamp()}] SESSION {context}");
    }

    /// <summary>Writes one classified episode block.</summary>
    public void WriteEpisode(EpisodeRecord e, SyncHealthClassification c)
    {
        var ci = CultureInfo.InvariantCulture;
        var verdict = c.Verdict switch
        {
            SyncHealthVerdict.NetworkStarvation => "network-starvation",
            SyncHealthVerdict.ClockSyncInstability => "clock-sync-instability",
            SyncHealthVerdict.DeviceClockSkew => "device-clock-skew",
            SyncHealthVerdict.LocalTiming => "local-timing",
            _ => "unknown",
        };

        var sb = new StringBuilder();
        sb.AppendLine(string.Create(ci,
            $"[{e.StartedAt:yyyy-MM-dd HH:mm:ss}] EPISODE verdict={verdict} duration={e.DurationSeconds:F1}s"));
        sb.AppendLine(string.Create(ci,
            $"  drops={e.Drops} inserts={e.Inserts} underruns={e.Underruns} reanchors={e.Reanchors} maxSyncErr={e.MaxAbsSyncErrorMs:+0.0;-0.0}ms"));
        sb.AppendLine(string.Create(ci,
            $"  minBuffer={e.MinBufferedMs:F0}ms/{e.TargetMs:F0}ms preRollMinBuffer={e.PreRollMinBufferedMs:F0}ms maxChunkGap={e.MaxChunkGapMs:F0}ms chunkAge={e.MaxChunkAgeMs:F0}ms bytesPerSec={e.BytesPerSecond:F0}"));
        sb.AppendLine(string.Create(ci,
            $"  rttJitter={e.MaxRttJitterMs:F1}ms offsetTravel={e.OffsetTravelMs:F1}ms dirFlips={e.DirectionFlips} cbGaps={e.CallbackGaps} rateSaturated={e.RateSaturated}"));
        sb.Append(string.Create(ci, $"  evidence=\"{c.Evidence}\""));
        if (c.EstimatedSkewPpm is { } ppm)
        {
            sb.Append(string.Create(ci, $" estSkewPpm={ppm:F0}"));
        }

        Append(sb.ToString());
    }

    private static string Timestamp() =>
        DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private void Append(string block)
    {
        lock (_writeLock)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, FileName);

                var info = new FileInfo(path);
                if (info.Exists && info.Length >= _maxBytes)
                {
                    var backup = Path.Combine(_directory, BackupFileName);
                    File.Delete(backup);            // no-op if missing
                    File.Move(path, backup);
                }

                File.AppendAllText(path, block + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never break playback; swallow and continue in-memory only.
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj --filter "FullyQualifiedName~SyncHealthLogTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SendspinClient.Services/Diagnostics/SyncHealthLog.cs tests/SendspinClient.Services.Tests/Diagnostics/SyncHealthLogTests.cs
git commit -m "feat: always-on size-capped sync-health log writer"
```

---

### Task 6: ReadCallbackGapTracker + BufferedAudioSampleSource hook (TDD)

`BufferedAudioSampleSource` is created per pipeline start by a factory in App.xaml.cs, so gap state lives in a DI-singleton tracker passed into it. Detects audio-thread starvation: gap between `Read` calls > max(100 ms, 2× expected callback interval).

**Files:**
- Create: `src/SendspinClient.Services/Diagnostics/ReadCallbackGapTracker.cs`
- Modify: `src/SendspinClient.Services/Audio/BufferedAudioSampleSource.cs`
- Test: `tests/SendspinClient.Services.Tests/Diagnostics/ReadCallbackGapTrackerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SendspinClient.Services.Tests/Diagnostics/ReadCallbackGapTrackerTests.cs
using SendspinClient.Services.Diagnostics;
using Xunit;

namespace SendspinClient.Services.Tests.Diagnostics;

public class ReadCallbackGapTrackerTests
{
    [Fact]
    public void RegularCallbacks_NoGaps()
    {
        var tracker = new ReadCallbackGapTracker();
        // 10 ms expected interval, callbacks arriving every 10 ms
        for (long t = 0; t < 1000; t += 10)
        {
            tracker.RecordRead(nowMs: t, expectedIntervalMs: 10);
        }

        Assert.Equal(0, tracker.GapCount);
    }

    [Fact]
    public void LateCallback_CountsGap_AndTracksMax()
    {
        var tracker = new ReadCallbackGapTracker();
        tracker.RecordRead(nowMs: 0, expectedIntervalMs: 10);
        tracker.RecordRead(nowMs: 10, expectedIntervalMs: 10);
        tracker.RecordRead(nowMs: 250, expectedIntervalMs: 10); // 240 ms gap

        Assert.Equal(1, tracker.GapCount);
        Assert.Equal(240, tracker.MaxGapMs);
    }

    [Fact]
    public void GapBelowFloor_Ignored()
    {
        var tracker = new ReadCallbackGapTracker();
        tracker.RecordRead(nowMs: 0, expectedIntervalMs: 10);
        tracker.RecordRead(nowMs: 80, expectedIntervalMs: 10); // 80 ms: > 2×expected but < 100 ms floor

        Assert.Equal(0, tracker.GapCount);
    }

    [Fact]
    public void FirstCallbackAfterReset_NotAGap()
    {
        var tracker = new ReadCallbackGapTracker();
        tracker.RecordRead(nowMs: 0, expectedIntervalMs: 10);
        tracker.Reset();
        tracker.RecordRead(nowMs: 5000, expectedIntervalMs: 10); // long pause spans the reset

        Assert.Equal(0, tracker.GapCount);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj --filter "FullyQualifiedName~ReadCallbackGapTrackerTests"`
Expected: compile error — `ReadCallbackGapTracker` does not exist.

- [ ] **Step 3: Implement ReadCallbackGapTracker**

```csharp
// src/SendspinClient.Services/Diagnostics/ReadCallbackGapTracker.cs
namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// Detects audio-thread starvation by timing gaps between sample-source read callbacks.
/// Written from the NAudio audio thread; read from the sync-health monitor thread.
/// </summary>
/// <remarks>
/// A gap is counted when the interval between consecutive reads exceeds
/// max(<see cref="GapFloorMs"/>, 2× expected callback interval). The floor avoids flagging
/// normal WASAPI callback jitter.
/// </remarks>
public sealed class ReadCallbackGapTracker
{
    internal const double GapFloorMs = 100;

    private long _lastReadMs = -1;
    private long _gapCount;
    private long _maxGapMsBits; // double stored via BitConverter for lock-free read

    /// <summary>Gets the number of starvation gaps observed.</summary>
    public long GapCount => Interlocked.Read(ref _gapCount);

    /// <summary>Gets the largest gap observed in milliseconds.</summary>
    public double MaxGapMs => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _maxGapMsBits));

    /// <summary>Records a read callback. Called from the audio thread — must stay allocation-free.</summary>
    /// <param name="nowMs">Current monotonic time (Environment.TickCount64).</param>
    /// <param name="expectedIntervalMs">Expected callback interval from buffer size and format.</param>
    public void RecordRead(long nowMs, double expectedIntervalMs)
    {
        var last = Interlocked.Exchange(ref _lastReadMs, nowMs);
        if (last < 0)
        {
            return; // first read after start/reset
        }

        var gapMs = nowMs - last;
        var threshold = Math.Max(GapFloorMs, 2 * expectedIntervalMs);
        if (gapMs > threshold)
        {
            Interlocked.Increment(ref _gapCount);
            double current;
            while (gapMs > (current = MaxGapMs))
            {
                var currentBits = BitConverter.DoubleToInt64Bits(current);
                var newBits = BitConverter.DoubleToInt64Bits(gapMs);
                if (Interlocked.CompareExchange(ref _maxGapMsBits, newBits, currentBits) == currentBits)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Resets timing state across pipeline restarts so the pause doesn't count as a gap.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _lastReadMs, -1);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj --filter "FullyQualifiedName~ReadCallbackGapTrackerTests"`
Expected: all PASS.

- [ ] **Step 5: Hook into BufferedAudioSampleSource**

Modify `src/SendspinClient.Services/Audio/BufferedAudioSampleSource.cs` — add an optional tracker parameter and a `RecordRead` call. Full updated file body (keep the existing copyright header):

```csharp
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using SendspinClient.Services.Diagnostics;

namespace SendspinClient.Services.Audio;

/// <summary>
/// Bridges <see cref="ITimedAudioBuffer"/> to <see cref="IAudioSampleSource"/>.
/// Provides current local time to the buffer for timed sample release.
/// </summary>
/// <remarks>
/// This class is called from NAudio's audio thread and must be fast and non-blocking.
/// It provides the current time to the timed buffer so that audio is released
/// at the correct moment for synchronized playback.
/// </remarks>
public sealed class BufferedAudioSampleSource : IAudioSampleSource
{
    private readonly ITimedAudioBuffer _buffer;
    private readonly Func<long> _getCurrentTimeMicroseconds;
    private readonly ReadCallbackGapTracker? _gapTracker;

    /// <inheritdoc/>
    public AudioFormat Format => _buffer.Format;

    /// <summary>
    /// Gets the underlying audio buffer.
    /// Used by the player to subscribe to rate changes for resampling sync correction.
    /// </summary>
    public ITimedAudioBuffer Buffer => _buffer;

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferedAudioSampleSource"/> class.
    /// </summary>
    /// <param name="buffer">The timed audio buffer to read from.</param>
    /// <param name="getCurrentTimeMicroseconds">Function that returns current local time in microseconds.</param>
    /// <param name="gapTracker">Optional tracker for audio-thread starvation diagnostics.</param>
    public BufferedAudioSampleSource(
        ITimedAudioBuffer buffer,
        Func<long> getCurrentTimeMicroseconds,
        ReadCallbackGapTracker? gapTracker = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(getCurrentTimeMicroseconds);

        _buffer = buffer;
        _getCurrentTimeMicroseconds = getCurrentTimeMicroseconds;
        _gapTracker = gapTracker;
        _gapTracker?.Reset();
    }

    /// <inheritdoc/>
    public int Read(float[] buffer, int offset, int count)
    {
        if (_gapTracker is not null)
        {
            // Expected interval: samples requested ÷ samples per ms
            var samplesPerMs = Format.SampleRate * Format.Channels / 1000.0;
            _gapTracker.RecordRead(Environment.TickCount64, count / samplesPerMs);
        }

        var currentTime = _getCurrentTimeMicroseconds();

        // Read from the timed buffer using the portion we need to fill
        var span = buffer.AsSpan(offset, count);
        var read = _buffer.Read(span, currentTime);

        // Fill remainder with silence if underrun
        if (read < count)
        {
            buffer.AsSpan(offset + read, count - read).Fill(0f);
        }

        // Always return requested count to keep NAudio happy (silence-filled if needed)
        return count;
    }
}
```

- [ ] **Step 6: Build and run all tests**

Run: `dotnet build SendspinClient.sln && dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: 0 errors, all tests PASS (existing 46 + new).

- [ ] **Step 7: Commit**

```bash
git add src/SendspinClient.Services/Diagnostics/ReadCallbackGapTracker.cs src/SendspinClient.Services/Audio/BufferedAudioSampleSource.cs tests/SendspinClient.Services.Tests/Diagnostics/ReadCallbackGapTrackerTests.cs
git commit -m "feat: audio-thread callback gap tracking"
```

---

### Task 7: SyncHealthMonitor + DI wiring

Composition root: 10 Hz timer → sample → detector → classifier → log + latest verdict. Thin orchestration; covered by build + manual smoke (its parts are unit-tested).

**Files:**
- Create: `src/SendspinClient.Services/Diagnostics/SyncHealthMonitor.cs`
- Modify: `src/SendspinClient/App.xaml.cs` (DI registration ~line 352, sourceFactory at line 348, activation in startup)

- [ ] **Step 1: Implement SyncHealthMonitor**

```csharp
// src/SendspinClient.Services/Diagnostics/SyncHealthMonitor.cs
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Synchronization;

namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// Always-on sync-health watchdog: samples pipeline + clock stats at 10 Hz, detects trouble
/// episodes, classifies them, and writes them to the dedicated sync-health log.
/// </summary>
/// <remarks>
/// Never affects playback: every tick is wrapped, failures log once and degrade to inert.
/// The latest verdict is exposed for the Stats window via <see cref="HealthDisplay"/>.
/// </remarks>
public sealed class SyncHealthMonitor : IDisposable
{
    private const int SampleIntervalMs = 100;

    // Matches SyncCorrectionOptions defaults used by the pipeline (syncOptions: null → SDK Default).
    private const double DeadbandMs = 2.0;
    private const double MaxSpeedCorrection = 0.02;

    private readonly IAudioPipeline _pipeline;
    private readonly IClockSynchronizer _clockSync;
    private readonly ReadCallbackGapTracker _gapTracker;
    private readonly SyncHealthLog _log;
    private readonly ILogger<SyncHealthMonitor> _logger;
    private readonly EpisodeDetector _detector = new(DeadbandMs, MaxSpeedCorrection);
    private readonly Timer _timer;

    private bool _wasActive;
    private bool _tickFaulted;
    private int _episodeCount;
    private volatile string _healthDisplay = "No issues detected";

    /// <summary>Gets the latest verdict line for the Stats window (e.g. "Network starvation suspected (2 episodes)").</summary>
    public string HealthDisplay => _healthDisplay;

    /// <summary>Gets the number of episodes recorded this session.</summary>
    public int EpisodeCount => _episodeCount;

    public SyncHealthMonitor(
        IAudioPipeline pipeline,
        IClockSynchronizer clockSync,
        ReadCallbackGapTracker gapTracker,
        SyncHealthLog log,
        ILogger<SyncHealthMonitor> logger)
    {
        _pipeline = pipeline;
        _clockSync = clockSync;
        _gapTracker = gapTracker;
        _log = log;
        _logger = logger;
        _timer = new Timer(OnTick, state: null, dueTime: SampleIntervalMs, period: SampleIntervalMs);
    }

    private void OnTick(object? state)
    {
        try
        {
            var stats = _pipeline.BufferStats;
            if (stats is null)
            {
                _wasActive = false;
                return;
            }

            if (!_wasActive)
            {
                _wasActive = true;
                WriteSessionHeader();
            }

            var clock = _clockSync.GetStatus();
            var format = _pipeline.OutputFormat;
            var sample = new SyncHealthSample
            {
                TimestampMs = Environment.TickCount64,
                SmoothedSyncErrorMs = stats.SmoothedSyncErrorMs,
                BufferedMs = stats.BufferedMs,
                TargetMs = stats.TargetMs,
                UnderrunCount = stats.UnderrunCount,
                SamplesDroppedForSync = stats.SamplesDroppedForSync,
                SamplesInsertedForSync = stats.SamplesInsertedForSync,
                ReanchorCount = stats.ReanchorCount,
                TargetPlaybackRate = stats.TargetPlaybackRate,
                TotalSamplesWritten = stats.TotalSamplesWritten,
                LastChunkAgeMs = stats.LastChunkAgeMs,
                MaxChunkGapMs = stats.MaxChunkGapMs,
                ChunkJitterMs = stats.ChunkJitterMs,
                BytesReceived = stats.BytesReceived,
                OffsetMs = clock.OffsetMilliseconds,
                RttJitterMs = clock.RttJitterMicroseconds / 1000.0,
                AdaptiveForgettingTriggerCount = clock.AdaptiveForgettingTriggerCount,
                CallbackGapCount = _gapTracker.GapCount,
                MaxCallbackGapMs = _gapTracker.MaxGapMs,
                SampleRate = format?.SampleRate ?? 48000,
                Channels = format?.Channels ?? 2,
            };

            if (_detector.Observe(in sample) is { } episode)
            {
                var classification = EpisodeClassifier.Classify(episode);
                _log.WriteEpisode(episode, classification);
                var count = Interlocked.Increment(ref _episodeCount);
                _healthDisplay = $"{Describe(classification)} ({count} episode{(count == 1 ? string.Empty : "s")})";
                _logger.LogWarning(
                    "Sync health episode: {Verdict} duration={Duration:F1}s evidence={Evidence}",
                    classification.Verdict, episode.DurationSeconds, classification.Evidence);
            }
        }
        catch (Exception ex)
        {
            if (!_tickFaulted)
            {
                _tickFaulted = true;
                _logger.LogError(ex, "Sync health monitor tick failed; diagnostics degraded");
            }
        }
    }

    private void WriteSessionHeader()
    {
        var format = _pipeline.OutputFormat;
        var version = typeof(SyncHealthMonitor).Assembly.GetName().Version?.ToString(3) ?? "?";
        _log.WriteSessionHeader(
            $"app={version} os={Environment.OSVersion.VersionString} " +
            $"format={format?.SampleRate}Hz/{format?.Channels}ch " +
            $"outputLatency={_pipeline.DetectedOutputLatencyMs}ms");
    }

    private static string Describe(SyncHealthClassification c) => c.Verdict switch
    {
        SyncHealthVerdict.NetworkStarvation => "Network starvation suspected",
        SyncHealthVerdict.ClockSyncInstability => "Clock sync instability suspected",
        SyncHealthVerdict.DeviceClockSkew => c.EstimatedSkewPpm is { } ppm
            ? $"Device clock skew suspected ({ppm:F0} ppm)"
            : "Device clock skew suspected",
        SyncHealthVerdict.LocalTiming => "Local timing problem suspected",
        _ => "Sync issues detected (cause unclear)",
    };

    public void Dispose() => _timer.Dispose();
}
```

Note: if Task 1 Step 3 found different 9.0.2 property names, adjust the sampler mapping lines here accordingly.

- [ ] **Step 2: Wire into App.xaml.cs**

Three changes in `src/SendspinClient/App.xaml.cs`:

(a) After the `IAudioPipeline` registration block (~line 352), add:

```csharp
// Sync health diagnostics (issue #33): always-on episode detection + sync-health.log
services.AddSingleton<ReadCallbackGapTracker>();
services.AddSingleton(new SyncHealthLog(AppPaths.LogDirectory));
services.AddSingleton<SyncHealthMonitor>();
```

(b) Change the `sourceFactory` line inside the `AudioPipeline` registration (line 348) from:

```csharp
sourceFactory: (buffer, timeFunc) => new BufferedAudioSampleSource(buffer, timeFunc),
```

to:

```csharp
sourceFactory: (buffer, timeFunc) => new BufferedAudioSampleSource(
    buffer, timeFunc, sp.GetRequiredService<ReadCallbackGapTracker>()),
```

(c) Activate the monitor at startup. Find where `OnStartup` resolves services after building the provider (search for `GetRequiredService<MainViewModel>` or similar in `OnStartup`) and add alongside:

```csharp
// Force-create the sync health monitor so its timer runs for the whole session
_ = _serviceProvider.GetRequiredService<SyncHealthMonitor>();
```

Add `using SendspinClient.Services.Diagnostics;` to App.xaml.cs usings.

- [ ] **Step 3: Build**

Run: `dotnet build SendspinClient.sln`
Expected: `0 Error(s)`. (Compile verifies the 9.0.2 property names used in the sampler mapping.)

- [ ] **Step 4: Commit**

```bash
git add src/SendspinClient.Services/Diagnostics/SyncHealthMonitor.cs src/SendspinClient/App.xaml.cs
git commit -m "feat: sync health monitor wired into app startup"
```

---

### Task 8: Stats window Health row

**Files:**
- Modify: `src/SendspinClient/ViewModels/StatsViewModel.cs` (ctor ~line 329, UpdateStats ~line 370, new properties near line 67)
- Modify: `src/SendspinClient/ViewModels/MainViewModel.cs` (StatsViewModel construction at line ~2605; ctor + field for the monitor)
- Modify: `src/SendspinClient/Views/StatsWindow.xaml` (Sync Status grid, lines 143–167)

- [ ] **Step 1: Add Health properties + monitor to StatsViewModel**

Add after the `_isPlaybackActive` property (~line 67):

```csharp
    /// <summary>
    /// Gets the sync health verdict line (latest episode classification, or all-clear).
    /// </summary>
    [ObservableProperty]
    private string _healthDisplay = "No issues detected";

    /// <summary>
    /// Gets the color for the health display (green all-clear, yellow when episodes exist).
    /// </summary>
    [ObservableProperty]
    private Brush _healthColor = Brushes.Gray;
```

Add a field, a ctor parameter, and an assignment (ctor at ~line 329):

```csharp
    private readonly SyncHealthMonitor _syncHealthMonitor;
```

```csharp
    public StatsViewModel(
        IAudioPipeline audioPipeline,
        IClockSynchronizer clockSynchronizer,
        ClientCapabilities clientCapabilities,
        SyncHealthMonitor syncHealthMonitor)
```

and inside the ctor body: `_syncHealthMonitor = syncHealthMonitor;`

Add `using SendspinClient.Services.Diagnostics;` to the usings.

In `UpdateStats()` (~line 370) add a call `UpdateHealthStats();` after `UpdateAudioFormatStats();`, and add the method:

```csharp
    private void UpdateHealthStats()
    {
        HealthDisplay = _syncHealthMonitor.HealthDisplay;
        HealthColor = _syncHealthMonitor.EpisodeCount == 0
            ? new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80))  // Green
            : new SolidColorBrush(Color.FromRgb(0xfb, 0xbf, 0x24)); // Yellow
    }
```

- [ ] **Step 2: Pass the monitor through MainViewModel**

In `src/SendspinClient/ViewModels/MainViewModel.cs`: add a `private readonly SyncHealthMonitor _syncHealthMonitor;` field, a `SyncHealthMonitor syncHealthMonitor` constructor parameter (append after the existing ones), assign it in the ctor, and change line ~2605 from:

```csharp
            var statsViewModel = new StatsViewModel(_audioPipeline, _clockSynchronizer, _clientCapabilities);
```

to:

```csharp
            var statsViewModel = new StatsViewModel(_audioPipeline, _clockSynchronizer, _clientCapabilities, _syncHealthMonitor);
```

Add `using SendspinClient.Services.Diagnostics;` if not present. DI resolves the new MainViewModel parameter automatically (it's registered via `services.AddSingleton<MainViewModel>()`).

- [ ] **Step 3: Add the XAML row**

In `src/SendspinClient/Views/StatsWindow.xaml`, Sync Status section: add a fourth `<RowDefinition Height="Auto"/>` to the RowDefinitions at lines 148–152, then after the "Playback Active" row (line 166) add:

```xml
                        <TextBlock Grid.Row="3" Grid.Column="0" Text="Health" Style="{StaticResource StatLabel}" Margin="0,8,0,0"/>
                        <TextBlock Grid.Row="3" Grid.Column="1" Text="{Binding HealthDisplay}"
                                   Style="{StaticResource StatValue}" Margin="0,8,0,0"
                                   Foreground="{Binding HealthColor}" TextWrapping="Wrap" MaxWidth="220"/>
```

- [ ] **Step 4: Build and run all tests**

Run: `dotnet build SendspinClient.sln && dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: 0 errors, all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SendspinClient/ViewModels/StatsViewModel.cs src/SendspinClient/ViewModels/MainViewModel.cs src/SendspinClient/Views/StatsWindow.xaml
git commit -m "feat: sync health verdict line in stats window"
```

---

### Task 9: Final verification + PR

- [ ] **Step 1: Full clean build + tests**

Run: `dotnet build SendspinClient.sln -c Release && dotnet test tests/SendspinClient.Services.Tests/SendspinClient.Services.Tests.csproj`
Expected: 0 errors; all tests pass. Check no NEW warnings in files this plan touched.

- [ ] **Step 2: Manual smoke test**

Run the app (`dotnet run --project src/SendspinClient/SendspinClient.csproj`), connect to a server, play a track for ~1 minute, then check:
- `%LOCALAPPDATA%\Sendspin\logs\sync-health.log` exists and contains one `SESSION` line.
- Stats for Nerds shows the `Health` row (green "No issues detected" on a healthy network).
- If feasible, briefly disable the network adapter mid-playback and re-enable: an episode block with `verdict=network-starvation` should appear in the log within seconds of the audio recovering.

- [ ] **Step 3: Push and create PR**

```bash
git push -u origin feat/sync-health-diagnostics
gh pr create --title "feat: sync health diagnostics (issue #33)" --body "Episode detection, root-cause classification, and always-on sync-health.log per docs/superpowers/specs/2026-06-11-sync-health-diagnostics-design.md. Closes the diagnostic gap in #33."
```
