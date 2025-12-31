using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Sync offset message received from GroupSync calibration tool.
/// Used to adjust static delay for speaker synchronization.
/// Format: { "type": "client/sync_offset", "payload": { "player_id": "...", "offset_ms": 12.5, "source": "groupsync" } }
/// </summary>
public sealed class ClientSyncOffsetMessage : IMessageWithPayload<ClientSyncOffsetPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientSyncOffset;

    [JsonPropertyName("payload")]
    required public ClientSyncOffsetPayload Payload { get; init; }

    /// <summary>
    /// Creates a sync offset message with the specified offset.
    /// </summary>
    public static ClientSyncOffsetMessage Create(string playerId, double offsetMs, string source = "groupsync")
    {
        return new ClientSyncOffsetMessage
        {
            Payload = new ClientSyncOffsetPayload
            {
                PlayerId = playerId,
                OffsetMs = offsetMs,
                Source = source,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };
    }
}

/// <summary>
/// Payload for client/sync_offset message.
/// </summary>
public sealed class ClientSyncOffsetPayload
{
    /// <summary>
    /// Player identifier this offset applies to.
    /// </summary>
    [JsonPropertyName("player_id")]
    required public string PlayerId { get; init; }

    /// <summary>
    /// Offset in milliseconds to apply.
    /// Positive = delay playback (plays later)
    /// Negative = advance playback (plays earlier)
    /// </summary>
    [JsonPropertyName("offset_ms")]
    public double OffsetMs { get; init; }

    /// <summary>
    /// Source of the calibration (e.g., "groupsync", "manual").
    /// </summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    /// <summary>
    /// Unix timestamp (ms) when the calibration was performed.
    /// </summary>
    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Timestamp { get; init; }
}

/// <summary>
/// Acknowledgement message sent back after applying sync offset.
/// Format: { "type": "client/sync_offset_ack", "payload": { ... } }
/// </summary>
public sealed class ClientSyncOffsetAckMessage : IMessageWithPayload<ClientSyncOffsetAckPayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientSyncOffsetAck;

    [JsonPropertyName("payload")]
    required public ClientSyncOffsetAckPayload Payload { get; init; }

    /// <summary>
    /// Creates an acknowledgement for a sync offset message.
    /// </summary>
    public static ClientSyncOffsetAckMessage Create(string playerId, double appliedOffsetMs, bool success, string? error = null)
    {
        return new ClientSyncOffsetAckMessage
        {
            Payload = new ClientSyncOffsetAckPayload
            {
                PlayerId = playerId,
                AppliedOffsetMs = appliedOffsetMs,
                Success = success,
                Error = error
            }
        };
    }
}

/// <summary>
/// Payload for client/sync_offset_ack message.
/// </summary>
public sealed class ClientSyncOffsetAckPayload
{
    /// <summary>
    /// Player identifier the offset was applied to.
    /// </summary>
    [JsonPropertyName("player_id")]
    required public string PlayerId { get; init; }

    /// <summary>
    /// The offset that was actually applied (may be clamped).
    /// </summary>
    [JsonPropertyName("applied_offset_ms")]
    public double AppliedOffsetMs { get; init; }

    /// <summary>
    /// Whether the offset was successfully applied.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the offset could not be applied.
    /// </summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}
