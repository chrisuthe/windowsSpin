// <copyright file="TimedAudioBuffer.cs" company="SendSpin">
// Copyright (c) SendSpin. All rights reserved.
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

    // Statistics
    private long _underrunCount;
    private long _overrunCount;
    private long _droppedSamples;
    private long _totalWritten;
    private long _totalRead;

    private bool _disposed;

    /// <inheritdoc/>
    public AudioFormat Format { get; }

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
            }

            // Read samples from circular buffer
            var toRead = Math.Min(buffer.Length, _count);
            var read = ReadSamplesFromBuffer(buffer.Slice(0, toRead));

            _count -= read;
            _totalRead += read;

            // Update segment tracking
            ConsumeSegments(read);

            // Fill remainder with silence if we didn't have enough
            if (read < buffer.Length)
            {
                buffer.Slice(read).Fill(0f);
            }

            return read;
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
    /// Represents a segment of samples with its target playback time.
    /// </summary>
    /// <param name="LocalPlaybackTime">Local time (microseconds) when this segment should play.</param>
    /// <param name="SampleCount">Number of interleaved samples in this segment.</param>
    private readonly record struct TimestampedSegment(long LocalPlaybackTime, int SampleCount);
}
