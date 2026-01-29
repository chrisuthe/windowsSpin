using System.Text.Json.Serialization;
using Sendspin.SDK.Protocol;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// State update message from server containing metadata and controller state.
/// This is the primary way Music Assistant sends track metadata to clients.
/// </summary>
public sealed class ServerStateMessage : IMessageWithPayload<ServerStatePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerState;

    [JsonPropertyName("payload")]
    required public ServerStatePayload Payload { get; init; }
}

/// <summary>
/// Payload for server/state message.
/// </summary>
public sealed class ServerStatePayload
{
    /// <summary>
    /// Current track metadata and playback progress.
    /// </summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerMetadata? Metadata { get; init; }

    /// <summary>
    /// Controller state (volume, mute, supported commands).
    /// </summary>
    [JsonPropertyName("controller")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ControllerState? Controller { get; init; }
}

/// <summary>
/// Track metadata from server/state message.
/// </summary>
public sealed class ServerMetadata
{
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("artist")]
    public string? Artist { get; init; }

    [JsonPropertyName("album_artist")]
    public string? AlbumArtist { get; init; }

    [JsonPropertyName("album")]
    public string? Album { get; init; }

    [JsonPropertyName("artwork_url")]
    public string? ArtworkUrl { get; init; }

    [JsonPropertyName("year")]
    public int? Year { get; init; }

    [JsonPropertyName("track")]
    public int? Track { get; init; }

    /// <summary>
    /// Playback progress information.
    /// Uses <see cref="Optional{T}"/> to distinguish:
    /// <list type="bullet">
    ///   <item><description><b>Absent</b>: No progress update (keep existing)</description></item>
    ///   <item><description><b>Present but null</b>: Track ended (clear progress)</description></item>
    ///   <item><description><b>Present with value</b>: Update progress</description></item>
    /// </list>
    /// </summary>
    [JsonPropertyName("progress")]
    public Optional<PlaybackProgress?> Progress { get; init; } = Optional<PlaybackProgress?>.Absent();

    [JsonPropertyName("repeat")]
    public string? Repeat { get; init; }

    [JsonPropertyName("shuffle")]
    public bool? Shuffle { get; init; }
}

/// <summary>
/// Playback progress information.
/// </summary>
public sealed class PlaybackProgress
{
    /// <summary>
    /// Current position in milliseconds.
    /// Using double to handle servers that send numeric values as floats.
    /// </summary>
    [JsonPropertyName("track_progress")]
    public double? TrackProgress { get; init; }

    /// <summary>
    /// Total duration in milliseconds.
    /// Using double to handle servers that send numeric values as floats.
    /// Nullable for streams with unknown duration.
    /// </summary>
    [JsonPropertyName("track_duration")]
    public double? TrackDuration { get; init; }

    /// <summary>
    /// Playback speed (1000 = normal speed).
    /// Using double to handle servers that send numeric values as floats.
    /// </summary>
    [JsonPropertyName("playback_speed")]
    public double? PlaybackSpeed { get; init; }
}

/// <summary>
/// Controller state from server/state message.
/// </summary>
public sealed class ControllerState
{
    [JsonPropertyName("supported_commands")]
    public List<string>? SupportedCommands { get; init; }

    [JsonPropertyName("volume")]
    public int? Volume { get; init; }

    [JsonPropertyName("muted")]
    public bool? Muted { get; init; }
}
