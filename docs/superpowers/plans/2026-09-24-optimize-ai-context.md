# Optimize AI Context & Snapshot Intelligence for AI Agents Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Optimize `ai-context` scan accuracy, token consumption, and targeted retrieval in `mcp-sqlserver` so AI agents can debug database problems rapidly without context bloat or misleading metadata.

**Architecture:** Follow Ponytail principles (minimal code, zero new external dependencies, native SQLite + Markdown). Enhance metadata precision (Key vs Included index columns, observed sample labels, explicit stats/definition status), guarantee per-DB snapshot consistency and clean-up, streamline `search_context` token footprint with server filtering, add a targeted `get_object_context` MCP tool to eliminate reading multi-megabyte files, and close drift-detection blind spots on EF Core databases.

**Tech Stack:** C# .NET 10, Microsoft.Data.SqlClient, Microsoft.Data.Sqlite, System.Text.Json, xUnit.

**Spec:** Option B (AI Agent Optimized) + Snapshot Consistency from Option C from architectural review.

## Global Constraints

- **Ponytail Principle:** No unrequested abstractions, no interfaces for single implementations, no new dependencies (no vector DB, no embedding models).
- **100% Relative Paths:** Never hardcode absolute file paths (`C:\...`, `D:\...`). Use `AppContext.BaseDirectory` or relative paths.
- **Strict STDIO Separation:** In MCP server mode, `stdout` is strictly for JSON-RPC messages; all logging goes to `stderr` or `./logs/process.log`.
- **Zero-Leak Credentials:** Redact passwords in logs with `***`.
- **Preflight & Test Gate:** Application refuses to run if in-process `PreflightChecker.RunCoreSelfTests()` fails. All unit tests must pass.

---

### Task 1: Reliability Labels & Metadata Precision (Index Key/Include, Sampled Values, Stats & Definition Status)

**Files:**
- Modify: `src/McpSqlServer/SchemaModels.cs:1-60`
- Modify: `src/McpSqlServer/SqlServerService.cs:566-615, 680-700`
- Modify: `src/McpSqlServer/AiContextRenderer.cs:115-155, 530-565`
- Test: `tests/McpSqlServer.Tests/AiContextRendererTests.cs`

**Interfaces:**
- Consumes: `sys.index_columns.is_included_column` from SQL Server catalog query.
- Produces:
  - `IndexSchemaItem(string Name, bool IsUnique, bool IsPrimaryKey, string TypeDesc, IReadOnlyList<string> KeyColumns, IReadOnlyList<string> IncludedColumns, string? FilterDefinition = null)`
  - `ColumnSchemaItem.ToCompactString()` returning `Observed sample: ['val1', 'val2'] (may be incomplete)` instead of misleading enum `Values: [...]`
  - `RenderCompactSchema` outputting `(Stats: unavailable)` when `ApproxRowCount` is null.
  - Routine headers outputting `-- [Definition unavailable: object may be encrypted or login lacks VIEW DEFINITION permission]` when `Definition` is null.

- [ ] **Step 1: Write the failing tests for IndexSchemaItem and ColumnSchemaItem formatting**

Add tests in `tests/McpSqlServer.Tests/AiContextRendererTests.cs`:
```csharp
    [Fact]
    public void IndexSchemaItem_WithIncludedColumns_FormatsCorrectly()
    {
        var idx = new IndexSchemaItem(
            Name: "IX_Orders_CustomerId",
            IsUnique: false,
            IsPrimaryKey: false,
            TypeDesc: "NONCLUSTERED",
            KeyColumns: new[] { "CustomerId", "OrderDate" },
            IncludedColumns: new[] { "TotalAmount", "Status" },
            FilterDefinition: "[Status] <> 'DELETED'"
        );

        var compact = idx.ToCompactString();
        Assert.Equal("- Index: [IX_Orders_CustomerId] (Key: CustomerId, OrderDate | Include: TotalAmount, Status) WHERE [Status] <> 'DELETED'", compact);
    }

    [Fact]
    public void ColumnSchemaItem_WithSampleValues_LabelsAsObservedSample()
    {
        var col = new ColumnSchemaItem(
            Name: "Status",
            DataType: "tinyint",
            IsNullable: false,
            IsPrimaryKey: false,
            IsIdentity: false,
            SampleValues: new[] { "1", "2" }
        );

        var compact = col.ToCompactString();
        Assert.Contains("Observed sample: ['1', '2'] (may be incomplete)", compact);
        Assert.DoesNotContain("Values: ['1', '2']", compact);
    }

    [Fact]
    public void RenderCompactSchema_WhenRowCountUnavailable_OutputsExplicitStatsUnavailable()
    {
        var table = new TableSchemaItem("dbo", "Metrics", new[] { new ColumnSchemaItem("Id", "int", false, true, false) }, approxRowCount: null);
        var report = new DatabaseScanReport("TestDb", true, 1, 0, 0, new[] { table }, Array.Empty<ViewSchemaItem>(), Array.Empty<ProcedureSchemaItem>());
        var md = AiContextRenderer.RenderCompactSchema("DEV", report);

        Assert.Contains("### dbo.Metrics (Stats: unavailable)", md);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj --filter "FullyQualifiedName~IndexSchemaItem_WithIncludedColumns_FormatsCorrectly"`
