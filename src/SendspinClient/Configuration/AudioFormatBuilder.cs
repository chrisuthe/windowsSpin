// <copyright file="AudioFormatBuilder.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.SDK.Models;
using SendspinClient.Services.Models;

namespace SendspinClient.Configuration;

/// <summary>
/// Builds audio format lists based on device capabilities for protocol negotiation.
/// </summary>
/// <remarks>
/// The order of formats in the list determines server preference - servers pick the
/// first format they support. This builder creates a prioritized list based on:
/// 1. User's preferred codec (FLAC or Opus)
/// 2. Device's native capabilities (sample rate, bit depth)
/// 3. Fallback formats for compatibility with older servers
/// </remarks>
public static class AudioFormatBuilder
{
    /// <summary>
    /// Builds a list of audio formats to advertise to the server.
    /// </summary>
    /// <param name="capabilities">The device's native audio capabilities.</param>
    /// <param name="preferredCodec">The user's preferred codec ("flac" or "opus").</param>
    /// <returns>An ordered list of supported audio formats.</returns>
    public static List<AudioFormat> BuildFormats(
        AudioDeviceCapabilities capabilities,
        string preferredCodec)
    {
        var formats = new List<AudioFormat>();
        var sampleRate = capabilities.NativeSampleRate;

        // WASAPI MixFormat reports 32 for 32-bit float (Windows Audio Engine internal format).
        // Cap at 24-bit since that's the max standard hi-res PCM bit depth.
        // When WASAPI reports 32-bit, the device can handle any format via Windows resampling.
        var bitDepth = Math.Min(capabilities.NativeBitDepth, 24);

        // Preferred codec first at native resolution
        if (preferredCodec == "flac")
        {
            // FLAC supports any sample rate and bit depth
            formats.Add(new AudioFormat
            {
                Codec = "flac",
                SampleRate = sampleRate,
                Channels = 2,
                BitDepth = bitDepth,
            });

            // Opus is capped at 48kHz per codec specification
            formats.Add(new AudioFormat
            {
                Codec = "opus",
                SampleRate = Math.Min(sampleRate, 48000),
                Channels = 2,
                Bitrate = 256,
            });
        }
        else
        {
            // Opus preferred - capped at 48kHz
            formats.Add(new AudioFormat
            {
                Codec = "opus",
                SampleRate = Math.Min(sampleRate, 48000),
                Channels = 2,
                Bitrate = 256,
            });

            // FLAC at native resolution
            formats.Add(new AudioFormat
            {
                Codec = "flac",
                SampleRate = sampleRate,
                Channels = 2,
                BitDepth = bitDepth,
            });
        }

        // PCM at native format as universal fallback
        formats.Add(new AudioFormat
        {
            Codec = "pcm",
            SampleRate = sampleRate,
            Channels = 2,
            BitDepth = bitDepth,
        });

        // Always include 48kHz/16-bit fallback for compatibility with older servers
        if (sampleRate != 48000 || bitDepth != 16)
        {
            formats.Add(new AudioFormat
            {
                Codec = "flac",
                SampleRate = 48000,
                Channels = 2,
                BitDepth = 16,
            });

            formats.Add(new AudioFormat
            {
                Codec = "pcm",
                SampleRate = 48000,
                Channels = 2,
                BitDepth = 16,
            });
        }

        return formats;
    }

    /// <summary>
    /// Gets a display string showing the codec priority order.
    /// </summary>
    /// <param name="formats">The list of audio formats.</param>
    /// <returns>A string like "FLAC → Opus → PCM".</returns>
    public static string GetCodecOrderDisplay(IEnumerable<AudioFormat> formats)
    {
        var codecs = formats
            .Select(f => f.Codec.ToUpperInvariant())
            .Distinct();
        return string.Join(" → ", codecs);
    }

    /// <summary>
    /// Gets a display string showing the unique format resolutions.
    /// </summary>
    /// <param name="formats">The list of audio formats.</param>
    /// <returns>A string like "96kHz/24-bit, 48kHz/16-bit".</returns>
    public static string GetFormatsDisplay(IEnumerable<AudioFormat> formats)
    {
        var resolutions = formats
            .Select(f => $"{f.SampleRate / 1000.0:0.#}kHz/{f.BitDepth ?? 16}-bit")
            .Distinct();
        return string.Join(", ", resolutions);
    }
}
