// <copyright file="AudioPipeline.cs" company="SendSpin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SendSpinClient.Core.Models;
using SendSpinClient.Core.Protocol;
using SendSpinClient.Core.Synchronization;

namespace SendSpinClient.Core.Audio;

/// <summary>
/// Orchestrates the complete audio pipeline from incoming chunks to output.
/// Manages decoder, buffer, and player lifecycle and coordinates their interaction.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline operates in the following states:
/// - Idle: No active stream
/// - Starting: Initializing components for a new stream
/// - Buffering: Accumulating audio before playback starts
/// - Playing: Actively playing audio
/// - Stopping: Shutting down the current stream
/// - Error: An error occurred
/// </para>
/// <para>
/// Audio flow:
/// 1. ProcessAudioChunk receives encoded audio with server timestamp
/// 2. Decoder converts to float PCM samples
/// 3. TimedAudioBuffer stores samples with playback timestamps
/// 4. NAudio reads from buffer when samples are due for playback
/// </para>
/// </remarks>
public sealed class AudioPipeline : IAudioPipeline
{
    private readonly ILogger<AudioPipeline> _logger;
    private readonly IAudioDecoderFactory _decoderFactory;
    private readonly IClockSynchronizer _clockSync;
    private readonly Func<AudioFormat, IClockSynchronizer, ITimedAudioBuffer> _bufferFactory;
    private readonly Func<IAudioPlayer> _playerFactory;
    private readonly Func<ITimedAudioBuffer, Func<long>, IAudioSampleSource> _sourceFactory;
    private readonly IHighPrecisionTimer _precisionTimer;

    private IAudioDecoder? _decoder;
    private ITimedAudioBuffer? _buffer;
    private IAudioPlayer? _player;
    private IAudioSampleSource? _sampleSource;

    private float[] _decodeBuffer = Array.Empty<float>();
    private AudioFormat? _currentFormat;
    private int _volume = 100;
    private bool _muted;
    private DateTime? _bufferReadyTime;
    private long _lastSyncLogTime;
    private long _chunksProcessed;

    // Maximum time to wait for clock sync convergence before starting playback anyway
    private static readonly TimeSpan MaxConvergenceWait = TimeSpan.FromSeconds(3);

    // How often to log sync status during playback (microseconds)
    private const long SyncLogIntervalMicroseconds = 5_000_000; // 5 seconds

    /// <inheritdoc/>
    public AudioPipelineState State { get; private set; } = AudioPipelineState.Idle;

    /// <inheritdoc/>
    public AudioBufferStats? BufferStats => _buffer?.GetStats();

    /// <inheritdoc/>
    public AudioFormat? CurrentFormat => _currentFormat;

    /// <inheritdoc/>
    public event EventHandler<AudioPipelineState>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<AudioPipelineError>? ErrorOccurred;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioPipeline"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="decoderFactory">Factory for creating audio decoders.</param>
    /// <param name="clockSync">Clock synchronizer for timestamp conversion.</param>
    /// <param name="bufferFactory">Factory for creating timed audio buffers.</param>
    /// <param name="playerFactory">Factory for creating audio players.</param>
    /// <param name="sourceFactory">Factory for creating sample sources.</param>
    /// <param name="precisionTimer">High-precision timer for accurate timing (optional, uses shared instance if null).</param>
    public AudioPipeline(
        ILogger<AudioPipeline> logger,
        IAudioDecoderFactory decoderFactory,
        IClockSynchronizer clockSync,
        Func<AudioFormat, IClockSynchronizer, ITimedAudioBuffer> bufferFactory,
        Func<IAudioPlayer> playerFactory,
        Func<ITimedAudioBuffer, Func<long>, IAudioSampleSource> sourceFactory,
        IHighPrecisionTimer? precisionTimer = null)
    {
        _logger = logger;
        _decoderFactory = decoderFactory;
        _clockSync = clockSync;
        _bufferFactory = bufferFactory;
        _playerFactory = playerFactory;
        _sourceFactory = sourceFactory;
        _precisionTimer = precisionTimer ?? HighPrecisionTimer.Shared;

        // Log timer precision at startup
        if (HighPrecisionTimer.IsHighResolution)
        {
            _logger.LogDebug(
                "Using high-precision timer with {Resolution:F2}ns resolution",
                HighPrecisionTimer.GetResolutionNanoseconds());
        }
        else
        {
            _logger.LogWarning("High-resolution timing not available, sync accuracy may be reduced");
        }
    }

