using System.Text.Json.Serialization;
using SendSpinClient.Core.Models;

namespace SendSpinClient.Core.Protocol.Messages;

/// <summary>
/// Initial handshake message sent by the client to announce its capabilities.
/// Uses the envelope format: { "type": "client/hello", "payload": { ... } }
/// </summary>
public sealed class ClientHelloMessage : IMessageWithPayload<ClientHelloPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientHello;

    [JsonPropertyName("payload")]
    required public ClientHelloPayload Payload { get; init; }

    /// <summary>
    /// Creates a ClientHelloMessage with the specified payload.
    /// </summary>
    public static ClientHelloMessage Create(
        string clientId,
        string name,
        List<string> supportedRoles,
        PlayerSupport? playerSupport = null,
        ArtworkSupport? artworkSupport = null,
        DeviceInfo? deviceInfo = null)
    {
        return new ClientHelloMessage
        {
            Payload = new ClientHelloPayload
            {
                ClientId = clientId,
                Name = name,
                Version = 1,
                SupportedRoles = supportedRoles,
                PlayerV1Support = playerSupport,
                ArtworkV1Support = artworkSupport,
                DeviceInfo = deviceInfo
            }
        };
    }
}

/// <summary>
/// Payload for the client/hello message.
/// </summary>
public sealed class ClientHelloPayload
{
    /// <summary>
    /// Unique client identifier (persistent across sessions).
    /// </summary>
    [JsonPropertyName("client_id")]
    required public string ClientId { get; init; }

    /// <summary>
    /// Human-readable client name.
    /// </summary>
    [JsonPropertyName("name")]
    required public string Name { get; init; }

    /// <summary>
    /// Protocol version (must be 1).
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// List of roles the client supports, in priority order.
    /// Each role includes version (e.g., "player@v1", "controller@v1").
    /// </summary>
    [JsonPropertyName("supported_roles")]
    required public List<string> SupportedRoles { get; init; }

    /// <summary>
    /// Player role support details.
    /// Note: aiosendspin uses "player_support" not "player@v1_support"
    /// </summary>
    [JsonPropertyName("player_support")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlayerSupport? PlayerV1Support { get; init; }

    /// <summary>
    /// Artwork role support details.
    /// Note: aiosendspin uses "artwork_support" not "artwork@v1_support"
    /// </summary>
    [JsonPropertyName("artwork_support")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArtworkSupport? ArtworkV1Support { get; init; }

    /// <summary>
    /// Device information.
    /// </summary>
    [JsonPropertyName("device_info")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeviceInfo? DeviceInfo { get; init; }
}

/// <summary>
/// Player role support details per the SendSpin spec.
/// </summary>
public sealed class PlayerSupport
{
    /// <summary>
    /// Supported audio formats.
    /// </summary>
    [JsonPropertyName("supported_formats")]
    public List<AudioFormatSpec> SupportedFormats { get; init; } = new();

    /// <summary>
    /// Audio buffer capacity in bytes.
    /// </summary>
    [JsonPropertyName("buffer_capacity")]
    public int BufferCapacity { get; init; } = 32_000_000; // 32MB like reference impl

    /// <summary>
    /// Supported player commands.
    /// </summary>
    [JsonPropertyName("supported_commands")]
    public List<string> SupportedCommands { get; init; } = new() { "volume", "mute" };
}

/// <summary>
/// Audio format specification for player support.
/// </summary>
public sealed class AudioFormatSpec
{
    [JsonPropertyName("codec")]
    required public string Codec { get; init; }

    [JsonPropertyName("channels")]
    public int Channels { get; init; } = 2;

    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; init; } = 48000;

    [JsonPropertyName("bit_depth")]
    public int BitDepth { get; init; } = 16;
}

/// <summary>
/// Artwork role support details.
/// </summary>
public sealed class ArtworkSupport
{
    /// <summary>
    /// Number of artwork channels.
    /// </summary>
    [JsonPropertyName("channels")]
    public int Channels { get; init; } = 1;

    /// <summary>
    /// Supported image formats.
    /// </summary>
    [JsonPropertyName("supported_formats")]
    public List<string> SupportedFormats { get; init; } = new() { "jpeg", "png" };

    /// <summary>
    /// Maximum artwork dimension in pixels.
    /// </summary>
    [JsonPropertyName("max_size")]
    public int MaxSize { get; init; } = 512;
}

/// <summary>
/// Device information.
/// </summary>
public sealed class DeviceInfo
{
    [JsonPropertyName("product_name")]
    public string? ProductName { get; init; } = "SendSpin Windows Client";

    [JsonPropertyName("manufacturer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Manufacturer { get; init; }

    [JsonPropertyName("software_version")]
    public string? SoftwareVersion { get; init; } = "0.1.0";
}
