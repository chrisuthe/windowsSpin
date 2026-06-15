// <copyright file="DynamicResamplerSampleProviderTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using NAudio.Wave;
using SendspinClient.Services.Audio;
using Xunit;

namespace SendspinClient.Services.Tests.Audio;

public class DynamicResamplerSampleProviderTests
{
    /// <summary>
    /// A source that always satisfies the full requested count, so any resampler
    /// shortfall is purely the WDL filter-priming deficit - never a genuine source underrun.
    /// </summary>
    private sealed class FullSampleProvider : ISampleProvider
    {
        private float _phase = -0.5f;

        public FullSampleProvider(int sampleRate, int channels)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            // Low-amplitude ramp so the output is real audio, not silence.
            for (var i = 0; i < count; i++)
            {
                buffer[offset + i] = _phase;
                _phase += 0.0001f;
                if (_phase > 0.5f)
                {
                    _phase = -0.5f;
                }
            }

            return count;
        }
    }

    [Fact]
    public void IdentityRate_FullSource_NeverPadsSilence()
    {
        const int sampleRate = 44100;
        const int channels = 2;

        var source = new FullSampleProvider(sampleRate, channels);

        // targetSampleRate == source rate => identity resampling (sync-correction only),
        // playback rate stays 1.0 with no correction provider. This is fletchowns' USB-DAC case.
        using var resampler = new DynamicResamplerSampleProvider(source, correctionProvider: null, targetSampleRate: sampleRate);

        // 882 interleaved samples == 441 frames == one 10 ms WASAPI period at 44.1 kHz.
        const int count = 882;
        var buffer = new float[count];

        // The first callback primes the WDL filter's history; like NAudio's own resampler it may
        // emit a single short frame here (one ~22 µs frame at stream start - inaudible).
        resampler.Read(buffer, 0, count);
        var shortsAfterPriming = resampler.ResamplerShortCount;
        Assert.True(
            shortsAfterPriming <= 1,
            $"startup priming should cost at most one frame, got {shortsAfterPriming}");

        // ~3 seconds of steady-state callbacks. The previous implementation under-fed the WDL
        // filter by its priming latency and padded silence on essentially every callback
        // (observed as 861 shorts in 21 s on a USB DAC). Output-driven feeding must accumulate
        // zero further shorts once primed.
        for (var i = 0; i < 300; i++)
        {
            var read = resampler.Read(buffer, 0, count);
            Assert.Equal(count, read);
        }

        Assert.Equal(shortsAfterPriming, resampler.ResamplerShortCount);
        Assert.Equal(0L, resampler.SourceEmptyCount);
    }
}
