using System.Text.Json.Serialization;

namespace SendSpinClient.Core.Protocol.Messages;

/// <summary>
/// Clock synchronization request sent by client.
/// Uses the envelope format: { "type": "client/time", "payload": { ... } }
/// Contains the client's current timestamp for round-trip calculation.
/// </summary>
public sealed class ClientTimeMessage : IMessageWithPayload<ClientTimePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientTime;

    [JsonPropertyName("payload")]
    public ClientTimePayload Payload { get; set; } = new();

    // Convenience accessor (excluded from serialization)
    [JsonIgnore]
    public long ClientTransmitted => Payload.ClientTransmitted;

    /// <summary>
    /// Creates a new time message with the current timestamp.
    /// </summary>
    public static ClientTimeMessage CreateNow()
    {
        return new ClientTimeMessage
        {
            Payload = new ClientTimePayload
            {
                ClientTransmitted = GetCurrentTimestampMicroseconds()
            }
        };
    }

    /// <summary>
    /// Gets the current monotonic timestamp in microseconds.
    /// Uses Stopwatch for high precision.
    /// </summary>
    public static long GetCurrentTimestampMicroseconds()
    {
        return System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000L
               / System.Diagnostics.Stopwatch.Frequency;
    }
}

/// <summary>
/// Payload for the client/time message.
/// </summary>
public sealed class ClientTimePayload
{
    /// <summary>
    /// Client's current monotonic clock timestamp in microseconds.
    /// This is T1 in the NTP-style 4-timestamp exchange.
    /// </summary>
    [JsonPropertyName("client_transmitted")]
    public long ClientTransmitted { get; set; }
}
