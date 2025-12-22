using System.Text.Json.Serialization;

namespace SendSpinClient.Core.Models;

/// <summary>
/// Represents an audio format specification.
/// </summary>
public sealed class AudioFormat
{
    /// <summary>
    /// Audio codec (e.g., "opus", "flac", "pcm").
    /// </summary>
    [JsonPropertyName("codec")]
    public string Codec { get; set; } = "opus";

    /// <summary>
    /// Sample rate in Hz (e.g., 44100, 48000).
    /// </summary>
    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; set; } = 48000;

    /// <summary>
    /// Number of audio channels (1 = mono, 2 = stereo).
    /// </summary>
    [JsonPropertyName("channels")]
    public int Channels { get; set; } = 2;

    /// <summary>
    /// Bits per sample (for PCM: 16, 24, 32).
    /// </summary>
    [JsonPropertyName("bit_depth")]
    public int? BitDepth { get; set; }

    /// <summary>
    /// Bitrate in kbps (for lossy codecs like Opus).
    /// </summary>
    [JsonPropertyName("bitrate")]
    public int? Bitrate { get; set; }

    /// <summary>
    /// Codec-specific header data (base64 encoded).
    /// For FLAC, this contains the STREAMINFO block.
    /// </summary>
    [JsonPropertyName("codec_header")]
    public string? CodecHeader { get; set; }

    public override string ToString()
    {
        var bitInfo = Bitrate.HasValue ? $" @ {Bitrate}kbps" : BitDepth.HasValue ? $" {BitDepth}bit" : "";
        return $"{Codec.ToUpperInvariant()} {SampleRate}Hz {Channels}ch{bitInfo}";
    }
}

/// <summary>
/// Common audio codec identifiers used in the SendSpin protocol.
/// </summary>
public static class AudioCodecs
{
    public const string Opus = "opus";
    public const string Flac = "flac";
    public const string Pcm = "pcm";
}
