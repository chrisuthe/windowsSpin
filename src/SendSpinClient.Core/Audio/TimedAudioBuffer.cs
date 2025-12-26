// <copyright file="TimedAudioBuffer.cs" company="SendSpin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using SendSpinClient.Core.Models;
using SendSpinClient.Core.Synchronization;

namespace SendSpinClient.Core.Audio;

/// <summary>
/// Thread-safe circular buffer that releases audio at the correct time based on server timestamps.
/// Uses IClockSynchronizer to convert server timestamps to local playback times.
/// </summary>
/// <remarks>
/// <para>
/// This buffer implements a producer-consumer pattern where:
/// - The WebSocket receive thread writes decoded audio with server timestamps
/// - The NAudio audio thread reads samples when their playback time arrives
/// </para>
/// <para>
/// Timing strategy:
/// - Each write associates samples with a server timestamp (when they should play)
/// - On read, we check if the oldest segment's playback time has arrived
/// - If not ready, we output silence (prevents playing audio too early)
/// - If past due, we play immediately (catches up on delayed audio)
/// </para>
/// </remarks>
public sealed class TimedAudioBuffer : ITimedAudioBuffer
{
    private readonly IClockSynchronizer _clockSync;
    private readonly object _lock = new();

    // Circular buffer for samples
    private float[] _buffer;
    private int _writePos;
    private int _readPos;
    private int _count;

    // Timestamp tracking - maps sample ranges to their playback times
    private readonly Queue<TimestampedSegment> _segments;
    private long _nextExpectedPlaybackTime;
    private bool _playbackStarted;

    // Configuration
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _samplesPerMs;

    // Sync correction constants (matching Python CLI behavior)
    // Deadband: don't correct errors smaller than this (avoids jitter from tiny corrections)
    private const long CorrectionDeadbandMicroseconds = 2_000; // 2ms

    // Maximum speed adjustment: limits how aggressively we correct (prevents audible artifacts)
    // 0.04 = 4% max speed change (matches Python CLI's _MAX_SPEED_CORRECTION)
    private const double MaxSpeedCorrection = 0.04;

    // Target time to fix the error (seconds) - how quickly we want to eliminate drift
    private const double CorrectionTargetSeconds = 2.0;

    // Re-anchor threshold: if error exceeds this, clear buffer and restart sync
    // 500ms matches Python CLI's _REANCHOR_THRESHOLD_US (more lenient than before)
    private const long ReanchorThresholdMicroseconds = 500_000; // 500ms

    // Sync correction state
    private int _dropEveryNFrames;        // Drop a frame every N frames (when playing too slow)
    private int _insertEveryNFrames;      // Insert a frame every N frames (when playing too fast)
    private int _framesSinceLastCorrection; // Counter for applying corrections
    private long _samplesDroppedForSync;  // Total samples dropped for sync correction
    private long _samplesInsertedForSync; // Total samples inserted for sync correction
    private bool _needsReanchor;          // Flag to trigger re-anchoring
    private float[]? _lastOutputFrame;    // Last output frame for smooth drop/insert (Python CLI approach)

    // Statistics
    private long _underrunCount;
    private long _overrunCount;
    private long _droppedSamples;
    private long _totalWritten;
    private long _totalRead;

    // Sync error tracking
    // Tracks the difference between where playback IS vs where it SHOULD be
    private long _playbackStartLocalTime;     // Local time when playback started (μs)
    private long _playbackStartServerTime;    // Server timestamp of first sample played (μs)
    private long _currentSyncErrorMicroseconds; // Positive = playing late, Negative = playing early
    private long _samplesPlayedSinceStart;    // Total samples output since playback started
    private double _microsecondsPerSample;    // Duration of one sample in microseconds

    private bool _disposed;

    /// <inheritdoc/>
    public AudioFormat Format { get; }

    /// <summary>
    /// Event raised when sync error is too large and re-anchoring is needed.
    /// The pipeline should clear the buffer and restart synchronized playback.
    /// </summary>
    public event EventHandler? ReanchorRequired;

    /// <inheritdoc/>
    public double TargetBufferMilliseconds { get; set; } = 100;

