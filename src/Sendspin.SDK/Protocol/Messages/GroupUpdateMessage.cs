using System.Text.Json.Serialization;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Message from server with group state updates.
/// Sent when playback state, metadata, or volume changes.
/// Uses the envelope format: { "type": "group/update", "payload": { ... } }
/// </summary>
public sealed class GroupUpdateMessage : IMessageWithPayload<GroupUpdatePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.GroupUpdate;

    [JsonPropertyName("payload")]
    public GroupUpdatePayload Payload { get; set; } = new();

    // Convenience accessors (excluded from serialization)
    [JsonIgnore]
    public string GroupId => Payload.GroupId;
    [JsonIgnore]
    public PlaybackState? PlaybackState => Payload.PlaybackState;
    [JsonIgnore]
    public int? Volume => Payload.Volume;
    [JsonIgnore]
    public bool? Muted => Payload.Muted;
    [JsonIgnore]
    public TrackMetadata? Metadata => Payload.Metadata;
    [JsonIgnore]
    public double? Position => Payload.Position;
    [JsonIgnore]
    public bool? Shuffle => Payload.Shuffle;
    [JsonIgnore]
    public string? Repeat => Payload.Repeat;
}

/// <summary>
/// Payload for the group/update message.
/// </summary>
public sealed class GroupUpdatePayload
{
    /// <summary>
    /// Group identifier.
    /// </summary>
    [JsonPropertyName("group_id")]
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Current playback state.
    /// </summary>
    [JsonPropertyName("playback_state")]
    public PlaybackState? PlaybackState { get; set; }

    /// <summary>
    /// Group volume level (0-100).
    /// </summary>
    [JsonPropertyName("volume")]
    public int? Volume { get; set; }

    /// <summary>
    /// Whether the group is muted.
    /// </summary>
    [JsonPropertyName("muted")]
    public bool? Muted { get; set; }

    /// <summary>
    /// Current track metadata.
    /// </summary>
    [JsonPropertyName("metadata")]
    public TrackMetadata? Metadata { get; set; }

    /// <summary>
    /// Current playback position in seconds.
    /// </summary>
    [JsonPropertyName("position")]
    public double? Position { get; set; }

    /// <summary>
    /// Whether shuffle is enabled.
    /// </summary>
    [JsonPropertyName("shuffle")]
    public bool? Shuffle { get; set; }

    /// <summary>
    /// Repeat mode.
    /// </summary>
    [JsonPropertyName("repeat")]
    public string? Repeat { get; set; }
}
