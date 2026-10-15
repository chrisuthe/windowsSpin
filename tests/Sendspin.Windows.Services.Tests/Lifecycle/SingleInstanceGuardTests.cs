// <copyright file="SingleInstanceGuardTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.Windows.Services.Lifecycle;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Lifecycle;

/// <summary>
/// Uses unique names so a running copy of the app, or a parallel test, never shares the guard's
/// mutex or pipe.
/// </summary>
public class SingleInstanceGuardTests
{
    [Fact]
    public void OnlyTheFirstInstanceStarts()
    {
        var (mutex, pipe) = UniqueNames();
        using var first = new SingleInstanceGuard(mutex, pipe);
        using var second = new SingleInstanceGuard(mutex, pipe);

        Assert.True(first.TryStart());
        Assert.False(second.TryStart());
    }

    [Fact]
    public async Task ASecondLaunch_AsksTheFirstToShowItsWindow()
    {
        // Both ends are CurrentUserOnly now; this pins that a same-user launch still gets through.
        var (mutex, pipe) = UniqueNames();
        using var first = new SingleInstanceGuard(mutex, pipe);
        var shown = new TaskCompletionSource();
        first.ShowWindowRequested += (_, _) => shown.TrySetResult();
        Assert.True(first.TryStart());

        using var second = new SingleInstanceGuard(mutex, pipe);
        Assert.False(second.TryStart());

        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void TheDefaultNamesAreScopedToTheSession()
    {
        // The mutex is session-scoped with or without the prefix; the pipe is not, because named
        // pipes share one machine-wide namespace, so its name has to carry the session.
        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;

        Assert.Equal(@"Local\Sendspin_SingleInstance", SingleInstanceGuard.DefaultMutexName);
        Assert.Equal($"Sendspin_ShowWindow_{sessionId}", SingleInstanceGuard.DefaultPipeName);
    }

    private static (string Mutex, string Pipe) UniqueNames()
    {
        var id = Guid.NewGuid().ToString("N");
        return ($@"Local\SendspinTest_{id}", $"SendspinTest_{id}");
    }
}
