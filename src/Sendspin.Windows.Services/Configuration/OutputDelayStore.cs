using Microsoft.Extensions.Logging;
using Sendspin.SDK.Client;

namespace Sendspin.Windows.Services.Configuration;

/// <summary>
/// Backs the SDK's output-delay persistence with the same user setting the delay slider uses,
/// so a delay the server sets (<c>set_output_delay</c>) survives a restart and shows in the UI.
/// </summary>
public sealed class OutputDelayStore : IOutputDelayStore
{
    private readonly Func<double> _read;
    private readonly Func<double, Task> _write;
    private readonly ILogger<OutputDelayStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutputDelayStore"/> class.
    /// </summary>
    /// <param name="read">Reads the saved output delay, in milliseconds.</param>
    /// <param name="write">Writes the output delay, in milliseconds, to the user settings.</param>
    /// <param name="logger">Logger for write failures.</param>
    public OutputDelayStore(Func<double> read, Func<double, Task> write, ILogger<OutputDelayStore> logger)
    {
        _read = read;
        _write = write;
        _logger = logger;
    }

    /// <summary>
    /// Raised after the SDK saves a delay, with the saved value in milliseconds.
    /// Raised on the SDK's thread, not the UI thread.
    /// </summary>
    public event Action<double>? Saved;

    /// <inheritdoc/>
    public double? Load() => _read();

    /// <inheritdoc/>
    public void Save(double outputDelayMs)
    {
        // The SDK calls this on its message-handling path, so the file write is not awaited.
        _ = WriteAsync(outputDelayMs);
        Saved?.Invoke(outputDelayMs);
    }

    private async Task WriteAsync(double outputDelayMs)
    {
        try
        {
            await _write(outputDelayMs);
            _logger.LogInformation("Output delay saved: {DelayMs}ms", outputDelayMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save output delay");
        }
    }
}
