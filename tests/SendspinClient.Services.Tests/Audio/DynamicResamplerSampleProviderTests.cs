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
    /// A source that always returns the full requested count of a constant DC value. A correct
    /// resampler passes DC through unchanged, so the only way an output sample can collapse toward
    /// zero is an injected silence pad - which makes silence-gap concealment directly testable.
    /// </summary>
    private sealed class ConstantSampleProvider : ISampleProvider
    {
        private readonly float _value;

        public ConstantSampleProvider(int sampleRate, int channels, float value)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _value = value;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, _value, offset, count);
            return count;
        }
    }

    [Fact]
    public void RateCorrection_ConcealsShortfalls_WithoutSilenceGaps()
    {
        const int sampleRate = 44100;
        const int channels = 2;
        const float dc = 0.5f;

        var source = new ConstantSampleProvider(sampleRate, channels, dc);

        // targetSampleRate == source rate => identity resampling, sync-correction only.
        using var resampler = new DynamicResamplerSampleProvider(source, correctionProvider: null, targetSampleRate: sampleRate);

        // 882 interleaved samples == 441 frames == one 10 ms WASAPI period at 44.1 kHz.
        const int count = 882;
        var buffer = new float[count];

        // Warm up: the WDL filter's output ramps from 0 to the DC level as its history fills.
        for (var i = 0; i < 5; i++)
        {
            resampler.Read(buffer, 0, count);
        }

        // ~3 s of callbacks under continuous drift correction - the rate is nudged every callback,
        // the regime where a USB DAC's drift keeps the corrector adjusting and the WDL filter comes
        // up 1-2 frames short. The previous code padded those shorts with digital silence (an
        // audible click, 861 events in 21 s observed on a USB DAC); the fix conceals them by holding
        // the last sample.
        //
        // Silence pads are bit-exact 0.0f (Array.Fill); a DC input through the resampler never
        // produces exact zero, and a held DC frame is ~0.5. So an exact-zero output sample is the
        // unambiguous signature of a leaked silence pad. (Note: the resampler still has its own
        // amplitude transient on each rate change - a non-zero dip - which is a separate, pre-
        // existing matter addressed by the clock/loop architecture work, not by concealment.)
        var rate = 1.0;
        var step = 0.00005;
        var silenceSamples = 0;
        for (var i = 0; i < 300; i++)
        {
            rate += step;
            if (rate is > 1.002 or < 0.998)
            {
                step = -step;
            }

            resampler.PlaybackRate = rate;
            resampler.Read(buffer, 0, count);

            foreach (var sample in buffer)
            {
                if (sample == 0f)
                {
                    silenceSamples++;
                }
            }
        }

        // Guard that the run actually hit the shortfall path, so the concealment was exercised.
        Assert.True(resampler.ResamplerShortCount > 0, "test did not exercise the resampler-short path");
        Assert.Equal(0, silenceSamples);
    }
}
