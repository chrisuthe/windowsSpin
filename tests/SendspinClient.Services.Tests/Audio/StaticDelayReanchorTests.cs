// <copyright file="StaticDelayReanchorTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Linq;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;
using Xunit;

namespace SendspinClient.Services.Tests.Audio;

/// <summary>
/// Guards the static-delay-change fix: re-anchoring timing must preserve buffered audio so a delay
/// tweak applies in place, instead of dumping the buffer (which, with a server that transmits far
/// ahead of playback, stalls for the whole transmit-ahead window — the 30-second silence).
/// </summary>
/// <remarks>
/// The app's <c>OnSettingsStaticDelayMsChanged</c> previously called <c>IAudioPipeline.Clear()</c>;
/// it now calls <c>ReanchorTiming()</c>, which forwards to <see cref="TimedAudioBuffer.ResetSyncTracking"/>.
/// These tests exercise that buffer primitive directly (the property the pipeline passthrough relies on).
/// </remarks>
public class StaticDelayReanchorTests
{
    private const int Rate = 48000;
    private const int Ch = 2;
    private const int ChunkFrames = 480;
    private const int ChunkSamples = ChunkFrames * Ch;
    private const double UsPerFrame = 1_000_000.0 / Rate;
    private const long Start = 1_000_000_000L;

    private sealed class FixedClock : IClockSynchronizer
    {
        private readonly long _offset;

        public FixedClock(long offset) => _offset = offset;

        public double StaticDelayMs { get; set; }

        public long ServerToClientTime(long t) => t + _offset - (long)(StaticDelayMs * 1000);

        public long ClientToServerTime(long t) => t - _offset + (long)(StaticDelayMs * 1000);

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
    /// Builds a buffer pre-filled with ~2s of audio (a deep buffer, as a far-ahead server produces)
    /// and reads a few chunks so playback is established.
    /// </summary>
    private static (TimedAudioBuffer Buffer, FixedClock Clock) SetupPlaying()
    {
        var format = new AudioFormat { Codec = "pcm", SampleRate = Rate, Channels = Ch, BitDepth = 32 };
        var clock = new FixedClock(Start);
        var buffer = new TimedAudioBuffer(format, clock, bufferCapacityMs: 4000, SyncCorrectionOptions.Default);

        var data = new float[ChunkSamples];
        Array.Fill(data, 0.25f);

        long frames = 0;
        for (var k = 0; k < 200; k++) // 200 × 10ms = 2000ms buffered
        {
            buffer.Write(data, (long)(frames * UsPerFrame));
            frames += ChunkFrames;
        }

        var outBuf = new float[ChunkSamples];
        for (var i = 0; i < 10; i++) // establish playback, consume ~100ms
        {
            buffer.ReadRaw(outBuf, Start + (long)((long)i * ChunkFrames * UsPerFrame));
        }

        return (buffer, clock);
    }

    [Fact]
    public void ReanchorViaResetSyncTracking_PreservesBufferedAudio_NoSilenceGap()
    {
        var (buffer, clock) = SetupPlaying();
        Assert.True(buffer.BufferedMilliseconds > 1000, "precondition: a deep buffer is present");

        // Apply a static-delay change the way the fixed app does: shift the clock, then re-anchor
        // (instead of Clear). This is what runs when the user moves the offset slider.
        clock.StaticDelayMs = 200;
        buffer.ResetSyncTracking();

        // The whole point of the fix: the buffered audio survives, so there is nothing to re-fetch
        // and therefore no transmit-ahead wait.
        Assert.True(
            buffer.BufferedMilliseconds > 1000,
            $"re-anchor must preserve buffered audio, but only {buffer.BufferedMilliseconds:F0}ms remained");

        // And audio keeps flowing immediately on the next read — no silence.
        var outBuf = new float[ChunkSamples];
        var read = buffer.ReadRaw(outBuf, Start + (long)(10L * ChunkFrames * UsPerFrame));
        Assert.True(read > 0 && outBuf.Any(s => s != 0f), "audio should keep flowing right after re-anchor");
    }

    [Fact]
    public void Clear_EmptiesBuffer_ProducesSilence_TheOldBehavior()
    {
        var (buffer, _) = SetupPlaying();
        Assert.True(buffer.BufferedMilliseconds > 1000);

        // The old static-delay path. Clear dumps everything; with a far-ahead server the only audio
        // that refills is future-timestamped, so playback waits the transmit-ahead window in silence.
        buffer.Clear();

        Assert.True(
            buffer.BufferedMilliseconds < 1,
            $"Clear should empty the buffer, but {buffer.BufferedMilliseconds:F0}ms remained");

        var outBuf = new float[ChunkSamples];
        buffer.ReadRaw(outBuf, Start + (long)(10L * ChunkFrames * UsPerFrame));
        Assert.True(outBuf.All(s => s == 0f), "an emptied buffer outputs silence");
    }
}
