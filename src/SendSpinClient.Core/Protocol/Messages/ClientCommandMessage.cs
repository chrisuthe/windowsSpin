using System.Text.Json.Serialization;

namespace SendSpinClient.Core.Protocol.Messages;

/// <summary>
/// Command message sent from client to control playback.
/// Uses the envelope format: { "type": "client/command", "payload": { "controller": { ... } } }
/// </summary>
public sealed class ClientCommandMessage : IMessageWithPayload<ClientCommandPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientCommand;

    [JsonPropertyName("payload")]
    required public ClientCommandPayload Payload { get; init; }

    /// <summary>
    /// Creates a command message with the specified command.
    /// </summary>
    public static ClientCommandMessage Create(string command, int? volume = null, bool? mute = null)
    {
        return new ClientCommandMessage
        {
            Payload = new ClientCommandPayload
            {
                Controller = new ControllerCommand
                {
                    Command = command,
                    Volume = volume,
                    Mute = mute
                }
            }
        };
    }
}

/// <summary>
/// Payload for client/command message.
/// </summary>
public sealed class ClientCommandPayload
{
    /// <summary>
    /// Controller commands for playback control.
    /// </summary>
    [JsonPropertyName("controller")]
    required public ControllerCommand Controller { get; init; }
}

/// <summary>
/// Controller command details.
/// </summary>
public sealed class ControllerCommand
{
    /// <summary>
    /// Command to execute (e.g., "play", "pause", "next", "previous").
    /// </summary>
    [JsonPropertyName("command")]
    required public string Command { get; init; }

    /// <summary>
    /// Volume level (0-100), only used when command is "volume".
    /// </summary>
    [JsonPropertyName("volume")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Volume { get; init; }

    /// <summary>
    /// Mute state, only used when command is "mute".
    /// </summary>
    [JsonPropertyName("mute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Mute { get; init; }
}

/// <summary>
/// Common command identifiers per the SendSpin spec.
/// </summary>
public static class Commands
{
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Stop = "stop";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string Volume = "volume";
    public const string Mute = "mute";
    public const string Shuffle = "shuffle";
    public const string Unshuffle = "unshuffle";
    public const string RepeatOff = "repeat_off";
    public const string RepeatOne = "repeat_one";
    public const string RepeatAll = "repeat_all";
    public const string Switch = "switch";
}
