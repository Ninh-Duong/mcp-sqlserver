using System.Text;
using System.Text.Json;

namespace McpSqlServer;

public record ServerRegistryItem(
    string ServerAlias,
    string ServerHost,
    string ServerVersion,
    int DatabaseCount,
    int TotalTableCount,
    int TotalViewCount,
    int TotalProcedureCount,
    DateTime LastScannedAt,
    int TotalFunctionCount = 0,
    int TotalTriggerCount = 0
);

public record SearchContextMatch(
    string ServerAlias,
    string Database,
    string Type,
    string Name,
    string Details,
    string RelativePath
);

public record SearchContextResult(
    string Query,
    int TotalMatches,
    IReadOnlyList<SearchContextMatch> Matches,
    string Message
);

public class AiContextRenderer
{
    public static async Task<IReadOnlyList<string>> RenderAndExportAsync(
        ServerScanResult scanResult,
        string outputDirectory = "./ai-context",
        CancellationToken cancellationToken = default)
    {
        var createdFiles = new List<string>();
        var baseDir = Path.GetFullPath(outputDirectory);
        var serversDir = Path.Combine(baseDir, "servers");
        var serverDir = Path.Combine(serversDir, scanResult.ServerAlias);

        Directory.CreateDirectory(serverDir);

        int totalTables = 0;
        int totalViews = 0;
        int totalProcedures = 0;
        int totalFunctions = 0;
        int totalTriggers = 0;

        // 1. Render Global Tables Map (with Critical Token Safety Warning)
        var globalMapSb = new StringBuilder();
        globalMapSb.AppendLine($"# Global Tables Map: {scanResult.ServerAlias}");
        globalMapSb.AppendLine();
        globalMapSb.AppendLine($"> ⚠️ CRITICAL AGENT INSTRUCTION: This file is large (~600KB+). NEVER use view_file to load the entire document.");
        globalMapSb.AppendLine($"> ALWAYS use grep_search to find specific columns/tables, or call MCP tool `search_context(query)` for instant ~50-token answers.");
        globalMapSb.AppendLine($"> Single-line index per table for reverse lookup on Server [{scanResult.ServerAlias}].");
        globalMapSb.AppendLine($"> Last updated: {scanResult.ScannedAt:yyyy-MM-dd HH:mm:ss}");
        globalMapSb.AppendLine();

        // 2. Render Fast Skeleton Router (Two-Tier Index for ultra-low token lookup ~20KB)
        var tableRouterSb = new StringBuilder();
        tableRouterSb.AppendLine($"# Database Tables Router: {scanResult.ServerAlias}");
        tableRouterSb.AppendLine();
        tableRouterSb.AppendLine($"> Ultra-compact skeleton index (~20KB) for low-token database routing. Avoid reading entire global map.");
        tableRouterSb.AppendLine($"> Flow: Locate database for table -> Open './databases/{{DB}}/schema.compact.md'.");
        tableRouterSb.AppendLine($"> Last updated: {scanResult.ScannedAt:yyyy-MM-dd HH:mm:ss}");
        tableRouterSb.AppendLine();

        var dbSummaries = new StringBuilder();
        dbSummaries.AppendLine($"# Server Overview: {scanResult.ServerAlias}");
        dbSummaries.AppendLine();
        dbSummaries.AppendLine($"- **Host:** `{scanResult.ServerHost}`");
        dbSummaries.AppendLine($"- **SQL Version:** {scanResult.ServerVersion}");
        dbSummaries.AppendLine($"- **Scan Time:** {scanResult.ScannedAt:yyyy-MM-dd HH:mm:ss}");
        dbSummaries.AppendLine($"- **Execution Duration:** {scanResult.ElapsedMs} ms");
        dbSummaries.AppendLine($"- **Total Databases:** {scanResult.Databases.Count}");
        dbSummaries.AppendLine($"- **Fast Router Map (~20KB):** [TABLES_ROUTER.compact.md](./TABLES_ROUTER.compact.md) (Ultra-low token table lookup)");
        dbSummaries.AppendLine($"- **Detailed Columns Map (~600KB):** [GLOBAL_TABLES_MAP.compact.md](./GLOBAL_TABLES_MAP.compact.md) (Grep only!)");
        dbSummaries.AppendLine();
        dbSummaries.AppendLine("## Databases Overview");
        dbSummaries.AppendLine();
        dbSummaries.AppendLine("| Database | Status | Tables | Views | Procedures | Functions | Triggers | Schema Compact Link |");
        dbSummaries.AppendLine("|:---|:---:|:---:|:---:|:---:|:---:|:---:|:---|");

        foreach (var db in scanResult.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            totalTables += db.TableCount;
            totalViews += db.ViewCount;
            totalProcedures += db.ProcedureCount;
            totalFunctions += db.FunctionCount;
            totalTriggers += db.TriggerCount;

            var status = db.Success ? "Success" : "Failed";
            var schemaLink = db.Success ? $"[databases/{db.DatabaseName}/schema.compact.md](./databases/{db.DatabaseName}/schema.compact.md)" : $"*(Error: {db.ErrorMessage})*";
            dbSummaries.AppendLine($"| **{db.DatabaseName}** | {status} | {db.TableCount} | {db.ViewCount} | {db.ProcedureCount} | {db.FunctionCount} | {db.TriggerCount} | {schemaLink} |");

            if (!db.Success) continue;

            // Add to Fast Router (skeleton table names only)
            if (db.Tables.Count > 0)
            {
                var tableNames = string.Join(", ", db.Tables.Select(t => t.Name));
                tableRouterSb.AppendLine($"- **`{db.DatabaseName}`**: {tableNames}");
            }

            // Add to Global Tables Map (with columns, PK, FK)
            foreach (var table in db.Tables)
            {
                var colsStr = string.Join(", ", table.Columns.Select(c => c.ToQuickMapString()));
                globalMapSb.AppendLine($"- `{db.DatabaseName}.{table.FullName}`: {colsStr}");
            }

            var dbDir = Path.Combine(serverDir, "databases", db.DatabaseName);
            var viewsDir = Path.Combine(dbDir, "views");
            var procsDir = Path.Combine(dbDir, "procedures");
            var funcsDir = Path.Combine(dbDir, "functions");
            var trgsDir = Path.Combine(dbDir, "triggers");

            Directory.CreateDirectory(dbDir);
            if (db.Views.Count > 0) Directory.CreateDirectory(viewsDir);
            if (db.Procedures.Count > 0) Directory.CreateDirectory(procsDir);
            if (db.Functions.Count > 0) Directory.CreateDirectory(funcsDir);
            if (db.Triggers.Count > 0) Directory.CreateDirectory(trgsDir);

            // Write schema.compact.md
            var compactSchemaPath = Path.Combine(dbDir, "schema.compact.md");
            var compactContent = RenderCompactSchema(scanResult.ServerAlias, db);
            await File.WriteAllTextAsync(compactSchemaPath, compactContent, Encoding.UTF8, cancellationToken);
            createdFiles.Add(compactSchemaPath);

            // Write individual View SQL files
            foreach (var view in db.Views)
            {
                var safeViewName = SanitizeFileName($"{view.Schema}.{view.Name}.sql");
                var viewPath = Path.Combine(viewsDir, safeViewName);
                var viewSql = $"-- Server: {scanResult.ServerAlias} | DB: {db.DatabaseName} | View: {view.FullName}\n" +
                              $"-- Auto-generated by MCP SQL Server\n\n" +
                              (view.Definition ?? "-- [No definition available]");
                await File.WriteAllTextAsync(viewPath, viewSql, Encoding.UTF8, cancellationToken);
                createdFiles.Add(viewPath);
            }

            // Write individual Procedure SQL files
            foreach (var proc in db.Procedures)
            {
                var safeProcName = SanitizeFileName($"{proc.Schema}.{proc.Name}.sql");
                var procPath = Path.Combine(procsDir, safeProcName);
                var paramsHeader = proc.Parameters.Count > 0
                    ? $"-- Parameters:\n" + string.Join("\n", proc.Parameters.Select(p => $"--   {p}")) + "\n"
                    : "-- Parameters: (None)\n";

                var procSql = $"-- Server: {scanResult.ServerAlias} | DB: {db.DatabaseName} | Procedure: {proc.FullName}\n" +
                              paramsHeader +
                              $"-- Auto-generated by MCP SQL Server\n\n" +
                              (proc.Definition ?? "-- [No definition available]");
                await File.WriteAllTextAsync(procPath, procSql, Encoding.UTF8, cancellationToken);
                createdFiles.Add(procPath);
            }

            // Write individual Function SQL files
            foreach (var func in db.Functions)
            {
                var safeFuncName = SanitizeFileName($"{func.Schema}.{func.Name}.sql");
                var funcPath = Path.Combine(funcsDir, safeFuncName);
                var paramsHeader = func.Parameters.Count > 0
                    ? $"-- Parameters:\n" + string.Join("\n", func.Parameters.Select(p => $"--   {p}")) + "\n"
                    : "-- Parameters: (None)\n";

                var funcSql = $"-- Server: {scanResult.ServerAlias} | DB: {db.DatabaseName} | Function: {func.FullName} ({func.TypeDesc})\n" +
                              paramsHeader +
                              $"-- Auto-generated by MCP SQL Server\n\n" +
                              (func.Definition ?? "-- [No definition available]");
                await File.WriteAllTextAsync(funcPath, funcSql, Encoding.UTF8, cancellationToken);
                createdFiles.Add(funcPath);
            }

            // Write individual Trigger SQL files
            foreach (var trg in db.Triggers)
            {
                var safeTrgName = SanitizeFileName($"{trg.Schema}.{trg.Name}.sql");
                var trgPath = Path.Combine(trgsDir, safeTrgName);
                var trgSql = $"-- Server: {scanResult.ServerAlias} | DB: {db.DatabaseName} | Trigger: {trg.FullName} on [{trg.TargetTable}] ({trg.Events})\n" +
                             $"-- Auto-generated by MCP SQL Server\n\n" +
                             (trg.Definition ?? "-- [No definition available]");
                await File.WriteAllTextAsync(trgPath, trgSql, Encoding.UTF8, cancellationToken);
                createdFiles.Add(trgPath);
            }
        }

        // Write servers/{ServerAlias}/TABLES_ROUTER.compact.md
        var routerPath = Path.Combine(serverDir, "TABLES_ROUTER.compact.md");
        await File.WriteAllTextAsync(routerPath, tableRouterSb.ToString(), Encoding.UTF8, cancellationToken);
        createdFiles.Add(routerPath);

        // Write servers/{ServerAlias}/GLOBAL_TABLES_MAP.compact.md
        var globalMapPath = Path.Combine(serverDir, "GLOBAL_TABLES_MAP.compact.md");
        await File.WriteAllTextAsync(globalMapPath, globalMapSb.ToString(), Encoding.UTF8, cancellationToken);
        createdFiles.Add(globalMapPath);

        // Write servers/{ServerAlias}/summary.md
        var serverSummaryPath = Path.Combine(serverDir, "summary.md");
        await File.WriteAllTextAsync(serverSummaryPath, dbSummaries.ToString(), Encoding.UTF8, cancellationToken);
        createdFiles.Add(serverSummaryPath);

        // 3. Update Registry & Master INDEX.md
        var registryItem = new ServerRegistryItem(
            ServerAlias: scanResult.ServerAlias,
            ServerHost: scanResult.ServerHost,
            ServerVersion: scanResult.ServerVersion,
            DatabaseCount: scanResult.Databases.Count,
            TotalTableCount: totalTables,
            TotalViewCount: totalViews,
            TotalProcedureCount: totalProcedures,
            LastScannedAt: scanResult.ScannedAt,
            TotalFunctionCount: totalFunctions,
            TotalTriggerCount: totalTriggers
        );

        var indexPath = await UpdateMasterIndexAsync(baseDir, registryItem, cancellationToken);
        createdFiles.Add(indexPath);

        return createdFiles;
    }

