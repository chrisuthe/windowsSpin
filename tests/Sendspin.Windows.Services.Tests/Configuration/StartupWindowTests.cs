// <copyright file="StartupWindowTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using Sendspin.Windows.Services.Configuration;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Configuration;

public class StartupWindowTests
{
    [Fact]
    public void ExplicitStartMinimized_WinsOverAMissingFirstLaunchFlag()
    {
        // Issue #53: StartMinimized saved as true, but HasLaunchedBefore never persisted, so every
        // launch took the first-launch branch and showed the window.
        Assert.False(StartupWindow.ShouldShow(hasLaunchedBefore: false, startMinimized: true));
    }

    [Fact]
    public void FreshInstall_ShowsTheWelcomeWindow()
    {
        // The shipped settings never set StartMinimized, so a first launch shows the window.
        Assert.True(StartupWindow.ShouldShow(hasLaunchedBefore: false, startMinimized: null));
    }

    [Fact]
    public void LaterLaunch_WithNoChoiceMade_StartsMinimized()
    {
        Assert.False(StartupWindow.ShouldShow(hasLaunchedBefore: true, startMinimized: null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartMinimizedOff_AlwaysShows(bool hasLaunchedBefore)
    {
        Assert.True(StartupWindow.ShouldShow(hasLaunchedBefore, startMinimized: false));
    }
}
