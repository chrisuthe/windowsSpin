// <copyright file="IAudioDecoder.cs" company="SendSpin">
// Copyright (c) SendSpin. All rights reserved.
// </copyright>

using SendSpinClient.Core.Models;

namespace SendSpinClient.Core.Audio;

/// <summary>
/// Decodes encoded audio frames to PCM samples.
/// Thread-safe: may be called from WebSocket receive thread.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>
    /// Gets the audio format this decoder was configured for.
    /// </summary>
    AudioFormat Format { get; }

    /// <summary>
    /// Gets the maximum samples that can be output from a single decode call.
    /// Used to pre-allocate buffers. Typically 960*2 for 20ms Opus stereo at 48kHz.
    /// </summary>
    int MaxSamplesPerFrame { get; }

    /// <summary>
    /// Decodes an encoded audio frame to interleaved float samples [-1.0, 1.0].
    /// </summary>
    /// <param name="encodedData">Encoded audio data (Opus/FLAC/PCM).</param>
    /// <param name="decodedSamples">Buffer to receive decoded samples.</param>
    /// <returns>Number of samples written (total across all channels).</returns>
    int Decode(ReadOnlySpan<byte> encodedData, Span<float> decodedSamples);

    /// <summary>
    /// Resets decoder state (for stream clear/seek operations).
    /// </summary>
    void Reset();
}
