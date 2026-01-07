// <copyright file="IAudioPlayer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Models;

namespace Sendspin.SDK.Audio;

/// <summary>
/// Manages audio output device and playback lifecycle.
/// </summary>
public interface IAudioPlayer : IAsyncDisposable
{
    /// <summary>
    /// Gets the current playback state.
    /// </summary>
    AudioPlayerState State { get; }

    /// <summary>
    /// Gets or sets the output volume (0.0 to 1.0).
    /// </summary>
    float Volume { get; set; }

    /// <summary>
    /// Gets or sets whether output is muted.
    /// </summary>
    bool IsMuted { get; set; }

    /// <summary>
    /// Gets the detected output latency in milliseconds.
    /// This represents the buffer latency of the audio output device.
    /// </summary>
    /// <remarks>
    /// This value is available after <see cref="InitializeAsync"/> completes.
    /// It can be used for informational/diagnostic purposes.
    /// </remarks>
    int OutputLatencyMs { get; }

    /// <summary>
    /// Gets the calibrated startup latency in milliseconds for push-model audio backends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This represents the measured time from first audio write to actual playback start
    /// on push-model backends (like ALSA) that must pre-fill their output buffer.
    /// </para>
    /// <para>
    /// Pull-model backends (like WASAPI) should return 0 since they don't pre-fill.
    /// </para>
    /// <para>
    /// This value is used by <see cref="ITimedAudioBuffer"/> to compensate for the
    /// constant negative sync error caused by buffer prefill.
    /// </para>
    /// </remarks>
    int CalibratedStartupLatencyMs => 0;

    /// <summary>
    /// Gets the current output audio format, or null if not initialized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This represents the audio format being sent to the audio output device.
    /// It is available after <see cref="InitializeAsync"/> completes.
    /// </para>
    /// <para>
    /// The output format may differ from the input format if resampling or
    /// format conversion is applied by the audio subsystem.
    /// </para>
    /// </remarks>
    AudioFormat? OutputFormat => null;

    /// <summary>
    /// Initializes the audio output with the specified format.
    /// </summary>
    /// <param name="format">Audio format to use.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the sample provider that supplies audio data.
    /// </summary>
    /// <param name="source">The audio sample source.</param>
    void SetSampleSource(IAudioSampleSource source);

    /// <summary>
    /// Starts audio playback.
    /// </summary>
    void Play();

    /// <summary>
    /// Pauses audio playback.
    /// </summary>
    void Pause();

    /// <summary>
    /// Stops audio playback and resets.
    /// </summary>
    void Stop();

    /// <summary>
    /// Switches to a different audio output device.
    /// </summary>
    /// <param name="deviceId">The device ID to switch to, or null for system default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    /// <remarks>
    /// This will briefly stop playback while reinitializing the audio output.
    /// The sample source is preserved, so playback resumes from the current position.
    /// </remarks>
    Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when playback state changes.
    /// </summary>
    event EventHandler<AudioPlayerState>? StateChanged;

    /// <summary>
    /// Event raised on playback errors.
    /// </summary>
    event EventHandler<AudioPlayerError>? ErrorOccurred;
}

/// <summary>
/// Audio player playback states.
/// </summary>
public enum AudioPlayerState
{
    /// <summary>
    /// Player has not been initialized.
    /// </summary>
    Uninitialized,

    /// <summary>
    /// Player is stopped.
    /// </summary>
    Stopped,

    /// <summary>
    /// Player is actively playing audio.
    /// </summary>
    Playing,

    /// <summary>
    /// Player is paused.
    /// </summary>
    Paused,

    /// <summary>
    /// Player encountered an error.
    /// </summary>
    Error,
}

/// <summary>
/// Represents an audio player error.
/// </summary>
/// <param name="Message">Error message.</param>
/// <param name="Exception">Optional exception that caused the error.</param>
public record AudioPlayerError(string Message, Exception? Exception = null);
