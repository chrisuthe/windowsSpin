// <copyright file="FlacDecoder.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio.Codecs.ThirdParty;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Audio.Codecs;

/// <summary>
/// FLAC audio decoder using SimpleFlac library.
/// Decodes FLAC-encoded audio frames to interleaved float PCM samples.
/// </summary>
/// <remarks>
/// This decoder handles streaming FLAC data by synthesizing a minimal FLAC header
/// for each frame. While this adds overhead, it provides robust frame-by-frame
/// decoding suitable for real-time streaming applications.
/// </remarks>
public sealed class FlacDecoder : IAudioDecoder
{
    /// <summary>
    /// STREAMINFO metadata block type.
    /// </summary>
    private const byte StreamInfoBlockType = 0x00;

    /// <summary>
    /// Flag indicating last metadata block.
    /// </summary>
    private const byte LastMetadataBlockFlag = 0x80;

    /// <summary>
    /// STREAMINFO block length (always 34 bytes).
    /// </summary>
    private const int StreamInfoLength = 34;

    /// <summary>
    /// Total header size: 4 (marker) + 4 (block header) + 34 (STREAMINFO).
    /// </summary>
    private const int HeaderSize = 42;

    private readonly ILogger _logger;
    private readonly byte[] _header;
    private float _sampleScaleFactor;
    private bool _scaleFactorCalibrated;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FlacDecoder"/> class.
    /// </summary>
    /// <param name="format">Audio format configuration.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <exception cref="ArgumentException">Thrown when format is not FLAC.</exception>
    public FlacDecoder(AudioFormat format, ILogger<FlacDecoder>? logger = null)
    {
        if (!string.Equals(format.Codec, AudioCodecs.Flac, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Expected FLAC format, got {format.Codec}", nameof(format));
        }

        _logger = logger ?? NullLogger<FlacDecoder>.Instance;
        Format = format;
        var bitsPerSample = format.BitDepth ?? 16;

        // Calculate scale factor for converting to float [-1.0, 1.0]
        // Use 1L to avoid integer overflow at 32-bit (1 << 31 overflows int, but not long)
        _sampleScaleFactor = 1.0f / (1L << (bitsPerSample - 1));

        // FLAC typically uses 4096 sample blocks, but can go up to 65535
        // We use 8192 as a reasonable max for streaming
        const int maxBlockSize = 8192;
        MaxSamplesPerFrame = maxBlockSize * format.Channels;

        // Use server-provided codec_header (real STREAMINFO) when available,
        // fall back to synthetic header otherwise
        if (format.CodecHeader is { } codecHeaderBase64)
        {
            _header = Convert.FromBase64String(codecHeaderBase64);
            _logger.LogDebug("FLAC decoder using server codec_header ({Bytes} bytes, {BitDepth}-bit)",
                _header.Length, bitsPerSample);
        }
        else
        {
            _header = BuildSyntheticHeader(format, maxBlockSize);
            _logger.LogDebug("FLAC decoder using synthetic header ({BitDepth}-bit)", bitsPerSample);
        }
    }

    /// <inheritdoc/>
    public AudioFormat Format { get; }

    /// <inheritdoc/>
    public int MaxSamplesPerFrame { get; }

    /// <inheritdoc/>
    public int Decode(ReadOnlySpan<byte> encodedData, Span<float> decodedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (encodedData.IsEmpty)
        {
            return 0;
        }

        // Create a stream containing header + FLAC frame data
        var streamData = new byte[_header.Length + encodedData.Length];
        _header.CopyTo(streamData, 0);
        encodedData.CopyTo(streamData.AsSpan(_header.Length));

        using var stream = new MemoryStream(streamData, writable: false);

        var options = new ThirdParty.FlacDecoder.Options
        {
            ConvertOutputToBytes = false, // We'll convert samples directly
            ValidateOutputHash = false,   // Not applicable for streaming
        };

        try
        {
            using var flacDecoder = new ThirdParty.FlacDecoder(stream, options);

            // Calibrate scale factor from actual FLAC STREAMINFO bit depth.
            // The stream/start message may report a different bit depth (e.g., 32 from PyAV's s32
            // container) than the actual FLAC encoding (e.g., 24-bit precision).
            if (!_scaleFactorCalibrated)
            {
                var actualBits = flacDecoder.BitsPerSample;
                _sampleScaleFactor = 1.0f / (1L << (actualBits - 1));
                _scaleFactorCalibrated = true;

                if (actualBits != (Format.BitDepth ?? 16))
                {
                    _logger.LogWarning(
                        "FLAC actual bit depth ({ActualBits}) differs from stream/start ({ReportedBits}), using actual",
                        actualBits, Format.BitDepth ?? 16);
                }
            }

            // Decode frame(s)
            int totalSamplesWritten = 0;

            while (flacDecoder.DecodeFrame())
            {
                var sampleCount = flacDecoder.BufferSampleCount;
                var channelCount = flacDecoder.ChannelCount;
                var samplesNeeded = sampleCount * channelCount;

                if (totalSamplesWritten + samplesNeeded > decodedSamples.Length)
                {
                    break; // Output buffer full
                }

                // Convert from long[][] (per-channel) to interleaved float
                ConvertToInterleavedFloat(
                    flacDecoder.BufferSamples,
                    sampleCount,
                    channelCount,
                    decodedSamples.Slice(totalSamplesWritten));

                totalSamplesWritten += samplesNeeded;
            }

            return totalSamplesWritten;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            _logger.LogWarning(ex, "FLAC frame decode failed ({BitDepth}-bit, {DataLen} bytes encoded)",
                Format.BitDepth ?? 16, encodedData.Length);
            return 0;
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        // FLAC frames are self-contained, no state to reset
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>
    /// Builds a synthetic FLAC header (fLaC marker + STREAMINFO block).
    /// </summary>
    private static byte[] BuildSyntheticHeader(AudioFormat format, int maxBlockSize)
    {
        var header = new byte[HeaderSize];
        var offset = 0;

        // fLaC marker (big-endian)
        header[offset++] = 0x66; // 'f'
        header[offset++] = 0x4C; // 'L'
        header[offset++] = 0x61; // 'a'
        header[offset++] = 0x43; // 'C'

        // Metadata block header: last block flag | type (0 = STREAMINFO)
        header[offset++] = LastMetadataBlockFlag | StreamInfoBlockType;

        // Block length (24-bit big-endian)
        header[offset++] = 0;
        header[offset++] = 0;
        header[offset++] = StreamInfoLength;

        // STREAMINFO block (34 bytes):
        // - Minimum block size (16 bits)
        var minBlockSize = (ushort)16; // FLAC minimum
        header[offset++] = (byte)(minBlockSize >> 8);
        header[offset++] = (byte)minBlockSize;

        // - Maximum block size (16 bits)
        header[offset++] = (byte)(maxBlockSize >> 8);
        header[offset++] = (byte)maxBlockSize;

        // - Minimum frame size (24 bits) - 0 = unknown
        header[offset++] = 0;
        header[offset++] = 0;
        header[offset++] = 0;

        // - Maximum frame size (24 bits) - 0 = unknown
        header[offset++] = 0;
        header[offset++] = 0;
        header[offset++] = 0;

        // Next 8 bytes encode: sample rate (20 bits), channels-1 (3 bits),
        // bits per sample-1 (5 bits), total samples (36 bits)
        var sampleRate = format.SampleRate;
        var channels = format.Channels;
        var bitsPerSample = format.BitDepth ?? 16;

        // Byte 0: sample rate bits 19-12
        header[offset++] = (byte)(sampleRate >> 12);

        // Byte 1: sample rate bits 11-4
        header[offset++] = (byte)(sampleRate >> 4);

        // Byte 2: sample rate bits 3-0 (4 bits) | channels-1 (3 bits) | bps-1 bit 4
        header[offset++] = (byte)(
            ((sampleRate & 0x0F) << 4) |
            ((channels - 1) << 1) |
            ((bitsPerSample - 1) >> 4));

        // Byte 3: bps-1 bits 3-0 (4 bits) | total samples bits 35-32 (4 bits)
        header[offset++] = (byte)(((bitsPerSample - 1) & 0x0F) << 4);

        // Bytes 4-7: total samples bits 31-0 (we use 0 = unknown for streaming)
        header[offset++] = 0;
        header[offset++] = 0;
        header[offset++] = 0;
        header[offset++] = 0;

        // MD5 signature (16 bytes) - all zeros for streaming
        for (int i = 0; i < 16; i++)
        {
            header[offset++] = 0;
        }

        return header;
    }

    /// <summary>
    /// Converts SimpleFlac's per-channel long samples to interleaved floats.
    /// </summary>
    private void ConvertToInterleavedFloat(
        long[][] channelSamples,
        int sampleCount,
        int channelCount,
        Span<float> output)
    {
        var outputIndex = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            for (int ch = 0; ch < channelCount; ch++)
            {
                // Convert to normalized float [-1.0, 1.0]
                output[outputIndex++] = channelSamples[ch][i] * _sampleScaleFactor;
            }
        }
    }
}
