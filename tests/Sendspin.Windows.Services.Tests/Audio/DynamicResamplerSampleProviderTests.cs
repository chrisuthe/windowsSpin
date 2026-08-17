// <copyright file="DynamicResamplerSampleProviderTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using NAudio.Wave;
using Sendspin.Windows.Services.Audio;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio;

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

    /// <summary>
    /// A continuous sine source; phase carries across reads so the signal itself has no
    /// discontinuities. Any output step above the sine's per-sample slope bound was
    /// manufactured by the resampler.
    /// </summary>
    private sealed class SineSampleProvider : ISampleProvider
    {
        private readonly double _phaseIncrement;
        private double _phase;

        public SineSampleProvider(int sampleRate, int channels, double frequencyHz)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _phaseIncrement = 2 * Math.PI * frequencyHz / sampleRate;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = WaveFormat.Channels;
            for (var i = 0; i < count; i += channels)
            {
                var sample = (float)(0.5 * Math.Sin(_phase));
                for (var c = 0; c < channels; c++)
                {
                    buffer[offset + i + c] = sample;
                }

                _phase += _phaseIncrement;
            }

            return count;
        }
    }

    /// <summary>
    /// Regression test for issue #63's audible click. WDL's IIR low-pass chain runs only while
    /// the resample ratio is off 1.0, and its filter history is never cleared. Toggling the
    /// playback rate between exactly 1.0 and 1.0+e - the corrector's None/Resampling limit
    /// cycle under ordinary clock drift - re-engages the chain against seconds-stale history,
    /// producing a signal-proportional broadband transient (the reported "soft click").
    /// A 101 Hz sine moves at most ~0.0066/sample, so any output step well above that bound
    /// is a manufactured discontinuity. 101 Hz and the 47-callback toggle period are chosen
    /// incommensurate so the stale history is never phase-aligned with the live signal.
    /// </summary>
    [Fact]
    public void RateCorrection_TogglingAcrossUnity_DoesNotProduceClicks()
    {
        const int sampleRate = 48000;
        const int channels = 2;
        const double frequency = 101.0;
        const int count = 960; // one 10 ms callback at 48 kHz stereo

        var source = new SineSampleProvider(sampleRate, channels, frequency);

        // targetSampleRate == source rate => identity resampling, sync-correction only.
        using var resampler = new DynamicResamplerSampleProvider(source, correctionProvider: null, targetSampleRate: sampleRate);

        var maxDelta = MeasureMaxSampleDelta(
            resampler, count, channels, callbacks: 1000,
            rateForCallback: cb => (cb / 47) % 2 == 0 ? 1.0 : 1.0004);

        var sineSlopeBound = 0.5 * 2 * Math.PI * frequency / sampleRate;
        Assert.True(
            maxDelta < 3 * sineSlopeBound,
            $"resampler manufactured a discontinuity of {maxDelta:F4} (sine slope bound {sineSlopeBound:F4}) - the issue #63 click");
    }

    /// <summary>
    /// Companion guard for the conversion branch: with genuine sample-rate conversion the
    /// ratio never reaches 1.0 at any correction rate, the low-pass chain stays continuously
    /// engaged (history stays warm), and rate toggling is click-free. PairingCodes that path so an
    /// identity-only filter change cannot regress it.
    /// </summary>
    [Fact]
    public void RateCorrection_TogglingDuringCompoundConversion_DoesNotProduceClicks()
    {
        const int sourceRate = 48000;
        const int targetRate = 44100;
        const int channels = 2;
        const double frequency = 101.0;
        const int count = 882; // one 10 ms callback at 44.1 kHz stereo

        var source = new SineSampleProvider(sourceRate, channels, frequency);
        using var resampler = new DynamicResamplerSampleProvider(source, correctionProvider: null, targetSampleRate: targetRate);

        var maxDelta = MeasureMaxSampleDelta(
            resampler, count, channels, callbacks: 1000,
            rateForCallback: cb => (cb / 47) % 2 == 0 ? 1.0 : 1.0004);

        var sineSlopeBound = 0.5 * 2 * Math.PI * frequency / targetRate;
        Assert.True(
            maxDelta < 3 * sineSlopeBound,
            $"compound conversion manufactured a discontinuity of {maxDelta:F4} (sine slope bound {sineSlopeBound:F4})");
    }

    /// <summary>
    /// PairingCodes the other half of the ratio-can-cross-unity branch: compound conversion must
    /// keep the WDL low-pass chain ENGAGED. The click tests cannot see this - a low-frequency
    /// sine is click-free with the chain removed too. Observable: for 48 kHz -> 44.1 kHz the
    /// chain's cutoff sits at ~19.9 kHz (0.90 x Nyquist / ratio), so a 21 kHz tone emerges
    /// near-silent with the chain engaged (~0.005 RMS, about -37 dB) but at ~0.21 RMS through
    /// bare linear interpolation. The 0.05 threshold sits 10x above the engaged level and 4x
    /// below the disengaged level.
    /// </summary>
    [Fact]
    public void CompoundConversion_AttenuatesContentAboveFilterCutoff()
    {
        const int sourceRate = 48000;
        const int targetRate = 44100;
        const int channels = 2;
        const double frequency = 21000.0;
        const int count = 882; // one 10 ms callback at 44.1 kHz stereo

        var source = new SineSampleProvider(sourceRate, channels, frequency);
        using var resampler = new DynamicResamplerSampleProvider(source, correctionProvider: null, targetSampleRate: targetRate);

        var buffer = new float[count];
        double sumSquares = 0;
        long samples = 0;
        for (var cb = 0; cb < 300; cb++)
        {
            resampler.Read(buffer, 0, count);
            if (cb < 50)
            {
                continue; // let the filter chain settle before measuring
            }

            foreach (var sample in buffer)
            {
                sumSquares += (double)sample * sample;
                samples++;
            }
        }

        var rms = Math.Sqrt(sumSquares / samples);
        Assert.True(
            rms < 0.05,
            $"21 kHz tone passed compound conversion at RMS {rms:F4} - the anti-alias chain is not engaged (engaged ~0.005, bare interpolation ~0.21)");
    }

    /// <summary>
    /// Drives the resampler through <paramref name="callbacks"/> read cycles, setting the
    /// playback rate per callback, and returns the largest same-channel sample-to-sample
    /// step in the output (measured across callback boundaries too). The first two callbacks
    /// are excluded as filter/priming warm-up.
    /// </summary>
    private static double MeasureMaxSampleDelta(
        DynamicResamplerSampleProvider resampler,
        int count,
        int channels,
        int callbacks,
        Func<int, double> rateForCallback)
    {
        var buffer = new float[count];
        var previous = new float[channels];
        var maxDelta = 0.0;

        for (var cb = 0; cb < callbacks; cb++)
        {
            resampler.PlaybackRate = rateForCallback(cb);
            resampler.Read(buffer, 0, count);

            if (cb >= 2)
            {
                for (var c = 0; c < channels; c++)
                {
                    var prev = previous[c];
                    for (var i = c; i < count; i += channels)
                    {
                        var delta = Math.Abs(buffer[i] - prev);
                        if (delta > maxDelta)
                        {
                            maxDelta = delta;
                        }

                        prev = buffer[i];
                    }
                }
            }

            for (var c = 0; c < channels; c++)
            {
                previous[c] = buffer[count - channels + c];
            }
        }

        return maxDelta;
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
