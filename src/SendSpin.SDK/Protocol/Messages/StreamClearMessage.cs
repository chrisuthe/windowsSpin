// <copyright file="StreamClearMessage.cs" company="SendSpin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Text.Json.Serialization;

namespace SendSpin.SDK.Protocol.Messages;

/// <summary>
/// Message from server indicating audio buffers should be cleared.
/// Uses envelope format: { "type": "stream/clear", "payload": { ... } }.
/// </summary>
public sealed class StreamClearMessage : IMessageWithPayload<StreamClearPayload>
{
    /// <inheritdoc/>
    [JsonPropertyName("type")]
    public string Type => MessageTypes.StreamClear;

    /// <inheritdoc/>
    [JsonPropertyName("payload")]
    public StreamClearPayload Payload { get; set; } = new();

    // Convenience accessors
    [JsonIgnore]
    public string? StreamId => Payload.StreamId;

    [JsonIgnore]
    public long? TargetTimestamp => Payload.TargetTimestamp;
}

/// <summary>
/// Payload for stream/clear message.
/// </summary>
public sealed class StreamClearPayload
{
    /// <summary>
    /// Gets or sets the stream identifier.
    /// </summary>
    [JsonPropertyName("stream_id")]
    public string? StreamId { get; set; }

    /// <summary>
    /// Gets or sets the new target timestamp after clear (if seeking).
    /// </summary>
    [JsonPropertyName("target_timestamp")]
    public long? TargetTimestamp { get; set; }
}
