using System.Text.Json.Serialization;

namespace SendSpinClient.Core.Protocol.Messages;

/// <summary>
/// Command message from server to control player state.
/// The server sends this to tell players what volume/mute to apply locally.
/// </summary>
public sealed class ServerCommandMessage : IMessageWithPayload<ServerCommandPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerCommand;

    [JsonPropertyName("payload")]
    required public ServerCommandPayload Payload { get; init; }
}

/// <summary>
/// Payload for server/command message.
/// </summary>
public sealed class ServerCommandPayload
{
    /// <summary>
    /// Player command details (volume, mute).
    /// </summary>
    [JsonPropertyName("player")]
    public PlayerCommand? Player { get; init; }
}

/// <summary>
/// Player command details from server.
/// Null properties indicate the server is not requesting a change to that setting.
/// </summary>
public sealed class PlayerCommand
{
    /// <summary>
    /// The command type (e.g., "volume", "mute").
    /// </summary>
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    /// <summary>
    /// Volume level (0-100). Null if volume is not being changed.
    /// </summary>
    [JsonPropertyName("volume")]
    public int? Volume { get; init; }

    /// <summary>
    /// Mute state. Null if mute is not being changed.
    /// </summary>
    [JsonPropertyName("mute")]
    public bool? Mute { get; init; }
}