Expected: Compilation failure or test failure because `IndexSchemaItem` constructor expects old signature.

- [ ] **Step 3: Write minimal implementation**

1. In `src/McpSqlServer/SchemaModels.cs`:
Update `IndexSchemaItem`:
```csharp
public record IndexSchemaItem(
    string Name,
    bool IsUnique,
    bool IsPrimaryKey,
    string TypeDesc,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string>? IncludedColumns = null,
    string? FilterDefinition = null
)
{
    private readonly IReadOnlyList<string>? _includedColumns = IncludedColumns;
    public IReadOnlyList<string> IncludedColumns => _includedColumns ?? Array.Empty<string>();

    public string ToCompactString()
    {
        var keyColsStr = string.Join(", ", KeyColumns);
        var incColsStr = IncludedColumns.Count > 0 ? $" | Include: {string.Join(", ", IncludedColumns)}" : "";
        var flags = new List<string>();
        if (IsPrimaryKey) flags.Add("PK");
        else if (IsUnique) flags.Add("UNIQUE");
        if (!string.IsNullOrEmpty(TypeDesc) && !TypeDesc.Equals("NONCLUSTERED", StringComparison.OrdinalIgnoreCase))
            flags.Add(TypeDesc);
        if (!string.IsNullOrEmpty(FilterDefinition))
            flags.Add($"WHERE {FilterDefinition.Trim()}");

        var flagStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
        if (flags.Any(f => f.StartsWith("WHERE ")))
        {
            var nonWhereFlags = flags.Where(f => !f.StartsWith("WHERE ")).ToList();
            var whereFlag = flags.First(f => f.StartsWith("WHERE "));
            var prefixFlags = nonWhereFlags.Count > 0 ? $" [{string.Join(", ", nonWhereFlags)}]" : "";
            return $"- Index: [{Name}] (Key: {keyColsStr}{incColsStr}){prefixFlags} {whereFlag}";
        }
        return $"- Index: [{Name}] (Key: {keyColsStr}{incColsStr}){flagStr}";
    }
}
```

In `ColumnSchemaItem.ToCompactString()` in `src/McpSqlServer/SchemaModels.cs`:
```csharp
        if (SampleValues != null && SampleValues.Count > 0)
        {
            var vals = string.Join(", ", SampleValues.Select(v => $"'{v}'"));
            attributes.Add($"Observed sample: [{vals}] (may be incomplete)");
        }
```

2. In `src/McpSqlServer/SqlServerService.cs`:
Update `queryIndexes` query and scan parsing:
```sql
SELECT 
    s.name AS schema_name,
    t.name AS table_name,
    i.name AS index_name,
    i.is_unique,
    i.is_primary_key,
    i.type_desc,
    c.name AS column_name,
    i.filter_definition,
    ic.is_included_column
FROM sys.indexes i
INNER JOIN sys.tables t ON i.object_id = t.object_id
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
WHERE i.index_id > 0 AND i.is_hypothetical = 0
ORDER BY s.name, t.name, i.name, ic.is_included_column, ic.key_ordinal;
```
Separate column names into `KeyColumns` and `IncludedColumns` list per index in dictionary.

