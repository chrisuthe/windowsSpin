using System.Text.Json.Serialization;
using SendSpinClient.Core.Models;

namespace SendSpinClient.Core.Protocol.Messages;

/// <summary>
/// Message from server indicating audio stream is starting.
/// Contains format information for decoder initialization.
/// </summary>
public sealed class StreamStartMessage : ServerMessage
{
    [JsonPropertyName("type")]
    public override string Type => MessageTypes.StreamStart;

    /// <summary>
    /// Audio format for the incoming stream.
    /// </summary>
    [JsonPropertyName("format")]
    public AudioFormat Format { get; set; } = new();

    /// <summary>
    /// Stream identifier (for multi-stream scenarios).
    /// </summary>
    [JsonPropertyName("stream_id")]
    public string? StreamId { get; set; }

    /// <summary>
    /// Playback target timestamp in server time (microseconds).
    /// Audio chunks should be played at or after this time.
    /// </summary>
    [JsonPropertyName("target_timestamp")]
    public long? TargetTimestamp { get; set; }
}
