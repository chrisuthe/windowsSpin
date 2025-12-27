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

    // Startup grace period: don't apply sync correction for first 500ms
    // This allows playback to stabilize before we start measuring drift
    private const long StartupGracePeriodMicroseconds = 500_000; // 500ms

    // Sync correction state
    private int _dropEveryNFrames;        // Drop a frame every N frames (when playing too slow)
    private int _insertEveryNFrames;      // Insert a frame every N frames (when playing too fast)
    private int _framesSinceLastCorrection; // Counter for applying corrections
    private long _samplesDroppedForSync;  // Total samples dropped for sync correction
    private long _samplesInsertedForSync; // Total samples inserted for sync correction
    private bool _needsReanchor;          // Flag to trigger re-anchoring
    private int _reanchorEventPending;    // 0 = not pending, 1 = pending (for thread-safe event coalescing)
    private float[]? _lastOutputFrame;    // Last output frame for smooth drop/insert (Python CLI approach)

    // Statistics
    private long _underrunCount;
    private long _overrunCount;
    private long _droppedSamples;
    private long _totalWritten;
    private long _totalRead;

    // Sync error tracking (CLI-style: track samples READ, not samples OUTPUT)
    // Key insight: We must track samples READ from buffer, not samples OUTPUT.
    // When dropping, we read MORE than we output → samplesReadTime advances → error shrinks.
    // When inserting, we read NOTHING → samplesReadTime stays → error grows toward 0.
    private long _playbackStartLocalTime;       // Local time when playback started (μs)
    private long _lastElapsedMicroseconds;      // Last calculated elapsed time (for stats)
    private long _currentSyncErrorMicroseconds; // Positive = behind (need DROP), Negative = ahead (need INSERT)
    private long _samplesReadSinceStart;        // Total samples READ (consumed) since playback started
    private long _samplesOutputSinceStart;      // Total samples OUTPUT since playback started (for stats)
    private double _microsecondsPerSample;      // Duration of one sample in microseconds

    private bool _disposed;
    private bool _hasEverPlayed;  // Track if we've ever started playback (for resume detection)

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

    /// <inheritdoc/>
    public long OutputLatencyMicroseconds { get; set; }

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

            // Start playback immediately when we have audio - don't wait for timestamps!
            // Python CLI approach: start when buffer is ready, let sync correction handle timing.
            // Waiting for timestamps would cause 2-5 second delays since server sends audio ahead.
            // The sync correction (drop/insert frames) will handle any drift over time.
            if (_segments.Count > 0 && !_playbackStarted)
            {
                var firstSegment = _segments.Peek();

                _playbackStarted = true;
                _hasEverPlayed = true;
                _nextExpectedPlaybackTime = firstSegment.LocalPlaybackTime;

                // Initialize sync error tracking (CLI-style: track samples READ)
                // Use the ACTUAL start time (now), not the intended playback time.
                // The first chunk's LocalPlaybackTime may be seconds in the FUTURE
                // (server sends audio ~5s ahead) - that's normal buffer, not sync error!
                //
                // sync_error = elapsedWallClock - samplesReadTime
                //   Positive = wall clock ahead = playing too slow = DROP to catch up
                //   Negative = wall clock behind = playing too fast = INSERT to slow down
                //
                // When dropping: samplesReadTime increases faster → error shrinks ✓
                _playbackStartLocalTime = currentLocalTime;
                _samplesReadSinceStart = 0;
                _samplesOutputSinceStart = 0;
            }

            // Check for re-anchor condition before reading
            if (_needsReanchor)
            {
                _needsReanchor = false;
                buffer.Fill(0f);

                // Raise event outside of lock to prevent deadlocks.
                // Use Interlocked to ensure only one event can be pending at a time,
                // preventing duplicate events from queuing up if Read() is called rapidly.
                if (Interlocked.CompareExchange(ref _reanchorEventPending, 1, 0) == 0)
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            ReanchorRequired?.Invoke(this, EventArgs.Empty);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _reanchorEventPending, 0);
                        }
                    });
                }

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

            // Update sync error tracking and correction rate (CLI-style approach)
            // IMPORTANT: Track both samplesRead AND samplesOutput separately!
            // - samplesRead advances the server cursor (what timestamp we're reading)
            // - samplesOutput advances wall clock (how much time has passed for output)
            // When dropping: read 2, output 1 → cursor advances faster → error shrinks ✓
            // When inserting: read 0, output 1 → cursor stays still → error grows toward 0 ✓
            if (_playbackStarted && outputCount > 0)
            {
                _samplesReadSinceStart += actualRead;
                _samplesOutputSinceStart += outputCount;

                CalculateSyncError(currentLocalTime);
                UpdateCorrectionRate();

                // Check if error is too large and we need to re-anchor
                // But skip this check during startup grace period
                var elapsedSinceStart = (long)(_samplesOutputSinceStart * _microsecondsPerSample);
                if (elapsedSinceStart >= StartupGracePeriodMicroseconds
                    && Math.Abs(_currentSyncErrorMicroseconds) > ReanchorThresholdMicroseconds)
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

            // Reset sync error tracking (CLI-style: reset EVERYTHING on clear)
            // This matches Python CLI's clear() behavior for track changes
            _playbackStartLocalTime = 0;
            _lastElapsedMicroseconds = 0;
            _samplesReadSinceStart = 0;
            _samplesOutputSinceStart = 0;
            _currentSyncErrorMicroseconds = 0;

            // Reset sync correction state
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            _framesSinceLastCorrection = 0;
            _needsReanchor = false;
            Interlocked.Exchange(ref _reanchorEventPending, 0);
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
                SamplesReadSinceStart = _samplesReadSinceStart,
                SamplesOutputSinceStart = _samplesOutputSinceStart,
                ElapsedSinceStartMs = _lastElapsedMicroseconds / 1000.0,
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
    /// Calculates the current sync error using CLI-style server cursor tracking.
    /// Must be called under lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CLI approach: sync_error = expected_server_position - actual_server_cursor
    /// </para>
    /// <para>
    /// Expected position = first server timestamp + elapsed wall clock time.
    /// Actual cursor = server timestamp we've READ up to (advanced by samplesRead).
    /// </para>
    /// <para>
    /// When DROPPING (read 2, output 1):
    ///   - Cursor advances by 2 frames worth of time
    ///   - Expected advances by 1 frame worth of time (wall clock)
    ///   - Error shrinks! (cursor catches up to expected) ✓
    /// </para>
    /// <para>
    /// When INSERTING (read 0, output 1):
    ///   - Cursor stays still
    ///   - Expected advances by 1 frame worth of time (wall clock)
    ///   - Error grows toward 0! (expected catches up to cursor) ✓
    /// </para>
    /// </remarks>
    private void CalculateSyncError(long currentLocalTime)
    {
        // Elapsed wall-clock time since playback started
        var elapsedTimeMicroseconds = currentLocalTime - _playbackStartLocalTime;
        _lastElapsedMicroseconds = elapsedTimeMicroseconds;

        // How much server time have we actually READ (consumed) from the buffer?
        var samplesReadTimeMicroseconds = (long)(_samplesReadSinceStart * _microsecondsPerSample);

        // Account for output buffer latency (WASAPI buffer delay).
        // When elapsed time = 50ms and we've read 50ms of samples, the speaker has
        // only played ~0ms (samples are still in the WASAPI buffer).
        // Without this compensation, we'd see a constant ~50ms "behind" error.
        //
        // By subtracting output latency from elapsed time, we're asking:
        // "How much audio should have ACTUALLY played through the speaker?"
        // instead of "How much wall clock time has passed?"
        var adjustedElapsedMicroseconds = elapsedTimeMicroseconds - OutputLatencyMicroseconds;

        // Sync error = adjusted_elapsed - samples_read_time
        // Positive = we haven't read enough (behind) = need to DROP (read faster)
        // Negative = we've read too much (ahead) = need to INSERT (slow down reading)
        _currentSyncErrorMicroseconds = adjustedElapsedMicroseconds - samplesReadTimeMicroseconds;
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
        // Skip correction during startup grace period to allow playback to stabilize
        // This prevents over-correction due to initial timing jitter
        var elapsedSinceStart = (long)(_samplesOutputSinceStart * _microsecondsPerSample);
        if (elapsedSinceStart < StartupGracePeriodMicroseconds)
        {
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            return;
        }

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

        // Continue until output buffer is full (not until we've consumed toRead)
        // When dropping, we consume MORE from input to fill output with real audio.
        // Previously, the loop exited when samplesConsumed >= toRead, leaving the
        // output buffer partially filled with silence - which doesn't speed up playback!
        while (outputPos < buffer.Length)
        {
            // Check if we have a full frame to read from internal buffer.
            // Use _count - samplesConsumed to check ACTUAL remaining, not planned toRead.
            var remainingInBuffer = _count - samplesConsumed;
            if (remainingInBuffer < frameSamples)
            {
                break; // Underrun - not enough audio in internal buffer
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
                // Check actual remaining in internal buffer, not the pre-calculated remainingInBuffer
                if (_count - samplesConsumed >= frameSamples)
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
