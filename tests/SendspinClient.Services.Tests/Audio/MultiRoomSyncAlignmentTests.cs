// <copyright file="MultiRoomSyncAlignmentTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;
using SendspinClient.Services.Audio;
using Xunit;
using Xunit.Abstractions;

namespace SendspinClient.Services.Tests.Audio;

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

    private readonly record struct SessionResult(long NetCorrectionSamples, double NetCorrectionMs, double FinalSyncErrorMs);

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
    private SessionResult RunDriftFreeSession(long prefillMicros, long calibratedStartupMicros, int seconds)
    {
        var format = new AudioFormat { Codec = "pcm", SampleRate = SampleRate, Channels = Channels, BitDepth = 32 };

        // ServerToClientTime(0) == VirtualStart, so the first segment is scheduled exactly at our start.
        var clock = new FixedOffsetClock(VirtualStart);

        using var buffer = new TimedAudioBuffer(format, clock, bufferCapacityMs: 4000, SyncCorrectionOptions.Default)
        {
            CalibratedStartupLatencyMicroseconds = calibratedStartupMicros,
        };
        var calculator = new SyncCorrectionCalculator(SyncCorrectionOptions.Default, SampleRate, Channels);

        long nowMicros = VirtualStart;
        using var source = new SyncCorrectedSampleSource(buffer, calculator, () => nowMicros);

        var prefillFrames = (long)(prefillMicros / UsPerFrame);
        var sampleData = new float[ChunkSamples];
        Array.Fill(sampleData, 0.25f);
        var outBuf = new float[ChunkSamples];

        long framesWritten = 0;

        void WriteChunk()
        {
            buffer.Write(sampleData, (long)(framesWritten * UsPerFrame));
            framesWritten += ChunkFrames;
        }

        // Pre-fill ~1000 ms so reads never underrun; the first segment's timestamp (0) sets the anchor.
        for (var k = 0; k < 100; k++)
        {
            WriteChunk();
        }

        var totalReads = seconds * 100; // 100 × 10 ms callbacks per second
        for (var i = 0; i < totalReads; i++)
        {
            // The device clock = frames actually rendered = frames pushed (this read inclusive),
            // minus the prefill the DAC is still holding. It reads ~0 until the prefill drains, then
            // advances 1:1 with output. (i+1) keeps a no-offset session at exactly zero sync error.
            var playedFrames = Math.Max(0, ((long)(i + 1) * ChunkFrames) - prefillFrames);
            nowMicros = VirtualStart + (long)(playedFrames * UsPerFrame);

            source.Read(outBuf, 0, ChunkSamples);

            // Keep the buffer topped up to roughly its initial depth (produce 10 ms per 10 ms consumed).
            WriteChunk();
        }

        var stats = buffer.GetStats();
        var net = stats.SamplesInsertedForSync - stats.SamplesDroppedForSync;
        var netMs = net / (double)Channels * UsPerFrame / 1000.0;

        _output.WriteLine(
            $"prefill={prefillMicros / 1000.0:F0}ms calib={calibratedStartupMicros / 1000.0:F0}ms -> " +
            $"inserted={stats.SamplesInsertedForSync} dropped={stats.SamplesDroppedForSync} " +
            $"net={net} samples ({netMs:F1}ms) finalErr={buffer.SyncErrorMicroseconds / 1000.0:F1}ms");

        return new SessionResult(net, netMs, buffer.SyncErrorMicroseconds / 1000.0);
    }

    // A player whose steady-state sync error sits within a small margin of zero is putting sample-T
    // out at ServerToClientTime(T) — i.e. in sync with every other player on the same server clock.
    // The margin covers the harness's one-callback (10ms) granularity floor: the simulated DAC reads
    // a whole 10ms chunk before its clock ticks, a quantization of the real prefill effect, not a
    // property of the code under test. The signal we care about is ~100ms vs ~0ms, not the floor.
    private const double InSyncToleranceMs = 15.0;

    // The threshold above which a player is audibly/measurably off the shared schedule. Huge margin
    // below the observed ~100ms so the test is about presence-of-misalignment, not a tuning knob.
    private const double OutOfSyncThresholdMs = 50.0;

    /// <summary>
    /// Control: with no drift and no startup offset, the player holds the server schedule exactly
    /// (steady-state sync error ≈ 0). Proves the harness measures real alignment, not noise.
    /// </summary>
    [Fact]
    public void ZeroDrift_NoStartupOffset_StaysOnSchedule()
    {
        var result = RunDriftFreeSession(prefillMicros: 0, calibratedStartupMicros: 0, seconds: 20);

        Assert.True(
            Math.Abs(result.FinalSyncErrorMs) < InSyncToleranceMs,
            $"drift-free playback should hold the schedule, but settled {result.FinalSyncErrorMs:F1}ms off");
    }

    /// <summary>
    /// The box: an uncompensated WASAPI-style prefill leaks straight into the external-correction
    /// path (ReadRaw never captures the startup baseline that the internal path does), leaving this
    /// player ~100 ms off the server schedule for the whole session — out of sync with other players.
    /// This is issue #33's "initial slowdown", and the multi-room-sync risk, measured at the code level.
    /// </summary>
    [Fact]
    public void StartupPrefill_Uncompensated_DriftsOffSchedule()
    {
        var result = RunDriftFreeSession(prefillMicros: 100_000, calibratedStartupMicros: 0, seconds: 20);

        Assert.True(
            Math.Abs(result.FinalSyncErrorMs) > OutOfSyncThresholdMs,
            $"expected the uncompensated prefill to leave the player far off schedule, " +
            $"got {result.FinalSyncErrorMs:F1}ms (net correction {result.NetCorrectionMs:F1}ms)");
    }

    /// <summary>
    /// The fix direction: seeding <see cref="ITimedAudioBuffer.CalibratedStartupLatencyMicroseconds"/>
    /// with the prefill cancels the constant offset before the corrector ever sees it, so the player
    /// holds the schedule (sync error ≈ 0). This is what the app must set on the buffer (≈ the WASAPI
    /// output latency) so it stays in sync with other players from the first second.
    /// </summary>
    [Fact]
    public void StartupPrefill_CompensatedViaCalibratedLatency_StaysOnSchedule()
    {
        var result = RunDriftFreeSession(prefillMicros: 100_000, calibratedStartupMicros: 100_000, seconds: 20);

        Assert.True(
            Math.Abs(result.FinalSyncErrorMs) < InSyncToleranceMs,
            $"compensated prefill should hold the schedule, but settled {result.FinalSyncErrorMs:F1}ms off");
    }
}
