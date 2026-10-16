namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>Turns a capture failure into a sentence the user can act on.</summary>
public static class CaptureErrorMessages
{
    /// <summary><c>E_ACCESSDENIED</c>: Windows privacy settings block recording.</summary>
    public const int AccessDenied = unchecked((int)0x80070005);

    /// <summary><c>AUDCLNT_E_DEVICE_IN_USE</c>: another app holds the endpoint exclusively.</summary>
    public const int DeviceInUse = unchecked((int)0x8889000A);

    /// <summary><c>AUDCLNT_E_DEVICE_INVALIDATED</c>: the endpoint was removed.</summary>
    public const int DeviceInvalidated = unchecked((int)0x88890004);

    /// <summary>Describes why capture from <paramref name="inputName"/> stopped.</summary>
    /// <param name="inputName">The input's display name.</param>
    /// <param name="exception">The failure.</param>
    /// <returns>A one-sentence message.</returns>
    public static string Describe(string inputName, Exception exception) => exception.HResult switch
    {
        AccessDenied =>
            "Windows privacy settings are blocking recording. Turn on \"Let desktop apps access your microphone\" in Settings › Privacy & security › Microphone.",
        DeviceInUse => $"{inputName} is in use by another app.",
        DeviceInvalidated => $"{inputName} was disconnected.",
        _ => $"{inputName} stopped unexpectedly.",
    };
}
