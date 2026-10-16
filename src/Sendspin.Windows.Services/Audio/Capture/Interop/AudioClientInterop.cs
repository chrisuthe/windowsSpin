using System.Runtime.InteropServices;

namespace Sendspin.Windows.Services.Audio.Capture.Interop;

// Minimal WASAPI declarations for process-loopback capture. NAudio 2.2.1 covers endpoint capture,
// but its AudioClient wrapper cannot be built around a client obtained from
// ActivateAudioInterfaceAsync (the constructor is internal), so this path declares its own.
// Vtable order matters: every IAudioClient method up to GetService is declared, in order.

/// <summary>Constants and the activation entry point.</summary>
internal static class AudioClientInterop
{
    public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    public const int ShareModeShared = 0;
    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint BufferFlagsSilent = 0x2;
    public const uint BufferFlagsTimestampError = 0x4;

    public const int ActivationTypeProcessLoopback = 1;
    public const int ProcessLoopbackModeExcludeTargetProcessTree = 1;
    public const ushort VariantTypeBlob = 65;

    public static readonly Guid IAudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IAudioCaptureClientId = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}

/// <summary><c>AUDIOCLIENT_ACTIVATION_PARAMS</c> for process loopback.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;
    public uint TargetProcessId;
    public int ProcessLoopbackMode;
}

/// <summary>A <c>PROPVARIANT</c> holding a <c>VT_BLOB</c>; sequential layout aligns the pointer on x86 and x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantBlob
{
    public ushort VariantType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public uint BlobSize;
    public IntPtr BlobData;
}

/// <summary><c>WAVEFORMATEX</c>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort Size;
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, [In] ref WaveFormatEx format, IntPtr audioSessionGuid);

    void GetBufferSize(out uint bufferFrames);

    void GetStreamLatency(out long latency);

    void GetCurrentPadding(out uint padding);

    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

    void GetMixFormat(out IntPtr format);

    void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    void Start();

    void Stop();

    void Reset();

    void SetEventHandle(IntPtr eventHandle);

    [return: MarshalAs(UnmanagedType.IUnknown)]
    object GetService([In, MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    // PreserveSig throughout: AUDCLNT_S_BUFFER_EMPTY is a success code worth seeing.
    [PreserveSig]
    int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint numFramesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint numFramesInNextPacket);
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

/// <summary>Marker: the completion handler may be called on any thread.</summary>
[ComImport]
[Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}
