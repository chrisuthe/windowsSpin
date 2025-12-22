using System.Text.Json.Serialization;

namespace SendSpinClient.Core.Protocol.Messages;

/// <summary>
/// Message sent by client when disconnecting gracefully.
/// </summary>
public sealed class ClientGoodbyeMessage : ClientMessage
{
    [JsonPropertyName("type")]
    public override string Type => MessageTypes.ClientGoodbye;

    /// <summary>
    /// Reason for disconnection.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "user_request";
}
