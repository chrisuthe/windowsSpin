using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// State update message sent from client to server.
/// Used to report client state (synchronized, error, external_source)
/// and player state (volume, mute).
/// </summary>
public sealed class ClientStateMessage : IMessageWithPayload<ClientStatePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientState;

    [JsonPropertyName("payload")]
    required public ClientStatePayload Payload { get; init; }

    /// <summary>
    /// Creates a synchronized state message with player volume/mute.
    /// This should be sent immediately after receiving server/hello.
    /// </summary>
    public static ClientStateMessage CreateSynchronized(int volume = 100, bool muted = false)
    {
        return new ClientStateMessage
        {
            Payload = new ClientStatePayload
            {
                State = "synchronized",
                Player = new PlayerStatePayload
                {
                    Volume = volume,
                    Muted = muted
                }
            }
        };
    }

    /// <summary>
    /// Creates an error state message.
    /// </summary>
    public static ClientStateMessage CreateError(string? errorMessage = null)
    {
        return new ClientStateMessage
        {
            Payload = new ClientStatePayload
            {
                State = "error",
                Player = errorMessage != null ? new PlayerStatePayload { Error = errorMessage } : null
            }
        };
    }
}

/// <summary>
/// Payload for client/state message.
/// </summary>
public sealed class ClientStatePayload
{
    /// <summary>
    /// Client state: "synchronized", "error", or "external_source".
    /// </summary>
    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? State { get; init; }

    /// <summary>
    /// Player-specific state (volume, mute, buffer level).
    /// Only included if client has player role.
    /// </summary>
    [JsonPropertyName("player")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlayerStatePayload? Player { get; init; }
}

/// <summary>
/// Player-specific state within client/state message.
/// </summary>
public sealed class PlayerStatePayload
{
    /// <summary>
    /// Player volume (0-100).
    /// </summary>
    [JsonPropertyName("volume")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Volume { get; init; }

    /// <summary>
    /// Whether the player is muted.
    /// </summary>
    [JsonPropertyName("muted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Muted { get; init; }

    /// <summary>
    /// Buffer level in milliseconds.
    /// </summary>
    [JsonPropertyName("buffer_level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BufferLevel { get; init; }

    /// <summary>
    /// Error message if in error state.
    /// </summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}
