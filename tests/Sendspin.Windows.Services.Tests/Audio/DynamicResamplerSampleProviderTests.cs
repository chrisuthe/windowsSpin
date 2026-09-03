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
    /// A source of a constant DC value. A correct resampler passes DC through unchanged, so the
    /// only way an output sample can collapse toward zero is an injected silence pad - which makes
    /// silence-gap concealment directly testable. <see cref="FramesBudget"/> optionally caps how
    /// much it will hand out, letting a test starve the resampler mid-callback on demand.
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

        /// <summary>Gets or sets the frames this source may still return; -1 means unlimited.</summary>
        public int FramesBudget { get; set; } = -1;

        public int Read(float[] buffer, int offset, int count)
        {
            if (FramesBudget >= 0)
            {
                count = Math.Min(count, FramesBudget * WaveFormat.Channels);
                FramesBudget -= count / WaveFormat.Channels;
            }

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
    /// Wraps a source and tallies the input frames actually pulled through it, so a test can
    /// compare frames consumed against frames produced and recover the resampler's effective
    /// pull ratio.
    /// </summary>
    private sealed class CountingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _inner;

        public CountingSampleProvider(ISampleProvider inner)
        {
            _inner = inner;
        }

        public WaveFormat WaveFormat => _inner.WaveFormat;

        public long FramesRead { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            FramesRead += read / WaveFormat.Channels;
            return read;
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
    /// engaged (history stays warm), and rate toggling is click-free. Pins that path so an
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
    /// Pins the other half of the ratio-can-cross-unity branch: compound conversion must
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
        const int frames = count / channels;
        var buffer = new float[count];

        // Warm up: the WDL filter's output ramps from 0 to the DC level as its history fills.
        source.FramesBudget = -1;
        for (var i = 0; i < 5; i++)
        {
            resampler.Read(buffer, 0, count);
        }

        // Starve the source part-way through every third callback so the drain loop runs out of
        // input and has to conceal the tail. This is the shortfall the render thread actually
        // sees - a mid-callback upstream stall. (It used to be provoked instead by nudging the
        // playback rate: under the old input-driven scheduling WDL's fractional residue was
        // discarded rather than carried, so ordinary drift correction left callbacks 1-2 frames
        // short. Output-driven scheduling nets that residue off the next request, so the residue
        // no longer starves anything and the rate sweep can no longer reach this path.)
        //
        // Silence pads are bit-exact 0.0f (Array.Fill); a DC input through the resampler never
        // produces exact zero, and a held DC frame is ~0.5. So an exact-zero output sample is the
        // unambiguous signature of a leaked silence pad.
        var silenceSamples = 0;
        for (var i = 0; i < 300; i++)
        {
            // 80% of a callback's worth: enough that the callback produces real content first,
            // so this exercises tail concealment rather than the all-silence empty-source path.
            source.FramesBudget = i % 3 == 0 ? frames * 4 / 5 : -1;
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

    /// <summary>
    /// Regression test for issue #73's rate-domain over-pull (the root cause of #63's runaway).
    /// The resampler must consume input frames at exactly <c>sourceRate / targetRate</c> per output
    /// frame. Any systematic mismatch is a RATE error, not an offset: it integrates, so the SDK's
    /// <c>samplesReadTime</c> walks away from wall clock without bound. The continuous corrector is
    /// capped at 0.5% by spec, so an error of this shape can never be closed downstream - it has to
    /// be right here.
    /// </summary>
    /// <remarks>
    /// Before the fix the provider ran WDL in feed (input-driven) mode while passing OUTPUT frame
    /// counts, so upsampling saturated the pull ratio at exactly 1.0: 48 kHz into 192 kHz pulled
    /// 4x the input it needed (+300%), and 44.1 kHz into 48 kHz +8.8%. Downsampling and identity
    /// were unaffected, which is why matched-rate machines looked healthy.
    /// </remarks>
    [Theory]
    [InlineData(48000, 192000)] // the issue's repro: 48 kHz stream into a 192 kHz DAC
    [InlineData(44100, 48000)]  // a milder upsampling case
    [InlineData(48000, 44100)]  // downsampling
    [InlineData(48000, 48000)]  // identity - must not be repaired by a special case
    public void PullRatio_MatchesNominalRatio_AcrossConversions(int sourceRate, int targetRate)
    {
        AssertPullRatio(sourceRate, targetRate, rateForCallback: _ => 1.0);
    }

    /// <summary>
    /// The effective pull rate must track <c>sourceRate / (targetRate / playbackRate)</c>, so a
    /// playback rate parked off 1.0 shifts the pull ratio by exactly that factor. A fix that only
    /// corrected the nominal ratio would drift again the moment the sync corrector engaged.
    /// </summary>
    [Theory]
    [InlineData(48000, 192000, 1.03)]
    [InlineData(48000, 192000, 0.97)]
    [InlineData(48000, 44100, 1.03)]
    public void PullRatio_TracksNominalRatio_WithPlaybackRateParkedOffUnity(int sourceRate, int targetRate, double playbackRate)
    {
        AssertPullRatio(sourceRate, targetRate, rateForCallback: _ => playbackRate, playbackRateFactor: playbackRate);
    }

    /// <summary>
    /// The same accounting must hold while the rate moves, which is the real steady state: the
    /// corrector nudges the rate every callback. Sweeping symmetrically about 1.0 leaves the
    /// time-averaged factor at 1.0, so the pull ratio must still land on the nominal ratio.
    /// </summary>
    [Fact]
    public void PullRatio_TracksNominalRatio_WithPlaybackRateToggling()
    {
        // A full number of complete cycles so the sweep averages to exactly 1.0 over the run.
        const int period = 40;
        AssertPullRatio(
            48000,
            192000,
            rateForCallback: cb => 1.0 + (0.03 * Math.Sin(2 * Math.PI * (cb % period) / period)),
            callbacks: 40 * period);
    }

    /// <summary>
    /// Drives the provider for a sustained run and asserts that input frames pulled per output
    /// frame produced matches <c>sourceRate / (targetRate / playbackRateFactor)</c> within 0.1%.
    /// The first callbacks are excluded so WDL's one-off filter priming - a fixed number of frames,
    /// not a rate error - cannot mask or manufacture a drift.
    /// </summary>
    private static void AssertPullRatio(
        int sourceRate,
        int targetRate,
        Func<int, double> rateForCallback,
        double playbackRateFactor = 1.0,
        int callbacks = 1000)
    {
        const int channels = 2;
        const int warmupCallbacks = 50;

        var counting = new CountingSampleProvider(new SineSampleProvider(sourceRate, channels, 101.0));
        using var resampler = new DynamicResamplerSampleProvider(counting, correctionProvider: null, targetSampleRate: targetRate);

        var framesPerCallback = targetRate / 100; // one 10 ms callback at the target rate
        var buffer = new float[framesPerCallback * channels];

        for (var cb = 0; cb < warmupCallbacks; cb++)
        {
            resampler.PlaybackRate = rateForCallback(cb);
            resampler.Read(buffer, 0, buffer.Length);
        }

        var framesReadAtStart = counting.FramesRead;
        long framesProduced = 0;

        for (var cb = 0; cb < callbacks; cb++)
        {
            resampler.PlaybackRate = rateForCallback(warmupCallbacks + cb);
            resampler.Read(buffer, 0, buffer.Length);
            framesProduced += framesPerCallback;
        }

        var framesPulled = counting.FramesRead - framesReadAtStart;
        var actualRatio = (double)framesPulled / framesProduced;
        var nominalRatio = sourceRate / (targetRate / playbackRateFactor);
        var errorPercent = ((actualRatio / nominalRatio) - 1) * 100;

        Assert.True(
            Math.Abs(errorPercent) < 0.1,
            $"{sourceRate}Hz -> {targetRate}Hz at rate {playbackRateFactor}: pulled {actualRatio:F6} input frames per output frame, " +
            $"nominal {nominalRatio:F6} - a {errorPercent:F3}% rate error. This integrates; the 0.5% corrector cap cannot close it.");
    }
}
