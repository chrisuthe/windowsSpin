using System.Text.Json.Serialization;
using SendSpin.SDK.Models;

namespace SendSpin.SDK.Protocol.Messages;

/// <summary>
/// Message from client requesting a format change for the audio stream.
/// Server will respond with a new stream/start if accepted.
/// </summary>
public sealed class StreamRequestFormatMessage : ClientMessage
{
    [JsonPropertyName("type")]
    public override string Type => MessageTypes.StreamRequestFormat;

    /// <summary>
    /// Requested audio format.
    /// </summary>
    [JsonPropertyName("format")]
    required public AudioFormat Format { get; init; }

    /// <summary>
    /// Stream identifier.
    /// </summary>
    [JsonPropertyName("stream_id")]
    public string? StreamId { get; init; }
}
