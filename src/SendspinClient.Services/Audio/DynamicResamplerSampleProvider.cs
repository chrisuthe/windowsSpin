// <copyright file="DynamicResamplerSampleProvider.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using NAudio.Dsp;
using NAudio.Wave;
using Sendspin.SDK.Audio;
namespace SendspinClient.Services.Audio;

/// <summary>
/// An <see cref="ISampleProvider"/> that applies dynamic playback rate adjustment for sync correction.
/// Uses NAudio's WDL resampler to smoothly adjust playback speed without audible artifacts.
/// </summary>
/// <remarks>
/// <para>
/// This component enables imperceptible sync correction by adjusting playback rate (0.96x-1.04x)
/// instead of discrete sample dropping/insertion which can cause audible clicks.
/// </para>
/// <para>
/// The rate is controlled by the <see cref="ISyncCorrectionProvider.TargetPlaybackRate"/> property,
/// which is calculated based on sync error magnitude.
/// </para>
/// </remarks>
public sealed class DynamicResamplerSampleProvider : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _source;
    private readonly ISyncCorrectionProvider? _correctionProvider;
    private readonly WdlResampler _resampler;
    private readonly ILogger? _logger;
    private readonly object _rateLock = new();
    private readonly int _targetSampleRate;

    private double _playbackRate = 1.0;
    private bool _disposed;

    // Last frame actually produced, held across a shortfall instead of writing silence.
    private readonly float[] _lastFrame;

    // Underrun tracking for diagnostics
    private long _sourceEmptyCount;
    private long _resamplerShortCount;
    private long _lastUnderrunLogTicks;
    private const long UnderrunLogIntervalTicks = TimeSpan.TicksPerSecond * 5; // Log at most every 5 seconds

    /// <summary>
    /// Minimum playback rate (4% slower).
    /// </summary>
    public const double MinRate = 0.96;

    /// <summary>
    /// Maximum playback rate (4% faster).
    /// </summary>
    public const double MaxRate = 1.04;

    /// <summary>
    /// Minimum rate change threshold before updating resampler (0.01% = 0.0001).
    /// Prevents excessive SetRates() calls that can disturb filter state.
    /// </summary>
    /// <remarks>
    /// Must be small enough to allow proportional sync correction to work.
    /// At 1ms error, proportional correction is ~0.03%, smoothed to 0.0045%.
    /// Threshold of 0.01% allows these small corrections through.
    /// </remarks>
    private const double RateChangeThreshold = 0.0001;

    /// <inheritdoc/>
    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// Gets the count of times the source returned no samples (buffer empty).
    /// </summary>
    /// <remarks>
    /// High counts indicate the upstream buffer is frequently empty, suggesting
    /// network issues or insufficient buffering.
    /// </remarks>
    public long SourceEmptyCount => Interlocked.Read(ref _sourceEmptyCount);

    /// <summary>
    /// Gets the count of times the resampler produced fewer samples than requested.
    /// </summary>
    /// <remarks>
    /// Non-zero counts after startup indicate the resampler input calculation may
    /// be incorrect or the source is providing fewer samples than expected.
    /// </remarks>
    public long ResamplerShortCount => Interlocked.Read(ref _resamplerShortCount);

    /// <summary>
    /// Gets or sets the current playback rate.
    /// </summary>
    /// <remarks>
    /// <para>Values: 1.0 = normal speed, &gt;1.0 = faster, &lt;1.0 = slower.</para>
    /// <para>Clamped to range <see cref="MinRate"/> to <see cref="MaxRate"/>.</para>
    /// <para>
    /// Rate changes are applied immediately to match JS/Python reference implementations.
    /// The buffer's EMA smoothing on sync error provides sufficient jitter filtering.
    /// </para>
    /// </remarks>
    public double PlaybackRate
    {
        get
        {
            lock (_rateLock)
            {
                return _playbackRate;
            }
        }
        set
        {
            var clamped = Math.Clamp(value, MinRate, MaxRate);
            lock (_rateLock)
            {
                // Apply rate immediately without smoothing - matches JS/Python reference implementations
                // Buffer's EMA on sync error already provides sufficient jitter filtering
                // Rate smoothing was causing ~230ms lag that destabilized the feedback loop
                if (Math.Abs(_playbackRate - clamped) > RateChangeThreshold)
                {
                    _playbackRate = clamped;
                    UpdateResamplerRates();
                }
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicResamplerSampleProvider"/> class.
    /// </summary>
    /// <param name="source">The upstream sample provider to read from.</param>
    /// <param name="correctionProvider">Optional sync correction provider to subscribe to for rate change events.</param>
    /// <param name="targetSampleRate">
    /// Target output sample rate (device native rate). When non-zero and different from source rate,
    /// the resampler performs compound conversion: sample rate conversion + sync correction in one pass.
    /// This avoids double-resampling when Windows Audio Engine would otherwise resample to the mixer rate.
    /// Pass 0 or the source sample rate to perform only sync correction.
    /// </param>
    /// <param name="logger">Optional logger for debugging.</param>
    public DynamicResamplerSampleProvider(
        ISampleProvider source,
        ISyncCorrectionProvider? correctionProvider = null,
        int targetSampleRate = 0,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _correctionProvider = correctionProvider;
        _logger = logger;

        // Determine target sample rate - use source rate if not specified
        _targetSampleRate = targetSampleRate > 0 ? targetSampleRate : source.WaveFormat.SampleRate;

        // Output WaveFormat uses target sample rate (may differ from source for rate conversion)
        if (_targetSampleRate != source.WaveFormat.SampleRate)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_targetSampleRate, source.WaveFormat.Channels);
            _logger?.LogInformation(
                "Resampler configured for {SourceRate}Hz → {TargetRate}Hz (+ sync correction)",
                source.WaveFormat.SampleRate,
                _targetSampleRate);
        }
        else
        {
            WaveFormat = source.WaveFormat;
            _logger?.LogDebug("Resampler configured for sync correction only (no sample rate conversion)");
        }

        _lastFrame = new float[WaveFormat.Channels];

        // Initialize WDL resampler
        // Reduced from 32 to 16 taps - 32-tap was causing audible artifacts during
        // dynamic rate changes. 16-tap provides good quality with faster adaptation
        // and less ringing on transients.
        _resampler = new WdlResampler();
        _resampler.SetMode(true, 16, false); // Interpolating, 16-tap sinc, no filter on output
        _resampler.SetFilterParms(0.90f, 0.60f); // 90% Nyquist, sharper transition (less processing)
        _resampler.SetFeedMode(true); // We're in output-driven mode (request N output samples)
        UpdateResamplerRates();

        // Subscribe to correction provider rate changes if available
        if (_correctionProvider != null)
        {
            _correctionProvider.CorrectionChanged += OnCorrectionChanged;
            _logger?.LogDebug("Subscribed to CorrectionChanged event from correction provider");
        }
        else
        {
            _logger?.LogWarning("No correction provider - rate changes will not be received!");
        }
    }

    /// <inheritdoc/>
    public int Read(float[] buffer, int offset, int count)
    {
        if (_disposed)
        {
            return 0;
        }

        var channels = WaveFormat.Channels;
        var outputFrames = count / channels;

        // Output-driven (feed) mode: ask the resampler how many INPUT frames it needs to
        // produce outputFrames - accounting for its current rate AND its internal sinc-filter
        // history - then read exactly that many straight into the resampler's own buffer.
        // This is NAudio's canonical WdlResamplingSampleProvider usage.
        //
        // ResamplePrepare's count is ratio*out + filter-priming margin - already-buffered input
        // (see NAudio's WdlResamplingSampleProvider). The previous implementation computed the
        // read size itself as count * rate * sourceRate/targetRate, which omits that priming
        // margin, so the filter was under-fed and emitted fewer samples than requested whenever
        // the rate sat off 1.0 (i.e. under continuous drift correction). The shortfall was padded
        // with digital silence and surfaced as audible stutter on revealing external DACs.
        //
        // We intentionally do NOT bypass the resampler at rate 1.0: the WDL filter carries state
        // across calls, and dropping out of the path disrupts it and causes pops.
        var framesNeeded = _resampler.ResamplePrepare(outputFrames, channels, out var inBuffer, out var inBufferOffset);
        var framesRead = _source.Read(inBuffer, inBufferOffset, framesNeeded * channels) / channels;

        if (framesRead == 0)
        {
            // Source genuinely empty (sustained upstream/network stall) - silence is correct here;
            // holding a sample across a long gap would leave an audible stuck DC offset.
            Interlocked.Increment(ref _sourceEmptyCount);
            LogUnderrunIfNeeded("source empty");
            Array.Fill(buffer, 0f, offset, count);
            return count;
        }

        var framesGenerated = _resampler.ResampleOut(buffer, offset, framesRead, outputFrames, channels);
        var samplesGenerated = framesGenerated * channels;

        // Remember the last frame actually produced, for zero-order-hold concealment.
        if (samplesGenerated >= channels)
        {
            Array.Copy(buffer, offset + samplesGenerated - channels, _lastFrame, 0, channels);
        }

        // Output-driven feeding removes the priming deficit, but the WDL filter can still come up
        // a frame or two short on a rate change (the input/output accounting momentarily shifts
        // when SetRates changes the ratio). Conceal that residual by holding the last produced
        // frame (zero-order hold) rather than writing silence: a silence gap is a step
        // discontinuity - a broadband click - while a held sample keeps the waveform continuous
        // and is inaudible at 1-2 frames. This is standard packet-loss concealment. If a callback
        // produced nothing at all, _lastFrame carries the previous callback's frame; it is zero
        // only before the very first frame of the stream.
        if (samplesGenerated < count)
        {
            Interlocked.Increment(ref _resamplerShortCount);
            LogUnderrunIfNeeded($"resampler short: got {samplesGenerated}, needed {count}");

            for (var i = samplesGenerated; i < count; i++)
            {
                buffer[offset + i] = _lastFrame[i % channels];
            }
        }

        return count;
    }

    /// <summary>
    /// Applies soft clipping to audio samples to prevent hard clipping distortion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses a cubic soft clipper that only engages above the threshold (0.95).
    /// Below threshold, audio passes through unchanged. Above threshold, samples
    /// are smoothly compressed toward ±1.0 using a cubic polynomial curve.
    /// </para>
    /// <para>
    /// This approach preserves audio quality in the normal range while preventing
    /// the harsh distortion that occurs when samples exceed ±1.0 at the DAC.
    /// </para>
    /// </remarks>
    private static void ApplySoftClipping(float[] buffer, int offset, int count)
    {
        // Threshold raised from 0.95 to 0.99 - the lower threshold was engaging too often
        // on normal music peaks, causing audible artifacts. 0.99 only engages on truly
        // clipping content while still preventing hard clipping at the DAC.
        const float threshold = 0.99f;
        const float ceiling = 1.0f;

        for (var i = 0; i < count; i++)
        {
            var sample = buffer[offset + i];
            var absSample = Math.Abs(sample);

            if (absSample > threshold)
            {
                // Cubic soft clip: smoothly compress samples between threshold and ceiling
                // Maps [threshold, infinity) to [threshold, ceiling)
                var excess = absSample - threshold;
                var range = ceiling - threshold;

                // Soft knee using cubic curve: y = threshold + range * (1 - 1/(1 + x/range)^2)
                // This provides smooth compression that asymptotically approaches ceiling
                var normalized = excess / range;
                var compressed = threshold + range * (1.0f - 1.0f / ((1.0f + normalized) * (1.0f + normalized)));

                buffer[offset + i] = sample > 0 ? compressed : -compressed;
            }
        }
    }

    /// <summary>
    /// Updates the resampler's rate settings for compound conversion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resampler combines sample rate conversion and sync correction in a single pass:
    /// - Sample rate conversion: source rate → target rate (e.g., 48kHz → 44.1kHz)
    /// - Sync correction: adjust output rate by playback rate factor
    /// </para>
    /// <para>
    /// Example: Source 48kHz, target 44.1kHz, playback rate 1.02x (speed up 2%)
    /// - Without sync correction: SetRates(48000, 44100)
    /// - With sync correction: SetRates(48000, 44100/1.02) = SetRates(48000, 43235)
    /// </para>
    /// </remarks>
    private void UpdateResamplerRates()
    {
        // Input rate is the source sample rate (server stream format)
        var sourceRate = (double)_source.WaveFormat.SampleRate;

        // Output rate combines: target device rate / playback rate
        // To speed up (playbackRate > 1): divide makes outRate smaller → resampler produces samples faster
        // To slow down (playbackRate < 1): divide makes outRate larger → resampler produces samples slower
        var outRate = _targetSampleRate / _playbackRate;

        _resampler.SetRates(sourceRate, outRate);
    }

    /// <summary>
    /// Handles correction change events from the sync correction provider.
    /// </summary>
    private void OnCorrectionChanged(ISyncCorrectionProvider provider)
    {
        PlaybackRate = provider.TargetPlaybackRate;
    }

    /// <summary>
    /// Logs an underrun event with rate limiting to avoid flooding logs.
    /// </summary>
    /// <param name="reason">Description of the underrun cause.</param>
    private void LogUnderrunIfNeeded(string reason)
    {
        var now = DateTime.UtcNow.Ticks;
        var lastLog = Interlocked.Read(ref _lastUnderrunLogTicks);

        // Only log if enough time has passed since last log
        if (now - lastLog > UnderrunLogIntervalTicks)
        {
            // Try to update the last log time (may fail if another thread beat us)
            if (Interlocked.CompareExchange(ref _lastUnderrunLogTicks, now, lastLog) == lastLog)
            {
                _logger?.LogWarning(
                    "Audio underrun detected ({Reason}). Total: sourceEmpty={SourceEmpty}, resamplerShort={ResamplerShort}",
                    reason,
                    Interlocked.Read(ref _sourceEmptyCount),
                    Interlocked.Read(ref _resamplerShortCount));
            }
        }
    }

    /// <summary>
    /// Releases resources and unsubscribes from events.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_correctionProvider != null)
        {
            _correctionProvider.CorrectionChanged -= OnCorrectionChanged;
        }
    }
}
