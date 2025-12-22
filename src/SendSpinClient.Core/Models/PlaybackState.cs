using System.Text.Json.Serialization;

namespace SendSpinClient.Core.Models;

/// <summary>
/// Represents the current playback state of a group.
/// JSON serialization uses snake_case naming via JsonStringEnumConverter.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PlaybackState>))]
public enum PlaybackState
{
    /// <summary>
    /// No media loaded or stopped.
    /// </summary>
    [JsonPropertyName("idle")]
    Idle,

    /// <summary>
    /// Stopped state (alias for Idle, used by some servers).
    /// </summary>
    [JsonPropertyName("stopped")]
    Stopped,

    /// <summary>
    /// Currently playing audio.
    /// </summary>
    [JsonPropertyName("playing")]
    Playing,

    /// <summary>
    /// Playback paused.
    /// </summary>
    [JsonPropertyName("paused")]
    Paused,

    /// <summary>
    /// Error state (e.g., buffer underrun, codec error).
    /// </summary>
    [JsonPropertyName("error")]
    Error
}
