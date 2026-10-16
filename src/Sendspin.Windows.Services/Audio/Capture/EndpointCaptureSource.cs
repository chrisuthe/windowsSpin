using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sendspin.SDK.Synchronization;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>
/// One recording endpoint — line in, microphone, USB interface — in WASAPI shared mode, converted
/// by Windows to <see cref="CaptureFormat"/>.
/// </summary>
public sealed class EndpointCaptureSource : WasapiCaptureSource
{
    private readonly string _deviceId;
    private MMDevice? _device;
    private AudioClient? _client;

    /// <summary>Initializes a new instance of the <see cref="EndpointCaptureSource"/> class.</summary>
    /// <param name="deviceId">The WASAPI endpoint ID.</param>
    /// <param name="timer">The clock capture times are expressed in.</param>
    /// <param name="logger">Logger for capture failures.</param>
    public EndpointCaptureSource(string deviceId, IHighPrecisionTimer timer, ILogger logger)
        : base(timer, logger)
    {
        _deviceId = deviceId;
    }

    /// <inheritdoc/>
    protected override ICapturePacketReader Open(EventWaitHandle packetReady)
    {
        using (var enumerator = new MMDeviceEnumerator())
        {
            _device = enumerator.GetDevice(_deviceId);
        }

        _client = _device.AudioClient;
        _client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            BufferDuration,
            0,
            new WaveFormat(CaptureFormat.SampleRate, CaptureFormat.BitsPerSample, CaptureFormat.Channels),
            Guid.Empty);
        _client.SetEventHandle(packetReady.SafeWaitHandle.DangerousGetHandle());
        var reader = new NAudioPacketReader(_client.AudioCaptureClient);
        _client.Start();
        return reader;
    }

    /// <inheritdoc/>
    protected override void Close()
    {
        try
        {
            _client?.Stop();
        }
        finally
        {
            _client?.Dispose();
            _client = null;
            _device?.Dispose();
            _device = null;
        }
    }

    private sealed class NAudioPacketReader(AudioCaptureClient capture) : ICapturePacketReader
    {
        public int GetNextPacketSize() => capture.GetNextPacketSize();

        public IntPtr GetBuffer(out int frames, out bool silent, out long qpcPosition)
        {
            IntPtr data = capture.GetBuffer(out frames, out AudioClientBufferFlags flags, out _, out qpcPosition);
            silent = (flags & AudioClientBufferFlags.Silent) != 0;
            return data;
        }

        public void ReleaseBuffer(int frames) => capture.ReleaseBuffer(frames);
    }
}
