// <copyright file="WasapiAudioPlayer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace SendspinClient.Services.Audio;

/// <summary>
/// Windows WASAPI audio player using NAudio.
/// Provides low-latency audio output via WASAPI shared mode.
/// </summary>
/// <remarks>
/// <para>
/// Uses WASAPI shared mode for broad device compatibility. While exclusive mode
/// offers lower latency, shared mode is more reliable across different audio
/// hardware configurations and allows other applications to use audio simultaneously.
/// </para>
/// <para>
/// The 50ms latency setting provides a good balance between responsiveness and
/// stability. Lower values may cause glitches on some hardware.
/// </para>
/// </remarks>
public sealed class WasapiAudioPlayer : IAudioPlayer
{
    private readonly ILogger<WasapiAudioPlayer> _logger;
    private readonly string? _deviceId;
    private WasapiOut? _wasapiOut;
    private AudioSampleProviderAdapter? _sampleProvider;
    private AudioFormat? _format;
    private float _volume = 1.0f;
    private bool _isMuted;
    private int _outputLatencyMs;

    /// <summary>
    /// Gets the detected output latency in milliseconds.
    /// This is the buffer latency reported by the WASAPI audio device.
    /// </summary>
    public int OutputLatencyMs => _outputLatencyMs;

    /// <inheritdoc/>
    public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;

    /// <inheritdoc/>
    /// <remarks>
    /// Volume is applied in software via the sample provider rather than through
    /// WASAPI endpoint volume. This avoids COM threading issues and provides
    /// consistent behavior across different audio hardware.
    /// </remarks>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_sampleProvider != null)
            {
                _sampleProvider.Volume = _volume;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            if (_sampleProvider != null)
            {
                _sampleProvider.IsMuted = value;
            }
        }
    }

    /// <inheritdoc/>
    public event EventHandler<AudioPlayerState>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<AudioPlayerError>? ErrorOccurred;

    /// <summary>
    /// Initializes a new instance of the <see cref="WasapiAudioPlayer"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="deviceId">
    /// Optional device ID for a specific audio output device.
    /// If null or empty, the system default device is used.
    /// </param>
    public WasapiAudioPlayer(ILogger<WasapiAudioPlayer> logger, string? deviceId = null)
    {
        _logger = logger;
        _deviceId = deviceId;
    }

    /// <inheritdoc/>
    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                try
                {
                    _format = format;

                    // Get the audio device - either specific device by ID or system default
                    MMDevice? device = null;
                    if (!string.IsNullOrEmpty(_deviceId))
                    {
                        try
                        {
                            using var enumerator = new MMDeviceEnumerator();
                            device = enumerator.GetDevice(_deviceId);
                            _logger.LogInformation("Using audio device: {DeviceName}", device.FriendlyName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to get device {DeviceId}, falling back to default", _deviceId);
                            device = null;
                        }
                    }

                    // Create WASAPI output in shared mode with 50ms latency
                    // Shared mode is more compatible; 50ms balances latency vs stability
                    if (device != null)
                    {
                        _wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: 50);
                    }
                    else
                    {
                        _wasapiOut = new WasapiOut(AudioClientShareMode.Shared, latency: 50);
                    }

                    _wasapiOut.PlaybackStopped += OnPlaybackStopped;

                    // Capture the output latency - this is the buffer latency we requested
                    // from WASAPI. The actual end-to-end latency also includes DAC/driver
                    // delays which vary by hardware and aren't directly queryable.
                    _outputLatencyMs = 50; // Our requested latency in shared mode

                    SetState(AudioPlayerState.Stopped);
                    _logger.LogInformation(
                        "WASAPI player initialized: {SampleRate}Hz {Channels}ch, latency: {Latency}ms, device: {Device}",
                        format.SampleRate,
                        format.Channels,
                        _outputLatencyMs,
                        device?.FriendlyName ?? "System Default");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize WASAPI player");
                    SetState(AudioPlayerState.Error);
                    ErrorOccurred?.Invoke(this, new AudioPlayerError("Failed to initialize audio output", ex));
                    throw;
                }
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public void SetSampleSource(IAudioSampleSource source)
    {
        if (_wasapiOut == null || _format == null)
        {
            throw new InvalidOperationException("Player not initialized. Call InitializeAsync first.");
        }

        ArgumentNullException.ThrowIfNull(source);

        // Create NAudio adapter with current volume/mute state
        _sampleProvider = new AudioSampleProviderAdapter(source, _format);
        _sampleProvider.Volume = _volume;
        _sampleProvider.IsMuted = _isMuted;

        // Initialize WASAPI with our provider
        _wasapiOut.Init(_sampleProvider);

        _logger.LogDebug("Sample source configured");
    }

    /// <inheritdoc/>
    public void Play()
    {
        if (_wasapiOut == null || _sampleProvider == null)
        {
            throw new InvalidOperationException("Player not initialized or no sample source set.");
        }

        _wasapiOut.Play();
        SetState(AudioPlayerState.Playing);
        _logger.LogInformation("Playback started");
    }

    /// <inheritdoc/>
    public void Pause()
    {
        _wasapiOut?.Pause();
        SetState(AudioPlayerState.Paused);
        _logger.LogInformation("Playback paused");
    }

    /// <inheritdoc/>
    public void Stop()
    {
        _wasapiOut?.Stop();
        SetState(AudioPlayerState.Stopped);
        _logger.LogInformation("Playback stopped");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_wasapiOut != null)
        {
            _wasapiOut.PlaybackStopped -= OnPlaybackStopped;
            _wasapiOut.Stop();
            _wasapiOut.Dispose();
            _wasapiOut = null;
        }

        _sampleProvider = null;
        SetState(AudioPlayerState.Uninitialized);

        await Task.CompletedTask;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _logger.LogError(e.Exception, "Playback stopped due to error");
            SetState(AudioPlayerState.Error);
            ErrorOccurred?.Invoke(this, new AudioPlayerError("Playback error", e.Exception));
        }
        else if (State == AudioPlayerState.Playing)
        {
            // Unexpected stop while playing
            SetState(AudioPlayerState.Stopped);
        }
    }

    private void SetState(AudioPlayerState newState)
    {
        if (State != newState)
        {
            _logger.LogDebug("Player state: {OldState} -> {NewState}", State, newState);
            State = newState;
            StateChanged?.Invoke(this, newState);
        }
    }
}
