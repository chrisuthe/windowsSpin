// <copyright file="IAudioSampleSource.cs" company="SendSpin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using SendSpin.SDK.Models;

namespace SendSpin.SDK.Audio;

/// <summary>
/// Provides audio samples to the audio player.
/// Bridges the timed buffer to NAudio's ISampleProvider.
/// </summary>
public interface IAudioSampleSource
{
    /// <summary>
    /// Gets the audio format.
    /// </summary>
    AudioFormat Format { get; }

    /// <summary>
    /// Reads samples into the buffer.
    /// Called from audio thread - must be fast and non-blocking.
    /// </summary>
    /// <param name="buffer">Buffer to fill.</param>
    /// <param name="offset">Offset in buffer.</param>
    /// <param name="count">Number of samples to read.</param>
    /// <returns>Samples read (may be less than count on underrun).</returns>
    int Read(float[] buffer, int offset, int count);
}
