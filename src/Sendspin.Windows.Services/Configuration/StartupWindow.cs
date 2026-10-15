namespace Sendspin.Windows.Services.Configuration;

/// <summary>
/// Decides whether the main window opens at startup or the app starts in the tray.
/// </summary>
public static class StartupWindow
{
    /// <summary>
    /// Gets whether to show the main window at startup.
    /// </summary>
    /// <param name="hasLaunchedBefore">The <c>App:HasLaunchedBefore</c> setting.</param>
    /// <param name="startMinimized">
    /// The <c>App:StartMinimized</c> setting, or null when the user has never set it.
    /// </param>
    /// <remarks>
    /// A first launch shows the window so a new user sees the welcome screen. An explicit Start
    /// Minimized choice wins over that, because it can only have been made from a previous launch.
    /// Before this, a first-launch flag that never persisted (issue #53) reopened the window on
    /// every launch, whatever the user had chosen. When unset, Start Minimized defaults to on.
    /// </remarks>
    public static bool ShouldShow(bool hasLaunchedBefore, bool? startMinimized) =>
        startMinimized switch
        {
            true => false,
            false => true,
            null => !hasLaunchedBefore,
        };
}
