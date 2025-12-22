// <copyright file="IAudioPipeline.cs" company="SendSpin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using SendSpinClient.Core.Models;
using SendSpinClient.Core.Protocol;

namespace SendSpinClient.Core.Audio;

/// <summary>
/// Orchestrates the complete audio pipeline from incoming chunks to output.
/// </summary>
public interface IAudioPipeline : IAsyncDisposable
{
    /// <summary>
    /// Gets the current pipeline state.
    /// </summary>
    AudioPipelineState State { get; }

    /// <summary>
    /// Gets the current buffer statistics, or null if not started.
    /// </summary>
    AudioBufferStats? BufferStats { get; }

    /// <summary>
    /// Gets the current audio format being decoded, or null if not streaming.
    /// </summary>
    AudioFormat? CurrentFormat { get; }

    /// <summary>
    /// Starts the pipeline with the specified stream format.
    /// Called when stream/start is received.
    /// </summary>
    /// <param name="format">Audio format for the stream.</param>
    /// <param name="targetTimestamp">Optional target timestamp for playback alignment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task StartAsync(AudioFormat format, long? targetTimestamp = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the pipeline.
    /// Called when stream/end is received.
    /// </summary>
    /// <returns>A task representing the async operation.</returns>
    Task StopAsync();

    /// <summary>
    /// Clears the buffer (for seek).
    /// Called when stream/clear is received.
    /// </summary>
    /// <param name="newTargetTimestamp">Optional new target timestamp.</param>
    void Clear(long? newTargetTimestamp = null);

    /// <summary>
    /// Processes an incoming audio chunk.
    /// </summary>
    /// <param name="chunk">The audio chunk to process.</param>
    void ProcessAudioChunk(AudioChunk chunk);

    /// <summary>
    /// Sets volume (0-100).
    /// </summary>
    /// <param name="volume">Volume level.</param>
    void SetVolume(int volume);

    /// <summary>
    /// Sets mute state.
    /// </summary>
    /// <param name="muted">Whether to mute.</param>
    void SetMuted(bool muted);

    /// <summary>
    /// Event raised when pipeline state changes.
    /// </summary>
    event EventHandler<AudioPipelineState>? StateChanged;

    /// <summary>
    /// Event raised on pipeline errors.
    /// </summary>
    event EventHandler<AudioPipelineError>? ErrorOccurred;
}

/// <summary>
/// Audio pipeline states.
/// </summary>
public enum AudioPipelineState
{
    /// <summary>
    /// Pipeline is idle, not processing audio.
    /// </summary>
    Idle,

    /// <summary>
    /// Pipeline is starting up.
    /// </summary>
    Starting,

    /// <summary>
    /// Pipeline is buffering audio before playback.
    /// </summary>
    Buffering,

    /// <summary>
    /// Pipeline is actively playing audio.
    /// </summary>
    Playing,

    /// <summary>
    /// Pipeline is stopping.
    /// </summary>
    Stopping,

    /// <summary>
    /// Pipeline encountered an error.
    /// </summary>
    Error,
}

/// <summary>
/// Represents an audio pipeline error.
/// </summary>
/// <param name="Message">Error message.</param>
/// <param name="Exception">Optional exception that caused the error.</param>
public record AudioPipelineError(string Message, Exception? Exception = null);
