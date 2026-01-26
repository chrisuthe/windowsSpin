using System.Text.Json.Serialization;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Models;

/// <summary>
/// Track metadata per Sendspin spec (server/state metadata object).
/// </summary>
/// <remarks>
/// <para>This model aligns with the Sendspin protocol specification for metadata.</para>
/// <para>All fields are populated from <c>server/state</c> message's metadata object.</para>
/// <para>
/// Duration and Position are computed from the nested Progress object for
/// backward compatibility with existing consumers.
/// </para>
/// </remarks>
public sealed class TrackMetadata
{
    /// <summary>
    /// Server timestamp (microseconds) when this metadata is valid.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

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
    /// Album artist (may differ from track artist on compilations).
    /// </summary>
    [JsonPropertyName("album_artist")]
    public string? AlbumArtist { get; set; }

    /// <summary>
    /// Album name.
    /// </summary>
    [JsonPropertyName("album")]
    public string? Album { get; set; }

    /// <summary>
    /// URL for album artwork.
    /// </summary>
    [JsonPropertyName("artwork_url")]
    public string? ArtworkUrl { get; set; }

    /// <summary>
    /// Release year.
    /// </summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>
    /// Track number on album.
    /// </summary>
    [JsonPropertyName("track")]
    public int? Track { get; set; }

    /// <summary>
    /// Playback progress information (position, duration, speed).
    /// </summary>
    [JsonPropertyName("progress")]
    public PlaybackProgress? Progress { get; set; }

    /// <summary>
    /// Repeat mode ("off", "one", "all").
    /// </summary>
    [JsonPropertyName("repeat")]
    public string? Repeat { get; set; }

    /// <summary>
    /// Whether shuffle is enabled.
    /// </summary>
    [JsonPropertyName("shuffle")]
    public bool? Shuffle { get; set; }

    /// <summary>
    /// Track duration in seconds (computed from Progress.TrackDuration).
    /// </summary>
    /// <remarks>
    /// Backward-compatibility property. Progress.TrackDuration is in milliseconds per spec.
    /// </remarks>
    [JsonIgnore]
    public double? Duration => Progress?.TrackDuration / 1000.0;

    /// <summary>
    /// Current playback position in seconds (computed from Progress.TrackProgress).
    /// </summary>
    /// <remarks>
    /// Backward-compatibility property. Progress.TrackProgress is in milliseconds per spec.
    /// </remarks>
    [JsonIgnore]
    public double? Position => Progress?.TrackProgress / 1000.0;

    /// <inheritdoc/>
    public override string ToString()
    {
        if (!string.IsNullOrEmpty(Artist))
            return $"{Artist} - {Title}";
        return Title ?? "Unknown Track";
    }
}
