// <copyright file="SourcePipelineContractTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;
using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

/// <summary>
/// The device driven by the SDK's own source pipeline: what Music Assistant actually receives.
/// Re-check when bumping the SDK.
/// </summary>
public class SourcePipelineContractTests
{
    private const int ChunkHeaderBytes = 9;

    [Fact]
    public async Task SilenceUntilSend_ThenTheInput_InOneStream()
    {
        var input = new FakeCaptureSource("mic");
        var silences = new List<FakeCaptureSource>();
        var device = new WindowsAudioCaptureDevice(
            () =>
            {
                var s = new FakeCaptureSource("silence");
                silences.Add(s);
                return s;
            },
            _ => input,
            NullLogger<WindowsAudioCaptureDevice>.Instance);
        await device.SetInputAsync("mic");

        var messages = new ConcurrentQueue<IMessage>();
        var chunks = new ConcurrentQueue<byte[]>();
        await using var pipeline = new SourceStreamPipeline(
            device,
            new KalmanClockSynchronizer(),
            m =>
            {
                messages.Enqueue(m);
                return Task.CompletedTask;
            },
            b =>
            {
                chunks.Enqueue(b);
                return Task.CompletedTask;
            },
            NullLogger.Instance,
            canStream: () => true);

        await pipeline.HandleCommandAsync("start");

        var start = Assert.Single(messages.OfType<ClientStreamStartMessage>()).Payload.Source;
        Assert.Equal(("pcm", 48000, 2, 16), (start.Codec, start.SampleRate, start.Channels, start.BitDepth));

        silences[0].Emit(captureTime: 1_000_000, frames: 960, fill: 0);
        await WaitUntilAsync(() => !chunks.IsEmpty);
        Assert.All(chunks, c => Assert.True(c.AsSpan(ChunkHeaderBytes).IndexOfAnyExcept((byte)0) < 0));

        await device.SetSendEnabledAsync(true);
        input.Emit(captureTime: 1_020_000, frames: 960, fill: 7);
        await WaitUntilAsync(() => chunks.Any(c => c.Length > ChunkHeaderBytes && c[ChunkHeaderBytes] == 7));

        await device.SetSendEnabledAsync(false);
        Assert.Single(messages.OfType<ClientStreamStartMessage>());
        Assert.DoesNotContain(messages, m => m is ClientStreamEndMessage);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(10);
        }
    }
}
