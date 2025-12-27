using System.Text.Json.Serialization;

namespace SendSpin.SDK.Protocol.Messages;

/// <summary>
/// Base interface for all SendSpin protocol messages.
/// Messages use an envelope format: { "type": "...", "payload": { ... } }
/// </summary>
public interface IMessage
{
    /// <summary>
    /// The message type identifier (e.g., "client/hello", "server/time").
    /// </summary>
    [JsonPropertyName("type")]
    string Type { get; }
}

/// <summary>
/// Interface for messages with a payload wrapper.
/// </summary>
public interface IMessageWithPayload<TPayload> : IMessage where TPayload : class
{
    /// <summary>
    /// The message payload containing all message-specific data.
    /// </summary>
    [JsonPropertyName("payload")]
    TPayload Payload { get; }
}

/// <summary>
/// Generic message envelope that wraps any payload with a type.
/// </summary>
public sealed class MessageEnvelope<TPayload> : IMessageWithPayload<TPayload> where TPayload : class
{
    [JsonPropertyName("type")]
    required public string Type { get; init; }

    [JsonPropertyName("payload")]
    required public TPayload Payload { get; init; }
}

/// <summary>
/// Base class for client-originated messages.
/// </summary>
public abstract class ClientMessage : IMessage
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

/// <summary>
/// Base class for server-originated messages.
/// </summary>
public abstract class ServerMessage : IMessage
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}
