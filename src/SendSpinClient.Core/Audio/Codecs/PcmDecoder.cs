// <copyright file="PcmDecoder.cs" company="SendSpin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Buffers.Binary;
using SendSpinClient.Core.Models;

namespace SendSpinClient.Core.Audio.Codecs;

/// <summary>
/// PCM "decoder" - converts raw PCM bytes to normalized float samples.
/// Supports 16-bit, 24-bit, and 32-bit integer PCM formats.
/// </summary>
public sealed class PcmDecoder : IAudioDecoder
{
    private readonly int _bytesPerSample;
    private bool _disposed;

    /// <inheritdoc/>
    public AudioFormat Format { get; }

    /// <inheritdoc/>
    public int MaxSamplesPerFrame { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PcmDecoder"/> class.
    /// </summary>
    /// <param name="format">Audio format configuration.</param>
    /// <exception cref="ArgumentException">Thrown when format is not PCM.</exception>
    public PcmDecoder(AudioFormat format)
    {
        if (!string.Equals(format.Codec, AudioCodecs.Pcm, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Expected PCM format, got {format.Codec}", nameof(format));
        }

        Format = format;
        _bytesPerSample = (format.BitDepth ?? 16) / 8;

        // Assume max 50ms frames for PCM at any sample rate
        MaxSamplesPerFrame = (format.SampleRate / 20) * format.Channels;
    }

    /// <inheritdoc/>
    public int Decode(ReadOnlySpan<byte> encodedData, Span<float> decodedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sampleCount = encodedData.Length / _bytesPerSample;
        var outputCount = Math.Min(sampleCount, decodedSamples.Length);

        var bitDepth = Format.BitDepth ?? 16;

        for (int i = 0; i < outputCount; i++)
        {
            var offset = i * _bytesPerSample;
            var slice = encodedData.Slice(offset, _bytesPerSample);

            decodedSamples[i] = bitDepth switch
            {
                16 => BinaryPrimitives.ReadInt16LittleEndian(slice) / 32768f,
                24 => Read24BitSample(slice) / 8388608f,
                32 => BinaryPrimitives.ReadInt32LittleEndian(slice) / 2147483648f,
                _ => throw new NotSupportedException($"Unsupported bit depth: {bitDepth}"),
            };
        }

        return outputCount;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        // PCM is stateless, nothing to reset
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>
    /// Reads a 24-bit signed integer sample from 3 bytes (little-endian).
    /// </summary>
    private static int Read24BitSample(ReadOnlySpan<byte> data)
    {
        // Read 24-bit as signed integer (little-endian)
        int value = data[0] | (data[1] << 8) | (data[2] << 16);

        // Sign extend from 24-bit to 32-bit
        if ((value & 0x800000) != 0)
        {
            value |= unchecked((int)0xFF000000);
        }

        return value;
    }
}
