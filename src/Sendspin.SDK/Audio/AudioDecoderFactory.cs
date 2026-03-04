// <copyright file="AudioDecoderFactory.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio.Codecs;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Audio;

/// <summary>
/// Factory for creating audio decoders based on the codec in the audio format.
/// </summary>
public sealed class AudioDecoderFactory : IAudioDecoderFactory
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioDecoderFactory"/> class.
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory for decoder diagnostics.</param>
    public AudioDecoderFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <inheritdoc/>
    public IAudioDecoder Create(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        return format.Codec.ToLowerInvariant() switch
        {
            AudioCodecs.Opus => new OpusDecoder(format),
            AudioCodecs.Pcm => new PcmDecoder(format),
            AudioCodecs.Flac => new FlacDecoder(format, _loggerFactory.CreateLogger<FlacDecoder>()),
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