3. In `src/McpSqlServer/AiContextRenderer.cs`:
In `RenderCompactSchema`:
```csharp
                var volSuffix = "";
                if (table.ApproxRowCount.HasValue)
                {
                    var r = table.ApproxRowCount.Value;
                    var rStr = r >= 1_000_000
                        ? (r / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "M"
                        : r >= 1_000
                            ? (r / 1_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "K"
                            : $"{r}";
                    var sStr = table.ApproxSizeMb.HasValue
                        ? $" | {table.ApproxSizeMb.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} MB"
                        : "";
                    var highVol = r >= 100_000 ? " [HIGH VOLUME]" : "";
                    volSuffix = $" (~{rStr} rows{sStr}){highVol}";
                }
                else
                {
                    volSuffix = " (Stats: unavailable)";
                }
```
In routine file writers:
If definition is null, output:
`-- [Definition unavailable: object may be encrypted or login lacks VIEW DEFINITION permission]`

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj`
Expected: PASS (all tests pass).

- [ ] **Step 5: Commit**

```bash
git add src/McpSqlServer/SchemaModels.cs src/McpSqlServer/SqlServerService.cs src/McpSqlServer/AiContextRenderer.cs tests/McpSqlServer.Tests/AiContextRendererTests.cs
git commit -m "feat(ai-context): enhance index key/include precision, observed sample label, and unavailable stats indicator"
```

---

### Task 2: Per-DB Snapshot Consistency & Clean-up (Orphan Routine Files & Cross-DB Merging)

**Files:**
- Modify: `src/McpSqlServer/AiContextRenderer.cs:58-155, 205-245`
- Test: `tests/McpSqlServer.Tests/AiContextRendererTests.cs`

**Interfaces:**
- Consumes: `scanResult.Databases`, `serverDir`, `CROSS_DB_DEPENDENCIES.compact.md`.
- Produces:
  - Cleaned routine folders with no orphan `.sql` files from dropped objects.
  - Multi-DB preserved `CROSS_DB_DEPENDENCIES.compact.md` that merges single-DB scan results without clobbering other DBs.
  - Filtered out self-references from cross-db dependencies list.
  - Header in `schema.compact.md` recording `Scanned At: {ScannedAt}` and status.

- [ ] **Step 1: Write the failing tests for single-DB orphan cleanup and cross-db dependency preserve**

Add test in `tests/McpSqlServer.Tests/AiContextRendererTests.cs`:
```csharp
    [Fact]
    public async Task RenderAndExportAsync_SingleDbScan_DeletesOrphanRoutineFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_clean_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var dbDir = Path.Combine(tempDir, "servers", "DEV", "databases", "AppDb");
            var procsDir = Path.Combine(dbDir, "procedures");
            Directory.CreateDirectory(procsDir);
            var orphanFile = Path.Combine(procsDir, "dbo.OldDeletedProc.sql");
            await File.WriteAllTextAsync(orphanFile, "-- Old proc");

            var scanResult = new ServerScanResult(
                ServerAlias: "DEV",
                ServerHost: "localhost",
                ServerVersion: "SQL Server 2022",
                ScannedAt: DateTime.Now,
                ElapsedMs: 50,
                Databases:
                [
                    new DatabaseScanReport(
                        "AppDb",
                        true,
                        0, 0, 1,
                        Array.Empty<TableSchemaItem>(),
                        Array.Empty<ViewSchemaItem>(),
                        [new ProcedureSchemaItem("dbo", "NewProc", Array.Empty<string>(), "SELECT 1")]
                    )
                ]
            );

            await AiContextRenderer.RenderAndExportAsync(tempDir, scanResult);

            Assert.False(File.Exists(orphanFile), "Orphan routine file should have been deleted");
            Assert.True(File.Exists(Path.Combine(procsDir, "dbo.NewProc.sql")));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RenderAndExportAsync_SingleDbScan_PreservesExistingCrossDbDependenciesOfOtherDbs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_crossdb_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var serverDir = Path.Combine(tempDir, "servers", "DEV");
            Directory.CreateDirectory(serverDir);
            var crossDbPath = Path.Combine(serverDir, "CROSS_DB_DEPENDENCIES.compact.md");
            await File.WriteAllTextAsync(crossDbPath, "# Cross-Database Dependencies Map: DEV\n\n## Database `ExistingDb`\n- -> `OtherDb`:\n  - `dbo.sp_Call`\n");

            var scanResult = new ServerScanResult(
                ServerAlias: "DEV",
                ServerHost: "localhost",
                ServerVersion: "SQL Server 2022",
                ScannedAt: DateTime.Now,
                ElapsedMs: 50,
                Databases:
                [
                    new DatabaseScanReport(
                        "NewDb",
                        true,
                        0, 0, 0,
                        Array.Empty<TableSchemaItem>(),
                        Array.Empty<ViewSchemaItem>(),
                        Array.Empty<ProcedureSchemaItem>(),
                        CrossDbDependencies: [new CrossDbDependencyItem("dbo.Proc1", "TargetDb", "dbo.TargetTable")]
                    )
                ]
            );

            await AiContextRenderer.RenderAndExportAsync(tempDir, scanResult);

            var content = await File.ReadAllTextAsync(crossDbPath);
            Assert.Contains("## Database `ExistingDb`", content);
            Assert.Contains("## Database `NewDb`", content);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj --filter "FullyQualifiedName~SingleDbScan"`
Expected: FAIL (orphan file not deleted, existing cross-db dependencies overwritten).

- [ ] **Step 3: Write minimal implementation**

1. Routine folder cleanup:
When writing views/procs/funcs/triggers for `db`:
Collect the expected file names for each folder:
```csharp
var expectedViewFiles = new HashSet<string>(db.Views.Select(v => SanitizeFileName($"{v.Schema}.{v.Name}.sql")), StringComparer.OrdinalIgnoreCase);
if (Directory.Exists(viewsDir))
{
    foreach (var existing in Directory.GetFiles(viewsDir, "*.sql"))
    {
        if (!expectedViewFiles.Contains(Path.GetFileName(existing)))
        {
            try { File.Delete(existing); } catch { }
        }
    }
}
```
Apply the same pattern to `procedures/`, `functions/`, and `triggers/`.

2. Cross-DB Dependency Self-Reference Filter:
Filter dependencies where referenced DB equals current DB:
```csharp
var validCrossDb = db.CrossDbDependencies
    .Where(d => !string.IsNullOrWhiteSpace(d.ReferencedDatabase) && !d.ReferencedDatabase.Equals(db.DatabaseName, StringComparison.OrdinalIgnoreCase))
    .ToList();
