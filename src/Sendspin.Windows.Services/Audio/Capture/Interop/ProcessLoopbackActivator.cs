using System.Runtime.InteropServices;

namespace Sendspin.Windows.Services.Audio.Capture.Interop;

/// <summary>
/// Activates an <see cref="IAudioClient"/> that captures everything Windows plays except one
/// process tree.
/// </summary>
internal static class ProcessLoopbackActivator
{
    /// <summary>Process loopback exists from Windows 10 version 2004 (build 19041).</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>Activates a loopback client excluding <paramref name="processId"/> and its children.</summary>
    /// <param name="processId">The process whose output to leave out — ours.</param>
    /// <returns>An uninitialized client.</returns>
    public static async Task<IAudioClient> ActivateExcludingProcessTreeAsync(int processId)
    {
        int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(paramsSize);
        IntPtr variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(
                new AudioClientActivationParams
                {
                    ActivationType = AudioClientInterop.ActivationTypeProcessLoopback,
                    TargetProcessId = (uint)processId,
                    ProcessLoopbackMode = AudioClientInterop.ProcessLoopbackModeExcludeTargetProcessTree,
                },
                paramsPtr,
                fDeleteOld: false);
            Marshal.StructureToPtr(
                new PropVariantBlob
                {
                    VariantType = AudioClientInterop.VariantTypeBlob,
                    BlobSize = (uint)paramsSize,
                    BlobData = paramsPtr,
                },
                variantPtr,
                fDeleteOld: false);

            var handler = new CompletionHandler();
            AudioClientInterop.ActivateAudioInterfaceAsync(
                AudioClientInterop.VirtualAudioDeviceProcessLoopback,
                AudioClientInterop.IAudioClientId,
                variantPtr,
                handler,
                out _);

            // Awaited inside the try so the parameter memory outlives the activation.
            return await handler.Task.ConfigureAwait(false);
        }
        finally
        {
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Task => _completion.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out int hr, out object activated);
                if (hr < 0)
                {
                    _completion.TrySetException(Marshal.GetExceptionForHR(hr)!);
                }
                else
                {
                    _completion.TrySetResult((IAudioClient)activated);
                }
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }
    }
}
