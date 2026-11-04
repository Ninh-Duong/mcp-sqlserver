using System.Text.Json;

namespace McpSqlServer;

/// <summary>Reads servers_registry.json from the offline snapshot (single implementation for the MCP tool and the CLI menu).</summary>
public static class ServerRegistryReader
{
    public static async Task<IReadOnlyDictionary<string, DatabaseMigrationStatus>?> LoadMigrationsAsync(
        string contextDirectory, string serverAlias, CancellationToken cancellationToken = default)
    {
        var registryPath = Path.Combine(Path.GetFullPath(contextDirectory), "servers_registry.json");
        if (!File.Exists(registryPath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(registryPath, cancellationToken);
            var items = JsonSerializer.Deserialize<List<ServerRegistryItem>>(json);
            var item = items?.FirstOrDefault(i => i.ServerAlias.Equals(serverAlias, StringComparison.OrdinalIgnoreCase))
                       ?? items?.FirstOrDefault();
            return item?.Migrations;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not read servers_registry.json ({ex.GetType().Name}).");
            return null;
        }
    }
}
