// <copyright file="DeviceRateSampleProviderTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using NAudio.Wave;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;
using Sendspin.Windows.Services.Audio;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio;

/// <summary>
/// Covers the one stage of the output chain the SDK deliberately does not own: converting to the
/// device's native mixer rate, so the Windows Audio Engine does not resample a second time.
/// </summary>
/// <remarks>
/// Sync correction itself moved to <see cref="SyncCorrectedSampleSource"/> in SDK PR #246, which
/// carries the rate-toggling and shortfall-concealment guards that used to live here. What stays
/// app-side is the WASAPI-specific conversion, and the seam where the corrected stream feeds it.
/// </remarks>
public class DeviceRateSampleProviderTests
{
    private const int SourceRate = 48000;
    private const int TargetRate = 44100;
    private const int Channels = 2;

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
    /// Stands in for <c>AudioSampleProviderAdapter</c>, which is internal to the services assembly:
    /// wraps an <see cref="IAudioSampleSource"/> as an <see cref="ISampleProvider"/> and reports the
    /// block full, since the corrected source fills every sample but returns only the count that
    /// came from the buffer.
    /// </summary>
    private sealed class SampleSourceProvider : ISampleProvider
    {
        private readonly IAudioSampleSource _source;

        public SampleSourceProvider(IAudioSampleSource source, int sampleRate, int channels)
        {
            _source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            _source.Read(buffer, offset, count);
            return count;
        }
    }

    /// <summary>
    /// A correction provider whose rate the test scripts directly, so the corrected stage can be
    /// driven across the dead-band boundary on demand instead of waiting for real drift.
    /// </summary>
    private sealed class ScriptedCorrectionProvider : ISyncCorrectionProvider
    {
        public SyncCorrectionMode CurrentMode { get; set; } = SyncCorrectionMode.Resampling;

        public int DropEveryNFrames => 0;

        public int InsertEveryNFrames => 0;

        public double TargetPlaybackRate { get; set; } = 1.0;

        public event Action<ISyncCorrectionProvider>? CorrectionChanged;

        public void UpdateFromSyncError(long rawMicroseconds, double smoothedMicroseconds)
        {
        }

        public void Reset() => CorrectionChanged?.Invoke(this);
    }

    /// <summary>
    /// A converged, zero-drift clock: <c>ServerToClientTime</c> is the identity, so a chunk stamped
    /// server-time <c>t</c> is due at local time <c>t</c>.
    /// </summary>
    private sealed class IdentityClock : IClockSynchronizer
    {
        public double OutputDelayMs { get; set; }

        public long ServerToClientTime(long serverTime) => serverTime;

        public long ServerToClientTimeUncompensated(long serverTime) => serverTime;

        public long ClientToServerTime(long clientTime) => clientTime;

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
    /// The re-targeted half of the old <c>RateCorrection_TogglingDuringCompoundConversion</c> guard.
    /// Compound conversion is no longer one resampler doing rate correction and device conversion at
    /// once — correction happens upstream in the SDK, conversion here — so the property worth pinning
    /// moved with it: driving the <b>whole chain</b> (corrected source → adapter → device conversion)
    /// while the correction rate toggles across unity must not manufacture a discontinuity at the
    /// seam. A 101 Hz sine moves at most ~0.0071/sample at 44.1 kHz, so any output step well above
    /// that bound was manufactured. 101 Hz and the 47-callback toggle period are incommensurate, so
    /// the toggle never lands phase-aligned with the signal.
    /// </summary>
    [Fact]
    public void CorrectedStream_TogglingAcrossUnity_ConvertsWithoutClicks()
    {
        const double frequency = 101.0;
        const int outputCount = 882;   // one 10 ms callback at 44.1 kHz stereo
        const int sourceCount = 960;   // one 10 ms callback at 48 kHz stereo
        const int callbacks = 1000;

        var format = new AudioFormat { Codec = "pcm", SampleRate = SourceRate, Channels = Channels, BitDepth = 32 };
        using var buffer = new TimedAudioBuffer(format, new IdentityClock(), bufferCapacityMs: 4000, SyncCorrectionOptions.Default);
        var provider = new ScriptedCorrectionProvider();

        long nowMicros = 0;
        using var corrected = new SyncCorrectedSampleSource(buffer, () => nowMicros, provider);
        var chain = new DeviceRateSampleProvider(
            new SampleSourceProvider(corrected, SourceRate, Channels), TargetRate);

        // Fill the buffer well ahead of the reads and keep it topped up, so nothing the test sees is
        // a starvation artefact rather than a conversion one.
        var sine = new SineSampleProvider(SourceRate, Channels, frequency);
        var chunk = new float[sourceCount];
        long writtenFrames = 0;

        void WriteChunk()
        {
            sine.Read(chunk, 0, sourceCount);
            buffer.Write(chunk, writtenFrames * 1_000_000L / SourceRate);
            writtenFrames += sourceCount / Channels;
        }

        for (var i = 0; i < 200; i++)
        {
            WriteChunk();
        }

        var output = new float[outputCount];
        var previous = new float[Channels];
        var maxDelta = 0.0;

        for (var cb = 0; cb < callbacks; cb++)
        {
            provider.TargetPlaybackRate = (cb / 47) % 2 == 0 ? 1.0 : 1.0004;
            nowMicros = (long)cb * outputCount / Channels * 1_000_000L / TargetRate;

            chain.Read(output, 0, outputCount);
            WriteChunk();

            if (cb >= 5)
            {
                for (var c = 0; c < Channels; c++)
                {
                    var prev = previous[c];
                    for (var i = c; i < outputCount; i += Channels)
                    {
                        maxDelta = Math.Max(maxDelta, Math.Abs(output[i] - prev));
                        prev = output[i];
                    }
                }
            }

            for (var c = 0; c < Channels; c++)
            {
                previous[c] = output[outputCount - Channels + c];
            }
        }

        Assert.Equal(0, chain.SourceEmptyCount);

        var sineSlopeBound = 0.5 * 2 * Math.PI * frequency / TargetRate;
        Assert.True(
            maxDelta < 3 * sineSlopeBound,
            $"the corrected → converted chain manufactured a discontinuity of {maxDelta:F4} (sine slope bound {sineSlopeBound:F4})");
    }

    /// <summary>
    /// Pins that device conversion keeps the WDL low-pass chain ENGAGED. The click test cannot see
    /// this — a low-frequency sine is click-free with the chain removed too. Observable: for
    /// 48 kHz → 44.1 kHz the chain's cutoff sits at ~19.9 kHz (0.90 × Nyquist / ratio), so a 21 kHz
    /// tone emerges near-silent with the chain engaged (~0.005 RMS, about -37 dB) but at ~0.21 RMS
    /// through bare linear interpolation. The 0.05 threshold sits 10x above the engaged level and 4x
    /// below the disengaged level.
    /// </summary>
    [Fact]
    public void DeviceConversion_AttenuatesContentAboveFilterCutoff()
    {
        const double frequency = 21000.0;
        const int count = 882; // one 10 ms callback at 44.1 kHz stereo

        var source = new SineSampleProvider(SourceRate, Channels, frequency);
        var resampler = new DeviceRateSampleProvider(source, TargetRate);

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
            $"21 kHz tone passed device conversion at RMS {rms:F4} - the anti-alias chain is not engaged (engaged ~0.005, bare interpolation ~0.21)");
    }
}
