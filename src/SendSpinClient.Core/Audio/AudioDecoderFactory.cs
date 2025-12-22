// <copyright file="AudioDecoderFactory.cs" company="SendSpin">
// Copyright (c) SendSpin. All rights reserved.
// </copyright>

using SendSpinClient.Core.Audio.Codecs;
using SendSpinClient.Core.Models;

namespace SendSpinClient.Core.Audio;

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
            AudioCodecs.Flac => throw new NotSupportedException(
                "FLAC decoding is not yet implemented. Consider using Opus or PCM format."),
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
            AudioCodecs.Flac => false, // Future: implement with dedicated library
            _ => false,
        };
    }
}
