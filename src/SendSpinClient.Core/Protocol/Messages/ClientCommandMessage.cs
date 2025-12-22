using System.Text.Json.Serialization;

namespace SendSpinClient.Core.Protocol.Messages;

/// <summary>
/// Command message sent from client to control playback.
/// </summary>
public sealed class ClientCommandMessage : ClientMessage
{
    [JsonPropertyName("type")]
    public override string Type => MessageTypes.ClientCommand;

    /// <summary>
    /// Command to execute (e.g., "play", "pause", "next", "previous").
    /// </summary>
    [JsonPropertyName("command")]
    required public string Command { get; init; }

    /// <summary>
    /// Target group ID.
    /// </summary>
    [JsonPropertyName("group_id")]
    public string? GroupId { get; init; }

    /// <summary>
    /// Additional command parameters.
    /// </summary>
    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Params { get; init; }
}

/// <summary>
/// Common command identifiers.
/// </summary>
public static class Commands
{
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Stop = "stop";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string Seek = "seek";
    public const string SetVolume = "set_volume";
    public const string SetMute = "set_mute";
    public const string ToggleShuffle = "toggle_shuffle";
    public const string SetRepeat = "set_repeat";
}