    public static string RenderCompactSchema(string serverAlias, DatabaseScanReport db)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Database Schema: {db.DatabaseName} (Server: {serverAlias})");
        sb.AppendLine();
        sb.AppendLine($"> Statistics: **{db.TableCount}** Tables | **{db.ViewCount}** Views | **{db.ProcedureCount}** Stored Procedures | **{db.FunctionCount}** Functions | **{db.TriggerCount}** Triggers");
        sb.AppendLine("> Token-optimized compact format for AI Agent schema lookup.");
        sb.AppendLine();

        sb.AppendLine("## 1. Tables");
        sb.AppendLine();
        if (db.Tables.Count == 0)
        {
            sb.AppendLine("*(No tables found or login lacks metadata permissions)*");
        }
        else
        {
            foreach (var table in db.Tables)
            {
                sb.AppendLine($"### {table.FullName}");
                foreach (var col in table.Columns)
                {
                    sb.AppendLine(col.ToCompactString());
                }

                if (table.CheckConstraints.Count > 0)
                {
                    var checksStr = string.Join("; ", table.CheckConstraints.Select(c => c.ToCompactString()));
                    sb.AppendLine($"- Check Constraints: {checksStr}");
                }

                if (table.Indexes.Count > 0)
                {
                    var idxStr = string.Join("; ", table.Indexes.Select(i => i.ToCompactString()));
                    sb.AppendLine($"- Indexes: {idxStr}");
                }

                if (table.Triggers.Count > 0)
                {
                    var trgStr = string.Join("; ", table.Triggers.Select(t => $"{t.Name} ({t.Events})"));
                    sb.AppendLine($"- Triggers: {trgStr}");
                }

                sb.AppendLine();
            }
        }

