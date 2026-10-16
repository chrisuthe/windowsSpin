using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Sendspin.Windows.Services.Audio.Capture.Interop;

namespace Sendspin.Windows.Services.Audio.Capture;

/// <summary>Something Send can capture from.</summary>
/// <param name="Id"><see cref="CaptureInputCatalog.DesktopId"/>, or a WASAPI endpoint ID.</param>
/// <param name="Name">Short name, used in the badge and tooltips.</param>
/// <param name="IsAvailable">False for a saved endpoint that is not plugged in.</param>
public sealed record CaptureInput(string Id, string Name, bool IsAvailable = true)
{
    /// <summary>Gets the label shown in the Settings picker.</summary>
    public string DisplayName =>
        !IsAvailable ? $"{Name} — not connected"
        : Id == CaptureInputCatalog.DesktopId ? $"{Name} (all apps except Sendspin)"
        : Name;
}

/// <summary>Lists what Send can capture from.</summary>
public interface ICaptureInputCatalog
{
    /// <summary>Lists the inputs available now: desktop audio first when supported, then active recording endpoints.</summary>
    /// <returns>The inputs.</returns>
    IReadOnlyList<CaptureInput> GetInputs();

    /// <summary>Describes an input by ID even when it is not available, so a saved choice can be shown by name.</summary>
    /// <param name="inputId">The input ID.</param>
    /// <returns>The input, or null if Windows does not know it at all.</returns>
    CaptureInput? Describe(string inputId);
}

/// <summary>Windows implementation of <see cref="ICaptureInputCatalog"/>.</summary>
public sealed class CaptureInputCatalog : ICaptureInputCatalog
{
    /// <summary>The ID of the desktop-audio input.</summary>
    public const string DesktopId = "desktop";

    private static readonly CaptureInput Desktop = new(DesktopId, "Desktop audio");

    /// <inheritdoc/>
    public IReadOnlyList<CaptureInput> GetInputs()
    {
        var inputs = new List<CaptureInput>();
        if (ProcessLoopbackActivator.IsSupported)
        {
            inputs.Add(Desktop);
        }

        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    inputs.Add(new CaptureInput(device.ID, device.FriendlyName));
                }
                catch (COMException)
                {
                    // Unplugged mid-enumeration; it is no longer an input.
                }
            }
        }

        return inputs;
    }

    /// <inheritdoc/>
    public CaptureInput? Describe(string inputId)
    {
        if (inputId == DesktopId)
        {
            return ProcessLoopbackActivator.IsSupported ? Desktop : null;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(inputId);
            return new CaptureInput(device.ID, device.FriendlyName, device.State == DeviceState.Active);
        }
        catch (COMException)
        {
            return null;
        }
    }
}
