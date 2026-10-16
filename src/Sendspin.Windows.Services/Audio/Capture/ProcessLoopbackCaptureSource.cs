using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Synchronization;
using Sendspin.Windows.Services.Audio.Capture.Interop;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// Desktop audio: everything Windows plays except Sendspin's own process tree.
/// </summary>
/// <remarks>
/// Excluding our own tree is what keeps this feedback-free when Music Assistant plays the input
/// back on this PC — the default for a device that is both player and source. Requires Windows 10
/// 2004 (<see cref="CaptureInputCatalog"/> hides the input before that).
/// </remarks>
public sealed class ProcessLoopbackCaptureSource : WasapiCaptureSource
{
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;

    /// <summary>Initializes a new instance of the <see cref="ProcessLoopbackCaptureSource"/> class.</summary>
    /// <param name="timer">The clock capture times are expressed in.</param>
    /// <param name="logger">Logger for capture failures.</param>
    public ProcessLoopbackCaptureSource(IHighPrecisionTimer timer, ILogger logger)
        : base(timer, logger)
    {
    }

    /// <inheritdoc/>
    protected override ICapturePacketReader Open(EventWaitHandle packetReady)
    {
        _client = ProcessLoopbackActivator.ActivateExcludingProcessTreeAsync(Environment.ProcessId)
            .GetAwaiter().GetResult();

        var format = new WaveFormatEx
        {
            FormatTag = 1, // WAVE_FORMAT_PCM
            Channels = CaptureFormat.Channels,
            SamplesPerSec = CaptureFormat.SampleRate,
            AvgBytesPerSec = CaptureFormat.SampleRate * CaptureFormat.BytesPerFrame,
            BlockAlign = CaptureFormat.BytesPerFrame,
            BitsPerSample = CaptureFormat.BitsPerSample,
            Size = 0,
        };
        _client.Initialize(
            AudioClientInterop.ShareModeShared,
            AudioClientInterop.StreamFlagsLoopback
                | AudioClientInterop.StreamFlagsEventCallback
                | AudioClientInterop.StreamFlagsAutoConvertPcm
                | AudioClientInterop.StreamFlagsSrcDefaultQuality,
            BufferDuration,
            0,
            ref format,
            IntPtr.Zero);
        _client.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle());
        _capture = (IAudioCaptureClient)_client.GetService(AudioClientInterop.IAudioCaptureClientId);
        _client.Start();
        return new InteropPacketReader(_capture);
    }

    /// <inheritdoc/>
    protected override void Close()
    {
        if (_client is not null)
        {
            try
            {
                _client.Stop();
            }
            catch (COMException)
            {
                // Not started, or the session is already gone.
            }
        }

        if (_capture is not null)
        {
            Marshal.ReleaseComObject(_capture);
            _capture = null;
        }

        if (_client is not null)
        {
            Marshal.ReleaseComObject(_client);
            _client = null;
        }
    }

    private sealed class InteropPacketReader(IAudioCaptureClient capture) : ICapturePacketReader
    {
        public int GetNextPacketSize()
        {
            Check(capture.GetNextPacketSize(out uint frames));
            return (int)frames;
        }

        public IntPtr GetBuffer(out int frames, out bool silent, out long qpcPosition)
        {
            Check(capture.GetBuffer(out IntPtr data, out uint count, out uint flags, out _, out ulong qpc));
            frames = (int)count;
            silent = (flags & AudioClientInterop.BufferFlagsSilent) != 0;
            qpcPosition = (flags & AudioClientInterop.BufferFlagsTimestampError) != 0 ? 0 : (long)qpc;
            return data;
        }

        public void ReleaseBuffer(int frames) => Check(capture.ReleaseBuffer((uint)frames));

        private static void Check(int hr)
        {
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
    }
}
