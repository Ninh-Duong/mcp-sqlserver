namespace McpSqlServer;

/// <summary>
/// Warns when the published executable is older than the source next to it (e.g. after `git pull`), the same check
/// `scripts/prepare.ps1` uses to decide whether to publish again. It never rebuilds: `serve` must start fast and
/// stdout belongs to the protocol.
/// </summary>
public static class BuildFreshness
{
    private static readonly string[] SourcePatterns = ["*.cs", "*.csproj", "*.props", "*.targets"];

    /// <summary>Logs a warning when running from `publish/` inside a checkout whose `src/` is newer.</summary>
    public static void WarnIfStale()
    {
        try
        {
            var publishDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!publishDir.Name.Equals("publish", StringComparison.OrdinalIgnoreCase) || publishDir.Parent is null) return;
            var built = BuiltFile(publishDir.FullName);
            if (built is not null && IsStale(publishDir.Parent.FullName, File.GetLastWriteTimeUtc(built)))
            {
                Logger.Warn("The published build is older than src/. Publish again: run `hubctl restart` in mcp-hub, or scripts/prepare.ps1 here.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only: a freshness check must never stop the server.
        }
    }

    /// <summary>
    /// The file whose time says when the build was made: the main dll (rewritten on every rebuild), or the executable
    /// for a single-file publish. The apphost exe alone is not enough: publish skips copying it when it is unchanged.
    /// </summary>
    public static string? BuiltFile(string publishDir)
    {
        var dll = Path.Combine(publishDir, "McpSqlServer.dll");
        if (File.Exists(dll)) return dll;
        var exe = Path.Combine(publishDir, OperatingSystem.IsWindows() ? "McpSqlServer.exe" : "McpSqlServer");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>True when any source file under <paramref name="repoRoot"/>/src is newer than <paramref name="builtUtc"/>.</summary>
    public static bool IsStale(string repoRoot, DateTime builtUtc)
    {
        var src = Path.Combine(repoRoot, "src");
        if (!Directory.Exists(src)) return false;
        return SourcePatterns
            .SelectMany(pattern => Directory.EnumerateFiles(src, pattern, SearchOption.AllDirectories))
            .Where(file => !IsBuildOutput(file))
            .Any(file => File.GetLastWriteTimeUtc(file) > builtUtc);
    }

    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase) ||
               file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase);
    }
}
