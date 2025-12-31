namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Sendspin protocol message type identifiers.
/// Format: "direction/action" where direction is "client" or "server"
/// </summary>
public static class MessageTypes
{
    // Handshake
    public const string ClientHello = "client/hello";
    public const string ServerHello = "server/hello";
    public const string ClientGoodbye = "client/goodbye";

    // Clock synchronization
    public const string ClientTime = "client/time";
    public const string ServerTime = "server/time";

    // Stream lifecycle
    public const string StreamStart = "stream/start";
    public const string StreamEnd = "stream/end";
    public const string StreamClear = "stream/clear";
    public const string StreamRequestFormat = "stream/request-format";

    // Group state
    public const string GroupUpdate = "group/update";

    // Player commands and state
    public const string ClientCommand = "client/command";
    public const string ServerCommand = "server/command";
    public const string ClientState = "client/state";
    public const string ServerState = "server/state";

    // Sync offset (GroupSync calibration)
    public const string ClientSyncOffset = "client/sync_offset";
    public const string ClientSyncOffsetAck = "client/sync_offset_ack";
}

/// <summary>
/// Binary message type identifiers (first byte of binary messages).
/// </summary>
public static class BinaryMessageTypes
{
    // Player audio (role 1, slots 0-3)
    public const byte PlayerAudio0 = 4;
    public const byte PlayerAudio1 = 5;
    public const byte PlayerAudio2 = 6;
    public const byte PlayerAudio3 = 7;

    // Artwork (role 2, slots 0-3)
    public const byte Artwork0 = 8;
    public const byte Artwork1 = 9;
    public const byte Artwork2 = 10;
    public const byte Artwork3 = 11;

    // Visualizer (role 4, slots 0-7)
    public const byte Visualizer0 = 16;
    // ... through Visualizer7 = 23

    public static bool IsPlayerAudio(byte type) => type >= 4 && type <= 7;
    public static bool IsArtwork(byte type) => type >= 8 && type <= 11;
    public static bool IsVisualizer(byte type) => type >= 16 && type <= 23;
}
