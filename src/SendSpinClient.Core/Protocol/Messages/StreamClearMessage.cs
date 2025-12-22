using System.Text.Json.Serialization;

namespace SendSpinClient.Core.Protocol.Messages;

/// <summary>
/// Message from server indicating audio buffers should be cleared.
/// Typically sent when seeking to a new position.
/// </summary>
public sealed class StreamClearMessage : ServerMessage
{
    [JsonPropertyName("type")]
    public override string Type => MessageTypes.StreamClear;

    /// <summary>
    /// Stream identifier.
    /// </summary>
    [JsonPropertyName("stream_id")]
    public string? StreamId { get; set; }

    /// <summary>
    /// New target timestamp after clear (if seeking).
    /// </summary>
    [JsonPropertyName("target_timestamp")]
    public long? TargetTimestamp { get; set; }
}
