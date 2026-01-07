// <copyright file="IDiagnosticAudioRecorder.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Audio;

namespace SendspinClient.Services.Diagnostics;

/// <summary>
/// Interface for diagnostic audio recording with sync metrics.
/// </summary>
/// <remarks>
/// <para>
/// The recorder captures audio samples and sync metrics for later analysis.
/// When enabled, it maintains a circular buffer of the last N seconds of audio
/// with embedded metric markers that can be saved to a WAV file on demand.
/// </para>
/// <para>
/// Usage pattern:
/// <list type="number">
/// <item>Call <see cref="Enable"/> to start capturing</item>
/// <item>Call <see cref="CaptureIfEnabled"/> from the audio thread to capture samples</item>
/// <item>Call <see cref="RecordMetrics"/> periodically to capture sync state</item>
/// <item>Call <see cref="SaveAsync"/> to dump the buffer to a WAV file</item>
/// <item>Call <see cref="Disable"/> to stop capturing and free memory</item>
/// </list>
/// </para>
/// </remarks>
public interface IDiagnosticAudioRecorder : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether recording is currently enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets the duration of audio currently buffered in seconds.
    /// Returns 0 if recording is disabled.
    /// </summary>
    double BufferedSeconds { get; }

    /// <summary>
    /// Gets the configured buffer duration in seconds.
    /// </summary>
    int BufferDurationSeconds { get; }

    /// <summary>
    /// Raised when the recording state changes (enabled/disabled).
    /// </summary>
    event EventHandler<bool>? RecordingStateChanged;

    /// <summary>
    /// Enables recording and allocates the buffer.
    /// </summary>
    /// <param name="sampleRate">The audio sample rate (e.g., 48000).</param>
    /// <param name="channels">The number of channels (e.g., 2 for stereo).</param>
    void Enable(int sampleRate, int channels);

    /// <summary>
    /// Disables recording and frees the buffer.
    /// </summary>
    void Disable();

    /// <summary>
    /// Captures audio samples if recording is enabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is called from the audio thread and MUST NOT BLOCK.
    /// When disabled, this is effectively a no-op (single boolean check).
    /// </para>
    /// </remarks>
    /// <param name="samples">The audio samples to capture.</param>
    void CaptureIfEnabled(ReadOnlySpan<float> samples);

    /// <summary>
    /// Records current sync metrics for correlation with audio.
    /// </summary>
    /// <remarks>
    /// Call this periodically (e.g., every 100ms) from the stats update loop.
    /// </remarks>
    /// <param name="stats">Current audio buffer statistics.</param>
    void RecordMetrics(AudioBufferStats stats);

    /// <summary>
    /// Saves the current buffer to a WAV file with embedded markers.
    /// </summary>
    /// <param name="directory">The directory to save to.</param>
    /// <returns>The full path to the saved WAV file, or null if recording is disabled.</returns>
    Task<string?> SaveAsync(string directory);
}