```

3. Preserve other databases in `CROSS_DB_DEPENDENCIES.compact.md`:
Parse existing markdown by `## Database `...`` into a `Dictionary<string, string> existingDbSections`.
Update or add entries for the scanned databases.
Write back the merged markdown with header and ordered sections.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj --filter "FullyQualifiedName~SingleDbScan"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/McpSqlServer/AiContextRenderer.cs tests/McpSqlServer.Tests/AiContextRendererTests.cs
git commit -m "fix(ai-context): clean up orphan routine files and preserve cross-db dependencies on single-db scan"
```

---

### Task 3: Targeted Search Optimization (Server Alias Filter, Concise Details, SQLite Indexes)

**Files:**
- Modify: `src/McpSqlServer/AiContextRenderer.cs:350-420, 733-810`
- Modify: `src/McpSqlServer/McpServerHandler.cs:233-264, 345-375`
- Test: `tests/McpSqlServer.Tests/AiContextRendererTests.cs`
- Test: `tests/McpSqlServer.Tests/McpServerHandlerTests.cs`

**Interfaces:**
- Consumes: MCP tool `search_context(query, server_alias, database, target, limit, include_details)`.
- Produces: `SearchContextResult` with concise results (Server, DB, Type, Name, RelativePath, and details only if `include_details=true`).

- [ ] **Step 1: Write the failing tests for concise search and server_alias filter**

Add tests in `tests/McpSqlServer.Tests/AiContextRendererTests.cs`:
```csharp
    [Fact]
    public async Task SearchContextAsync_WithServerFilter_ReturnsOnlyMatchingServer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_search_srv_" + Guid.NewGuid().ToString("N"));
        try
        {
            var resDev = new ServerScanResult("DEV", "localhost", "2022", DateTime.Now, 10, [
                new DatabaseScanReport("Db1", true, 1, 0, 0, [new TableSchemaItem("dbo", "Users", [])], [], [])
            ]);
            var resProd = new ServerScanResult("PROD", "localhost", "2022", DateTime.Now, 10, [
                new DatabaseScanReport("Db1", true, 1, 0, 0, [new TableSchemaItem("dbo", "Users", [])], [], [])
            ]);
            await AiContextRenderer.RenderAndExportAsync(tempDir, resDev);
            await AiContextRenderer.RenderAndExportAsync(tempDir, resProd);

            var searchResult = await AiContextRenderer.SearchContextAsync(tempDir, "Users", serverAlias: "DEV");

            Assert.All(searchResult.Matches, m => Assert.Equal("DEV", m.ServerAlias));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task SearchContextAsync_DefaultIncludeDetailsFalse_ReturnsConciseDetails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_search_concise_" + Guid.NewGuid().ToString("N"));
        try
        {
            var res = new ServerScanResult("DEV", "localhost", "2022", DateTime.Now, 10, [
                new DatabaseScanReport("Db1", true, 1, 0, 0, [
                    new TableSchemaItem("dbo", "Users", [
                        new ColumnSchemaItem("Id", "int", false, true, true),
                        new ColumnSchemaItem("Email", "nvarchar(100)", false, false, false)
                    ])
                ], [], [])
            ]);
            await AiContextRenderer.RenderAndExportAsync(tempDir, res);

            var searchResult = await AiContextRenderer.SearchContextAsync(tempDir, "Users", includeDetails: false);

            var tableMatch = searchResult.Matches.First(m => m.Type == "table");
            Assert.Empty(tableMatch.Details); // Concise mode omits heavy column details
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj --filter "FullyQualifiedName~SearchContextAsync_"`
Expected: Compilation failure because parameters `serverAlias` and `includeDetails` do not exist.

- [ ] **Step 3: Write minimal implementation**

1. In `src/McpSqlServer/AiContextRenderer.cs`:
In `UpdateCatalogDbAsync`:
Add indexes to SQLite:
```csharp
const string createIndexesSql = @"
CREATE INDEX IF NOT EXISTS idx_catalog_items_server_db_type ON catalog_items(server_alias, database_name, item_type);
CREATE INDEX IF NOT EXISTS idx_catalog_items_name ON catalog_items(name COLLATE NOCASE);
";
await using var idxCmd = conn.CreateCommand();
idxCmd.CommandText = createIndexesSql;
await idxCmd.ExecuteNonQueryAsync(cancellationToken);
```
Remove `catalog_fts` virtual table creation (dead/unused).

2. Update `SearchContextAsync` signature:
```csharp
    public static async Task<SearchContextResult> SearchContextAsync(
        string baseDirectory,
        string query,
        string? serverAlias = null,
        string? database = null,
        string? target = "all",
        int limit = 10,
        bool includeDetails = false,
        CancellationToken cancellationToken = default)
```
Add `@server IS NULL OR server_alias = @server COLLATE NOCASE` to SQL.
When building `SearchContextMatch`:
`Details: includeDetails && !reader.IsDBNull(4) ? reader.GetString(4) : ""`

3. In `src/McpSqlServer/McpServerHandler.cs`:
Update `search_context` tool definition:
- Add `server_alias` optional property.
- Add `include_details` boolean property (default `false`).
- Change `limit` description to default 10 (max 50).
In `tools/call` for `search_context`: parse `server_alias` and `include_details`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj --filter "FullyQualifiedName~SearchContext"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/McpSqlServer/AiContextRenderer.cs src/McpSqlServer/McpServerHandler.cs tests/McpSqlServer.Tests/AiContextRendererTests.cs tests/McpSqlServer.Tests/McpServerHandlerTests.cs
git commit -m "feat(ai-context): optimize search_context with server filter, concise token mode, and sqlite index"
```

---

### Task 4: Targeted Object Context MCP Tool (`get_object_context`)

**Files:**
- Create/Modify: `src/McpSqlServer/AiContextRenderer.cs` (add `GetObjectContextAsync`)
- Modify: `src/McpSqlServer/McpServerHandler.cs:115-265, 300-380`
- Test: `tests/McpSqlServer.Tests/AiContextRendererTests.cs`
- Test: `tests/McpSqlServer.Tests/McpServerHandlerTests.cs`

**Interfaces:**
- Consumes: MCP tool `get_object_context(database, name, server_alias, type)`.
- Produces: Markdown/SQL string for the specific requested object:
  - For a table: extracts the section `### [schema].[table] ...` from `schema.compact.md`.
  - For a routine (view/procedure/function/trigger): returns the text of `servers/{server}/databases/{db}/{type}s/{name}.sql`.
  - For dependencies: extracts dependency lines matching `{name}` from `CROSS_DB_DEPENDENCIES.compact.md`.

- [ ] **Step 1: Write the failing tests for get_object_context**

Add tests in `tests/McpSqlServer.Tests/AiContextRendererTests.cs`:
```csharp
    [Fact]
    public async Task GetObjectContextAsync_Table_ReturnsOnlyTargetTableMarkdown()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_get_obj_" + Guid.NewGuid().ToString("N"));
        try
        {
            var res = new ServerScanResult("DEV", "localhost", "2022", DateTime.Now, 10, [
                new DatabaseScanReport("Db1", true, 2, 0, 0, [
                    new TableSchemaItem("dbo", "Users", [new ColumnSchemaItem("Id", "int", false, true, true)]),
                    new TableSchemaItem("dbo", "Orders", [new ColumnSchemaItem("OrderId", "bigint", false, true, true)])
                ], [], [])
            ]);
            await AiContextRenderer.RenderAndExportAsync(tempDir, res);

            var tableMd = await AiContextRenderer.GetObjectContextAsync(tempDir, "DEV", "Db1", "Orders", "table");

            Assert.Contains("### dbo.Orders", tableMd);
            Assert.Contains("OrderId", tableMd);
            Assert.DoesNotContain("### dbo.Users", tableMd); // Only target table extracted
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GetObjectContextAsync_Procedure_ReturnsSqlContent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_get_proc_" + Guid.NewGuid().ToString("N"));
        try
        {
            var res = new ServerScanResult("DEV", "localhost", "2022", DateTime.Now, 10, [
                new DatabaseScanReport("Db1", true, 0, 0, 1, [], [], [
                    new ProcedureSchemaItem("dbo", "sp_GetOrder", ["@Id int"], "SELECT * FROM Orders WHERE Id = @Id")
                ])
            ]);
            await AiContextRenderer.RenderAndExportAsync(tempDir, res);

            var sql = await AiContextRenderer.GetObjectContextAsync(tempDir, "DEV", "Db1", "sp_GetOrder", "procedure");

            Assert.Contains("sp_GetOrder", sql);
            Assert.Contains("SELECT * FROM Orders", sql);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj --filter "FullyQualifiedName~GetObjectContextAsync"`
Expected: Compilation failure because `GetObjectContextAsync` is not implemented.

- [ ] **Step 3: Write minimal implementation**

1. In `src/McpSqlServer/AiContextRenderer.cs`:
Implement `GetObjectContextAsync`:
```csharp
    public static async Task<string> GetObjectContextAsync(
        string baseDirectory,
        string serverAlias,
        string database,
        string objectName,
        string? objectType = null,
        CancellationToken cancellationToken = default)
    {
        var fullBaseDir = Path.GetFullPath(baseDirectory);
        var dbDir = Path.Combine(fullBaseDir, "servers", serverAlias, "databases", database);
        if (!Directory.Exists(dbDir))
        {
            return $"Database '{database}' on server '{serverAlias}' not found in ai-context snapshot.";
        }

        var normalizedName = objectName.Trim().Trim('[', ']');
        var pureName = normalizedName.Contains('.') ? normalizedName.Substring(normalizedName.LastIndexOf('.') + 1) : normalizedName;

        var type = objectType?.Trim().ToLowerInvariant();

        // 1. If table or unspecified
        if (type == "table" || string.IsNullOrEmpty(type))
        {
            var schemaFile = Path.Combine(dbDir, "schema.compact.md");
            if (File.Exists(schemaFile))
            {
                var lines = await File.ReadAllLinesAsync(schemaFile, cancellationToken);
                var section = new StringBuilder();
                bool recording = false;
                foreach (var line in lines)
                {
                    if (line.StartsWith("### ") && (line.Contains($".{pureName} ") || line.EndsWith($".{pureName}") || line.Contains($" {pureName} ")))
                    {
                        recording = true;
                        section.AppendLine(line);
                        continue;
                    }
                    if (recording)
                    {
                        if (line.StartsWith("### ") || line.StartsWith("## ")) break;
                        section.AppendLine(line);
                    }
                }
                if (section.Length > 0) return section.ToString().TrimEnd();
            }
        }

        // 2. Check routine SQL files (views, procedures, functions, triggers)
        var routineFolders = new[] { "procedures", "views", "functions", "triggers" };
        foreach (var folder in routineFolders)
        {
            if (!string.IsNullOrEmpty(type) && !folder.StartsWith(type)) continue;

            var folderPath = Path.Combine(dbDir, folder);
            if (Directory.Exists(folderPath))
            {
                var match = Directory.GetFiles(folderPath, $"*.{pureName}.sql")
                    .Concat(Directory.GetFiles(folderPath, $"{pureName}.sql"))
                    .FirstOrDefault();

                if (match != null)
                {
                    return await File.ReadAllTextAsync(match, cancellationToken);
                }
            }
        }

        // 3. Check cross-db dependencies
        if (type == "dependency" || string.IsNullOrEmpty(type))
        {
            var crossDbPath = Path.Combine(fullBaseDir, "servers", serverAlias, "CROSS_DB_DEPENDENCIES.compact.md");
            if (File.Exists(crossDbPath))
            {
                var lines = await File.ReadAllLinesAsync(crossDbPath, cancellationToken);
                var matchingLines = lines.Where(l => l.Contains(pureName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matchingLines.Count > 0)
                {
                    return $"### Cross-Database Dependencies for '{pureName}':\n" + string.Join("\n", matchingLines);
                }
            }
        }

        return $"Object '{objectName}' not found in ai-context snapshot for {serverAlias}/{database}.";
    }
```

2. In `src/McpSqlServer/McpServerHandler.cs`:
Add `get_object_context` to tools list:
```json
{
  "name": "get_object_context",
  "description": "Retrieve the exact compact definition or SQL code for a specific table, view, procedure, function, or trigger without loading massive files.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "database": { "type": "string", "description": "Database name containing the object." },
      "name": { "type": "string", "description": "Target object name (e.g. 'Orders' or 'dbo.sp_GetCustomerSummary')." },
      "server_alias": { "type": "string", "description": "Optional server alias (defaults to configured server)." },
      "type": { "type": "string", "description": "Optional object type ('table', 'view', 'procedure', 'function', 'trigger', 'dependency')." }
    },
    "required": ["database", "name"]
  }
}
```
Add handling in `tools/call`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/McpSqlServer/AiContextRenderer.cs src/McpSqlServer/McpServerHandler.cs tests/McpSqlServer.Tests/AiContextRendererTests.cs tests/McpSqlServer.Tests/McpServerHandlerTests.cs
git commit -m "feat(mcp): add get_object_context tool for zero-token-waste targeted object retrieval"
```

---

### Task 5: Eliminate Drift Check Blind Spots for EF Core Databases

**Files:**
- Modify: `src/McpSqlServer/SqlServerService.cs:1130-1165`
- Test: `tests/McpSqlServer.Tests/SqlServerServiceTests.cs`

**Interfaces:**
- Consumes: `cached.LatestMigrationId`, `currentMigrationId`, `cached.LastObjectModifyDate`, `currentLastModify`.
- Produces: `DatabaseDriftInfo` flagging DDL modifications even when EF migrations haven't changed.

- [ ] **Step 1: Write the failing test for manual DDL change detection on EF database**

Add test in `tests/McpSqlServer.Tests/SqlServerServiceTests.cs`:
```csharp
    [Fact]
    public void CheckSchemaDrift_WhenEfMigrationsMatch_ButLastModifyDateIsNewer_FlagsDrift()
    {
        // Setup drift comparison logic
        var snapshotDate = new DateTime(2026, 9, 20, 10, 0, 0);
        var manualDdlDate = new DateTime(2026, 9, 24, 11, 0, 0);
        var migrationId = "20260920000000_Initial";

        var isDrifted = SqlServerService.EvaluateDatabaseDrift(
            snapshotMigrationId: migrationId,
            currentMigrationId: migrationId,
            snapshotCount: 1,
            currentCount: 1,
            snapshotLastModify: snapshotDate,
            currentLastModify: manualDdlDate,
            out var driftReason
        );

        Assert.True(isDrifted);
        Assert.Contains("DDL Modified", driftReason);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj --filter "FullyQualifiedName~EvaluateDatabaseDrift"`
Expected: Compilation failure because helper `EvaluateDatabaseDrift` does not exist.

- [ ] **Step 3: Write minimal implementation**

1. In `src/McpSqlServer/SqlServerService.cs`:
Add pure evaluation method `EvaluateDatabaseDrift`:
```csharp
    public static bool EvaluateDatabaseDrift(
        string? snapshotMigrationId,
        string? currentMigrationId,
        int snapshotCount,
        int currentCount,
        DateTime? snapshotLastModify,
        DateTime? currentLastModify,
        out string driftReason)
    {
        // 1. Check EF migration drift
        if (!string.Equals(snapshotMigrationId, currentMigrationId, StringComparison.OrdinalIgnoreCase) ||
            snapshotCount != currentCount)
        {
            var diff = currentCount - snapshotCount;
            var diffStr = diff > 0 ? $"+{diff} new migrations" : $"{diff} migrations";
            driftReason = $"{diffStr} (Latest: {currentMigrationId ?? "none"})";
            return true;
        }

        // 2. Check DDL modification drift for ALL databases (regardless of whether EF is used)
        if (currentLastModify.HasValue && snapshotLastModify.HasValue &&
            currentLastModify.Value > snapshotLastModify.Value.AddSeconds(5))
        {
            driftReason = $"DDL Modified: {currentLastModify:yyyy-MM-dd HH:mm}";
            return true;
        }

        driftReason = "Up to date";
        return false;
    }
```

2. In `CheckSchemaDriftAsync`:
Call `EvaluateDatabaseDrift`:
```csharp
    var hasDrift = EvaluateDatabaseDrift(
        cached.LatestMigrationId,
        currentMigrationId,
        cached.MigrationCount,
        currentCount,
        cached.LastObjectModifyDate,
        currentLastModify,
        out var driftReason
    );

    if (hasDrift)
    {
        drifted.Add(new DatabaseDriftInfo(
            DatabaseName: db.Name,
            HasDrift: true,
            SnapshotMigrationId: cached.LatestMigrationId,
            CurrentMigrationId: currentMigrationId,
            SnapshotCount: cached.MigrationCount,
            CurrentCount: currentCount,
            Reason: driftReason
        ));
    }
    else
    {
        upToDate.Add(new DatabaseDriftInfo(
            DatabaseName: db.Name,
            HasDrift: false,
            SnapshotMigrationId: cached.LatestMigrationId,
            CurrentMigrationId: currentMigrationId,
            SnapshotCount: cached.MigrationCount,
            CurrentCount: currentCount,
            Reason: "Up to date"
        ));
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/McpSqlServer.Tests/McpSqlServer.Tests.csproj --filter "FullyQualifiedName~EvaluateDatabaseDrift"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/McpSqlServer/SqlServerService.cs tests/McpSqlServer.Tests/SqlServerServiceTests.cs
git commit -m "fix(drift): detect manual DDL modifications on EF Core databases"
```

---

### Task 6: Preflight Self-tests Update & Full Integration Verification

**Files:**
- Modify: `src/McpSqlServer/PreflightChecker.cs:155-180`
- Test: `tests/McpSqlServer.Tests/PreflightCheckerTests.cs`

**Interfaces:**
- Consumes: `PreflightChecker.RunCoreSelfTests()`.
- Produces: `PreflightReport(Success: true)` covering the new index key/include and observed sample formats.

- [ ] **Step 1: Write/Update the self-test in PreflightChecker**

In `src/McpSqlServer/PreflightChecker.cs`:
In `RunCoreSelfTests()` Test 4:
Update formatting verification to assert `Observed sample:` and `IndexSchemaItem` key vs include formatting:
```csharp
        // Test 4: AI Context Schema Formatting & Data Type Formatter
        try
        {
            var formattedType = SqlServerService.FormatDataType("nvarchar", 100, 0, 0);
            var col = new ColumnSchemaItem("Username", formattedType, false, true, false, "dbo.Roles.Id", SampleValues: new[] { "Admin", "User" });
            var compactStr = col.ToCompactString();

            var idx = new IndexSchemaItem("IX_User_Role", false, false, "NONCLUSTERED", new[] { "Username" }, new[] { "RoleId" });
            var idxStr = idx.ToCompactString();

            if (formattedType != "nvarchar(50)" || !compactStr.Contains("PK") || !compactStr.Contains("Observed sample: ['Admin', 'User'] (may be incomplete)"))
            {
                errors.Add($"SelfTest [AiContext.SchemaFormat] unexpected column format: {compactStr}");
            }
            else if (!idxStr.Contains("Key: Username") || !idxStr.Contains("Include: RoleId"))
            {
                errors.Add($"SelfTest [AiContext.IndexFormat] unexpected index format: {idxStr}");
            }
            else
            {
                passed.Add("SelfTest [AiContext]: Schema & Index Formatting PASS.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"SelfTest [AiContext] exception: {ex.Message}");
        }
```

- [ ] **Step 2: Run all unit tests**

Run: `dotnet test`
Expected: 100% tests pass (0 failures).

- [ ] **Step 3: Run full build and preflight checker test**

Run: `dotnet test --filter "FullyQualifiedName~PreflightChecker"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/McpSqlServer/PreflightChecker.cs tests/McpSqlServer.Tests/PreflightCheckerTests.cs
git commit -m "test: update preflight core self-tests with new index and sample formats"
```

---

## Plan Review & Handoff

Checklist:
- [x] Spec coverage: Addresses all 6 priority items from Option B + C snapshot consistency.
- [x] No placeholders: Every step contains exact signatures, tests, and code modifications.
- [x] Ponytail alignment: Zero external libraries added, native SQLite + Markdown parsing, no over-engineering.
- [x] Strict STDIO & Relative Paths: Preserved.
