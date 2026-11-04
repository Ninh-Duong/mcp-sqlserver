using System.Text.RegularExpressions;

namespace McpSqlServer.Tests;

/// <summary>
/// Enforces docs/RULES.md and docs/ARCHITECTURE.md by scanning the source. If one of these fails, the change
/// breaks the architecture: fix the change, or (for a deliberate decision) add an ADR and update RULES.md in the same PR.
/// </summary>
public class ArchitectureRulesTests
{
    internal static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "McpSqlServer.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("Repository root (McpSqlServer.slnx) not found.");
    }

    private static string Src(params string[] parts) => Path.Combine([RepoRoot, "src", "McpSqlServer", .. parts]);

    private static IEnumerable<string> SourceFiles(string? folder = null)
    {
        var root = folder is null ? Src() : Src(folder);
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                            !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            : [];
    }

    private static string Rel(string file) => Path.GetRelativePath(Src(), file).Replace('\\', '/');

    /// <summary>Strips comments and string literals so rules only judge real code.</summary>
    private static string CodeOnly(string file)
    {
        var text = File.ReadAllText(file);
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"//[^\r\n]*", " ");
        return text;
    }

    private static List<string> Offenders(IEnumerable<string> files, string pattern, bool codeOnly = true)
        => files.Where(f => Regex.IsMatch(codeOnly ? CodeOnly(f) : File.ReadAllText(f), pattern)).Select(Rel).OrderBy(x => x).ToList();

    // ---- RULES #1: one gateway to SQL Server -----------------------------------------------------

    [Fact]
    public void Rule1_SqlClientConnectionsAndCommands_LiveOnlyInSecurityAndScanning()
    {
        var allowed = new[] { "Security/", "Scanning/" };

        var offenders = Offenders(SourceFiles(), @"\b(SqlConnection|SqlCommand|SqlDataReader|SqlBulkCopy|SqlDataAdapter)\b")
            .Where(f => !allowed.Any(a => f.StartsWith(a, StringComparison.Ordinal)))
            .ToList();

        Assert.True(offenders.Count == 0,
            "RULES #1: SqlConnection/SqlCommand are only allowed in Security/ (the read-only gateway) and Scanning/ (offline CLI scan). Offenders: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("Investigation")]
    [InlineData("Mcp")]
    [InlineData("Context")]
    [InlineData("Configuration")]
    public void Rule1_UpperLayers_DoNotReferenceSqlClient(string folder)
    {
        var offenders = Offenders(SourceFiles(folder), @"Microsoft\.Data\.SqlClient");

        Assert.True(offenders.Count == 0, $"{folder}/ must not reference Microsoft.Data.SqlClient. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Rule1_SqlServerService_DoesNotReferenceSqlClient()
    {
        Assert.Empty(Offenders([Src("SqlServerService.cs")], @"Microsoft\.Data\.SqlClient"));
    }

    // ---- ARCHITECTURE: layering ------------------------------------------------------------------

    [Fact]
    public void Layering_McpAndInvestigation_NeverUseTheOfflineScanner()
    {
        var offenders = Offenders(SourceFiles("Mcp").Concat(SourceFiles("Investigation")),
            @"\b(DatabaseSchemaScanner|ScanCheckpoint|ScanServerAsync|ScanDatabaseSchemaAsync|RenderAndExportAsync)\b");

        Assert.True(offenders.Count == 0, "Mcp/ and Investigation/ must not call the offline scanner (it is CLI-only). Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Layering_SecurityDoesNotDependOnUpperLayers()
    {
        var offenders = Offenders(SourceFiles("Security"), @"\b(InvestigationService|SqlTools|McpHost|ToolResults|AiContextRenderer|SqlServerService)\b");

        Assert.True(offenders.Count == 0, "Security/ is the bottom layer and must not depend on tools or services. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Layering_ToolsAreAThinAdapter_NoSqlTextInMcp()
    {
        // SQL text belongs in Investigation/ (fixed catalog queries) or comes from the caller (run_select).
        var offenders = Offenders(SourceFiles("Mcp"), @"""\s*(SELECT|WITH|INSERT|UPDATE|DELETE|EXEC)\s", codeOnly: false);

        Assert.True(offenders.Count == 0, "Mcp/ must not contain SQL text. Offenders: " + string.Join(", ", offenders));
    }

    // ---- RULES #2: tools are read-only forever ---------------------------------------------------

    [Fact]
    public void Rule2_NoToolIsEverDeclaredWritableOrDestructive()
    {
        var offenders = Offenders(SourceFiles("Mcp"), @"ReadOnly\s*=\s*false|Destructive\s*=\s*true");

        Assert.True(offenders.Count == 0, "RULES #2: this MCP is read-only. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Rule2_EveryToolDeclaresReadOnlyExplicitly()
    {
        var text = File.ReadAllText(Src("Mcp", "SqlTools.cs"));
        var declarations = Regex.Matches(text, @"\[McpServerTool\(([^)]*)\)\]");

        Assert.NotEmpty(declarations);
        foreach (Match m in declarations)
            Assert.Contains("ReadOnly = true", m.Groups[1].Value);
    }

    // ---- RULES #3: the validator is an AST allowlist ---------------------------------------------

    [Fact]
    public void Rule3_Validator_IsAstBased_NotRegex()
    {
        var validator = Src("Security", "SelectOnlyValidator.cs");
        var code = CodeOnly(validator);

        Assert.DoesNotContain("Regex", code);
        Assert.Contains("TSqlParser", File.ReadAllText(validator).Replace("TSql170Parser", "TSqlParser"));
        Assert.Contains("SelectStatement", code);
    }

    [Fact]
    public void Rule3_NoOtherCodePathValidatesSqlWithRegexKeywordLists()
    {
        // The old blocklist (INSERT|UPDATE|DELETE|...) must never come back.
        var offenders = Offenders(SourceFiles(), @"\(\?i\)\(?(INSERT|UPDATE|DELETE|DROP)\|", codeOnly: false)
            .Concat(Offenders(SourceFiles(), @"""[^""]*INSERT\|UPDATE\|DELETE[^""]*""", codeOnly: false))
            .Distinct().ToList();

        Assert.True(offenders.Count == 0, "RULES #3: keyword blocklists are forbidden; use SelectOnlyValidator. Offenders: " + string.Join(", ", offenders));
    }

    // ---- RULES #4: the executor keeps its safety layers ------------------------------------------

    [Fact]
    public void Rule4_Executor_AlwaysValidatesRollsBackAndUsesReadOnlyIntent()
    {
        var executor = CodeOnly(Src("Security", "ReadOnlyQueryExecutor.cs"));

        Assert.Contains("SelectOnlyValidator.Validate", executor);
        Assert.Contains("RollbackAsync", executor);
        Assert.DoesNotMatch(@"\.CommitAsync|\.Commit\(", executor);
        Assert.Contains("SET LOCK_TIMEOUT", executor);
        Assert.Contains("SET ROWCOUNT", executor);
        Assert.Contains("validation.Sql!", executor); // the validated text is what gets executed

        var options = CodeOnly(Src("ConnectionOptions.cs"));
        Assert.Contains("ApplicationIntent.ReadOnly", options);
    }

    [Fact]
    public void Rule4_Executor_NeverExecutesCallerSqlThatSkippedValidation()
    {
        var executor = CodeOnly(Src("Security", "ReadOnlyQueryExecutor.cs"));

        // Only the validated text, fixed guard statements and SHOWPLAN toggles may become a SqlCommand.
        var commands = Regex.Matches(executor, @"new SqlCommand\(([^,)]+)").Select(m => m.Groups[1].Value.Trim()).ToList();
        var allowed = new[] { "\"SET SHOWPLAN_XML ON;\"", "\"SET SHOWPLAN_XML OFF;\"", "\"SET ROWCOUNT 0;\"", "guard", "sql" };

        Assert.All(commands, c => Assert.Contains(c, allowed));
    }

    // ---- RULES #5/#6: no string-built SQL, no SQL in logs ----------------------------------------

    [Fact]
    public void Rule5_Investigation_InterpolatesOnlyQuotedIdentifiers()
    {
        // The only interpolated SQL is the lookup builder, which uses SqlIdentifier.Quote on catalog-verified names.
        var text = File.ReadAllText(Src("Investigation", "InvestigationService.cs"));
        var interpolatedSql = Regex.Matches(text, @"\$""(SELECT|WITH)[^""]*""").Select(m => m.Value).ToList();

        Assert.Single(interpolatedSql);
        Assert.Contains("{select}", interpolatedSql[0]);
        Assert.Contains("{obj.Quoted}", interpolatedSql[0]);
        Assert.Contains("SqlIdentifier.Quote", text);
    }

    [Fact]
    public void Rule6_Logs_NeverContainSqlTextOrRowData()
    {
        var files = SourceFiles("Security").Concat(SourceFiles("Investigation")).Concat(SourceFiles("Mcp")).Append(Src("SqlServerService.cs"));
        var offenders = new List<string>();

        foreach (var file in files)
        {
            foreach (var line in CodeOnly(file).Split('\n').Where(l => l.Contains("Logger.")))
            {
                // Counts ({rows.Count}) are fine; the data itself is not.
                if (Regex.IsMatch(line, @"\{\s*(sql|query|validation\.Sql|built\.Sql|parameters|filters|keyValue|key_value)\b") ||
                    Regex.IsMatch(line, @"\{\s*rows?\b(?!\.(Count|Length)\b)"))
                    offenders.Add($"{Rel(file)}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "RULES #6: never log SQL, parameters or data. Offenders:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Rule6_Stdout_IsReservedForTheProtocol()
    {
        // Console.Out / Console.Write* in the server path would corrupt the JSON-RPC stream. CLI-only files are exempt.
        var cliOnly = new[] { "CliMenu.cs", "Program.cs", "PreflightChecker.cs" };
        var offenders = Offenders(SourceFiles().Where(f => !cliOnly.Contains(Path.GetFileName(f))),
            @"Console\.(Out|Write|WriteLine|OpenStandardOutput)\b");

        Assert.True(offenders.Count == 0, "RULES #6: only stderr/Logger outside the CLI. Offenders: " + string.Join(", ", offenders));
    }

    // ---- RULES #7: the hub contract --------------------------------------------------------------

    [Fact]
    public void Rule7_Startup_NeverExitsBecauseCredentialsAreMissing()
    {
        var offenders = Offenders(SourceFiles("Mcp").Concat(SourceFiles("Configuration")), @"Environment\.Exit\(|Environment\.FailFast\(");

        Assert.True(offenders.Count == 0, "RULES #7: the server must start without credentials. Offenders: " + string.Join(", ", offenders));
    }
}
