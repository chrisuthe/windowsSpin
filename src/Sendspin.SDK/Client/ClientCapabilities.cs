using Sendspin.SDK.Models;

namespace Sendspin.SDK.Client;

/// <summary>
/// Defines the capabilities this client advertises to the server.
/// </summary>
public sealed class ClientCapabilities
{
    /// <summary>
    /// Unique client identifier (persisted across sessions).
    /// Format follows reference implementation: sendspin-windows-{hostname}
    /// </summary>
    public string ClientId { get; set; } = $"sendspin-windows-{Environment.MachineName.ToLowerInvariant()}";

    /// <summary>
    /// Human-readable client name.
    /// </summary>
    public string ClientName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Roles this client supports, in priority order.
    /// Matches reference implementation: controller, player, metadata (no artwork for now).
    /// </summary>
    public List<string> Roles { get; set; } = new()
    {
        "controller@v1",
        "player@v1",
        "metadata@v1"
    };

    /// <summary>
    /// Audio formats the client can decode.
    /// Order matters - server picks the first format it supports.
    /// </summary>
    public List<AudioFormat> AudioFormats { get; set; } = new()
    {
        new AudioFormat { Codec = "opus", SampleRate = 48000, Channels = 2, Bitrate = 256 },
        new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 },
        new AudioFormat { Codec = "flac", SampleRate = 48000, Channels = 2 },  // Last - server prefers earlier formats
    };

    /// <summary>
    /// Audio buffer capacity in bytes (32MB like reference implementation).
    /// </summary>
    public int BufferCapacity { get; set; } = 32_000_000;

    /// <summary>
    /// Preferred artwork formats.
    /// </summary>
    public List<string> ArtworkFormats { get; set; } = new() { "jpeg", "png" };

    /// <summary>
    /// Maximum artwork dimension.
    /// </summary>
    public int ArtworkMaxSize { get; set; } = 512;
}
