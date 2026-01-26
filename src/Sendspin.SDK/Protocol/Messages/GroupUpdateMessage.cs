using System.Text.Json.Serialization;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Message from server with group state updates.
/// Sent when playback state or group identity changes.
/// Uses the envelope format: { "type": "group/update", "payload": { ... } }
/// </summary>
/// <remarks>
/// <para>
/// Per Sendspin spec, <c>group/update</c> only contains playback state and group identity.
/// Volume, mute, and metadata are delivered separately via <c>server/state</c>.
/// </para>
/// </remarks>
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
    public string? GroupName => Payload.GroupName;
    [JsonIgnore]
    public PlaybackState? PlaybackState => Payload.PlaybackState;
}

/// <summary>
/// Payload for the group/update message per Sendspin spec.
/// </summary>
/// <remarks>
/// Contains only playback state and group identity.
/// Volume/mute comes via <c>server/state</c> controller object.
/// Metadata comes via <c>server/state</c> metadata object.
/// </remarks>
public sealed class GroupUpdatePayload
{
    /// <summary>
    /// Group identifier.
    /// </summary>
    [JsonPropertyName("group_id")]
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Friendly display name of the group.
    /// </summary>
    [JsonPropertyName("group_name")]
    public string? GroupName { get; set; }

    /// <summary>
    /// Current playback state (playing, paused, stopped).
    /// </summary>
    [JsonPropertyName("playback_state")]
    public PlaybackState? PlaybackState { get; set; }
}