        sb.AppendLine("## 2. Views Summary");
        sb.AppendLine();
        if (db.Views.Count == 0)
        {
            sb.AppendLine("*(No views found)*");
        }
        else
        {
            foreach (var view in db.Views)
            {
                var viewFile = SanitizeFileName($"{view.Schema}.{view.Name}.sql");
                sb.AppendLine($"- **`{view.FullName}`** -> [View SQL definition](./views/{viewFile})");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## 3. Stored Procedures Summary");
        sb.AppendLine();
        if (db.Procedures.Count == 0)
        {
            sb.AppendLine("*(No stored procedures found)*");
        }
        else
        {
            foreach (var proc in db.Procedures)
            {
                var procFile = SanitizeFileName($"{proc.Schema}.{proc.Name}.sql");
                var paramList = proc.Parameters.Count > 0 ? $"({string.Join(", ", proc.Parameters)})" : "()";
                sb.AppendLine($"- **`{proc.FullName}`** `{paramList}` -> [View SQL definition](./procedures/{procFile})");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## 4. Functions Summary");
        sb.AppendLine();
        if (db.Functions.Count == 0)
        {
            sb.AppendLine("*(No user-defined functions found)*");
        }
        else
        {
            foreach (var func in db.Functions)
            {
                var funcFile = SanitizeFileName($"{func.Schema}.{func.Name}.sql");
                var paramList = func.Parameters.Count > 0 ? $"({string.Join(", ", func.Parameters)})" : "()";
                sb.AppendLine($"- **`{func.FullName}`** `{paramList}` ({func.TypeDesc}) -> [View SQL definition](./functions/{funcFile})");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## 5. Triggers Summary");
        sb.AppendLine();
        if (db.Triggers.Count == 0)
        {
            sb.AppendLine("*(No triggers found)*");
        }
        else
        {
            foreach (var trg in db.Triggers)
            {
                var trgFile = SanitizeFileName($"{trg.Schema}.{trg.Name}.sql");
                var disabledStr = trg.IsDisabled ? " [DISABLED]" : "";
                sb.AppendLine($"- **`{trg.FullName}`** on `[{trg.TargetTable}]` ({trg.Events}){disabledStr} -> [View SQL definition](./triggers/{trgFile})");
            }
        }

        return sb.ToString();
    }

    private static async Task<string> UpdateMasterIndexAsync(
        string baseDir,
        ServerRegistryItem newItem,
        CancellationToken cancellationToken)
    {
        var registryPath = Path.Combine(baseDir, "servers_registry.json");
        var registry = new Dictionary<string, ServerRegistryItem>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(registryPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(registryPath, cancellationToken);
                var items = JsonSerializer.Deserialize<List<ServerRegistryItem>>(json);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        registry[item.ServerAlias] = item;
                    }
                }
            }
            catch
            {
                // Fallback if file is corrupted
            }
        }

        registry[newItem.ServerAlias] = newItem;

        var orderedItems = registry.Values.OrderBy(x => x.ServerAlias).ToList();
        var serializedRegistry = JsonSerializer.Serialize(orderedItems, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(registryPath, serializedRegistry, Encoding.UTF8, cancellationToken);

        // Render Master INDEX.md
        var indexSb = new StringBuilder();
        indexSb.AppendLine("# AI Context Master Index");
        indexSb.AppendLine();
        indexSb.AppendLine("> Central map of all SQL Server Instances for high-speed, token-optimized AI Agent lookup.");
        indexSb.AppendLine($"> Last updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        indexSb.AppendLine();
        indexSb.AppendLine("## 1. Servers & Environments");
        indexSb.AppendLine();
        indexSb.AppendLine("| Server Alias | Host | Version | Databases | Tables | Views | SPs | Functions | Triggers | Last Scanned | Server Docs |");
        indexSb.AppendLine("|:---|:---|:---|:---:|:---:|:---:|:---:|:---:|:---:|:---|:---|");

        foreach (var item in orderedItems)
        {
            var serverDocLink = $"[View Server {item.ServerAlias}](./servers/{item.ServerAlias}/summary.md)";
            var versionShort = item.ServerVersion.Split('\n')[0].Trim();
            if (versionShort.Length > 30) versionShort = versionShort[..27] + "...";

            indexSb.AppendLine($"| **{item.ServerAlias}** | `{item.ServerHost}` | {versionShort} | {item.DatabaseCount} | {item.TotalTableCount} | {item.TotalViewCount} | {item.TotalProcedureCount} | {item.TotalFunctionCount} | {item.TotalTriggerCount} | {item.LastScannedAt:yyyy-MM-dd HH:mm} | {serverDocLink} |");
        }

        indexSb.AppendLine();
        indexSb.AppendLine("## 2. Token-Optimized AI Agent Debug & Troubleshooting Workflow");
        indexSb.AppendLine();
        indexSb.AppendLine("> 💡 **CRITICAL TOKEN RULE**: Do NOT load large map files into LLM context window.");
        indexSb.AppendLine();
        indexSb.AppendLine("1. **Instant Search (50 tokens):** Call MCP tool `search_context(query: \"...\")` to locate exact DB, Table, Column, Procedure, Function, or Trigger.");
        indexSb.AppendLine("2. **Low-Token Table Routing (4,000 tokens):** If searching manually, open `./servers/{ALIAS}/TABLES_ROUTER.compact.md` (~20KB) to find the hosting database.");
        indexSb.AppendLine("3. **Inspect Compact Schema:** Open `./servers/{ALIAS}/databases/{DB_NAME}/schema.compact.md` for column data types, defaults, check constraints, PK, FK, and indexes.");
        indexSb.AppendLine("4. **Inspect Routine Logic:** Open targeted SQL files in `views/`, `procedures/`, `functions/`, or `triggers/`.");
        indexSb.AppendLine("5. **Query Live Data:** Call MCP tool `execute_query` with a read-only SELECT query to inspect real rows causing the bug.");
        indexSb.AppendLine("6. **Compare Environments:** Compare `schema.compact.md` between DEV and PROD to detect schema drift.");

        var masterIndexPath = Path.Combine(baseDir, "INDEX.md");
        await File.WriteAllTextAsync(masterIndexPath, indexSb.ToString(), Encoding.UTF8, cancellationToken);

        return masterIndexPath;
    }

    public static async Task<SearchContextResult> SearchContextAsync(
        string baseDirectory,
        string query,
        string? database = null,
        string? target = "all",
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchContextResult(query, 0, Array.Empty<SearchContextMatch>(), "Query string cannot be empty.");
        }

        var fullBaseDir = Path.GetFullPath(baseDirectory);
        var serversDir = Path.Combine(fullBaseDir, "servers");
        if (!Directory.Exists(serversDir))
        {
            return new SearchContextResult(query, 0, Array.Empty<SearchContextMatch>(), "No ai-context found. Please run 'scan_server_context' tool first.");
        }

        var q = query.Trim();
        var targetType = string.IsNullOrWhiteSpace(target) ? "all" : target.Trim().ToLowerInvariant();
        var matches = new List<SearchContextMatch>();

        foreach (var serverPath in Directory.GetDirectories(serversDir))
        {
            var serverAlias = Path.GetFileName(serverPath);

            // 1. Search Global Tables Map
            var globalMapFile = Path.Combine(serverPath, "GLOBAL_TABLES_MAP.compact.md");
            if (File.Exists(globalMapFile) && (targetType == "all" || targetType == "table" || targetType == "column"))
            {
                using var reader = new StreamReader(globalMapFile, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) != null && matches.Count < limit)
                {
                    if (!line.StartsWith("- `")) continue;

                    // Format: - `DB.Schema.Table`: Col1(PK), Col2...
                    var colonIdx = line.IndexOf("`:");
                    if (colonIdx < 0) continue;

                    var tablePart = line.Substring(3, colonIdx - 3); // DB.Schema.Table
                    var colsPart = line.Substring(colonIdx + 2).Trim();

                    var dot1 = tablePart.IndexOf('.');
                    if (dot1 < 0) continue;
                    var dbName = tablePart[..dot1];
                    var fullTableName = tablePart[(dot1 + 1)..];

                    if (!string.IsNullOrEmpty(database) && !dbName.Equals(database, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Check table name match
                    var isTableMatch = fullTableName.Contains(q, StringComparison.OrdinalIgnoreCase);
                    if (isTableMatch && (targetType == "all" || targetType == "table"))
                    {
                        matches.Add(new SearchContextMatch(
                            ServerAlias: serverAlias,
                            Database: dbName,
                            Type: "table",
                            Name: fullTableName,
                            Details: colsPart.Length > 120 ? colsPart[..117] + "..." : colsPart,
                            RelativePath: $"servers/{serverAlias}/databases/{dbName}/schema.compact.md"
                        ));
                        if (matches.Count >= limit) break;
                    }

                    // Check column match
                    if (targetType == "all" || targetType == "column")
                    {
                        var cols = colsPart.Split(',');
                        foreach (var rawCol in cols)
                        {
                            var colTrim = rawCol.Trim();
                            if (colTrim.Contains(q, StringComparison.OrdinalIgnoreCase))
                            {
                                matches.Add(new SearchContextMatch(
                                    ServerAlias: serverAlias,
                                    Database: dbName,
                                    Type: "column",
                                    Name: $"{fullTableName}.{colTrim}",
                                    Details: $"Table {fullTableName} contains column '{colTrim}'",
                                    RelativePath: $"servers/{serverAlias}/databases/{dbName}/schema.compact.md"
                                ));
                                if (matches.Count >= limit) break;
                            }
                        }
                    }
                }
            }

            // 2. Search Routines (Views, Procedures, Functions, Triggers)
            var dbsDir = Path.Combine(serverPath, "databases");
            if (Directory.Exists(dbsDir) && (targetType == "all" || targetType == "routine" || targetType == "procedure" || targetType == "view" || targetType == "function" || targetType == "trigger"))
            {
                var targetDbDirs = string.IsNullOrEmpty(database)
                    ? Directory.GetDirectories(dbsDir)
                    : Directory.GetDirectories(dbsDir).Where(d => Path.GetFileName(d).Equals(database, StringComparison.OrdinalIgnoreCase));

                foreach (var dbPath in targetDbDirs)
                {
                    if (matches.Count >= limit) break;
                    var dbName = Path.GetFileName(dbPath);

                    var routineSubfolders = new[]
                    {
                        ("procedures", "procedure"),
                        ("views", "view"),
                        ("functions", "function"),
                        ("triggers", "trigger")
                    };

                    foreach (var (folder, type) in routineSubfolders)
                    {
                        if (matches.Count >= limit) break;
                        if (targetType != "all" && targetType != "routine" && targetType != type) continue;

                        var folderPath = Path.Combine(dbPath, folder);
                        if (!Directory.Exists(folderPath)) continue;

                        foreach (var file in Directory.GetFiles(folderPath, "*.sql"))
                        {
                            if (matches.Count >= limit) break;
                            var fileName = Path.GetFileName(file);
                            if (fileName.Contains(q, StringComparison.OrdinalIgnoreCase))
                            {
                                var routineName = fileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                                    ? fileName[..^4]
                                    : fileName;

                                matches.Add(new SearchContextMatch(
                                    ServerAlias: serverAlias,
                                    Database: dbName,
                                    Type: type,
                                    Name: routineName,
                                    Details: $"Matched {type} file in {folder}/",
                                    RelativePath: $"servers/{serverAlias}/databases/{dbName}/{folder}/{fileName}"
                                ));
                            }
                        }
                    }
                }
            }
        }

        return new SearchContextResult(
            Query: q,
            TotalMatches: matches.Count,
            Matches: matches,
            Message: matches.Count == 0
                ? $"No matches found for '{q}'."
                : $"Found {matches.Count} match(es) for '{q}'."
        );
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(fileName.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
