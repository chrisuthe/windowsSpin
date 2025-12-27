// <copyright file="IAudioDecoderFactory.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Models;

namespace Sendspin.SDK.Audio;

/// <summary>
/// Factory for creating audio decoders based on format.
/// </summary>
public interface IAudioDecoderFactory
{
    /// <summary>
    /// Creates a decoder for the specified format.
    /// </summary>
    /// <param name="format">Audio format from stream/start message.</param>
    /// <returns>Configured decoder instance.</returns>
    /// <exception cref="NotSupportedException">If codec is not supported.</exception>
    IAudioDecoder Create(AudioFormat format);

    /// <summary>
    /// Checks if a codec is supported.
    /// </summary>
    /// <param name="codec">Codec name to check.</param>
    /// <returns>True if the codec is supported.</returns>
    bool IsSupported(string codec);
}
