using System.Buffers.Binary;
using SendSpinClient.Core.Protocol.Messages;

namespace SendSpinClient.Core.Protocol;

/// <summary>
/// Parses binary protocol messages (audio chunks, artwork, visualizer data).
/// </summary>
public static class BinaryMessageParser
{
    /// <summary>
    /// Minimum binary message size (1 byte type + 8 bytes timestamp).
    /// </summary>
    public const int MinimumMessageSize = 9;

    /// <summary>
    /// Parses a binary message header.
    /// </summary>
    /// <param name="data">Raw binary message data.</param>
    /// <param name="messageType">The message type identifier.</param>
    /// <param name="timestamp">Server timestamp in microseconds.</param>
    /// <param name="payload">The payload data after the header.</param>
    /// <returns>True if parsing succeeded.</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> data,
        out byte messageType,
        out long timestamp,
        out ReadOnlySpan<byte> payload)
    {
        messageType = 0;
        timestamp = 0;
        payload = default;

        if (data.Length < MinimumMessageSize)
        {
            return false;
        }

        messageType = data[0];
        timestamp = BinaryPrimitives.ReadInt64BigEndian(data.Slice(1, 8));
        payload = data.Slice(MinimumMessageSize);

        return true;
    }

    /// <summary>
    /// Parses a binary audio message.
    /// </summary>
    public static AudioChunk? ParseAudioChunk(ReadOnlySpan<byte> data)
    {
        if (!TryParse(data, out var type, out var timestamp, out var payload))
        {
            return null;
        }

        if (!BinaryMessageTypes.IsPlayerAudio(type))
        {
            return null;
        }

        return new AudioChunk
        {
            Slot = (byte)(type - BinaryMessageTypes.PlayerAudio0),
            ServerTimestamp = timestamp,
            EncodedData = payload.ToArray()
        };
    }

    /// <summary>
    /// Parses a binary artwork message.
    /// </summary>
    public static ArtworkChunk? ParseArtworkChunk(ReadOnlySpan<byte> data)
    {
        if (!TryParse(data, out var type, out var timestamp, out var payload))
        {
            return null;
        }

        if (!BinaryMessageTypes.IsArtwork(type))
        {
            return null;
        }

        return new ArtworkChunk
        {
            Channel = (byte)(type - BinaryMessageTypes.Artwork0),
            Timestamp = timestamp,
            ImageData = payload.ToArray()
        };
    }

    /// <summary>
    /// Gets the message category from a binary message type byte.
    /// </summary>
    public static BinaryMessageCategory GetCategory(byte messageType)
    {
        if (BinaryMessageTypes.IsPlayerAudio(messageType))
            return BinaryMessageCategory.PlayerAudio;
        if (BinaryMessageTypes.IsArtwork(messageType))
            return BinaryMessageCategory.Artwork;
        if (BinaryMessageTypes.IsVisualizer(messageType))
            return BinaryMessageCategory.Visualizer;
        if (messageType >= 192)
            return BinaryMessageCategory.ApplicationSpecific;

        return BinaryMessageCategory.Unknown;
    }
}

/// <summary>
/// Categories of binary messages.
/// </summary>
public enum BinaryMessageCategory
{
    Unknown,
    PlayerAudio,
    Artwork,
    Visualizer,
    ApplicationSpecific
}

/// <summary>
/// Represents a parsed audio chunk.
/// </summary>
public sealed class AudioChunk
{
    /// <summary>
    /// Audio slot (0-3, for multi-stream).
    /// </summary>
    public byte Slot { get; init; }

    /// <summary>
    /// Server timestamp when this audio should be played (microseconds).
    /// </summary>
    public long ServerTimestamp { get; init; }

    /// <summary>
    /// Encoded audio data (Opus/FLAC/PCM).
    /// </summary>
    public required byte[] EncodedData { get; init; }

    /// <summary>
    /// Decoded PCM samples (set after decoding).
    /// </summary>
    public float[]? DecodedSamples { get; set; }

    /// <summary>
    /// Playback position within decoded samples.
    /// </summary>
    public int PlaybackPosition { get; set; }
}

/// <summary>
/// Represents a parsed artwork chunk.
/// </summary>
public sealed class ArtworkChunk
{
    /// <summary>
    /// Artwork channel (0-3).
    /// </summary>
    public byte Channel { get; init; }

    /// <summary>
    /// Timestamp for this artwork.
    /// </summary>
    public long Timestamp { get; init; }

    /// <summary>
    /// Raw image data (JPEG/PNG).
    /// </summary>
    public required byte[] ImageData { get; init; }
}