    /// <inheritdoc/>
    public double BufferedMilliseconds
    {
        get
        {
            lock (_lock)
            {
                return _count / (double)_samplesPerMs;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsReadyForPlayback
    {
        get
        {
            lock (_lock)
            {
                // Ready when we have at least 80% of target buffer
                return BufferedMilliseconds >= TargetBufferMilliseconds * 0.8;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TimedAudioBuffer"/> class.
    /// </summary>
    /// <param name="format">Audio format for samples.</param>
    /// <param name="clockSync">Clock synchronizer for timestamp conversion.</param>
    /// <param name="bufferCapacityMs">Maximum buffer capacity in milliseconds (default 500ms).</param>
    public TimedAudioBuffer(AudioFormat format, IClockSynchronizer clockSync, int bufferCapacityMs = 500)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(clockSync);

        Format = format;
        _clockSync = clockSync;
        _sampleRate = format.SampleRate;
        _channels = format.Channels;
        _samplesPerMs = (_sampleRate * _channels) / 1000;

        // Pre-allocate buffer for specified duration
        var bufferSamples = bufferCapacityMs * _samplesPerMs;
        _buffer = new float[bufferSamples];
        _segments = new Queue<TimestampedSegment>();

        // Calculate microseconds per interleaved sample (for sync error calculation)
        // For stereo 48kHz: 1,000,000 / (48000 * 2) = ~10.42 μs per sample
        _microsecondsPerSample = 1_000_000.0 / (_sampleRate * _channels);
    }

    /// <inheritdoc/>
    public void Write(ReadOnlySpan<float> samples, long serverTimestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (samples.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            // Convert server timestamp to local playback time
            var localPlaybackTime = _clockSync.ServerToClientTime(serverTimestamp);

            // Check for overrun - drop oldest if needed
            if (_count + samples.Length > _buffer.Length)
            {
                var toDrop = (_count + samples.Length) - _buffer.Length;
                DropOldestSamples(toDrop);
                _overrunCount++;
            }

            // Write samples to circular buffer
            WriteSamplesToBuffer(samples);

            // Track this segment's timestamp
            _segments.Enqueue(new TimestampedSegment(localPlaybackTime, samples.Length));
            _count += samples.Length;
            _totalWritten += samples.Length;
        }
    }

    /// <inheritdoc/>
    public int Read(Span<float> buffer, long currentLocalTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            // If buffer is empty, output silence
            if (_count == 0)
            {
                if (_playbackStarted)
                {
                    _underrunCount++;
                }

                buffer.Fill(0f);
                return 0;
            }

            // Check if the oldest segment is ready for playback
            if (_segments.Count > 0 && !_playbackStarted)
            {
                var firstSegment = _segments.Peek();

                // Don't start playing until the first segment's time arrives
                // Allow 5ms early start to account for processing delays
                if (currentLocalTime < firstSegment.LocalPlaybackTime - 5000)
                {
                    buffer.Fill(0f);
                    return 0;
                }

                _playbackStarted = true;
                _nextExpectedPlaybackTime = firstSegment.LocalPlaybackTime;

                // Initialize sync error tracking
                // Record when playback started and what server time that corresponds to
                _playbackStartLocalTime = currentLocalTime;
                _playbackStartServerTime = _clockSync.ClientToServerTime(currentLocalTime);
                _samplesPlayedSinceStart = 0;
            }

            // Check for re-anchor condition before reading
            if (_needsReanchor)
            {
                _needsReanchor = false;
                buffer.Fill(0f);

                // Raise event outside of lock to prevent deadlocks
                Task.Run(() => ReanchorRequired?.Invoke(this, EventArgs.Empty));
                return 0;
            }

            // Calculate how many samples we want to read, potentially adjusted for sync correction
            var toRead = Math.Min(buffer.Length, _count);

            // Apply sync correction: drop or insert frames
            var (actualRead, outputCount) = ReadWithSyncCorrection(buffer, toRead);

            _count -= actualRead;
            _totalRead += actualRead;

            // Update segment tracking
            ConsumeSegments(actualRead);

            // Update sync error tracking and correction rate
            if (_playbackStarted && actualRead > 0)
            {
                _samplesPlayedSinceStart += actualRead;
                CalculateSyncError(currentLocalTime);
                UpdateCorrectionRate();

                // Check if error is too large and we need to re-anchor
                if (Math.Abs(_currentSyncErrorMicroseconds) > ReanchorThresholdMicroseconds)
                {
                    _needsReanchor = true;
                }
            }

            // Fill remainder with silence if we didn't have enough
            if (outputCount < buffer.Length)
            {
                buffer.Slice(outputCount).Fill(0f);
            }

            return outputCount;
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (_lock)
        {
            _writePos = 0;
            _readPos = 0;
            _count = 0;
            _segments.Clear();
            _playbackStarted = false;
            _nextExpectedPlaybackTime = 0;

            // Reset sync error tracking
            _playbackStartLocalTime = 0;
            _playbackStartServerTime = 0;
            _samplesPlayedSinceStart = 0;
            _currentSyncErrorMicroseconds = 0;

            // Reset sync correction state
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            _framesSinceLastCorrection = 0;
            _needsReanchor = false;
            _lastOutputFrame = null;
            // Note: Don't reset _samplesDroppedForSync/_samplesInsertedForSync - these are cumulative stats
        }
    }

    /// <inheritdoc/>
    public AudioBufferStats GetStats()
    {
        lock (_lock)
        {
            return new AudioBufferStats
            {
                BufferedMs = _count / (double)_samplesPerMs,
                TargetMs = TargetBufferMilliseconds,
                UnderrunCount = _underrunCount,
                OverrunCount = _overrunCount,
                DroppedSamples = _droppedSamples,
                TotalSamplesWritten = _totalWritten,
                TotalSamplesRead = _totalRead,
                SyncErrorMicroseconds = _currentSyncErrorMicroseconds,
                IsPlaybackActive = _playbackStarted,
                SamplesDroppedForSync = _samplesDroppedForSync,
                SamplesInsertedForSync = _samplesInsertedForSync,
                CurrentCorrectionMode = _dropEveryNFrames > 0
                    ? SyncCorrectionMode.Dropping
                    : (_insertEveryNFrames > 0 ? SyncCorrectionMode.Inserting : SyncCorrectionMode.None),
            };
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_lock)
        {
            _buffer = Array.Empty<float>();
            _segments.Clear();
        }
    }

    /// <summary>
    /// Writes samples to the circular buffer.
    /// Must be called under lock.
    /// </summary>
    private void WriteSamplesToBuffer(ReadOnlySpan<float> samples)
    {
        var written = 0;
        while (written < samples.Length)
        {
            var chunkSize = Math.Min(samples.Length - written, _buffer.Length - _writePos);
            samples.Slice(written, chunkSize).CopyTo(_buffer.AsSpan(_writePos, chunkSize));
            _writePos = (_writePos + chunkSize) % _buffer.Length;
            written += chunkSize;
        }
    }

    /// <summary>
    /// Reads samples from the circular buffer.
    /// Must be called under lock.
    /// </summary>
    private int ReadSamplesFromBuffer(Span<float> buffer)
    {
        var read = 0;
        while (read < buffer.Length && read < _count)
        {
            var chunkSize = Math.Min(buffer.Length - read, _buffer.Length - _readPos);
            chunkSize = Math.Min(chunkSize, _count - read);
            _buffer.AsSpan(_readPos, chunkSize).CopyTo(buffer.Slice(read, chunkSize));
            _readPos = (_readPos + chunkSize) % _buffer.Length;
            read += chunkSize;
        }

        return read;
    }

    /// <summary>
    /// Drops the oldest samples to make room for new data.
    /// Must be called under lock.
    /// </summary>
    private void DropOldestSamples(int toDrop)
    {
        var dropped = 0;
        while (dropped < toDrop && _count > 0)
        {
            var chunkSize = Math.Min(toDrop - dropped, _buffer.Length - _readPos);
            chunkSize = Math.Min(chunkSize, _count);
            _readPos = (_readPos + chunkSize) % _buffer.Length;
            _count -= chunkSize;
            dropped += chunkSize;
        }

        _droppedSamples += dropped;

        // Also update segment tracking
        ConsumeSegments(dropped);
    }

    /// <summary>
    /// Consumes segment tracking entries for read/dropped samples.
    /// Must be called under lock.
    /// </summary>
    private void ConsumeSegments(int samplesConsumed)
    {
        var remaining = samplesConsumed;
        while (remaining > 0 && _segments.Count > 0)
        {
            var segment = _segments.Peek();
            if (segment.SampleCount <= remaining)
            {
                remaining -= segment.SampleCount;
                _segments.Dequeue();
            }
            else
            {
                // Partial segment - update remaining count
                _segments.Dequeue();
                _segments.Enqueue(segment with { SampleCount = segment.SampleCount - remaining });
                break;
            }
        }
    }

    /// <summary>
    /// Calculates the current sync error by comparing actual vs expected playback position.
    /// Must be called under lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sync error calculation:
    /// - Expected position: Based on elapsed SERVER time since playback started
    /// - Actual position: Based on samples we've actually output
    /// - Error = Expected - Actual (positive = we're behind, negative = we're ahead)
    /// </para>
    /// <para>
    /// CRITICAL: We must use ClientToServerTime to convert current local time to server time.
    /// Simply adding elapsed local time assumes clocks run at the same rate, which is wrong
    /// if there's drift. Without this correction, the sync error accumulates incorrectly
    /// on each play/pause/play cycle, causing progressive desync.
    /// </para>
    /// </remarks>
    private void CalculateSyncError(long currentLocalTime)
    {
        // Convert current local time to server time, properly accounting for drift
        // This is critical: we can't just add elapsed local time because clocks drift!
        var currentServerTime = _clockSync.ClientToServerTime(currentLocalTime);

        // How much server time has elapsed since playback started?
        var elapsedServerTimeMicroseconds = currentServerTime - _playbackStartServerTime;

        // What server timestamp ARE we at based on samples output?
        // Each sample represents a fixed duration of server time
        var actualServerTimeDelta = (long)(_samplesPlayedSinceStart * _microsecondsPerSample);

        // Sync error: positive means we're behind (playing late), negative means ahead (playing early)
        // Compare elapsed server time (what we SHOULD have played) vs actual output (what we DID play)
        _currentSyncErrorMicroseconds = elapsedServerTimeMicroseconds - actualServerTimeDelta;
    }

    /// <summary>
    /// Updates the correction rate based on current sync error.
    /// Must be called under lock.
    /// </summary>
    /// <remarks>
    /// Calculates how often to drop or insert frames to correct the sync error
    /// within the target correction time, while respecting the max speed adjustment.
    /// </remarks>
    private void UpdateCorrectionRate()
    {
        var absError = Math.Abs(_currentSyncErrorMicroseconds);

        // If error is within deadband, no correction needed
        if (absError <= CorrectionDeadbandMicroseconds)
        {
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            return;
        }

        // Calculate desired corrections per second to fix error within target time
        // Error in frames = error_us * (sample_rate * channels) / 1,000,000
        var framesError = absError * _sampleRate / 1_000_000.0;
        var desiredCorrectionsPerSec = framesError / CorrectionTargetSeconds;

        // Calculate frames per second
        var framesPerSecond = (double)_sampleRate; // One frame = one sample per channel

        // Limit correction rate to max speed adjustment
        var maxCorrectionsPerSec = framesPerSecond * MaxSpeedCorrection;
        var actualCorrectionsPerSec = Math.Min(desiredCorrectionsPerSec, maxCorrectionsPerSec);

        // Calculate how often to apply a correction (every N frames)
        var correctionInterval = actualCorrectionsPerSec > 0
            ? (int)(framesPerSecond / actualCorrectionsPerSec)
            : 0;

        // Minimum interval to prevent too-aggressive correction
        correctionInterval = Math.Max(correctionInterval, _channels * 10);

        if (_currentSyncErrorMicroseconds > 0)
        {
            // Playing too slow - need to drop frames to catch up
            _dropEveryNFrames = correctionInterval;
            _insertEveryNFrames = 0;
        }
        else
        {
            // Playing too fast - need to insert frames to slow down
            _dropEveryNFrames = 0;
            _insertEveryNFrames = correctionInterval;
        }
    }

    /// <summary>
    /// Reads samples with sync correction applied (drop or insert frames as needed).
    /// Must be called under lock.
    /// </summary>
    /// <param name="buffer">Output buffer to fill.</param>
    /// <param name="toRead">Number of samples to read from internal buffer.</param>
    /// <returns>Tuple of (samples consumed from buffer, samples written to output).</returns>
    /// <remarks>
    /// Uses the Python CLI approach for smoother corrections:
    /// - Drop: Read TWO frames from input, output the LAST frame (skip one input)
    /// - Insert: Output the last frame AGAIN without reading from input
    /// This maintains audio continuity by always using recently-played samples.
    /// </remarks>
    private (int ActualRead, int OutputCount) ReadWithSyncCorrection(Span<float> buffer, int toRead)
    {
        var frameSamples = _channels; // One frame = all channels for one time point

        // Initialize last output frame if needed
        _lastOutputFrame ??= new float[frameSamples];

        // If no correction needed, use optimized bulk read
        if (_dropEveryNFrames == 0 && _insertEveryNFrames == 0)
        {
            var read = ReadSamplesFromBuffer(buffer.Slice(0, toRead));

            // Save last frame for potential future corrections
            if (read >= frameSamples)
            {
                buffer.Slice(read - frameSamples, frameSamples).CopyTo(_lastOutputFrame);
            }

            return (read, read);
        }

        // Process frame by frame, applying corrections (Python CLI approach)
        var outputPos = 0;
        var samplesConsumed = 0;
        Span<float> tempFrame = stackalloc float[frameSamples];

        while (samplesConsumed < toRead && outputPos < buffer.Length)
        {
            // Check if we have a full frame to read
            var remainingInBuffer = toRead - samplesConsumed;
            if (remainingInBuffer < frameSamples)
            {
                break;
            }

            // Check remaining output space
            if (buffer.Length - outputPos < frameSamples)
            {
                break;
            }

            _framesSinceLastCorrection++;

            // Check if we should DROP a frame (Python CLI: read TWO, output last)
            if (_dropEveryNFrames > 0 && _framesSinceLastCorrection >= _dropEveryNFrames)
            {
                _framesSinceLastCorrection = 0;

                // Read the frame we're replacing (consume it)
                ReadSamplesFromBuffer(tempFrame);
                samplesConsumed += frameSamples;

                // Read the frame we're DROPPING (consume it too)
                if (remainingInBuffer >= frameSamples * 2)
                {
                    ReadSamplesFromBuffer(tempFrame);
                    samplesConsumed += frameSamples;
                }

                // Output the LAST frame instead (smoother transition)
                _lastOutputFrame.AsSpan().CopyTo(buffer.Slice(outputPos, frameSamples));
                outputPos += frameSamples;
                _samplesDroppedForSync += frameSamples;
                continue;
            }

            // Check if we should INSERT a frame (Python CLI: output last frame WITHOUT reading)
            if (_insertEveryNFrames > 0 && _framesSinceLastCorrection >= _insertEveryNFrames)
            {
                _framesSinceLastCorrection = 0;

                // Output last frame WITHOUT consuming input (slows down playback)
                _lastOutputFrame.AsSpan().CopyTo(buffer.Slice(outputPos, frameSamples));
                outputPos += frameSamples;
                _samplesInsertedForSync += frameSamples;

                // Don't increment samplesConsumed - we didn't read anything
                // But do decrement framesSinceLastCorrection tracking (we output a frame)
                continue;
            }

            // Normal frame: read from buffer and output
            var frameSpan = buffer.Slice(outputPos, frameSamples);
            ReadSamplesFromBuffer(frameSpan);
            samplesConsumed += frameSamples;

            // Save as last output frame for future corrections
            frameSpan.CopyTo(_lastOutputFrame);
            outputPos += frameSamples;
        }

        return (samplesConsumed, outputPos);
    }

    /// <summary>
    /// Represents a segment of samples with its target playback time.
    /// </summary>
    /// <param name="LocalPlaybackTime">Local time (microseconds) when this segment should play.</param>
    /// <param name="SampleCount">Number of interleaved samples in this segment.</param>
    private readonly record struct TimestampedSegment(long LocalPlaybackTime, int SampleCount);
}
