using System.Text.Json.Serialization;

namespace Sendspin.SDK.Models;

/// <summary>
/// Aggregate state for display purposes. Populated from multiple message types.
/// </summary>
/// <remarks>
/// <para>Field sources per Sendspin spec:</para>
/// <list type="bullet">
///   <item><c>GroupId</c>, <c>Name</c>, <c>PlaybackState</c> - from <c>group/update</c></item>
///   <item><c>Volume</c>, <c>Muted</c> - from <c>server/state</c> controller object</item>
///   <item><c>Metadata</c>, <c>Shuffle</c>, <c>Repeat</c> - from <c>server/state</c> metadata object</item>
/// </list>
/// <para>
/// Note: Per Sendspin spec, <c>group/update</c> only contains playback state and identity.
/// Volume, mute, and metadata are delivered separately via <c>server/state</c>.
/// </para>
/// </remarks>
public sealed class GroupState
{
    /// <summary>
    /// Unique group identifier.
    /// </summary>
    /// <remarks>Source: <c>group/update</c> message.</remarks>
    [JsonPropertyName("group_id")]
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Display name for the group.
    /// </summary>
    /// <remarks>Source: <c>group/update</c> message.</remarks>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Current playback state (playing, paused, stopped, idle).
    /// </summary>
    /// <remarks>Source: <c>group/update</c> message.</remarks>
    [JsonPropertyName("playback_state")]
    public PlaybackState PlaybackState { get; set; } = PlaybackState.Idle;

    /// <summary>
    /// Group volume level (0-100).
    /// </summary>
    /// <remarks>Source: <c>server/state</c> controller object.</remarks>
    [JsonPropertyName("volume")]
    public int Volume { get; set; } = 100;

    /// <summary>
    /// Whether the group is muted.
    /// </summary>
    /// <remarks>Source: <c>server/state</c> controller object.</remarks>
    [JsonPropertyName("muted")]
    public bool Muted { get; set; }

    /// <summary>
    /// Current track metadata.
    /// </summary>
    /// <remarks>Source: <c>server/state</c> metadata object.</remarks>
    [JsonPropertyName("metadata")]
    public TrackMetadata? Metadata { get; set; }

    /// <summary>
    /// Whether shuffle is enabled.
    /// </summary>
    /// <remarks>Source: <c>server/state</c> metadata object.</remarks>
    [JsonPropertyName("shuffle")]
    public bool Shuffle { get; set; }

    /// <summary>
    /// Repeat mode ("off", "one", "all").
    /// </summary>
    /// <remarks>Source: <c>server/state</c> metadata object.</remarks>
    [JsonPropertyName("repeat")]
    public string? Repeat { get; set; }
}
