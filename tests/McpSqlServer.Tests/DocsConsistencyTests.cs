using System.Text.Json;
using System.Text.RegularExpressions;
using McpSqlServer.Configuration;
using ModelContextProtocol.Server;

namespace McpSqlServer.Tests;

/// <summary>Skips unless the sibling mcp-hub repository is next to this one (it is on a developer machine, not in isolated CI).</summary>
public sealed class FactIfHubPresentAttribute : FactAttribute
{
    public FactIfHubPresentAttribute()
    {
        if (!Directory.Exists(DocsConsistencyTests.HubRoot))
            Skip = "Sibling ../mcp-hub not found.";
    }
}

/// <summary>Keeps docs honest: they must describe the code, link to files that exist, and match the hub files (RULES #8).</summary>
public class DocsConsistencyTests
{
    private static readonly string Root = ArchitectureRulesTests.RepoRoot;
    internal static string HubRoot { get; } = Path.GetFullPath(Path.Combine(Root, "..", "mcp-hub"));

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static List<string> ToolNames() =>
        typeof(Mcp.SqlTools).GetMethods()
            .Select(m => (McpServerToolAttribute?)Attribute.GetCustomAttribute(m, typeof(McpServerToolAttribute)))
            .Where(a => a is not null)
            .Select(a => a!.Name!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    // ---- documents exist -------------------------------------------------------------------------

    [Theory]
    [InlineData("README.md")]
    [InlineData("AGENTS.md")]
    [InlineData("CLAUDE.md")]
    [InlineData("docs/ARCHITECTURE.md")]
    [InlineData("docs/RULES.md")]
    [InlineData("docs/SECURITY.md")]
    [InlineData("docs/STANDARDS.md")]
    [InlineData("docs/TOOLS.md")]
    [InlineData("docs/CONFIGURATION.md")]
    [InlineData("docs/HUB_INTEGRATION.md")]
    [InlineData("docs/INVESTIGATION_PLAYBOOK.md")]
    [InlineData("docs/readonly-login.sql")]
    [InlineData("docs/adr/README.md")]
    [InlineData("docs/adr/0001-keep-csharp-scriptdom.md")]
    [InlineData("docs/adr/0002-select-only-defense-in-depth.md")]
    [InlineData("docs/adr/0003-read-uncommitted-default.md")]
    [InlineData("docs/adr/0004-official-mcp-sdk-and-hub-startup.md")]
    public void RequiredDocument_Exists(string relativePath)
    {
        Assert.True(File.Exists(Path.Combine(Root, relativePath)), $"Missing document: {relativePath}");
    }

    [Fact]
    public void ClaudeMd_ImportsAgentsMd()
    {
        Assert.Equal("@AGENTS.md", Read("CLAUDE.md").Trim());
    }

    [Fact]
    public void ObsoleteDocuments_AreGone()
    {
        Assert.False(File.Exists(Path.Combine(Root, "RULE.md")), "RULE.md was merged into docs/RULES.md");
        Assert.False(File.Exists(Path.Combine(Root, "PARENT_MCP_GUIDE.md")), "PARENT_MCP_GUIDE.md was replaced by docs/HUB_INTEGRATION.md");
    }

    // ---- links -----------------------------------------------------------------------------------

    [Fact]
    public void RelativeMarkdownLinks_PointToExistingFiles()
    {
        var files = new[] { "README.md", "AGENTS.md" }.Select(f => Path.Combine(Root, f))
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "docs"), "*.md", SearchOption.AllDirectories));
        var broken = new List<string>();

        foreach (var file in files)
        {
            var text = Regex.Replace(File.ReadAllText(file), @"```.*?```", string.Empty, RegexOptions.Singleline); // ignore code blocks
            foreach (Match m in Regex.Matches(text, @"\]\(([^)\s]+)\)"))
            {
                var target = m.Groups[1].Value;
                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) || target.StartsWith("mailto:") || target.StartsWith('#')) continue;

                var path = target.Split('#')[0];
                var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, path));
                if (!File.Exists(full) && !Directory.Exists(full))
                    broken.Add($"{Path.GetRelativePath(Root, file)} -> {target}");
            }
        }

        Assert.True(broken.Count == 0, "Broken links:\n" + string.Join("\n", broken));
    }

    // ---- tools <-> TOOLS.md ----------------------------------------------------------------------

    [Fact]
    public void ToolsMd_HasExactlyOneSectionPerTool()
    {
        var documented = Regex.Matches(Read("docs", "TOOLS.md"), @"^### (\w+)\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(ToolNames(), documented);
    }

    [Fact]
    public void ToolsMd_StatesTheToolCount()
    {
        Assert.Contains($"{ToolNames().Count} tools", Read("docs", "TOOLS.md"));
        Assert.Contains($"{ToolNames().Count} tools", Read("README.md"));
    }

    [Fact]
    public void Readme_MentionsEveryTool()
    {
        var readme = Read("README.md");
        Assert.All(ToolNames(), name => Assert.Contains($"`{name}`", readme));
    }

    [Fact]
    public void ToolDescriptions_AreWrittenForAgents()
    {
        foreach (var method in typeof(Mcp.SqlTools).GetMethods().Where(m => Attribute.IsDefined(m, typeof(McpServerToolAttribute))))
        {
            var description = ((System.ComponentModel.DescriptionAttribute?)Attribute.GetCustomAttribute(method, typeof(System.ComponentModel.DescriptionAttribute)))?.Description;
            Assert.False(string.IsNullOrWhiteSpace(description), method.Name + " needs a [Description]");
            foreach (var p in method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)))
                Assert.True(Attribute.IsDefined(p, typeof(System.ComponentModel.DescriptionAttribute)), $"{method.Name}.{p.Name} needs a [Description]");
        }
    }

    // ---- configuration <-> CONFIGURATION.md ------------------------------------------------------

    [Fact]
    public void ConfigurationMd_DocumentsEveryKeyAndEnvironmentVariable()
    {
        var doc = Read("docs", "CONFIGURATION.md");

        foreach (var (key, envVars) in ConfigLoader.KnownKeys)
        {
            Assert.Contains($"`{key}`", doc);
            Assert.Contains($"`{envVars[0]}`", doc);
        }
    }

    [Fact]
    public void DbconfigExample_UsesOnlyKnownKeys()
    {
        var known = ConfigLoader.KnownKeys.Select(k => k.Key.Replace("_", "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        known.UnionWith(["connecttimeoutseconds", "querytimeoutseconds"]);

        using var doc = JsonDocument.Parse(Read("dbconfig.example.json"));
        foreach (var property in doc.RootElement.EnumerateObject())
            Assert.Contains(property.Name.Replace("_", ""), known);
    }

    [Fact]
    public void DbconfigExample_LoadsThroughTheSameLoader()
    {
        var result = ConfigLoader.Load(Path.Combine(Root, "dbconfig.example.json"), _ => null, centralConfigPath: string.Empty);

        Assert.Empty(result.Warnings);
        Assert.Equal("read_uncommitted", result.Options.Isolation);
        Assert.Equal(30, result.Options.QueryTimeout);
    }

    // ---- hub files (only when mcp-hub is next to this repository) --------------------------------

    [FactIfHubPresent]
    public void Hub_AllowlistExposesExactlyTheTools_AsSqlPrefixed_AndAllAreReadOnly()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(HubRoot, "config", "hub.example.json")));

        var server = doc.RootElement.GetProperty("servers").GetProperty("mcp-sqlserver");
        Assert.False(server.GetProperty("enabled").GetBoolean(), "New children ship disabled.");
        Assert.EndsWith("/mcp-sqlserver.git", server.GetProperty("repository").GetString());

        var tools = doc.RootElement.GetProperty("features").GetProperty("sql").GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(ToolNames(), tools.Select(t => t.GetProperty("name").GetString()!).OrderBy(n => n, StringComparer.Ordinal).ToList());
        Assert.All(tools, t =>
        {
            Assert.Equal("mcp-sqlserver", t.GetProperty("server").GetString());
            Assert.Equal("sql__" + t.GetProperty("name").GetString(), t.GetProperty("exposeAs").GetString());
        });

        // Data tools must never be cached by the hub: the data changes while an issue is investigated.
        foreach (var name in new[] { "lookup_rows", "get_related_rows", "run_select", "explain_select" })
            Assert.False(tools.Single(t => t.GetProperty("name").GetString() == name).TryGetProperty("cacheSeconds", out _), name + " must not be cached");
    }

    [FactIfHubPresent]
    public void Hub_ContractDocumentMentionsEveryExposedTool()
    {
        var contract = File.ReadAllText(Path.Combine(HubRoot, "docs", "mcps", "mcp-sqlserver.md"));

        foreach (var name in ToolNames())
            Assert.Contains($"sql__{name}", contract);
    }

    [FactIfHubPresent]
    public void Hub_ChildTemplate_MatchesWhatTheServerExpects()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(HubRoot, "config", "mcps", "mcp-sqlserver.example.json")));
        var root = doc.RootElement;

        Assert.Equal("stdio", root.GetProperty("transport").GetString());
        Assert.Equal("serve", root.GetProperty("args")[0].GetString());
        Assert.Equal("../mcp-sqlserver", root.GetProperty("cwd").GetString());
        Assert.EndsWith("mcp-sqlserver.json", root.GetProperty("env").GetProperty(ConfigLoader.CentralConfigEnvVar).GetString());

        // The template's section must load without warnings and contain no secrets.
        var section = root.GetProperty("mssql");
        Assert.Equal(string.Empty, section.GetProperty("password").GetString());
        Assert.Equal(string.Empty, section.GetProperty("username").GetString());

        var path = Path.Combine(Path.GetTempPath(), "hub-template-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, root.GetRawText());
            var loaded = ConfigLoader.Load(Path.Combine(Path.GetTempPath(), "none.json"), _ => null, path);
            Assert.Empty(loaded.Warnings);
            Assert.Equal("hub", loaded.Options.ConfigSources["server"]);
        }
        finally { File.Delete(path); }
    }
}
