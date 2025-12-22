// <copyright file="StreamEndMessage.cs" company="SendSpin">
// Copyright (c) SendSpin. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace SendSpinClient.Core.Protocol.Messages;

/// <summary>
/// Message from server indicating audio stream has ended.
/// Uses envelope format: { "type": "stream/end", "payload": { ... } }.
/// </summary>
public sealed class StreamEndMessage : IMessageWithPayload<StreamEndPayload>
{
    /// <inheritdoc/>
    [JsonPropertyName("type")]
    public string Type => MessageTypes.StreamEnd;

    /// <inheritdoc/>
    [JsonPropertyName("payload")]
    public StreamEndPayload Payload { get; set; } = new();

    // Convenience accessors
    [JsonIgnore]
    public string? Reason => Payload.Reason;

    [JsonIgnore]
    public string? StreamId => Payload.StreamId;
}

/// <summary>
/// Payload for stream/end message.
/// </summary>
public sealed class StreamEndPayload
{
    /// <summary>
    /// Gets or sets the reason for stream ending.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets the stream identifier.
    /// </summary>
    [JsonPropertyName("stream_id")]
    public string? StreamId { get; set; }
}
