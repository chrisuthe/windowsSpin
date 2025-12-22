using System.Text.Json.Serialization;

namespace SendSpinClient.Core.Protocol.Messages;

/// <summary>
/// Clock synchronization response from server.
/// Uses the envelope format: { "type": "server/time", "payload": { ... } }
/// Contains timestamps for calculating clock offset and round-trip time.
/// </summary>
public sealed class ServerTimeMessage : IMessageWithPayload<ServerTimePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ServerTime;

    [JsonPropertyName("payload")]
    public ServerTimePayload Payload { get; set; } = new();

    // Convenience accessors (excluded from serialization)
    [JsonIgnore]
    public long ClientTransmitted => Payload.ClientTransmitted;
    [JsonIgnore]
    public long ServerReceived => Payload.ServerReceived;
    [JsonIgnore]
    public long ServerTransmitted => Payload.ServerTransmitted;
}

/// <summary>
/// Payload for the server/time message.
/// </summary>
public sealed class ServerTimePayload
{
    /// <summary>
    /// Client's transmitted timestamp (T1), echoed back.
    /// </summary>
    [JsonPropertyName("client_transmitted")]
    public long ClientTransmitted { get; set; }

    /// <summary>
    /// Server's receive timestamp (T2) - when the server received client/time.
    /// </summary>
    [JsonPropertyName("server_received")]
    public long ServerReceived { get; set; }

    /// <summary>
    /// Server's transmit timestamp (T3) - when the server sent this response.
    /// </summary>
    [JsonPropertyName("server_transmitted")]
    public long ServerTransmitted { get; set; }
}

/*
 * Clock Offset Calculation (NTP-style):
 *
 * T1 = client_transmitted (client sends)
 * T2 = server_received (server receives)
 * T3 = server_transmitted (server sends)
 * T4 = client receives (measured locally)
 *
 * Offset = ((T2 - T1) + (T3 - T4)) / 2
 * RTT = (T4 - T1) - (T3 - T2)
 *
 * The offset tells us: server_time = client_time + offset
 */
