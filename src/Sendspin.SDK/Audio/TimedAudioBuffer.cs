// <copyright file="TimedAudioBuffer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Audio;

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
    private readonly ILogger<TimedAudioBuffer> _logger;
    private readonly IClockSynchronizer _clockSync;
    private readonly SyncCorrectionOptions _syncOptions;
    private readonly object _lock = new();

    // Rate limiting for underrun/overrun logging (microseconds)
    private const long UnderrunLogIntervalMicroseconds = 1_000_000; // Log at most once per second
    private long _lastUnderrunLogTime;
    private long _underrunsSinceLastLog;

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

    // Scheduled start: when playback should begin (supports static delay feature)
    // The first segment's LocalPlaybackTime includes any static delay from IClockSynchronizer.
    // We wait until this time arrives before outputting audio.
    private long _scheduledStartLocalTime;      // Target local time when playback should start (μs)
    private bool _waitingForScheduledStart;     // True while waiting for scheduled start time

    // Sync error tracking (CLI-style: track samples READ, not samples OUTPUT)
    // Key insight: We must track samples READ from buffer, not samples OUTPUT.
    // When dropping, we read MORE than we output → samplesReadTime advances → error shrinks.
    // When inserting, we read NOTHING → samplesReadTime stays → error grows toward 0.
    private long _playbackStartLocalTime;       // Local time when playback actually started (μs)
    private long _lastElapsedMicroseconds;      // Last calculated elapsed time (for stats)
    private long _currentSyncErrorMicroseconds; // Positive = behind (need DROP), Negative = ahead (need INSERT)
    private double _smoothedSyncErrorMicroseconds; // EMA-filtered sync error for stable correction decisions
    private long _samplesReadSinceStart;        // Total samples READ (consumed) since playback started
    private long _samplesOutputSinceStart;      // Total samples OUTPUT since playback started (for stats)
    private double _microsecondsPerSample;      // Duration of one sample in microseconds

    // Sync error smoothing (matches JS library approach)
    // EMA filter prevents jittery correction decisions from measurement noise.
    // Alpha of 0.1 means ~10 updates to reach 63% of a step change.
    // At ~10ms audio callbacks, this is ~100ms to stabilize after a change.
    private const double SyncErrorSmoothingAlpha = 0.1;

    private bool _disposed;

    /// <inheritdoc/>
    public AudioFormat Format { get; }

    /// <inheritdoc/>
    public SyncCorrectionOptions SyncOptions => _syncOptions.Clone();

    /// <summary>
    /// Event raised when sync error is too large and re-anchoring is needed.
    /// The pipeline should clear the buffer and restart synchronized playback.
    /// </summary>
    public event EventHandler? ReanchorRequired;

    /// <inheritdoc/>
    public double TargetBufferMilliseconds { get; set; } = 250;

    /// <inheritdoc/>
    public double TargetPlaybackRate { get; private set; } = 1.0;

    /// <inheritdoc/>
    public event Action<double>? TargetPlaybackRateChanged;

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

    /// <inheritdoc/>
    public long CalibratedStartupLatencyMicroseconds { get; set; }

    /// <inheritdoc/>
    public long SyncErrorMicroseconds
    {
        get
        {
            lock (_lock)
            {
                return _currentSyncErrorMicroseconds;
            }
        }
    }

    /// <inheritdoc/>
    public double SmoothedSyncErrorMicroseconds
    {
        get
        {
            lock (_lock)
            {
                return _smoothedSyncErrorMicroseconds;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TimedAudioBuffer"/> class.
    /// </summary>
    /// <param name="format">Audio format for samples.</param>
    /// <param name="clockSync">Clock synchronizer for timestamp conversion.</param>
    /// <param name="bufferCapacityMs">Maximum buffer capacity in milliseconds (default 500ms).</param>
    /// <param name="syncOptions">Optional sync correction options. Uses <see cref="SyncCorrectionOptions.Default"/> if not provided.</param>
    /// <param name="logger">Optional logger for diagnostics (uses NullLogger if not provided).</param>
    public TimedAudioBuffer(
        AudioFormat format,
        IClockSynchronizer clockSync,
        int bufferCapacityMs = 500,
        SyncCorrectionOptions? syncOptions = null,
        ILogger<TimedAudioBuffer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(clockSync);

        _logger = logger ?? NullLogger<TimedAudioBuffer>.Instance;
        Format = format;
        _clockSync = clockSync;
        _syncOptions = syncOptions?.Clone() ?? SyncCorrectionOptions.Default;
        _syncOptions.Validate();
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
                _logger.LogWarning(
                    "Buffer overrun #{Count}: dropped {DroppedMs:F1}ms of audio to make room (buffer full at {CapacityMs}ms)",
                    _overrunCount,
                    toDrop / (double)_samplesPerMs,
                    _buffer.Length / (double)_samplesPerMs);
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
                    _underrunsSinceLastLog++;
                    LogUnderrunIfNeeded(currentLocalTime);
                }

                buffer.Fill(0f);
                return 0;
            }

            // Scheduled start logic: wait for the target playback time before starting
            // This enables the StaticDelayMs feature to work correctly.
            //
            // The first segment's LocalPlaybackTime includes any static delay from
            // IClockSynchronizer.ServerToClientTime(). By waiting for this time to arrive,
            // positive static delay causes us to start later (as intended).
            //
            // Without this, we'd start immediately and the static delay would only affect
            // sync error calculation, which can't handle large offsets (exceeds re-anchor threshold).
            if (_segments.Count > 0 && !_playbackStarted)
            {
                var firstSegment = _segments.Peek();

                // First time seeing audio: capture the scheduled start time
                if (!_waitingForScheduledStart)
                {
                    _scheduledStartLocalTime = firstSegment.LocalPlaybackTime;
                    _waitingForScheduledStart = true;
                    _nextExpectedPlaybackTime = firstSegment.LocalPlaybackTime;
                }

                // Check if we've reached the scheduled start time (with grace window)
                var timeUntilStart = _scheduledStartLocalTime - currentLocalTime;
                if (timeUntilStart > _syncOptions.ScheduledStartGraceWindowMicroseconds)
                {
                    // Not ready yet - output silence and wait
                    buffer.Fill(0f);
                    return 0;
                }

                // Scheduled time arrived - start playback!
                _playbackStarted = true;
                _waitingForScheduledStart = false;

                // Initialize sync error tracking (CLI-style: track samples READ)
                //
                // For push-model backends (ALSA), we've already consumed samples to pre-fill
                // the output buffer before playback starts. By backdating the anchor by the
                // startup latency, elapsed time matches the samples we've already read.
                //
                // sync_error = elapsedWallClock - samplesReadTime
                //   Positive = wall clock ahead = playing too slow = DROP to catch up
                //   Negative = wall clock behind = playing too fast = INSERT to slow down
                //
                // This handles static buffer fill time architecturally, so sync correction
                // only needs to handle drift and fluctuations.
                _playbackStartLocalTime = currentLocalTime - CalibratedStartupLatencyMicroseconds;
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
                    try
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
                    catch
                    {
                        // Task.Run can throw (e.g., ThreadPool exhaustion, OutOfMemoryException).
                        // Reset the pending flag so future re-anchor events are not blocked.
                        Interlocked.Exchange(ref _reanchorEventPending, 0);
                        throw;
                    }
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
                if (elapsedSinceStart >= _syncOptions.StartupGracePeriodMicroseconds
                    && Math.Abs(_currentSyncErrorMicroseconds) > _syncOptions.ReanchorThresholdMicroseconds)
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
    public int ReadRaw(Span<float> buffer, long currentLocalTime)
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
                    _underrunsSinceLastLog++;
                    LogUnderrunIfNeeded(currentLocalTime);
                }

                buffer.Fill(0f);
                return 0;
            }

            // Scheduled start logic (same as Read)
            if (_segments.Count > 0 && !_playbackStarted)
            {
                var firstSegment = _segments.Peek();

                if (!_waitingForScheduledStart)
                {
                    _scheduledStartLocalTime = firstSegment.LocalPlaybackTime;
                    _waitingForScheduledStart = true;
                    _nextExpectedPlaybackTime = firstSegment.LocalPlaybackTime;
                }

                var timeUntilStart = _scheduledStartLocalTime - currentLocalTime;
                if (timeUntilStart > _syncOptions.ScheduledStartGraceWindowMicroseconds)
                {
                    buffer.Fill(0f);
                    return 0;
                }

                _playbackStarted = true;
                _waitingForScheduledStart = false;
                _playbackStartLocalTime = currentLocalTime - CalibratedStartupLatencyMicroseconds;
                _samplesReadSinceStart = 0;
                _samplesOutputSinceStart = 0;
            }

            // Check for re-anchor condition
            if (_needsReanchor)
            {
                _needsReanchor = false;
                buffer.Fill(0f);

                if (Interlocked.CompareExchange(ref _reanchorEventPending, 1, 0) == 0)
                {
                    try
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
                    catch
                    {
                        // Task.Run can throw (e.g., ThreadPool exhaustion, OutOfMemoryException).
                        // Reset the pending flag so future re-anchor events are not blocked.
                        Interlocked.Exchange(ref _reanchorEventPending, 0);
                        throw;
                    }
                }

                return 0;
            }

            // Read samples directly WITHOUT sync correction
            var toRead = Math.Min(buffer.Length, _count);
            ReadSamplesFromBuffer(buffer.Slice(0, toRead));

            _count -= toRead;
            _totalRead += toRead;
            ConsumeSegments(toRead);

            // Update sync error tracking (but don't apply correction - caller does that)
            if (_playbackStarted && toRead > 0)
            {
                _samplesReadSinceStart += toRead;
                _samplesOutputSinceStart += toRead;

                CalculateSyncError(currentLocalTime);
                // NOTE: We do NOT call UpdateCorrectionRate() here.
                // The caller is responsible for correction via ISyncCorrectionProvider.

                // Check re-anchor threshold
                var elapsedSinceStart = (long)(_samplesOutputSinceStart * _microsecondsPerSample);
                if (elapsedSinceStart >= _syncOptions.StartupGracePeriodMicroseconds
                    && Math.Abs(_currentSyncErrorMicroseconds) > _syncOptions.ReanchorThresholdMicroseconds)
                {
                    _needsReanchor = true;
                }
            }

            // Fill remainder with silence if needed
            if (toRead < buffer.Length)
            {
                buffer.Slice(toRead).Fill(0f);
            }

            return toRead;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Contract:</b> Either <paramref name="samplesDropped"/> OR <paramref name="samplesInserted"/>
    /// should be non-zero, but not both simultaneously. Dropping and inserting in the same correction
    /// cycle is logically invalid - you either need to speed up (drop) or slow down (insert), not both.
    /// </para>
    /// <para>
    /// The <see cref="SyncCorrectionCalculator"/> enforces this by design - it only sets either
    /// <see cref="ISyncCorrectionProvider.DropEveryNFrames"/> or <see cref="ISyncCorrectionProvider.InsertEveryNFrames"/>
    /// to a non-zero value, never both. However, if using a custom correction provider, ensure this
    /// invariant is maintained.
    /// </para>
    /// </remarks>
    public void NotifyExternalCorrection(int samplesDropped, int samplesInserted)
    {
        if (samplesDropped < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samplesDropped), samplesDropped,
                "Sample count must be non-negative.");
        }

        if (samplesInserted < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samplesInserted), samplesInserted,
                "Sample count must be non-negative.");
        }

        // Debug assertion: dropping and inserting simultaneously is logically invalid.
        // At runtime, we don't throw because SyncCorrectionCalculator already ensures
        // mutual exclusivity, and the tracking math still works (just unusual).
        System.Diagnostics.Debug.Assert(
            samplesDropped == 0 || samplesInserted == 0,
            $"NotifyExternalCorrection called with both dropped ({samplesDropped}) and inserted ({samplesInserted}) > 0. " +
            "This is logically invalid - correction should be either drop OR insert, not both.");

        lock (_lock)
        {
            // When dropping: we read MORE samples than we output
            // This advances the server cursor, making sync error smaller
            _samplesReadSinceStart += samplesDropped;
            _samplesDroppedForSync += samplesDropped;

            // When inserting: we output samples WITHOUT consuming from buffer
            // ReadRaw already added the full read count to _samplesReadSinceStart,
            // but inserted samples came from duplicating previous output, not from new input.
            // So we need to SUBTRACT them to reflect actual consumption from buffer.
            _samplesReadSinceStart -= samplesInserted;
            _samplesInsertedForSync += samplesInserted;
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

            // Reset scheduled start state
            _scheduledStartLocalTime = 0;
            _waitingForScheduledStart = false;

            // Reset sync error tracking (CLI-style: reset EVERYTHING on clear)
            // This matches Python CLI's clear() behavior for track changes
            _playbackStartLocalTime = 0;
            _lastElapsedMicroseconds = 0;
            _samplesReadSinceStart = 0;
            _samplesOutputSinceStart = 0;
            _currentSyncErrorMicroseconds = 0;
            _smoothedSyncErrorMicroseconds = 0;

            // Reset sync correction state
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            _framesSinceLastCorrection = 0;
            _needsReanchor = false;
            Interlocked.Exchange(ref _reanchorEventPending, 0);
            _lastOutputFrame = null;
            TargetPlaybackRate = 1.0;
            // Note: Don't reset _samplesDroppedForSync/_samplesInsertedForSync - these are cumulative stats
        }
    }

    /// <summary>
    /// Resets sync error tracking without clearing buffer content.
    /// Use this after audio device switches to prevent timing discontinuities
    /// from triggering false sync corrections.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Clear"/>, this preserves buffered audio and only resets
    /// the timing state. The next audio callback will re-anchor timing from scratch.
    /// </remarks>
    public void ResetSyncTracking()
    {
        lock (_lock)
        {
            // Signal that playback needs to re-establish its timing anchor
            // on the next Read() call, but keep buffered audio
            _playbackStarted = false;

            // Reset scheduled start state (will re-capture from next segment)
            _scheduledStartLocalTime = 0;
            _waitingForScheduledStart = false;

            // Reset sync error tracking
            _playbackStartLocalTime = 0;
            _lastElapsedMicroseconds = 0;
            _samplesReadSinceStart = 0;
            _samplesOutputSinceStart = 0;
            _currentSyncErrorMicroseconds = 0;
            _smoothedSyncErrorMicroseconds = 0;

            // Reset sync correction state
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            _framesSinceLastCorrection = 0;
            _needsReanchor = false;
            Interlocked.Exchange(ref _reanchorEventPending, 0);
            _lastOutputFrame = null;
            TargetPlaybackRate = 1.0;
        }
    }

    /// <inheritdoc/>
    public AudioBufferStats GetStats()
    {
        lock (_lock)
        {
            // Determine current correction mode based on active correction method
            SyncCorrectionMode correctionMode;
            if (_dropEveryNFrames > 0)
                correctionMode = SyncCorrectionMode.Dropping;
            else if (_insertEveryNFrames > 0)
                correctionMode = SyncCorrectionMode.Inserting;
            else if (Math.Abs(TargetPlaybackRate - 1.0) > 0.0001)
                correctionMode = SyncCorrectionMode.Resampling;
            else
                correctionMode = SyncCorrectionMode.None;

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
                SmoothedSyncErrorMicroseconds = _smoothedSyncErrorMicroseconds,
                IsPlaybackActive = _playbackStarted,
                SamplesDroppedForSync = _samplesDroppedForSync,
                SamplesInsertedForSync = _samplesInsertedForSync,
                CurrentCorrectionMode = correctionMode,
                TargetPlaybackRate = TargetPlaybackRate,
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
    /// Peeks samples from the circular buffer without advancing read position.
    /// Must be called under lock.
    /// </summary>
    /// <param name="destination">Buffer to copy samples into.</param>
    /// <param name="count">Number of samples to peek.</param>
    /// <returns>Number of samples actually peeked.</returns>
    private int PeekSamplesFromBuffer(Span<float> destination, int count)
    {
        var toPeek = Math.Min(count, _count);
        var peeked = 0;
        var tempReadPos = _readPos;

        while (peeked < toPeek && peeked < destination.Length)
        {
            var chunkSize = Math.Min(toPeek - peeked, _buffer.Length - tempReadPos);
            chunkSize = Math.Min(chunkSize, destination.Length - peeked);
            _buffer.AsSpan(tempReadPos, chunkSize).CopyTo(destination.Slice(peeked, chunkSize));
            tempReadPos = (tempReadPos + chunkSize) % _buffer.Length;
            peeked += chunkSize;
        }

        return peeked;
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

        // Sync error = elapsed - samples_read_time
        //
        // Positive = we haven't read enough (behind) = need to DROP (read faster)
        // Negative = we've read too much (ahead) = need to INSERT (slow down)
        //
        // Note: For push-model backends (ALSA), the static buffer pre-fill time is handled
        // by backdating _playbackStartLocalTime when playback starts. This keeps the sync
        // error formula clean and focused on drift/fluctuations only.
        _currentSyncErrorMicroseconds = elapsedTimeMicroseconds - samplesReadTimeMicroseconds;

        // Apply EMA smoothing to filter measurement jitter.
        // This prevents rapid correction changes from noisy measurements while still
        // tracking the underlying trend. The smoothed value is used for correction decisions.
        //
        // Special case: if smoothed error is 0 (just started or after reset), initialize
        // it to the current raw error to avoid slow ramp-up that causes rate oscillation.
        if (_smoothedSyncErrorMicroseconds == 0 && _currentSyncErrorMicroseconds != 0)
        {
            _smoothedSyncErrorMicroseconds = _currentSyncErrorMicroseconds;
        }
        else
        {
            _smoothedSyncErrorMicroseconds = SyncErrorSmoothingAlpha * _currentSyncErrorMicroseconds
                + (1 - SyncErrorSmoothingAlpha) * _smoothedSyncErrorMicroseconds;
        }
    }

    /// <summary>
    /// Updates the correction rate based on current sync error.
    /// Must be called under lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implements a tiered correction strategy:
    /// </para>
    /// <list type="bullet">
    /// <item>Error &lt; 1ms (deadband): No correction, playback rate = 1.0</item>
    /// <item>Error 1-15ms: Proportional rate adjustment (error / targetSeconds), clamped to max</item>
    /// <item>Error &gt; 15ms: Frame drop/insert for faster correction</item>
    /// </list>
    /// <para>
    /// Uses EMA-smoothed sync error to prevent jittery corrections from measurement noise.
    /// Proportional correction (matching Python CLI) prevents overshoot by adjusting rate
    /// based on error magnitude rather than using fixed rate steps.
    /// </para>
    /// </remarks>
    private void UpdateCorrectionRate()
    {
        // Skip correction during startup grace period to allow playback to stabilize
        // This prevents over-correction due to initial timing jitter
        var elapsedSinceStart = (long)(_samplesOutputSinceStart * _microsecondsPerSample);
        if (elapsedSinceStart < _syncOptions.StartupGracePeriodMicroseconds)
        {
            SetTargetPlaybackRate(1.0);
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            return;
        }

        // Use smoothed error for correction decisions (filters measurement jitter)
        var absError = Math.Abs(_smoothedSyncErrorMicroseconds);

        // Thresholds for correction tiers
        const long DeadbandThreshold = 1_000;      // 1ms - no correction below this
        const long ResamplingThreshold = 15_000;   // 15ms - above this use drop/insert

        // Tier 1: Deadband - error is small enough to ignore
        if (absError < DeadbandThreshold)
        {
            SetTargetPlaybackRate(1.0);
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            return;
        }

        // Tier 2: Proportional rate correction (1-15ms errors)
        // Rate = 1.0 + (error_µs / target_seconds / 1,000,000)
        // This calculates the rate needed to eliminate the error over the target time.
        // Example: 10ms error with 3s target → rate = 1.00333 (0.33% faster)
        if (absError < ResamplingThreshold)
        {
            // Calculate proportional correction (matching Python CLI approach)
            var correctionFactor = _smoothedSyncErrorMicroseconds
                / _syncOptions.CorrectionTargetSeconds
                / 1_000_000.0;

            // Clamp to configured maximum speed adjustment
            correctionFactor = Math.Clamp(correctionFactor,
                -_syncOptions.MaxSpeedCorrection,
                _syncOptions.MaxSpeedCorrection);

            var newRate = 1.0 + correctionFactor;
            SetTargetPlaybackRate(newRate);
            _dropEveryNFrames = 0;
            _insertEveryNFrames = 0;
            return;
        }

        // Tier 3: Large errors (>15ms) - use frame drop/insert for faster correction
        // Reset playback rate to 1.0 since we're using discrete sample correction
        SetTargetPlaybackRate(1.0);

        // Calculate desired corrections per second to fix error within target time
        // Error in frames = error_us * sample_rate / 1,000,000
        var framesError = absError * _sampleRate / 1_000_000.0;
        var desiredCorrectionsPerSec = framesError / _syncOptions.CorrectionTargetSeconds;

        // Calculate frames per second
        var framesPerSecond = (double)_sampleRate;

        // Limit correction rate to max speed adjustment
        var maxCorrectionsPerSec = framesPerSecond * _syncOptions.MaxSpeedCorrection;
        var actualCorrectionsPerSec = Math.Min(desiredCorrectionsPerSec, maxCorrectionsPerSec);

        // Calculate how often to apply a correction (every N frames)
        var correctionInterval = actualCorrectionsPerSec > 0
            ? (int)(framesPerSecond / actualCorrectionsPerSec)
            : 0;

        // Minimum interval to prevent too-aggressive correction
        correctionInterval = Math.Max(correctionInterval, _channels * 10);

        if (_smoothedSyncErrorMicroseconds > 0)
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
    /// Sets the target playback rate and raises the change event if different.
    /// Must be called under lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Thread Safety Note:</b> This event is intentionally fired while holding the buffer lock.
    /// </para>
    /// <para>
    /// Deadlock analysis (why this is safe):
    /// <list type="bullet">
    /// <item>This event is marked [Obsolete] - new code uses ISyncCorrectionProvider.CorrectionChanged instead</item>
    /// <item>No active subscribers exist in the current codebase</item>
    /// <item>Even when subscribers existed, they only stored the value (no callback into buffer)</item>
    /// <item>Firing outside the lock would require Task.Run allocation on every rate change (~100Hz)</item>
    /// </list>
    /// </para>
    /// <para>
    /// If you add a subscriber, ensure it does NOT call any TimedAudioBuffer methods or you will deadlock.
    /// </para>
    /// </remarks>
    private void SetTargetPlaybackRate(double rate)
    {
        if (Math.Abs(TargetPlaybackRate - rate) > 0.0001)
        {
            TargetPlaybackRate = rate;
            // Fire event inline while holding lock. This is safe because:
            // 1. The event is [Obsolete] with no active subscribers
            // 2. Any future subscriber must be lightweight (just store value, no callbacks)
            // 3. Firing via Task.Run would add allocation overhead on every rate change
            TargetPlaybackRateChanged?.Invoke(rate);
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

            // Check if we should DROP a frame (read two, output interpolated blend)
            if (_dropEveryNFrames > 0 && _framesSinceLastCorrection >= _dropEveryNFrames)
            {
                _framesSinceLastCorrection = 0;

                // Need at least 2 frames for interpolated drop
                if (_count - samplesConsumed >= frameSamples * 2)
                {
                    // Read frame A (the one before the drop point)
                    ReadSamplesFromBuffer(tempFrame);
                    samplesConsumed += frameSamples;

                    // Read frame B (the one we're skipping over)
                    Span<float> droppedFrame = stackalloc float[frameSamples];
                    ReadSamplesFromBuffer(droppedFrame);
                    samplesConsumed += frameSamples;

                    // Output interpolated blend: (A + B) / 2 for smooth transition
                    var outputSpan = buffer.Slice(outputPos, frameSamples);
                    for (int i = 0; i < frameSamples; i++)
                    {
                        outputSpan[i] = (tempFrame[i] + droppedFrame[i]) * 0.5f;
                    }

                    // Save interpolated frame as last output for continuity
                    outputSpan.CopyTo(_lastOutputFrame);
                    outputPos += frameSamples;
                    _samplesDroppedForSync += frameSamples;
                    continue;
                }
                else if (_count - samplesConsumed >= frameSamples)
                {
                    // Fallback: only 1 frame available, output it directly
                    ReadSamplesFromBuffer(tempFrame);
                    samplesConsumed += frameSamples;
                    tempFrame.CopyTo(buffer.Slice(outputPos, frameSamples));
                    tempFrame.CopyTo(_lastOutputFrame);
                    outputPos += frameSamples;
                    continue;
                }
            }

            // Check if we should INSERT a frame (output interpolated without consuming)
            if (_insertEveryNFrames > 0 && _framesSinceLastCorrection >= _insertEveryNFrames)
            {
                _framesSinceLastCorrection = 0;

                var outputSpan = buffer.Slice(outputPos, frameSamples);

                // Try to peek at next frame for interpolation (without consuming)
                if (_count - samplesConsumed >= frameSamples)
                {
                    Span<float> nextFrame = stackalloc float[frameSamples];
                    PeekSamplesFromBuffer(nextFrame, frameSamples);

                    // Output interpolated: (lastOutput + nextInput) / 2 for smooth transition
                    for (int i = 0; i < frameSamples; i++)
                    {
                        outputSpan[i] = (_lastOutputFrame[i] + nextFrame[i]) * 0.5f;
                    }

                    // Save interpolated frame for continuity
                    outputSpan.CopyTo(_lastOutputFrame);
                }
                else
                {
                    // Fallback: no next frame available, duplicate last
                    _lastOutputFrame.AsSpan().CopyTo(outputSpan);
                }

                outputPos += frameSamples;
                _samplesInsertedForSync += frameSamples;
                // Don't increment samplesConsumed - we didn't consume from buffer
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
    /// Logs underrun events with rate limiting to prevent log spam.
    /// Must be called under lock.
    /// </summary>
    /// <remarks>
    /// During severe underruns, this method can be called many times per second
    /// (once per audio callback, typically every ~10ms). Rate limiting ensures
    /// we log at most once per second while still capturing the total count.
    /// </remarks>
    private void LogUnderrunIfNeeded(long currentLocalTime)
    {
        // Check if enough time has passed since last log
        if (currentLocalTime - _lastUnderrunLogTime < UnderrunLogIntervalMicroseconds)
        {
            return;
        }

        // Log the accumulated underruns
        _logger.LogWarning(
            "Buffer underrun: {Count} events in last {IntervalMs}ms (total: {TotalCount}). " +
            "Buffer empty, outputting silence. Check network/decoding performance.",
            _underrunsSinceLastLog,
            (currentLocalTime - _lastUnderrunLogTime) / 1000,
            _underrunCount);

        // Reset rate limit state
        _lastUnderrunLogTime = currentLocalTime;
        _underrunsSinceLastLog = 0;
    }

    /// <summary>
    /// Represents a segment of samples with its target playback time.
    /// </summary>
    /// <param name="LocalPlaybackTime">Local time (microseconds) when this segment should play.</param>
    /// <param name="SampleCount">Number of interleaved samples in this segment.</param>
    private readonly record struct TimestampedSegment(long LocalPlaybackTime, int SampleCount);
}
