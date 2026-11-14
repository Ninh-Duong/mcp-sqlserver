using System.Text.Json;
using McpSqlServer.Configuration;

namespace McpSqlServer.Tests;

/// <summary>
/// hub-requirements.json: mcp-hub warns when these values are empty in the Hub file. Each key must be a
/// field the loader reads from the Hub section, so the warning never names a setting the server ignores.
/// </summary>
public class HubRequirementsTests
{
    [Fact]
    public void Declares_only_mssql_keys_that_the_loader_reads_from_the_hub_section()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ArchitectureRulesTests.RepoRoot, "hub-requirements.json")));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());

        var keys = new List<string>();
        foreach (var group in root.GetProperty("groups").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(group.GetProperty("label").GetString()));
            foreach (var alternative in group.GetProperty("anyOf").EnumerateArray())
            {
                foreach (var key in alternative.EnumerateArray())
                {
                    keys.Add(key.GetString()!);
                }
            }
        }
        Assert.NotEmpty(keys);

        var loader = File.ReadAllText(Path.Combine(ArchitectureRulesTests.RepoRoot, "src", "McpSqlServer", "Configuration", "ConfigLoader.cs"));
        foreach (var key in keys)
        {
            var parts = key.Split('.');
            Assert.Equal(2, parts.Length);
            Assert.Contains(parts[0], ConfigLoader.HubSectionNames);
            Assert.Contains($"\"{parts[1]}\"", loader);
        }
    }
}
