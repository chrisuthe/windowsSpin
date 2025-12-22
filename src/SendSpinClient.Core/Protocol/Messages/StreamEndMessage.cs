using System.Text.Json.Serialization;

namespace SendSpinClient.Core.Protocol.Messages;

/// <summary>
/// Message from server indicating audio stream has ended.
/// Client should stop playback and clear buffers.
/// </summary>
public sealed class StreamEndMessage : ServerMessage
{
    [JsonPropertyName("type")]
    public override string Type => MessageTypes.StreamEnd;

    /// <summary>
    /// Reason for stream ending.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>
    /// Stream identifier.
    /// </summary>
    [JsonPropertyName("stream_id")]
    public string? StreamId { get; set; }
}
