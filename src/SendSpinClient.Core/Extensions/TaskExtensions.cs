// <copyright file="TaskExtensions.cs" company="SendSpin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace SendSpinClient.Core.Extensions;

/// <summary>
/// Extension methods for <see cref="Task"/> to handle common async patterns safely.
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// Executes a task in a fire-and-forget manner while properly handling exceptions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this instead of <c>_ = SomeAsync()</c> which swallows exceptions silently.
    /// This method ensures exceptions are logged rather than lost.
    /// </para>
    /// <para>
    /// Example usage:
    /// <code>
    /// // Instead of: _ = DoSomethingAsync();
    /// DoSomethingAsync().SafeFireAndForget(_logger);
    /// </code>
    /// </para>
    /// </remarks>
    /// <param name="task">The task to execute.</param>
    /// <param name="logger">Optional logger for error reporting. If null, exceptions are silently observed.</param>
    /// <param name="caller">Automatically captured caller member name for diagnostics.</param>
    public static async void SafeFireAndForget(
        this Task task,
        ILogger? logger = null,
        [CallerMemberName] string? caller = null)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected and should not be logged as an error
            logger?.LogDebug("Fire-and-forget task cancelled in {Caller}", caller);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Fire-and-forget task failed in {Caller}", caller);
        }
    }

    /// <summary>
    /// Executes a task in a fire-and-forget manner with a custom error handler.
    /// </summary>
    /// <param name="task">The task to execute.</param>
    /// <param name="onError">Action to invoke when an exception occurs.</param>
    /// <param name="caller">Automatically captured caller member name for diagnostics.</param>
    public static async void SafeFireAndForget(
        this Task task,
        Action<Exception> onError,
        [CallerMemberName] string? caller = null)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected - no action needed
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
    }
}
