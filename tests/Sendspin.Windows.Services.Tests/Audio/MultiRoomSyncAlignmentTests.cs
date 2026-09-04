// <copyright file="MultiRoomSyncAlignmentTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;
using Sendspin.Windows.Services.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Sendspin.Windows.Services.Tests.Audio;

/// <summary>
/// Boxes the core multi-room-sync invariant at the code level, with no audio hardware.
/// </summary>
/// <remarks>
/// <para>
/// Multi-room sync reduces to a single-player property: every player slaves to the same server
/// clock, so two players are in sync iff each independently outputs the sample tagged server-time
/// <c>T</c> at <c>ServerToClientTime(T)</c>. You never need a second player to test it.
/// </para>
/// <para>
/// The corollary these tests assert: <b>for a zero-drift clock, the cumulative drop/insert
/// correction must stay near zero.</b> No drift means no correction is needed; any net correction
/// physically stretches or compresses the stream, shifting this player's absolute output position
/// away from the server-anchored schedule — i.e. out of sync with everyone else. Net inserted
/// frames × frame-duration = exactly how many milliseconds late this player ends up.
/// </para>
/// <para>
/// The harness drives the real <see cref="TimedAudioBuffer"/> + <see cref="SyncCorrectionCalculator"/>
/// + <see cref="SyncCorrectedSampleSource"/> (the app's external-correction path) through a
/// simulated, perfectly drift-free session. It models WASAPI's habit of gulping its ~100 ms output
/// buffer at <c>Play()</c>, which makes the device clock lag the samples already read — the constant
/// negative startup offset behind issue #33's "initial slowdown".
/// </para>
/// </remarks>
public class MultiRoomSyncAlignmentTests
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int ChunkFrames = 480;                 // 10 ms WASAPI-style callback
    private const int ChunkSamples = ChunkFrames * Channels;
    private const double UsPerFrame = 1_000_000.0 / SampleRate;
    private const long VirtualStart = 1_000_000_000L;    // arbitrary local-clock epoch

    private readonly ITestOutputHelper _output;

    public MultiRoomSyncAlignmentTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A perfectly converged, zero-drift clock. ServerToClientTime is a fixed linear map, so the
    /// "correct" local play time for every sample is known exactly.
    /// </summary>
    private sealed class FixedOffsetClock : IClockSynchronizer
    {
        private readonly long _offset; // ServerToClientTime(t) = t + offset (minus StaticDelay)

        public FixedOffsetClock(long offset) => _offset = offset;

        public double StaticDelayMs { get; set; }

        public long ServerToClientTime(long serverTime) => serverTime + _offset - (long)(StaticDelayMs * 1000);

        public long ClientToServerTime(long clientTime) => clientTime - _offset + (long)(StaticDelayMs * 1000);

        public bool IsConverged => true;

        public bool HasMinimalSync => true;

        public void ProcessMeasurement(long t1, long t2, long t3, long t4)
        {
        }

        public void Reset()
        {
        }

        public ClockSyncStatus GetStatus() => new() { IsConverged = true, IsDriftReliable = true };
    }

    /// <summary>
    /// Collects the buffer's own log lines, so a test can assert on which path it took rather than
    /// inferring it. The startup baseline capture and the stale-audio branch both announce
    /// themselves here.
    /// </summary>
    private sealed class CapturingLogger : ILogger<TimedAudioBuffer>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private readonly record struct SessionResult(
        long NetCorrectionSamples,
        double NetCorrectionMs,
        double FinalSyncErrorMs,
        double AlignmentErrorMs,
        IReadOnlyList<string> BufferLog);

    /// <summary>
    /// Simulates a drift-free playback session and returns the net drop/insert correction.
    /// </summary>
    /// <param name="prefillMicros">
    /// Output-buffer prefill the backend gulps before its clock starts advancing (WASAPI ≈ 100 ms).
    /// The device clock lags the consumed audio by this much, producing a constant negative startup error.
    /// </param>
    /// <param name="calibratedStartupMicros">
    /// Value fed to <see cref="ITimedAudioBuffer.CalibratedStartupLatencyMicroseconds"/> to compensate
    /// the prefill. 0 = uncompensated (today's external-correction path).
    /// </param>
    /// <param name="seconds">Simulated session length.</param>
    private SessionResult RunDriftFreeSession(
        long prefillMicros,
        long calibratedStartupMicros,
        int seconds,
        long outputDelayMicros = 0,
        long startLateMicros = 0)
    {
        var format = new AudioFormat { Codec = "pcm", SampleRate = SampleRate, Channels = Channels, BitDepth = 32 };

        // ServerToClientTime(0) == VirtualStart, so the first segment is scheduled exactly at our start.
        var clock = new FixedOffsetClock(VirtualStart);

        var log = new CapturingLogger();
        using var buffer = new TimedAudioBuffer(
            format, clock, bufferCapacityMs: 4000, SyncCorrectionOptions.Default, log)
        {
            CalibratedStartupLatencyMicroseconds = calibratedStartupMicros,

            // AudioPipeline sets this from the player's resolved output latency. It is the knob
            // that actually moves audio in the air: ScheduledLocalTimeFor subtracts it, pulling
            // every scheduled start earlier so samples handed over early still leave the device
            // on the server's schedule.
            OutputLatencyMicroseconds = outputDelayMicros,
        };
        var calculator = new SyncCorrectionCalculator(SyncCorrectionOptions.Default, SampleRate, Channels);

        long nowMicros = VirtualStart;
        using var source = new SyncCorrectedSampleSource(buffer, calculator, () => nowMicros);

        var prefillFrames = (long)(prefillMicros / UsPerFrame);
        var sampleData = new float[ChunkSamples];
        var outBuf = new float[ChunkSamples];

        long framesWritten = 0;

        void WriteChunk()
        {
            // Tag every frame with its own source index instead of a constant, so emitted audio
            // carries its identity. This is what lets the harness measure where the player really
            // is: SyncErrorMicroseconds has the self-measured baseline subtracted out of it
            // (syncError = (elapsed - CalibratedStartupLatency) - samplesReadTime - baseline), so
            // an absorbed constant offset is invisible to it by construction. A frame's tag is not.
            for (var f = 0; f < ChunkFrames; f++)
            {
                var tag = (float)(framesWritten + f);
                for (var c = 0; c < Channels; c++)
                {
                    sampleData[(f * Channels) + c] = tag;
                }
            }

            buffer.Write(sampleData, (long)(framesWritten * UsPerFrame));
            framesWritten += ChunkFrames;
        }

        // Pre-fill ~1000 ms so reads never underrun; the first segment's timestamp (0) sets the anchor.
        for (var k = 0; k < 100; k++)
        {
            WriteChunk();
        }

        var totalReads = seconds * 100; // 100 × 10 ms callbacks per second

        // Everything handed to the DAC, in order, so the frame sounding at any device-clock
        // instant can be looked up by its position in the output stream.
        var emitted = new float[totalReads * ChunkFrames];

        for (var i = 0; i < totalReads; i++)
        {
            // The device clock = frames actually rendered = frames pushed (this read inclusive),
            // minus the prefill the DAC is still holding. It reads ~0 until the prefill drains, then
            // advances 1:1 with output. (i+1) keeps a no-offset session at exactly zero sync error.
            var playedFrames = Math.Max(0, ((long)(i + 1) * ChunkFrames) - prefillFrames);
            nowMicros = VirtualStart + startLateMicros + (long)(playedFrames * UsPerFrame);

            source.Read(outBuf, 0, ChunkSamples);

            for (var f = 0; f < ChunkFrames; f++)
            {
                emitted[(i * ChunkFrames) + f] = outBuf[f * Channels];
            }

            // Keep the buffer topped up to roughly its initial depth (produce 10 ms per 10 ms consumed).
            WriteChunk();
        }

        // Absolute alignment, measured the way a microphone on one timebase would.
        //
        // Wall time is NOT the device clock: each callback is 10 ms of real time regardless of how
        // much the DAC has drained, so after every read the wall clock has advanced one chunk while
        // the DAC still holds the prefill. Deriving both from the same counter is what makes an
        // uncompensated prefill invisible — the player looks on time against a clock that is itself
        // late. Sync error is measured against the device clock; being in sync with another player
        // is a statement about wall time, and the two only agree once the prefill is compensated.
        // Content is still anchored at server timestamp 0, so starting late means the frames due
        // at the end are further along by exactly the lateness.
        var lateFrames = (long)(startLateMicros / UsPerFrame);
        var wallFrames = lateFrames + ((long)totalReads * ChunkFrames); // real time elapsed, in frames
        var renderedFrames = Math.Max(0, wallFrames - lateFrames - prefillFrames); // DAC output position
        var soundingTag = emitted[Math.Min(renderedFrames, emitted.Length - 1)];
        var alignmentMs = (soundingTag - wallFrames) * UsPerFrame / 1000.0;

        var stats = buffer.GetStats();
        var net = stats.SamplesInsertedForSync - stats.SamplesDroppedForSync;
        var netMs = net / (double)Channels * UsPerFrame / 1000.0;

        _output.WriteLine(
            $"prefill={prefillMicros / 1000.0:F0}ms calib={calibratedStartupMicros / 1000.0:F0}ms -> " +
            $"inserted={stats.SamplesInsertedForSync} dropped={stats.SamplesDroppedForSync} " +
            $"net={net} samples ({netMs:F1}ms) finalErr={buffer.SyncErrorMicroseconds / 1000.0:F1}ms " +
            $"alignment={alignmentMs:F1}ms");

        foreach (var line in log.Lines)
        {
            _output.WriteLine($"    buffer: {line}");
        }

        return new SessionResult(net, netMs, buffer.SyncErrorMicroseconds / 1000.0, alignmentMs, log.Lines);
    }

    // A player whose absolute alignment sits within a small margin of zero is putting sample-T out
    // at ServerToClientTime(T) — i.e. in sync with every other player on the same server clock.
    // The margin covers the harness's one-callback (10ms) granularity floor: the simulated DAC reads
    // a whole 10ms chunk before its clock ticks, a quantization of the real prefill effect, not a
    // property of the code under test. The signal we care about is ~100ms vs ~0ms, not the floor.
    private const double InSyncToleranceMs = 15.0;

    // The threshold above which a player is audibly/measurably off the shared schedule. Huge margin
    // below the observed ~100ms so the test is about presence-of-misalignment, not a tuning knob.
    private const double OutOfSyncThresholdMs = 50.0;

    /// <summary>
    /// Control: with no drift, no prefill and no declared latency, the player holds the server
    /// schedule. Proves the harness measures real alignment, not noise.
    /// </summary>
    [Fact]
    public void ZeroDrift_NoStartupOffset_StaysOnSchedule()
    {
        var result = RunDriftFreeSession(prefillMicros: 0, calibratedStartupMicros: 0, seconds: 20);

        Assert.True(
            Math.Abs(result.AlignmentErrorMs) < InSyncToleranceMs,
            $"drift-free playback should hold the schedule, but sat {result.AlignmentErrorMs:F1}ms off");
    }

    /// <summary>
    /// The box: a prefill the buffer has not been told about leaves this player ~100 ms behind the
    /// server schedule for the whole session — out of sync with every other player on that clock.
    /// </summary>
    /// <remarks>
    /// The reported sync error does not show it. The SDK self-measures the startup residual at the
    /// end of the grace period and <em>absorbs</em> it, logging "constant offset will not be
    /// corrected", so <see cref="ITimedAudioBuffer.SyncErrorMicroseconds"/> settles near zero while
    /// the audio stays 100 ms late. That is why this asserts on measured alignment: an assertion on
    /// the reported error passes at ~0 ms precisely when the misalignment is worst.
    /// </remarks>
    [Fact]
    public void UndeclaredPrefill_LeavesThePlayerOffSchedule()
    {
        var result = RunDriftFreeSession(prefillMicros: 100_000, calibratedStartupMicros: 0, seconds: 20);

        Assert.True(
            Math.Abs(result.AlignmentErrorMs) > OutOfSyncThresholdMs,
            $"expected an undeclared prefill to leave the player far off schedule, got " +
            $"{result.AlignmentErrorMs:F1}ms alignment (reported error {result.FinalSyncErrorMs:F1}ms)");

        Assert.True(
            Math.Abs(result.FinalSyncErrorMs) < InSyncToleranceMs,
            $"the reported error is expected to look healthy while the player is off schedule — if " +
            $"this fails the SDK stopped absorbing the residual, and the box above can be reconsidered " +
            $"(reported {result.FinalSyncErrorMs:F1}ms)");
    }

    /// <summary>
    /// The fix, and what the app already does: declaring the output latency on the buffer pre-rolls
    /// every scheduled start by that much (<c>ScheduledLocalTimeFor</c> subtracts it), so audio
    /// handed over early still leaves the device on the server's schedule.
    /// <c>AudioPipeline</c> sets this from <c>IAudioPlayer.OutputLatencyMs</c>, which
    /// <c>WasapiAudioPlayer</c> resolves through its StreamLatency → DeviceBuffer → Estimated ladder.
    /// </summary>
    [Fact]
    public void DeclaredOutputLatency_CompensatesThePrefill()
    {
        var result = RunDriftFreeSession(
            prefillMicros: 100_000, calibratedStartupMicros: 0, seconds: 20, outputDelayMicros: 100_000);

        Assert.True(
            Math.Abs(result.AlignmentErrorMs) < InSyncToleranceMs,
            $"a declared output latency should put the audio back on schedule, but the player sat " +
            $"{result.AlignmentErrorMs:F1}ms off");
    }

    /// <summary>
    /// <see cref="ITimedAudioBuffer.CalibratedStartupLatencyMicroseconds"/> does not move audio.
    /// </summary>
    /// <remarks>
    /// It backdates the error anchor (<c>_playbackStartLocalTime = _scheduledStartLocalTime -
    /// CalibratedStartupLatency</c>), which changes what the buffer <em>reports</em>, not what the
    /// device emits or when. Pinned because it is an easy and expensive thing to assume otherwise:
    /// seeding it does not improve multi-room alignment, and it is not a substitute for declaring
    /// the output latency. Only <see cref="DeclaredOutputLatency_CompensatesThePrefill"/> does that.
    /// </remarks>
    [Fact]
    public void CalibratedStartupLatency_DoesNotChangeAlignment()
    {
        var uncalibrated = RunDriftFreeSession(
            prefillMicros: 100_000, calibratedStartupMicros: 0, seconds: 20);
        var calibrated = RunDriftFreeSession(
            prefillMicros: 100_000, calibratedStartupMicros: 100_000, seconds: 20);

        Assert.Equal(uncalibrated.AlignmentErrorMs, calibrated.AlignmentErrorMs, precision: 1);

        Assert.True(
            Math.Abs(calibrated.AlignmentErrorMs) > OutOfSyncThresholdMs,
            $"seeding the calibrated startup latency is not expected to correct alignment, but the " +
            $"player came out {calibrated.AlignmentErrorMs:F1}ms off");
    }

    /// <summary>
    /// A start that arrives after its scheduled time takes <c>SkipStaleAudio</c>, discarding the
    /// audio that can no longer be played and re-deriving the anchor. That re-derivation runs
    /// <c>ScheduledLocalTimeFor</c> a second time, against a head cursor that has already advanced,
    /// so it is worth pinning that it does not lose or double-count the output-latency pre-roll:
    /// however late the start, the audio still lands on the server's schedule.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(200_000)]
    [InlineData(500_000)]
    public void LateStart_WithDeclaredOutputLatency_StaysOnSchedule(long startLateMicros)
    {
        var result = RunDriftFreeSession(
            prefillMicros: 100_000, calibratedStartupMicros: 0, seconds: 20,
            outputDelayMicros: 100_000, startLateMicros: startLateMicros);

        Assert.Contains(result.BufferLog, l => l.Contains("stale audio", StringComparison.Ordinal));

        Assert.True(
            Math.Abs(result.AlignmentErrorMs) < InSyncToleranceMs,
            $"a start {startLateMicros / 1000}ms late should still land on schedule, but the player " +
            $"sat {result.AlignmentErrorMs:F1}ms off");
    }

    /// <summary>
    /// The absorbed startup baseline is not a measure of misalignment, and must never be read as one.
    /// </summary>
    /// <remarks>
    /// It is a property of the error tracker: <c>CaptureSyncErrorBaseline</c> snapshots whatever
    /// residual is outstanding when the startup grace period ends and rebases on it, logging
    /// "constant offset will not be corrected". A large absorbed baseline therefore says the tracker
    /// rebased, not that audio is late — here ~100 ms is absorbed while the player is exactly on
    /// schedule. Reading a baseline in a log as a misalignment is the same self-report trap as
    /// treating <c>error=+0.00ms</c> as "aligned", and it is the reason a live session showing
    /// "Captured startup (raw) sync-error baseline: -95.5ms" is not by itself evidence of a bug.
    /// </remarks>
    [Fact]
    public void AbsorbedStartupBaseline_IsNotMisalignment()
    {
        var result = RunDriftFreeSession(
            prefillMicros: 100_000, calibratedStartupMicros: 0, seconds: 20, outputDelayMicros: 100_000);

        var captured = Assert.Single(
            result.BufferLog.Where(l => l.Contains("sync-error baseline", StringComparison.Ordinal)));

        Assert.Contains("constant offset will not be corrected", captured, StringComparison.Ordinal);

        Assert.True(
            Math.Abs(result.AlignmentErrorMs) < InSyncToleranceMs,
            $"the player should be on schedule despite the absorbed baseline ({captured}), but sat " +
            $"{result.AlignmentErrorMs:F1}ms off");
    }
}
