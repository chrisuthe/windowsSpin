// <copyright file="SyncCorrectedSampleSource.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Buffers;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace SendspinClient.Services.Audio;

/// <summary>
/// Bridges <see cref="ITimedAudioBuffer"/> to <see cref="IAudioSampleSource"/> with external sync correction.
/// </summary>
/// <remarks>
/// <para>
/// This class reads raw samples from the buffer (no internal correction) and applies
/// drop/insert corrections based on an <see cref="ISyncCorrectionProvider"/>.
/// </para>
/// <para>
/// The drop/insert algorithm mirrors the Python CLI approach:
/// - Drop: Read two frames, output only the last one (skip one input frame)
/// - Insert: Output the last frame again without reading from buffer
/// This maintains audio continuity by always using recently-played samples.
/// </para>
/// </remarks>
public sealed class SyncCorrectedSampleSource : IAudioSampleSource, IDisposable
{
    private readonly ITimedAudioBuffer _buffer;
    private readonly ISyncCorrectionProvider _correctionProvider;
    private readonly Func<long> _getCurrentTimeMicroseconds;
    private readonly ILogger? _logger;
    private readonly int _channels;

    // State for frame-by-frame correction
    private int _framesSinceLastCorrection;
    private float[]? _lastOutputFrame;
    private bool _disposed;

    // Logging rate limiter
    private long _lastLogTicks;
    private long _totalDropped;
    private long _totalInserted;
    private const long LogIntervalTicks = TimeSpan.TicksPerSecond; // Log every second

    /// <inheritdoc/>
    /// <remarks>
    /// This property does not check disposal state because it is only accessed during audio chain
    /// construction, before playback starts. The disposal sequence guarantees no access after
    /// disposal: WASAPI output is stopped first, then the resampler is disposed (stopping reads),
    /// and only then is this source disposed.
    /// </remarks>
    public AudioFormat Format => _buffer.Format;

    /// <summary>
    /// Gets the underlying audio buffer.
    /// Used by the player to access buffer properties and stats.
    /// </summary>
    /// <remarks>
    /// This property does not check disposal state because it is only accessed during audio chain
    /// construction or for diagnostics while playback is active. The disposal sequence in
    /// <see cref="WasapiAudioPlayer"/> ensures WASAPI output is stopped before this source is disposed,
    /// so no reads can occur after disposal.
    /// </remarks>
    public ITimedAudioBuffer Buffer => _buffer;

    /// <summary>
    /// Gets the sync correction provider.
    /// Can be used to subscribe to correction changes or access correction state.
    /// </summary>
    /// <remarks>
    /// This property does not check disposal state because it is only accessed during audio chain
    /// construction to pass to the resampler. The resampler subscribes to the provider's
    /// <see cref="ISyncCorrectionProvider.CorrectionChanged"/> event and unsubscribes in its own
    /// Dispose method, which is called before this source is disposed.
    /// </remarks>
    public ISyncCorrectionProvider CorrectionProvider => _correctionProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncCorrectedSampleSource"/> class.
    /// </summary>
    /// <param name="buffer">The timed audio buffer to read from.</param>
    /// <param name="correctionProvider">Provider for sync correction decisions.</param>
    /// <param name="getCurrentTimeMicroseconds">Function that returns current local time in microseconds.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public SyncCorrectedSampleSource(
        ITimedAudioBuffer buffer,
        ISyncCorrectionProvider correctionProvider,
        Func<long> getCurrentTimeMicroseconds,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(correctionProvider);
        ArgumentNullException.ThrowIfNull(getCurrentTimeMicroseconds);

