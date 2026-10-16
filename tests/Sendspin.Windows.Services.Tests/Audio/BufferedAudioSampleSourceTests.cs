// <copyright file="BufferedAudioSampleSourceTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;
using Sendspin.Windows.Services.Audio;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio;

/// <summary>
/// The pipeline holds this source, not the correction source the player builds around it, so the
/// pipeline's lifecycle calls only reach that correction source through it.
/// </summary>
public class BufferedAudioSampleSourceTests
{
    [Fact]
    public void PipelineClear_ReachesWhateverWrapsTheSource()
    {
        // AudioPipeline.Clear resets its source only through IPlaybackLifecycleAware; a seek
        // that does not reach the player's correction source splices pre-seek audio in.
        using var buffer = CreateBuffer();
        var source = new BufferedAudioSampleSource(buffer, () => 0);
        var resets = 0;
        source.ResetRequested += (_, _) => resets++;

        var lifecycle = Assert.IsAssignableFrom<IPlaybackLifecycleAware>(source);
        lifecycle.Reset();

        Assert.Equal(1, resets);
    }

    private static TimedAudioBuffer CreateBuffer()
    {
        var format = new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 32 };
        return new TimedAudioBuffer(format, new FixedClock(), bufferCapacityMs: 1000, SyncCorrectionOptions.Default);
    }

    private sealed class FixedClock : IClockSynchronizer
    {
        public bool IsConverged => true;

        public bool HasMinimalSync => true;

        public double OutputDelayMs { get; set; }

        public long ServerToClientTime(long serverTime) => serverTime;

        public long ServerToClientTimeUncompensated(long serverTime) => serverTime;

        public long ClientToServerTime(long clientTime) => clientTime;

        public void ProcessMeasurement(long t1, long t2, long t3, long t4)
        {
        }

        public ClockSyncStatus GetStatus() => new();

        public void Reset()
        {
        }
    }
}
