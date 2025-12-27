using System.Text.Json.Serialization;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Protocol.Messages;

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
    /// Gets the current timestamp in microseconds using the shared high-precision timer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CRITICAL: This MUST use the same timer as audio playback timing (HighPrecisionTimer).
    /// Previously this used raw Stopwatch ticks while audio used Unix-based time from
    /// HighPrecisionTimer, causing a time base mismatch of billions of seconds!
    /// </para>
    /// <para>
    /// The clock offset calculation works correctly as long as T1/T4 (client times)
    /// are in the same time base as the audio playback timer.
    /// </para>
    /// </remarks>
    public static long GetCurrentTimestampMicroseconds()
    {
        return HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds();
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
