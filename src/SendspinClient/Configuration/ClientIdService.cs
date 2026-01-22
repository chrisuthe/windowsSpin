using System.IO;

namespace SendspinClient.Configuration;

/// <summary>
/// Manages the persistent client ID that uniquely identifies this installation.
/// The ID is stored in a dedicated file and survives app reinstalls.
/// </summary>
public static class ClientIdService
{
    /// <summary>
    /// Gets the path to the client ID file.
    /// Located at %LocalAppData%\WindowsSpin\client_id.
    /// </summary>
    public static string ClientIdPath { get; } = Path.Combine(AppPaths.UserDataDirectory, "client_id");

    /// <summary>
    /// Gets or creates the persistent client ID for this installation.
    /// The ID is generated once on first run and persisted for all future sessions.
    /// </summary>
    /// <returns>The persistent client ID in format: sendspin-windows-{uuid}</returns>
    public static string GetOrCreateClientId()
    {
        // Try to read existing ID
        if (File.Exists(ClientIdPath))
        {
            var existingId = File.ReadAllText(ClientIdPath).Trim();
            if (!string.IsNullOrEmpty(existingId))
            {
                return existingId;
            }
        }

        // Generate and persist new ID
        AppPaths.EnsureUserDataDirectoryExists();
        var newId = $"sendspin-windows-{Guid.NewGuid():D}";
        File.WriteAllText(ClientIdPath, newId);
        return newId;
    }
}
