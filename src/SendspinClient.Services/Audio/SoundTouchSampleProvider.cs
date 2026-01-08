// <copyright file="SoundTouchSampleProvider.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Sendspin.SDK.Audio;
using SoundTouch;
using SendspinClient.Services.Diagnostics;

namespace SendspinClient.Services.Audio;

/// <summary>
/// An <see cref="ISampleProvider"/> that applies dynamic playback rate adjustment for sync correction
/// using the SoundTouch library. This is an alternative to WDL resampler that may produce fewer artifacts.
/// </summary>
/// <remarks>
/// <para>
/// SoundTouch is a time-stretch/pitch-shift library that uses WSOLA (Waveform Similarity Overlap-Add)
/// algorithm. When used for rate changes (not tempo or pitch independently), it provides smooth
/// playback speed adjustment without the sinc filter artifacts that can occur with WDL resampler.
/// </para>
/// <para>
/// The rate is controlled by the <see cref="ISyncCorrectionProvider.TargetPlaybackRate"/> property,
/// which is calculated based on sync error magnitude.
/// </para>
/// </remarks>
public sealed class SoundTouchSampleProvider : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _source;
    private readonly ISyncCorrectionProvider? _correctionProvider;
    private readonly SoundTouchProcessor _processor;
    private readonly ILogger? _logger;
    private readonly IDiagnosticAudioRecorder? _diagnosticRecorder;
    private readonly object _rateLock = new();
    private readonly int _channels;

    private double _playbackRate = 1.0;
    private double _targetRate = 1.0;
    private float[] _sourceBuffer;
    private float[] _processorOutputBuffer;
    private bool _disposed;

    /// <summary>
    /// Smoothing factor for rate changes (0.0 to 1.0).
    /// Lower values = smoother/slower transitions, higher = faster response.
    /// At 0.10, we move 10% toward target each update.
    /// </summary>
    private const double RateSmoothingFactor = 0.10;

    /// <summary>
    /// Minimum rate change threshold before updating processor (0.01% = 0.0001).
    /// Prevents excessive rate changes that can disturb WSOLA processing.
    /// </summary>
    private const double RateChangeThreshold = 0.0001;

    // Underrun tracking for diagnostics
    private long _sourceEmptyCount;
    private long _processorShortCount;
    private long _lastUnderrunLogTicks;
    private const long UnderrunLogIntervalTicks = TimeSpan.TicksPerSecond * 5;

    /// <summary>
    /// Minimum playback rate (4% slower).
    /// </summary>
    public const double MinRate = 0.96;

    /// <summary>
    /// Maximum playback rate (4% faster).
    /// </summary>
    public const double MaxRate = 1.04;

    /// <inheritdoc/>
    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// Gets the count of times the source returned no samples (buffer empty).
    /// </summary>
    public long SourceEmptyCount => Interlocked.Read(ref _sourceEmptyCount);

    /// <summary>
    /// Gets the count of times the processor produced fewer samples than requested.
    /// </summary>
    public long ProcessorShortCount => Interlocked.Read(ref _processorShortCount);

    /// <summary>
    /// Gets or sets the current playback rate.
    /// </summary>
    /// <remarks>
    /// <para>Values: 1.0 = normal speed, &gt;1.0 = faster, &lt;1.0 = slower.</para>
    /// <para>Clamped to range <see cref="MinRate"/> to <see cref="MaxRate"/>.</para>
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
                // This prevents rapid rate changes from disturbing WSOLA processing
                var smoothedRate = _playbackRate + (_targetRate - _playbackRate) * RateSmoothingFactor;

                // Only update processor if change exceeds threshold
                if (Math.Abs(_playbackRate - smoothedRate) > RateChangeThreshold)
                {
                    _playbackRate = smoothedRate;
                    // SoundTouch Rate: 1.0 = normal, >1.0 = faster playback
                    _processor.Rate = (float)_playbackRate;
                }
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SoundTouchSampleProvider"/> class.
    /// </summary>
    /// <param name="source">The upstream sample provider to read from.</param>
    /// <param name="correctionProvider">Optional sync correction provider to subscribe to for rate change events.</param>
    /// <param name="logger">Optional logger for debugging.</param>
    /// <param name="diagnosticRecorder">Optional diagnostic recorder for audio capture.</param>
    public SoundTouchSampleProvider(
        ISampleProvider source,
        ISyncCorrectionProvider? correctionProvider = null,
        ILogger? logger = null,
        IDiagnosticAudioRecorder? diagnosticRecorder = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _correctionProvider = correctionProvider;
        _logger = logger;
        _diagnosticRecorder = diagnosticRecorder;
        _channels = source.WaveFormat.Channels;

        // Output format matches input (SoundTouch doesn't change sample rate, just playback rate)
        WaveFormat = source.WaveFormat;

        // Initialize SoundTouch processor
        _processor = new SoundTouchProcessor();
        _processor.SampleRate = source.WaveFormat.SampleRate;
        _processor.Channels = _channels;

        // Use settings optimized for rate changes (not tempo/pitch independently)
        // These settings minimize artifacts during dynamic rate adjustment
        _processor.Rate = 1.0;
        _processor.Tempo = 1.0;
        _processor.Pitch = 1.0;

        // Use automatic sequence length for best quality
        _processor.SetSetting(SettingId.UseQuickSeek, 0); // Disable quick seek for better quality
        _processor.SetSetting(SettingId.UseAntiAliasFilter, 1); // Enable anti-alias filter

        _logger?.LogInformation(
            "SoundTouch processor initialized: {SampleRate}Hz, {Channels} channels",
            source.WaveFormat.SampleRate,
            _channels);

        // Pre-allocate buffers
        // SoundTouch may need more input samples than output due to its windowed processing
        const int MaxExpectedOutputRequest = 16384;
        const double BufferMargin = 1.5; // Margin for rate + SoundTouch's internal buffering
        _sourceBuffer = new float[(int)(MaxExpectedOutputRequest * MaxRate * BufferMargin)];
        _processorOutputBuffer = new float[MaxExpectedOutputRequest * 2];

        // Subscribe to correction provider rate changes if available
        if (_correctionProvider != null)
        {
            _correctionProvider.CorrectionChanged += OnCorrectionChanged;
            _logger?.LogDebug("SoundTouch: Subscribed to CorrectionChanged event");
        }
        else
        {
            _logger?.LogWarning("SoundTouch: No correction provider - rate changes will not be received!");
        }
    }

    /// <inheritdoc/>
    public int Read(float[] buffer, int offset, int count)
    {
        if (_disposed)
        {
            return 0;
        }

        var framesRequested = count / _channels;
        var totalSamplesOutput = 0;

        // Keep feeding input and retrieving output until we have enough
        while (totalSamplesOutput < count)
        {
            // First, try to get any samples already processed by SoundTouch
            var framesAvailable = _processor.AvailableSamples;
            if (framesAvailable > 0)
            {
                var framesToRead = Math.Min(framesAvailable, (count - totalSamplesOutput) / _channels);
                var framesReceived = _processor.ReceiveSamples(_processorOutputBuffer, framesToRead);
                var samplesRead = framesReceived * _channels;

                if (samplesRead > 0)
                {
                    // Copy samples manually to avoid Array.Copy type mismatch issues
                    for (int i = 0; i < samplesRead; i++)
                    {
                        buffer[offset + totalSamplesOutput + i] = _processorOutputBuffer[i];
                    }

                    totalSamplesOutput += samplesRead;
                }

                continue;
            }

            // Need to feed more input to SoundTouch
            double currentRate;
            lock (_rateLock)
            {
                currentRate = _playbackRate;
            }

            // Calculate input needed accounting for rate adjustment
            // Use 1.1x margin (reduced from 1.5x to prevent sync error spikes from over-reading)
            var outputFramesNeeded = (count - totalSamplesOutput) / _channels;
            var inputSamplesNeeded = (int)Math.Ceiling(outputFramesNeeded * currentRate * _channels * 1.1);
            inputSamplesNeeded = Math.Min(inputSamplesNeeded, _sourceBuffer.Length);

            // Read from source
            var inputRead = _source.Read(_sourceBuffer, 0, inputSamplesNeeded);

            if (inputRead == 0)
            {
                // Source is empty
                if (totalSamplesOutput == 0)
                {
                    Interlocked.Increment(ref _sourceEmptyCount);
                    LogUnderrunIfNeeded("source empty");
                    Array.Fill(buffer, 0f, offset, count);
                    return count;
                }

                // Flush any remaining samples from processor
                _processor.Flush();
                framesAvailable = _processor.AvailableSamples;
                if (framesAvailable > 0)
                {
                    var framesToRead = Math.Min(framesAvailable, (count - totalSamplesOutput) / _channels);
                    var framesReceived = _processor.ReceiveSamples(_processorOutputBuffer, framesToRead);
                    var samplesRead = framesReceived * _channels;
                    if (samplesRead > 0)
                    {
                        // Copy samples manually to avoid Array.Copy type mismatch issues
                        for (int i = 0; i < samplesRead; i++)
                        {
                            buffer[offset + totalSamplesOutput + i] = _processorOutputBuffer[i];
                        }

                        totalSamplesOutput += samplesRead;
                    }
                }

                break;
            }

            // Feed samples to SoundTouch (expects samples, not frames)
            var framesToPut = inputRead / _channels;
            _processor.PutSamples(_sourceBuffer, framesToPut);
        }

        // If we didn't generate enough output, pad with silence
        if (totalSamplesOutput < count)
        {
            Interlocked.Increment(ref _processorShortCount);
            LogUnderrunIfNeeded($"processor short: got {totalSamplesOutput}, needed {count}");
            Array.Fill(buffer, 0f, offset + totalSamplesOutput, count - totalSamplesOutput);
        }

        // Capture audio for diagnostic recording if enabled
        _diagnosticRecorder?.CaptureIfEnabled(buffer.AsSpan(offset, count));

        return count;
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
    private void LogUnderrunIfNeeded(string reason)
    {
        var now = DateTime.UtcNow.Ticks;
        var lastLog = Interlocked.Read(ref _lastUnderrunLogTicks);

        if (now - lastLog > UnderrunLogIntervalTicks)
        {
            if (Interlocked.CompareExchange(ref _lastUnderrunLogTicks, now, lastLog) == lastLog)
            {
                _logger?.LogWarning(
                    "SoundTouch underrun ({Reason}). Total: sourceEmpty={SourceEmpty}, processorShort={ProcessorShort}",
                    reason,
                    Interlocked.Read(ref _sourceEmptyCount),
                    Interlocked.Read(ref _processorShortCount));
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

        // SoundTouchProcessor doesn't implement IDisposable, but we should clear it
        _processor.Clear();
    }
}
