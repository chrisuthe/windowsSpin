// <copyright file="SilenceSourceTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Audio.Source;
using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

public class SilenceSourceTests
{
    private const long Chunk = 20_000;

    private static (SilenceSource Source, FakeTimer Timer, List<CapturedAudio> Frames) Create(long start = 1_000_000)
    {
        var timer = new FakeTimer { Now = start };
        var source = new SilenceSource(timer, runPacer: false);
        var frames = new List<CapturedAudio>();
        source.FramesCaptured += (_, f) => frames.Add(f);
        return (source, timer, frames);
    }

    [Fact]
    public async Task EmitsContiguousTwentyMillisecondChunksOfZeros()
    {
        var (source, timer, frames) = Create();
        await source.StartAsync();

        timer.Now += (3 * Chunk) + 5_000;
        source.Pump();

        Assert.Equal(new[] { 1_000_000L, 1_020_000, 1_040_000 }, frames.Select(f => f.CaptureTimeMicroseconds));
        Assert.All(frames, f =>
        {
            Assert.Equal(960 * CaptureFormat.BytesPerFrame, f.Pcm.Length);
            Assert.True(f.Pcm.Span.IndexOfAnyExcept((byte)0) < 0);
        });
    }

    [Fact]
    public async Task DoesNotDrift_OverThousandsOfUnevenTicks()
    {
        var (source, timer, frames) = Create(start: 0);
        await source.StartAsync();

        for (int i = 0; i < 20_000; i++)
        {
            timer.Now += 7_000;
            source.Pump();
        }

        // 140 s of clock is exactly 7000 chunks, each starting where the last one ended.
        Assert.Equal(7_000, frames.Count);
        Assert.Equal(6_999 * Chunk, frames[^1].CaptureTimeMicroseconds);
    }

    [Fact]
    public async Task AfterAStall_ResumesFromNow_InsteadOfBursting()
    {
        // Sleep/resume: minutes pass between pumps. Bursting them as silence would flood the stream.
        var (source, timer, frames) = Create();
        await source.StartAsync();

        timer.Now += 300_000_000;
        source.Pump();

        var only = Assert.Single(frames);
        Assert.Equal(timer.Now - Chunk, only.CaptureTimeMicroseconds);
    }

    [Fact]
    public async Task EmitsNothing_BeforeStartOrAfterStop()
    {
        var (source, timer, frames) = Create();

        timer.Now += 5 * Chunk;
        source.Pump();
        Assert.Empty(frames);

        await source.StartAsync();
        await source.StopAsync();
        timer.Now += 5 * Chunk;
        source.Pump();
        Assert.Empty(frames);
    }
}