    /// <inheritdoc/>
    public async Task StartAsync(AudioFormat format, long? targetTimestamp = null, CancellationToken cancellationToken = default)
    {
        // Stop any existing stream first
        if (State != AudioPipelineState.Idle && State != AudioPipelineState.Error)
        {
            await StopAsync();
        }

        SetState(AudioPipelineState.Starting);

        try
        {
            _currentFormat = format;

            // Create decoder for the stream format
            _decoder = _decoderFactory.Create(format);
            _decodeBuffer = new float[_decoder.MaxSamplesPerFrame];

            _logger.LogDebug(
                "Decoder created for {Codec}, max frame size: {MaxSamples} samples",
                format.Codec,
                _decoder.MaxSamplesPerFrame);

            // Create timed buffer
            _buffer = _bufferFactory(format, _clockSync);

            // Subscribe to buffer reanchor event (if buffer supports it)
            if (_buffer is TimedAudioBuffer timedBuffer)
            {
                timedBuffer.ReanchorRequired += OnReanchorRequired;
            }

            // Create audio player
            _player = _playerFactory();
            await _player.InitializeAsync(format, cancellationToken);

            // Create sample source bridging buffer to player
            _sampleSource = _sourceFactory(_buffer, GetCurrentLocalTimeMicroseconds);
            _player.SetSampleSource(_sampleSource);

            // Apply volume/mute settings
            _player.Volume = _volume / 100f;
            _player.IsMuted = _muted;

            // Subscribe to player events
            _player.StateChanged += OnPlayerStateChanged;
            _player.ErrorOccurred += OnPlayerError;

            SetState(AudioPipelineState.Buffering);
            _logger.LogInformation("Audio pipeline started: {Format}", format);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start audio pipeline");
            await CleanupAsync();
            SetState(AudioPipelineState.Error);
            ErrorOccurred?.Invoke(this, new AudioPipelineError("Failed to start pipeline", ex));
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        if (State == AudioPipelineState.Idle)
        {
            return;
        }

        SetState(AudioPipelineState.Stopping);

        await CleanupAsync();

        SetState(AudioPipelineState.Idle);
        _logger.LogInformation("Audio pipeline stopped");
    }

    /// <inheritdoc/>
    public void Clear(long? newTargetTimestamp = null)
    {
        _buffer?.Clear();
        _decoder?.Reset();
        _bufferReadyTime = null;

        if (State == AudioPipelineState.Playing)
        {
            SetState(AudioPipelineState.Buffering);
        }

        _logger.LogDebug("Audio buffer cleared");
    }

    /// <inheritdoc/>
    public void ProcessAudioChunk(AudioChunk chunk)
    {
        if (_decoder == null || _buffer == null)
        {
            _logger.LogWarning("Received audio chunk but pipeline not started");
            return;
        }

        try
        {
            // Decode the audio frame
            var samplesDecoded = _decoder.Decode(chunk.EncodedData, _decodeBuffer);

            if (samplesDecoded > 0)
            {
                // Add decoded samples to buffer with server timestamp
                _buffer.Write(_decodeBuffer.AsSpan(0, samplesDecoded), chunk.ServerTimestamp);
                _chunksProcessed++;

                // Periodically log sync error during playback
                if (State == AudioPipelineState.Playing)
                {
                    LogSyncStatusIfNeeded();
                }

                // Check if we should start playback
                if (State == AudioPipelineState.Buffering && _buffer.IsReadyForPlayback)
                {
                    // Track when buffer first became ready
                    _bufferReadyTime ??= DateTime.UtcNow;

                    // Wait for clock sync to converge before starting - prevents large initial offset errors
                    // But don't wait forever - start anyway after timeout (better to play than silence)
                    var waitedForConvergence = DateTime.UtcNow - _bufferReadyTime.Value;
                    if (_clockSync.IsConverged)
                    {
                        StartPlayback();
                    }
                    else if (waitedForConvergence >= MaxConvergenceWait)
                    {
                        var syncStatus = _clockSync.GetStatus();
                        _logger.LogWarning(
                            "Clock sync not converged after {WaitMs:F0}ms (measurements={Count}, uncertainty={UncertaintyMs:F2}ms), starting playback anyway",
                            waitedForConvergence.TotalMilliseconds,
                            syncStatus.MeasurementCount,
                            syncStatus.OffsetUncertaintyMicroseconds / 1000.0);
                        StartPlayback();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log but don't crash - one bad frame shouldn't stop the stream
            _logger.LogWarning(ex, "Error processing audio chunk, skipping frame");
        }
    }

    /// <inheritdoc/>
    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        if (_player != null)
        {
            _player.Volume = _volume / 100f;
        }

        _logger.LogDebug("Volume set to {Volume}%", _volume);
    }

    /// <inheritdoc/>
    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (_player != null)
        {
            _player.IsMuted = muted;
        }

        _logger.LogDebug("Mute set to {Muted}", muted);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    /// <summary>
    /// Gets the current local time in microseconds using high-precision timer.
    /// Used by the sample source to know when to release audio.
    /// </summary>
    /// <remarks>
    /// Uses Stopwatch-based timing instead of DateTimeOffset for microsecond precision.
    /// DateTimeOffset.UtcNow only has ~15ms resolution on Windows.
    /// </remarks>
    private long GetCurrentLocalTimeMicroseconds()
    {
        return _precisionTimer.GetCurrentTimeMicroseconds();
    }

    private void StartPlayback()
    {
        if (_player == null)
        {
            return;
        }

        try
        {
            var syncStatus = _clockSync.GetStatus();
            _player.Play();
            _bufferReadyTime = null; // Reset for next buffering cycle

            // Reset sync monitoring counters
            _lastSyncLogTime = _precisionTimer.GetCurrentTimeMicroseconds();
            _chunksProcessed = 0;

            SetState(AudioPipelineState.Playing);
            _logger.LogInformation(
                "Starting playback: buffer={BufferMs:F0}ms, sync offset={OffsetMs:F2}ms (±{UncertaintyMs:F2}ms), " +
                "timer resolution={ResolutionNs:F0}ns",
                _buffer?.BufferedMilliseconds ?? 0,
                syncStatus.OffsetMilliseconds,
                syncStatus.OffsetUncertaintyMicroseconds / 1000.0,
                HighPrecisionTimer.GetResolutionNanoseconds());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start playback");
            ErrorOccurred?.Invoke(this, new AudioPipelineError("Failed to start playback", ex));
        }
    }

    private async Task CleanupAsync()
    {
        // Unsubscribe from events
        if (_player != null)
        {
            _player.StateChanged -= OnPlayerStateChanged;
            _player.ErrorOccurred -= OnPlayerError;
            _player.Stop();
            await _player.DisposeAsync();
            _player = null;
        }

        // Unsubscribe from buffer events
        if (_buffer is TimedAudioBuffer timedBuffer)
        {
            timedBuffer.ReanchorRequired -= OnReanchorRequired;
        }

        _decoder?.Dispose();
        _decoder = null;

        _buffer?.Dispose();
        _buffer = null;

        _sampleSource = null;
        _decodeBuffer = Array.Empty<float>();
        _currentFormat = null;
        _bufferReadyTime = null;
    }

    private void OnReanchorRequired(object? sender, EventArgs e)
    {
        var stats = _buffer?.GetStats();
        _logger.LogWarning(
            "Re-anchoring required: sync error {SyncErrorMs:F1}ms exceeds threshold. " +
            "Dropped={Dropped}, Inserted={Inserted}. Clearing buffer for resync.",
            stats?.SyncErrorMs ?? 0,
            stats?.SamplesDroppedForSync ?? 0,
            stats?.SamplesInsertedForSync ?? 0);

        // Clear and restart buffering
        Clear();
    }

    private void OnPlayerStateChanged(object? sender, AudioPlayerState state)
    {
        if (state == AudioPlayerState.Error)
        {
            SetState(AudioPipelineState.Error);
        }
    }

    private void OnPlayerError(object? sender, AudioPlayerError error)
    {
        _logger.LogError(error.Exception, "Player error: {Message}", error.Message);
        ErrorOccurred?.Invoke(this, new AudioPipelineError(error.Message, error.Exception));
    }

    private void SetState(AudioPipelineState newState)
    {
        if (State != newState)
        {
            _logger.LogDebug("Pipeline state: {OldState} -> {NewState}", State, newState);
            State = newState;
            StateChanged?.Invoke(this, newState);
        }
    }

    /// <summary>
    /// Logs sync status periodically during playback for monitoring drift.
    /// </summary>
    private void LogSyncStatusIfNeeded()
    {
        var currentTime = _precisionTimer.GetCurrentTimeMicroseconds();

        // Only log every SyncLogIntervalMicroseconds
        if (currentTime - _lastSyncLogTime < SyncLogIntervalMicroseconds)
        {
            return;
        }

        _lastSyncLogTime = currentTime;
        var stats = _buffer?.GetStats();
        var clockStatus = _clockSync.GetStatus();

        if (stats is { IsPlaybackActive: true })
        {
            var syncErrorMs = stats.SyncErrorMs;
            var absError = Math.Abs(syncErrorMs);

            // Format correction mode for logging
            var correctionInfo = stats.CurrentCorrectionMode switch
            {
                SyncCorrectionMode.Dropping => $"DROPPING (dropped={stats.SamplesDroppedForSync})",
                SyncCorrectionMode.Inserting => $"INSERTING (inserted={stats.SamplesInsertedForSync})",
                _ => "none",
            };

            // Use appropriate log level based on sync error magnitude
            if (absError > 50) // > 50ms - significant drift
            {
                _logger.LogWarning(
                    "Sync drift detected: error={SyncErrorMs:+0.00;-0.00}ms, correction={Correction}, " +
                    "buffer={BufferMs:F0}ms, chunks={Chunks}",
                    syncErrorMs,
                    correctionInfo,
                    stats.BufferedMs,
                    _chunksProcessed);
            }
            else if (absError > 10) // > 10ms - noticeable
            {
                _logger.LogInformation(
                    "Sync status: error={SyncErrorMs:+0.00;-0.00}ms, correction={Correction}, " +
                    "buffer={BufferMs:F0}ms",
                    syncErrorMs,
                    correctionInfo,
                    stats.BufferedMs);
            }
            else // < 10ms - good sync
            {
                _logger.LogDebug(
                    "Sync OK: error={SyncErrorMs:+0.00;-0.00}ms, buffer={BufferMs:F0}ms",
                    syncErrorMs,
                    stats.BufferedMs);
            }
        }
    }
}
