// <copyright file="StreamStartMessage.cs" company="SendSpin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Text.Json.Serialization;
using SendSpinClient.Core.Models;

namespace SendSpinClient.Core.Protocol.Messages;

/// <summary>
/// Message from server indicating audio stream is starting.
/// Uses envelope format: { "type": "stream/start", "payload": { ... } }.
/// </summary>
public sealed class StreamStartMessage : IMessageWithPayload<StreamStartPayload>
{
    /// <inheritdoc/>
    [JsonPropertyName("type")]
    public string Type => MessageTypes.StreamStart;

    /// <inheritdoc/>
    [JsonPropertyName("payload")]
    public StreamStartPayload Payload { get; set; } = new();

    // Convenience accessors
    [JsonIgnore]
    public AudioFormat Format => Payload.Format;

    [JsonIgnore]
    public string? StreamId => Payload.StreamId;

    [JsonIgnore]
    public long? TargetTimestamp => Payload.TargetTimestamp;
}

/// <summary>
/// Payload for stream/start message.
/// </summary>
public sealed class StreamStartPayload
{
    /// <summary>
    /// Gets or sets the audio format for the incoming stream.
    /// </summary>
    [JsonPropertyName("format")]
    public AudioFormat Format { get; set; } = new();

    /// <summary>
    /// Gets or sets the stream identifier (for multi-stream scenarios).
    /// </summary>
    [JsonPropertyName("stream_id")]
    public string? StreamId { get; set; }

    /// <summary>
    /// Gets or sets the playback target timestamp in server time (microseconds).
    /// Audio chunks should be played at or after this time.
    /// </summary>
    [JsonPropertyName("target_timestamp")]
    public long? TargetTimestamp { get; set; }
}
