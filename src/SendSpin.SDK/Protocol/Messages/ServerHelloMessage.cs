using System.Text.Json.Serialization;

namespace SendSpin.SDK.Protocol.Messages;

/// <summary>
/// Server response to client hello, confirming role activations.
/// Uses the envelope format: { "type": "server/hello", "payload": { ... } }
/// </summary>
public sealed class ServerHelloMessage : IMessageWithPayload<ServerHelloPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerHello;

    [JsonPropertyName("payload")]
    public ServerHelloPayload Payload { get; set; } = new();

    // Convenience accessors (excluded from serialization)
    [JsonIgnore]
    public string ServerId => Payload.ServerId;
    [JsonIgnore]
    public string? Name => Payload.Name;
    [JsonIgnore]
    public List<string> ActiveRoles => Payload.ActiveRoles;
    [JsonIgnore]
    public string? GroupId => Payload.GroupId;
}

/// <summary>
/// Payload for the server/hello message.
/// </summary>
public sealed class ServerHelloPayload
{
    /// <summary>
    /// Unique server identifier.
    /// </summary>
    [JsonPropertyName("server_id")]
    public string ServerId { get; set; } = string.Empty;

    /// <summary>
    /// Server name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Protocol version supported by the server.
    /// </summary>
    [JsonPropertyName("protocol_version")]
    public string? ProtocolVersion { get; set; }

    /// <summary>
    /// Roles activated by the server for this client.
    /// List of versioned role strings (e.g., ["player@v1", "controller@v1"]).
    /// </summary>
    [JsonPropertyName("active_roles")]
    public List<string> ActiveRoles { get; set; } = new();

    /// <summary>
    /// Reason for this connection.
    /// </summary>
    [JsonPropertyName("connection_reason")]
    public string? ConnectionReason { get; set; }

    /// <summary>
    /// Group ID the client is assigned to.
    /// </summary>
    [JsonPropertyName("group_id")]
    public string? GroupId { get; set; }

    /// <summary>
    /// Application-specific support data.
    /// </summary>
    [JsonPropertyName("support")]
    public Dictionary<string, object>? Support { get; set; }
}
