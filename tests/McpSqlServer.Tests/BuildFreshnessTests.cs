namespace McpSqlServer.Tests;

public sealed class BuildFreshnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcp-sqlserver-fresh-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Built = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public BuildFreshnessTests() => Directory.CreateDirectory(Path.Combine(_root, "src", "App"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private string Touch(DateTime whenUtc, params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, whenUtc);
        return path;
    }

    [Fact]
    public void OlderSources_AreNotStale()
    {
        Touch(Built.AddDays(-1), "src", "App", "Program.cs");
        Touch(Built.AddDays(-1), "src", "App", "App.csproj");

        Assert.False(BuildFreshness.IsStale(_root, Built));
    }

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("App.csproj")]
    [InlineData("Directory.Build.props")]
    public void ANewerSourceFile_IsStale(string name)
    {
        Touch(Built.AddDays(-1), "src", "App", "Old.cs");
        Touch(Built.AddMinutes(1), "src", "App", name);

        Assert.True(BuildFreshness.IsStale(_root, Built));
    }

    [Fact]
    public void NewerBuildOutput_IsIgnored()
    {
        Touch(Built.AddDays(1), "src", "App", "obj", "Generated.cs");
        Touch(Built.AddDays(1), "src", "App", "bin", "Release", "Other.cs");

        Assert.False(BuildFreshness.IsStale(_root, Built));
    }

    [Fact]
    public void MissingSourceFolder_IsNotStale()
    {
        Directory.Delete(Path.Combine(_root, "src"), true);

        Assert.False(BuildFreshness.IsStale(_root, Built));
    }

    [Fact]
    public void BuiltFile_PrefersTheDll_ThenTheExecutable_ThenNothing()
    {
        var publish = Path.Combine(_root, "publish");
        Directory.CreateDirectory(publish);
        Assert.Null(BuildFreshness.BuiltFile(publish));

        var exe = Touch(Built, "publish", OperatingSystem.IsWindows() ? "McpSqlServer.exe" : "McpSqlServer");
        Assert.Equal(exe, BuildFreshness.BuiltFile(publish));

        var dll = Touch(Built, "publish", "McpSqlServer.dll");
        Assert.Equal(dll, BuildFreshness.BuiltFile(publish));
    }
}
