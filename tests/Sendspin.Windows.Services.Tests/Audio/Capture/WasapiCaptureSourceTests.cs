// <copyright file="WasapiCaptureSourceTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio.Source;
using Sendspin.Windows.Services.Audio.Capture;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Audio.Capture;

public class WasapiCaptureSourceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Packets_AreCopiedOut_InOrder()
    {
        await using var source = new TestSource();
        var frames = Collect(source, count: 2);
        await source.StartAsync();

        source.Reader.Enqueue([1, 1, 1, 1, 2, 2, 2, 2], silent: false, qpc: 0);
        source.Reader.Enqueue([3, 3, 3, 3], silent: false, qpc: 0);
        source.Signal();

        var got = await frames.Task.WaitAsync(Timeout);
        Assert.Equal(new byte[] { 1, 1, 1, 1, 2, 2, 2, 2 }, got[0].Pcm.ToArray());
        Assert.Equal(new byte[] { 3, 3, 3, 3 }, got[1].Pcm.ToArray());
    }

    [Fact]
    public async Task ASilentPacket_IsDeliveredAsZeros()
    {
        await using var source = new TestSource();
        var frames = Collect(source, count: 1);
        await source.StartAsync();

        source.Reader.Enqueue([9, 9, 9, 9], silent: true, qpc: 0);
        source.Signal();

        Assert.Equal(new byte[4], (await frames.Task.WaitAsync(Timeout))[0].Pcm.ToArray());
    }

    [Fact]
    public async Task WithoutAQpcPosition_TheTimestampIsNowMinusTheDuration()
    {
        await using var source = new TestSource(new FakeTimer { Now = 10_000_000 });
        var frames = Collect(source, count: 1);
        await source.StartAsync();

        source.Reader.Enqueue(new byte[960 * CaptureFormat.BytesPerFrame], silent: false, qpc: 0);
        source.Signal();

        Assert.Equal(10_000_000 - 20_000, (await frames.Task.WaitAsync(Timeout))[0].CaptureTimeMicroseconds);
    }

    [Fact]
    public async Task AReadFailure_RaisesFaultedOnce()
    {
        await using var source = new TestSource();
        var faults = new ConcurrentQueue<Exception>();
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Faulted += (_, ex) =>
        {
            faults.Enqueue(ex);
            faulted.TrySetResult();
        };
        await source.StartAsync();

        source.Reader.FailWith = new COMException("gone", CaptureErrorMessages.DeviceInvalidated);
        source.Reader.Enqueue([1, 1, 1, 1], silent: false, qpc: 0);
        source.Signal();

        await faulted.Task.WaitAsync(Timeout);
        await Task.Delay(200);
        Assert.Single(faults);
    }

    [Fact]
    public async Task AnOpenFailure_FailsTheStart_AndCloses()
    {
        await using var source = new TestSource { OpenFailure = new UnauthorizedAccessException() };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => source.StartAsync());
        Assert.True(source.Closed);
    }

    [Fact]
    public async Task Stop_Closes_AndNoFramesFollow()
    {
        await using var source = new TestSource();
        int frames = 0;
        source.FramesCaptured += (_, _) => Interlocked.Increment(ref frames);
        await source.StartAsync();

        await source.StopAsync();
        source.Reader.Enqueue([1, 1, 1, 1], silent: false, qpc: 0);

        Assert.True(source.Closed);
        Assert.Equal(0, frames);
    }

    private static TaskCompletionSource<List<CapturedAudio>> Collect(ICaptureSource source, int count)
    {
        var frames = new List<CapturedAudio>();
        var done = new TaskCompletionSource<List<CapturedAudio>>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.FramesCaptured += (_, f) =>
        {
            frames.Add(f);
            if (frames.Count == count)
            {
                done.TrySetResult(frames);
            }
        };
        return done;
    }

    private sealed class TestSource(FakeTimer? timer = null)
        : WasapiCaptureSource(timer ?? new FakeTimer(), NullLogger.Instance)
    {
        private EventWaitHandle? _packetReady;

        public FakeReader Reader { get; } = new();

        public Exception? OpenFailure { get; init; }

        public bool Closed { get; private set; }

        public void Signal() => _packetReady!.Set();

        protected override ICapturePacketReader Open(EventWaitHandle packetReady)
        {
            _packetReady = packetReady;
            if (OpenFailure is not null)
            {
                throw OpenFailure;
            }

            return Reader;
        }

        protected override void Close() => Closed = true;
    }

    private sealed class FakeReader : ICapturePacketReader
    {
        private readonly ConcurrentQueue<(byte[] Data, bool Silent, long Qpc)> _packets = new();
        private IntPtr _locked;

        public Exception? FailWith { get; set; }

        public void Enqueue(byte[] data, bool silent, long qpc) => _packets.Enqueue((data, silent, qpc));

        public int GetNextPacketSize() =>
            _packets.TryPeek(out var p) ? p.Data.Length / CaptureFormat.BytesPerFrame : 0;

        public IntPtr GetBuffer(out int frames, out bool silent, out long qpcPosition)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }

            _packets.TryDequeue(out var p);
            frames = p.Data.Length / CaptureFormat.BytesPerFrame;
            silent = p.Silent;
            qpcPosition = p.Qpc;
            _locked = Marshal.AllocHGlobal(p.Data.Length);
            Marshal.Copy(p.Data, 0, _locked, p.Data.Length);
            return _locked;
        }

        public void ReleaseBuffer(int frames) => Marshal.FreeHGlobal(_locked);
    }
}
