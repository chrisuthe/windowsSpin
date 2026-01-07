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
/// The rate is controlled by the <see cref="ITimedAudioBuffer.TargetPlaybackRate"/> property,
/// which is calculated based on sync error magnitude.
/// </para>
/// </remarks>
public sealed class DynamicResamplerSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly ITimedAudioBuffer? _buffer;
    private readonly WdlResampler _resampler;
    private readonly ILogger? _logger;
    private readonly object _rateLock = new();
    private readonly int _targetSampleRate;

    private double _playbackRate = 1.0;
    private double _targetRate = 1.0;
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
    /// Smoothing factor for rate changes (0.0 to 1.0).
    /// Lower values = smoother/slower transitions, higher = faster response.
    /// At 0.15, we move 15% toward target each update (~10ms), reaching 90% in ~150ms.
    /// </summary>
    /// <remarks>
    /// Must be high enough that smoothed changes exceed <see cref="RateChangeThreshold"/>.
    /// For 0.5% target rate change: smoothed = 0.005 * 0.15 = 0.00075 > 0.0001 threshold.
    /// </remarks>
    private const double RateSmoothingFactor = 0.15;

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
    /// Rate changes are exponentially smoothed to prevent audio artifacts from rapid
    /// resampler ratio changes. The actual rate gradually moves toward the target.
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
                // Store the target rate
                _targetRate = clamped;

                // Apply exponential smoothing: move partway toward target
                // This prevents rapid rate changes from disturbing resampler filter state
                var smoothedRate = _playbackRate + (_targetRate - _playbackRate) * RateSmoothingFactor;

                // Only update resampler if change exceeds threshold (reduces SetRates calls)
                if (Math.Abs(_playbackRate - smoothedRate) > RateChangeThreshold)
                {
                    _playbackRate = smoothedRate;
                    UpdateResamplerRates();
                }
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicResamplerSampleProvider"/> class.
    /// </summary>
    /// <param name="source">The upstream sample provider to read from.</param>
    /// <param name="buffer">Optional buffer to subscribe to for rate change events.</param>
    /// <param name="targetSampleRate">
    /// Target output sample rate (device native rate). When non-zero and different from source rate,
    /// the resampler performs compound conversion: sample rate conversion + sync correction in one pass.
    /// This avoids double-resampling when Windows Audio Engine would otherwise resample to the mixer rate.
    /// Pass 0 or the source sample rate to perform only sync correction.
    /// </param>
    /// <param name="logger">Optional logger for debugging.</param>
    public DynamicResamplerSampleProvider(
        ISampleProvider source,
        ITimedAudioBuffer? buffer = null,
        int targetSampleRate = 0,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _buffer = buffer;
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

        // Initialize WDL resampler
        _resampler = new WdlResampler();
        _resampler.SetMode(true, 16, true); // Interpolating, 16-tap sinc filter for high quality
        _resampler.SetFilterParms();
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

        // Subscribe to buffer rate changes if available
        if (_buffer != null)
        {
            _buffer.TargetPlaybackRateChanged += OnTargetPlaybackRateChanged;
            _logger?.LogDebug("Subscribed to TargetPlaybackRateChanged event from buffer");
        }
        else
        {
            _logger?.LogWarning("No buffer provided - rate changes will not be received!");
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

        return framesGenerated * channels;
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
    /// Handles rate change events from the audio buffer.
    /// </summary>
    private void OnTargetPlaybackRateChanged(double newRate)
    {
        PlaybackRate = newRate;
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

        if (_buffer != null)
        {
            _buffer.TargetPlaybackRateChanged -= OnTargetPlaybackRateChanged;
        }
    }
}
