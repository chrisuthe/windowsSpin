// <copyright file="SyncMetricRingBuffer.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Diagnostics;

/// <summary>
/// Lock-free circular buffer for sync metric snapshots.
/// Stores metrics at regular intervals for correlation with audio samples.
/// </summary>
/// <remarks>
/// <para>
/// At 100ms intervals, a 45-second buffer needs ~450 entries.
/// We use a larger capacity (1024) to handle longer recordings and
/// provide a power-of-2 size for efficient modulo operations.
/// </para>
/// </remarks>
public sealed class SyncMetricRingBuffer
{
    private readonly SyncMetricSnapshot[] _buffer;
    private readonly int _capacity;
    private readonly int _mask;

    // Write index - only modified by producer
    private long _writeIndex;

    /// <summary>
    /// Gets the buffer capacity in snapshots.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets the total number of snapshots written since creation.
    /// </summary>
    public long TotalSnapshotsWritten => Volatile.Read(ref _writeIndex);

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMetricRingBuffer"/> class.
    /// </summary>
    /// <param name="capacity">The buffer capacity. Will be rounded up to power of 2.</param>
    public SyncMetricRingBuffer(int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);

        _capacity = RoundUpToPowerOfTwo(capacity);
        _mask = _capacity - 1;
        _buffer = new SyncMetricSnapshot[_capacity];
    }

    /// <summary>
    /// Records a new metric snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot to record.</param>
    public void Record(SyncMetricSnapshot snapshot)
    {
        var writeIdx = Volatile.Read(ref _writeIndex);
        _buffer[writeIdx & _mask] = snapshot;
        Volatile.Write(ref _writeIndex, writeIdx + 1);
    }

    /// <summary>
    /// Gets all snapshots whose sample positions fall within the specified range.
    /// </summary>
    /// <param name="startSamplePosition">The start sample position (inclusive).</param>
    /// <param name="endSamplePosition">The end sample position (exclusive).</param>
    /// <returns>Array of snapshots within the range, ordered by sample position.</returns>
    public SyncMetricSnapshot[] GetSnapshotsInRange(long startSamplePosition, long endSamplePosition)
    {
        var writeIdx = Volatile.Read(ref _writeIndex);
        var snapshotsAvailable = (int)Math.Min(writeIdx, _capacity);
        var startIdx = writeIdx - snapshotsAvailable;

        var results = new List<SyncMetricSnapshot>();

        for (var i = 0; i < snapshotsAvailable; i++)
        {
            var snapshot = _buffer[(startIdx + i) & _mask];
            if (snapshot.SamplePosition >= startSamplePosition &&
                snapshot.SamplePosition < endSamplePosition)
            {
                results.Add(snapshot);
            }
        }

        // Sort by sample position in case of any ordering issues
        results.Sort((a, b) => a.SamplePosition.CompareTo(b.SamplePosition));

        return results.ToArray();
    }

    /// <summary>
    /// Gets all available snapshots.
    /// </summary>
    /// <returns>Array of all snapshots currently in the buffer.</returns>
    public SyncMetricSnapshot[] GetAllSnapshots()
    {
        var writeIdx = Volatile.Read(ref _writeIndex);
        var snapshotsAvailable = (int)Math.Min(writeIdx, _capacity);
        var startIdx = writeIdx - snapshotsAvailable;

        var results = new SyncMetricSnapshot[snapshotsAvailable];

        for (var i = 0; i < snapshotsAvailable; i++)
        {
            results[i] = _buffer[(startIdx + i) & _mask];
        }

        return results;
    }

    /// <summary>
    /// Resets the buffer to empty state.
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _writeIndex, 0);
        Array.Clear(_buffer);
    }

    /// <summary>
    /// Rounds a value up to the next power of 2.
    /// </summary>
    private static int RoundUpToPowerOfTwo(int value)
    {
        if (value <= 0)
        {
            return 1;
        }

        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }
}
