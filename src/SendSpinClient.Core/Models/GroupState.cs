using System.Text.Json.Serialization;

namespace SendSpinClient.Core.Models;

/// <summary>
/// Represents the state of a player group.
/// </summary>
public sealed class GroupState
{
    /// <summary>
    /// Unique group identifier.
    /// </summary>
    [JsonPropertyName("group_id")]
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Display name for the group.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Current playback state.
    /// </summary>
    [JsonPropertyName("playback_state")]
    public PlaybackState PlaybackState { get; set; } = PlaybackState.Idle;

    /// <summary>
    /// Group volume level (0-100).
    /// </summary>
    [JsonPropertyName("volume")]
    public int Volume { get; set; } = 100;

    /// <summary>
    /// Whether the group is muted.
    /// </summary>
    [JsonPropertyName("muted")]
    public bool Muted { get; set; }

    /// <summary>
    /// Current track metadata.
    /// </summary>
    [JsonPropertyName("metadata")]
    public TrackMetadata? Metadata { get; set; }

    /// <summary>
    /// Whether shuffle is enabled.
    /// </summary>
    [JsonPropertyName("shuffle")]
    public bool Shuffle { get; set; }

    /// <summary>
    /// Repeat mode ("off", "one", "all").
    /// </summary>
    [JsonPropertyName("repeat")]
    public string? Repeat { get; set; }
}