        _buffer = buffer;
        _correctionProvider = correctionProvider;
        _getCurrentTimeMicroseconds = getCurrentTimeMicroseconds;
        _logger = logger;
        _channels = buffer.Format.Channels;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// This method uses <see cref="ArrayPool{T}.Shared"/> to avoid allocating temporary
    /// buffers on every audio callback. Audio threads are real-time sensitive, and GC
    /// pauses from frequent allocations can cause audible glitches (clicks/pops).
    /// At typical 10ms callback intervals with 960-9600 sample requests, avoiding these
    /// allocations prevents ~100 allocations/second of 4-40KB each.
    /// </remarks>
    public int Read(float[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var currentTime = _getCurrentTimeMicroseconds();

        // Rent a buffer from the pool to avoid GC allocations in the audio thread.
        // ArrayPool may return a buffer larger than requested; we only use 'count' elements.
        var tempBuffer = ArrayPool<float>.Shared.Rent(count);
        try
        {
            var rawRead = _buffer.ReadRaw(tempBuffer.AsSpan(0, count), currentTime);

            // Update correction provider with current sync error
            _correctionProvider.UpdateFromSyncError(
                _buffer.SyncErrorMicroseconds,
                _buffer.SmoothedSyncErrorMicroseconds);

            // Get current correction settings
            var dropEveryN = _correctionProvider.DropEveryNFrames;
            var insertEveryN = _correctionProvider.InsertEveryNFrames;

            // Apply correction if needed
            var (outputCount, samplesDropped, samplesInserted) = ApplyCorrection(
                tempBuffer, rawRead, buffer.AsSpan(offset, count), dropEveryN, insertEveryN);

            // Notify buffer of external corrections for accurate sync tracking
            if (samplesDropped > 0 || samplesInserted > 0)
            {
                _buffer.NotifyExternalCorrection(samplesDropped, samplesInserted);
                _totalDropped += samplesDropped;
                _totalInserted += samplesInserted;
            }

            // Notify correction provider of samples processed
            if (_correctionProvider is SyncCorrectionCalculator calculator)
            {
                calculator.NotifySamplesProcessed(outputCount);
            }

            // Rate-limited logging of correction state
            LogCorrectionState(dropEveryN, insertEveryN);

            // Fill remainder with silence if underrun
            if (outputCount < count)
            {
                buffer.AsSpan(offset + outputCount, count - outputCount).Fill(0f);
            }

            // Always return requested count to keep NAudio happy
            return count;
        }
        finally
        {
            // Always return the buffer to the pool, even if an exception occurs.
            // clearArray: false for performance - audio data doesn't need zeroing.
            ArrayPool<float>.Shared.Return(tempBuffer, clearArray: false);
        }
    }

    /// <summary>
    /// Resets correction state.
    /// Call this when buffer is cleared or playback restarts.
    /// </summary>
    public void Reset()
    {
        _framesSinceLastCorrection = 0;
        _lastOutputFrame = null;
        _totalDropped = 0;
        _totalInserted = 0;
        _correctionProvider.Reset();
    }

    /// <summary>
    /// Logs correction state at a rate-limited interval.
    /// </summary>
    private void LogCorrectionState(int dropEveryN, int insertEveryN)
    {
        if (_logger == null)
        {
            return;
        }

        var now = DateTime.UtcNow.Ticks;
        if (now - _lastLogTicks < LogIntervalTicks)
        {
            return;
        }

        _lastLogTicks = now;

        var syncError = _buffer.SmoothedSyncErrorMicroseconds / 1000.0; // Convert to ms
        var mode = _correctionProvider.CurrentMode;

        _logger.LogDebug(
            "SyncCorrection: error={SyncError:+0.00;-0.00}ms mode={Mode} dropEveryN={DropN} insertEveryN={InsertN} totalDropped={Dropped} totalInserted={Inserted}",
            syncError,
            mode,
            dropEveryN,
            insertEveryN,
            _totalDropped,
            _totalInserted);
    }

    /// <summary>
    /// Applies sync correction to samples using CLI-style drop/insert.
    /// </summary>
    /// <returns>Tuple of (output sample count, samples dropped, samples inserted).</returns>
    private (int OutputCount, int SamplesDropped, int SamplesInserted) ApplyCorrection(
        float[] input, int inputCount, Span<float> output, int dropEveryN, int insertEveryN)
    {
        var frameSamples = _channels;

        // Initialize last output frame if needed
        _lastOutputFrame ??= new float[frameSamples];

        // If no correction needed, copy directly
        if (dropEveryN == 0 && insertEveryN == 0)
        {
            var toCopy = Math.Min(inputCount, output.Length);
            input.AsSpan(0, toCopy).CopyTo(output);

            // Save last frame for potential future corrections
            if (toCopy >= frameSamples)
            {
                input.AsSpan(toCopy - frameSamples, frameSamples).CopyTo(_lastOutputFrame);
            }

            return (toCopy, 0, 0);
        }

        // Process frame by frame, applying corrections
        var inputPos = 0;
        var outputPos = 0;
        var samplesDropped = 0;
        var samplesInserted = 0;

        while (outputPos < output.Length)
        {
            // Check if we have input available
            var remainingInput = inputCount - inputPos;

            _framesSinceLastCorrection++;

            // Check if we should DROP a frame (read two, output one)
            // This catches up when we're behind - consume 2 frames, output 1
            if (dropEveryN > 0 && _framesSinceLastCorrection >= dropEveryN)
            {
                _framesSinceLastCorrection = 0;

                if (remainingInput >= frameSamples * 2)
                {
                    // Skip the first frame (consume but don't output - this is the "dropped" frame)
                    inputPos += frameSamples;

                    // Read and output the second frame
                    var droppedFrameOutput = output.Slice(outputPos, frameSamples);
                    input.AsSpan(inputPos, frameSamples).CopyTo(droppedFrameOutput);
                    inputPos += frameSamples;

                    // Save as last output frame for potential future inserts
                    droppedFrameOutput.CopyTo(_lastOutputFrame);

                    outputPos += frameSamples;
                    samplesDropped += frameSamples;
                    continue;
                }
            }

            // Check if we should INSERT a frame (output without reading)
            if (insertEveryN > 0 && _framesSinceLastCorrection >= insertEveryN)
            {
                _framesSinceLastCorrection = 0;

                // Check we have space in output
                if (output.Length - outputPos >= frameSamples)
                {
                    // Output last frame WITHOUT consuming input
                    _lastOutputFrame.AsSpan().CopyTo(output.Slice(outputPos, frameSamples));
                    outputPos += frameSamples;
                    samplesInserted += frameSamples;
                    continue;
                }
            }

            // Normal frame: read from input and output
            if (remainingInput < frameSamples)
            {
                break; // No more input
            }

            if (output.Length - outputPos < frameSamples)
            {
                break; // No more output space
            }

            var frameSpan = output.Slice(outputPos, frameSamples);
            input.AsSpan(inputPos, frameSamples).CopyTo(frameSpan);
            inputPos += frameSamples;

            // Save as last output frame
            frameSpan.CopyTo(_lastOutputFrame);
            outputPos += frameSamples;
        }

        return (outputPos, samplesDropped, samplesInserted);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lastOutputFrame = null;
    }
}
