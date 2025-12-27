using System.Text.Json.Serialization;

namespace SendSpin.SDK.Models;

/// <summary>
/// Represents metadata about the currently playing track.
/// </summary>
public sealed class TrackMetadata
{
    /// <summary>
    /// Track title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Artist name(s).
    /// </summary>
    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    /// <summary>
    /// Album name.
    /// </summary>
    [JsonPropertyName("album")]
    public string? Album { get; set; }

    /// <summary>
    /// Track duration in seconds.
    /// </summary>
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    /// <summary>
    /// Current playback position in seconds.
    /// </summary>
    [JsonPropertyName("position")]
    public double? Position { get; set; }

    /// <summary>
    /// URI or identifier for album artwork.
    /// </summary>
    [JsonPropertyName("artwork_uri")]
    public string? ArtworkUri { get; set; }

    /// <summary>
    /// URL for album artwork (from server/state messages).
    /// </summary>
    [JsonPropertyName("artwork_url")]
    public string? ArtworkUrl { get; set; }

    /// <summary>
    /// Track URI or identifier.
    /// </summary>
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    /// <summary>
    /// Media type (e.g., "track", "radio", "podcast").
    /// </summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    public override string ToString()
    {
        if (!string.IsNullOrEmpty(Artist))
            return $"{Artist} - {Title}";
        return Title ?? "Unknown Track";
    }
}
