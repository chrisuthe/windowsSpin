using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Message sent by client when disconnecting gracefully.
/// Uses the envelope format: { "type": "client/goodbye", "payload": { ... } }
/// </summary>
public sealed class ClientGoodbyeMessage : IMessageWithPayload<ClientGoodbyePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientGoodbye;

    [JsonPropertyName("payload")]
    required public ClientGoodbyePayload Payload { get; init; }

    /// <summary>
    /// Creates a ClientGoodbyeMessage with the specified reason.
    /// </summary>
    public static ClientGoodbyeMessage Create(string reason = "user_request")
    {
        return new ClientGoodbyeMessage
        {
            Payload = new ClientGoodbyePayload { Reason = reason }
        };
    }
}

/// <summary>
/// Payload for the client/goodbye message.
/// </summary>
public sealed class ClientGoodbyePayload
{
    /// <summary>
    /// Reason for disconnection.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "user_request";
}
