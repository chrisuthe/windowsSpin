// <copyright file="StreamStartMessage.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Text.Json.Serialization;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Protocol.Messages;

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

    // Convenience accessor
    [JsonIgnore]
    public AudioFormat Format => Payload.Format;
}

/// <summary>
/// Payload for stream/start message per Sendspin spec.
/// </summary>
public sealed class StreamStartPayload
{
    /// <summary>
    /// Gets or sets the audio format for the incoming stream.
    /// The "player" object contains codec, channels, sample_rate, bit_depth, and codec_header.
    /// </summary>
    [JsonPropertyName("player")]
    public AudioFormat Format { get; set; } = new();
}
