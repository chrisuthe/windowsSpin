// <copyright file="AudioDecoderFactory.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Audio.Codecs;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Audio;

/// <summary>
/// Factory for creating audio decoders based on the codec in the audio format.
/// </summary>
public sealed class AudioDecoderFactory : IAudioDecoderFactory
{
    /// <inheritdoc/>
    public IAudioDecoder Create(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        return format.Codec.ToLowerInvariant() switch
        {
            AudioCodecs.Opus => new OpusDecoder(format),
            AudioCodecs.Pcm => new PcmDecoder(format),
            AudioCodecs.Flac => new FlacDecoder(format),
            _ => throw new NotSupportedException($"Unsupported audio codec: {format.Codec}"),
        };
    }

    /// <inheritdoc/>
    public bool IsSupported(string codec)
    {
        ArgumentNullException.ThrowIfNull(codec);

        return codec.ToLowerInvariant() switch
        {
            AudioCodecs.Opus => true,
            AudioCodecs.Pcm => true,
            AudioCodecs.Flac => true,
            _ => false,
        };
    }
}
