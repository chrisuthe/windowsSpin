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

    private double _playbackRate = 1.0;
    private float[] _sourceBuffer;
    private bool _disposed;

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
                if (Math.Abs(_playbackRate - clamped) > 0.0001)
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
    /// <param name="buffer">Optional buffer to subscribe to for rate change events.</param>
    /// <param name="logger">Optional logger for debugging.</param>
    public DynamicResamplerSampleProvider(ISampleProvider source, ITimedAudioBuffer? buffer = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _buffer = buffer;
        _logger = logger;
        WaveFormat = source.WaveFormat;

        // Initialize WDL resampler
        _resampler = new WdlResampler();
        _resampler.SetMode(true, 2, false); // Interpolating, 2-tap sinc filter, linear phase
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(true); // We're in output-driven mode (request N output samples)
        UpdateResamplerRates();

        // Allocate source buffer (enough for typical reads with margin for rate adjustment)
        // At 1.04x rate, we need 4% more input samples
        _sourceBuffer = new float[8192];

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

        // Calculate how many source samples we need for the requested output samples
        // At rate > 1.0 (speeding up), we need MORE input samples
        // At rate < 1.0 (slowing down), we need FEWER input samples
        var inputSamplesNeeded = (int)Math.Ceiling(count * currentRate);

        // Ensure source buffer is large enough
        if (_sourceBuffer.Length < inputSamplesNeeded)
        {
            _sourceBuffer = new float[inputSamplesNeeded * 2];
        }

        // Read from source
        var inputRead = _source.Read(_sourceBuffer, 0, inputSamplesNeeded);

        if (inputRead == 0)
        {
            // Source is empty - fill with silence
            Array.Fill(buffer, 0f, offset, count);
            return count;
        }

        // Resample
        var outputGenerated = Resample(_sourceBuffer, inputRead, buffer, offset, count);

        // If we didn't generate enough output (shouldn't happen often), pad with silence
        if (outputGenerated < count)
        {
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
    /// Updates the resampler's rate settings.
    /// </summary>
    private void UpdateResamplerRates()
    {
        // SetRates(inRate, outRate): to speed up playback, output rate < input rate
        // e.g., at 1.02x rate, we consume input faster: SetRates(48000, 48000/1.02)
        var inRate = WaveFormat.SampleRate;
        var outRate = (int)(WaveFormat.SampleRate / _playbackRate);
        _resampler.SetRates(inRate, outRate);
    }

    /// <summary>
    /// Handles rate change events from the audio buffer.
    /// </summary>
    private void OnTargetPlaybackRateChanged(double newRate)
    {
        PlaybackRate = newRate;
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
