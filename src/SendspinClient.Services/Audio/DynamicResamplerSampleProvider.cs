// <copyright file="DynamicResamplerSampleProvider.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using NAudio.Dsp;
using NAudio.Wave;
using Sendspin.SDK.Audio;
using SendspinClient.Services.Diagnostics;

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
    private readonly IDiagnosticAudioRecorder? _diagnosticRecorder;
    private readonly object _rateLock = new();
    private readonly int _targetSampleRate;

    private double _playbackRate = 1.0;
    private float[] _sourceBuffer;
    private bool _disposed;

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
    /// <param name="diagnosticRecorder">Optional diagnostic recorder for audio capture.</param>
    public DynamicResamplerSampleProvider(
        ISampleProvider source,
        ISyncCorrectionProvider? correctionProvider = null,
        int targetSampleRate = 0,
        ILogger? logger = null,
        IDiagnosticAudioRecorder? diagnosticRecorder = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _correctionProvider = correctionProvider;
        _logger = logger;
        _diagnosticRecorder = diagnosticRecorder;

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

        // Initialize WDL resampler
        // Reduced from 32 to 16 taps - 32-tap was causing audible artifacts during
        // dynamic rate changes. 16-tap provides good quality with faster adaptation
        // and less ringing on transients.
        _resampler = new WdlResampler();
        _resampler.SetMode(true, 16, false); // Interpolating, 16-tap sinc, no filter on output
        _resampler.SetFilterParms(0.90f, 0.60f); // 90% Nyquist, sharper transition (less processing)
        _resampler.SetFeedMode(true); // We're in output-driven mode (request N output samples)
        UpdateResamplerRates();

        // Pre-allocate source buffer sized for worst-case to avoid audio thread allocations.
        // Must account for:
        // - Max WASAPI buffer request (~16384 samples for 100ms+ latency at 48kHz stereo)
        // - Max playback rate (1.04x)
        // - Sample rate conversion ratio (e.g., 96kHz→48kHz = 2.0x)
        //
        // Formula: maxOutputRequest * MaxRate * sampleRateRatio * safetyMargin
        const int MaxExpectedOutputRequest = 16384;
        const double SafetyMargin = 1.2; // 20% extra headroom
        var sampleRateRatio = (double)source.WaveFormat.SampleRate / _targetSampleRate;
        var bufferSize = (int)(MaxExpectedOutputRequest * MaxRate * sampleRateRatio * SafetyMargin);
        _sourceBuffer = new float[bufferSize];

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

        double currentRate;
        lock (_rateLock)
        {
            currentRate = _playbackRate;
        }

        // NOTE: We intentionally do NOT bypass the resampler even at rate 1.0.
        // Bypassing causes audible pops when transitioning in/out of resampling mode
        // because the WDL resampler has internal filter state that gets disrupted.
        // At rate 1.0, the resampler acts as a passthrough but maintains consistent state.

        // Calculate how many source samples we need for the requested output samples.
        // Must account for BOTH sample rate conversion AND sync correction:
        // - Sample rate ratio: sourceRate / targetRate (e.g., 48k/44.1k = 1.088)
        // - Playback rate: currentRate (e.g., 1.02 for 2% speedup)
        //
        // Example: 48kHz source, 44.1kHz target, 1.02x playback, 2048 output samples:
        // inputNeeded = 2048 * 1.02 * (48000/44100) = 2048 * 1.02 * 1.088 = 2274
        //
        // Without this, downsampling (source > target) would starve the resampler,
        // causing it to produce fewer output samples and resulting in silence gaps.
        var sampleRateRatio = (double)_source.WaveFormat.SampleRate / _targetSampleRate;
        var inputSamplesNeeded = (int)Math.Ceiling(count * currentRate * sampleRateRatio);

        // Ensure source buffer is large enough.
        // This should rarely happen with proper pre-allocation, but handle it as a safety net.
        if (_sourceBuffer.Length < inputSamplesNeeded)
        {
            _logger?.LogWarning(
                "Audio thread buffer reallocation triggered: needed {Needed}, had {Had}. " +
                "Consider increasing MaxExpectedOutputRequest.",
                inputSamplesNeeded,
                _sourceBuffer.Length);
            _sourceBuffer = new float[inputSamplesNeeded * 2];
        }

        // Read from source
        var inputRead = _source.Read(_sourceBuffer, 0, inputSamplesNeeded);

        if (inputRead == 0)
        {
            // Source is empty - fill with silence and track underrun
            Interlocked.Increment(ref _sourceEmptyCount);
            LogUnderrunIfNeeded("source empty");
            Array.Fill(buffer, 0f, offset, count);
            return count;
        }

        // Resample
        var outputGenerated = Resample(_sourceBuffer, inputRead, buffer, offset, count);

        // Capture audio for diagnostic recording if enabled
        // Zero overhead when disabled - null check is branch-predicted away
        _diagnosticRecorder?.CaptureIfEnabled(buffer.AsSpan(offset, outputGenerated));

        // If we didn't generate enough output, pad with silence and track underrun
        if (outputGenerated < count)
        {
            Interlocked.Increment(ref _resamplerShortCount);
            LogUnderrunIfNeeded($"resampler short: got {outputGenerated}, needed {count}");
            Array.Fill(buffer, 0f, offset + outputGenerated, count - outputGenerated);
        }

        return count;
    }

    /// <summary>
    /// Performs the actual resampling using WDL resampler.
    /// </summary>
    private int Resample(float[] input, int inputCount, float[] output, int outputOffset, int outputCount)
    {
        var channels = WaveFormat.Channels;
        var inputFrames = inputCount / channels;
        var outputFrames = outputCount / channels;

        // Get resampler's input buffer - includes offset parameter for where to write
        var framesNeeded = _resampler.ResamplePrepare(outputFrames, channels, out var inBuffer, out var inBufferOffset);

        // Copy our input to resampler's buffer at the correct offset
        var framesToCopy = Math.Min(inputFrames, framesNeeded);
        var samplesToCopy = framesToCopy * channels;
        for (var i = 0; i < samplesToCopy; i++)
        {
            inBuffer[inBufferOffset + i] = input[i];
        }

        // Process through resampler directly into output buffer
        var framesGenerated = _resampler.ResampleOut(output, outputOffset, framesToCopy, outputFrames, channels);

        var samplesGenerated = framesGenerated * channels;

        // Soft clipping disabled - was causing audible "overdrive" artifacts even on normal audio.
        // The WDL resampler's output is typically within ±1.0 for well-mastered content.
        // Any occasional overshoots from sinc filter ringing will be handled by the DAC's
        // built-in clipping, which is less audible than our soft clipper engaging frequently.
        // ApplySoftClipping(output, outputOffset, samplesGenerated);

        return samplesGenerated;
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
