using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace McpSqlServer;

public class AiContextRenderer
{
    public const int TableModularThreshold = 50;

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

        // Delete legacy monolithic GLOBAL_TABLES_MAP.compact.md to eliminate token trap
        var legacyGlobalMap = Path.Combine(serverDir, "GLOBAL_TABLES_MAP.compact.md");
        if (File.Exists(legacyGlobalMap))
        {
            try { File.Delete(legacyGlobalMap); } catch { }
        }

        // 1. Export schema files, views, procedures, functions, triggers
        int scanTotalTables = 0;
        int scanTotalViews = 0;
        int scanTotalProcedures = 0;
        int scanTotalFunctions = 0;
        int scanTotalTriggers = 0;

        var scannedDbNames = new HashSet<string>(scanResult.Databases.Select(d => d.DatabaseName), StringComparer.OrdinalIgnoreCase);

        foreach (var db in scanResult.Databases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            scanTotalTables += db.TableCount;
            scanTotalViews += db.ViewCount;
            scanTotalProcedures += db.ProcedureCount;
            scanTotalFunctions += db.FunctionCount;
            scanTotalTriggers += db.TriggerCount;

            if (!db.Success) continue;

            var dbDir = Path.Combine(serverDir, "databases", db.DatabaseName);
            var tablesDir = Path.Combine(dbDir, "tables");
            var viewsDir = Path.Combine(dbDir, "views");
            var procsDir = Path.Combine(dbDir, "procedures");
            var funcsDir = Path.Combine(dbDir, "functions");
            var trgsDir = Path.Combine(dbDir, "triggers");

            Directory.CreateDirectory(dbDir);
            if (db.Views.Count > 0) Directory.CreateDirectory(viewsDir);
            if (db.Procedures.Count > 0) Directory.CreateDirectory(procsDir);
            if (db.Functions.Count > 0) Directory.CreateDirectory(funcsDir);
            if (db.Triggers.Count > 0) Directory.CreateDirectory(trgsDir);

            // Export individual Table files if table count exceeds threshold
            if (db.Tables.Count > TableModularThreshold)
            {
                Directory.CreateDirectory(tablesDir);
                foreach (var table in db.Tables)
                {
                    var safeTableName = SanitizeFileName($"{table.Schema}.{table.Name}.compact.md");
                    var tablePath = Path.Combine(tablesDir, safeTableName);
                    var tableContent = $"# Table: {table.FullName} (Database: {db.DatabaseName} | Server: {scanResult.ServerAlias})\n\n" + table.RenderCompactTableDefinition();
                    await File.WriteAllTextAsync(tablePath, tableContent, Encoding.UTF8, cancellationToken);
                    createdFiles.Add(tablePath);
                }
                CleanupOrphanFiles(tablesDir, db.Tables.Select(t => SanitizeFileName($"{t.Schema}.{t.Name}.compact.md")), "*.compact.md");
            }
            else if (Directory.Exists(tablesDir))
            {
                // Clean up tables directory if no longer above threshold
                try { Directory.Delete(tablesDir, true); } catch { }
            }

            // Write local database dependencies.compact.md
            var validCrossDb = db.CrossDbDependencies
                .Where(d => !string.IsNullOrWhiteSpace(d.ReferencedDatabase) && !d.ReferencedDatabase.Equals(db.DatabaseName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var dbDepPath = Path.Combine(dbDir, "dependencies.compact.md");
            if (validCrossDb.Count > 0)
            {
                var dbSb = new StringBuilder();
                dbSb.AppendLine($"# Database Dependencies: {db.DatabaseName} (Server: {scanResult.ServerAlias})");
                dbSb.AppendLine();
                dbSb.AppendLine($"> Inter-database references originating from `{db.DatabaseName}`.");
                dbSb.AppendLine();
                var grouped = validCrossDb.GroupBy(d => d.ReferencedDatabase, StringComparer.OrdinalIgnoreCase);
                foreach (var grp in grouped)
                {
                    dbSb.AppendLine($"## References **`{grp.Key}`**");
                    foreach (var item in grp)
                    {
                        dbSb.AppendLine($"- `{db.DatabaseName}.{item.ReferencingEntity}` -> `{grp.Key}.{item.ReferencedEntity}`");
                    }
                    dbSb.AppendLine();
                }
                await File.WriteAllTextAsync(dbDepPath, dbSb.ToString(), Encoding.UTF8, cancellationToken);
                createdFiles.Add(dbDepPath);
            }
            else if (File.Exists(dbDepPath))
            {
                try { File.Delete(dbDepPath); } catch { }
            }

            // Write schema.compact.md
            var compactSchemaPath = Path.Combine(dbDir, "schema.compact.md");
            var compactContent = RenderCompactSchema(scanResult.ServerAlias, db);
            await File.WriteAllTextAsync(compactSchemaPath, compactContent, Encoding.UTF8, cancellationToken);
            createdFiles.Add(compactSchemaPath);

            // Cleanup orphan routine files that no longer exist on database
            CleanupOrphanSqlFiles(viewsDir, db.Views.Select(v => SanitizeFileName($"{v.Schema}.{v.Name}.sql")));
            CleanupOrphanSqlFiles(procsDir, db.Procedures.Select(p => SanitizeFileName($"{p.Schema}.{p.Name}.sql")));
            CleanupOrphanSqlFiles(funcsDir, db.Functions.Select(f => SanitizeFileName($"{f.Schema}.{f.Name}.sql")));
            CleanupOrphanSqlFiles(trgsDir, db.Triggers.Select(t => SanitizeFileName($"{t.Schema}.{t.Name}.sql")));

            // Write individual View SQL files
            foreach (var view in db.Views)
            {
                var safeViewName = SanitizeFileName($"{view.Schema}.{view.Name}.sql");
                var viewPath = Path.Combine(viewsDir, safeViewName);
                var viewSql = $"-- Server: {scanResult.ServerAlias} | DB: {db.DatabaseName} | View: {view.FullName}\n" +
                              $"-- Auto-generated by MCP SQL Server\n\n" +
                              (view.Definition ?? "-- [Definition unavailable: object may be encrypted or login lacks VIEW DEFINITION permission]");
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
                              (proc.Definition ?? "-- [Definition unavailable: object may be encrypted or login lacks VIEW DEFINITION permission]");
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
                              (func.Definition ?? "-- [Definition unavailable: object may be encrypted or login lacks VIEW DEFINITION permission]");
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
                             (trg.Definition ?? "-- [Definition unavailable: object may be encrypted or login lacks VIEW DEFINITION permission]");
                await File.WriteAllTextAsync(trgPath, trgSql, Encoding.UTF8, cancellationToken);
                createdFiles.Add(trgPath);
            }
        }

        // 2. Update SQLite Catalog (catalog.db with FTS5)
        var catalogDbPath = await UpdateCatalogDbAsync(baseDir, scanResult.ServerAlias, scanResult.Databases, cancellationToken);
        createdFiles.Add(catalogDbPath);

        // Clean up legacy GLOBAL_TABLES_MAP.compact.md if present
        var legacyMapPath = Path.Combine(serverDir, "GLOBAL_TABLES_MAP.compact.md");
        if (File.Exists(legacyMapPath))
        {
            try { File.Delete(legacyMapPath); } catch { }
        }

        // 3. Write / Merge TABLES_ROUTER.compact.md
        var routerPath = Path.Combine(serverDir, "TABLES_ROUTER.compact.md");
        var existingRouterLines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(routerPath))
        {
            var lines = await File.ReadAllLinesAsync(routerPath, cancellationToken);
            foreach (var l in lines)
            {
                if (l.StartsWith("- **`") && l.Contains("`**:"))
                {
                    var closeIdx = l.IndexOf("`**:");
                    var db = l.Substring(5, closeIdx - 5);
                    existingRouterLines[db] = l;
                }
            }
        }

        var systemDbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "master", "msdb", "model", "tempdb", "rdsadmin" };
        foreach (var sysDb in systemDbs)
        {
            existingRouterLines.Remove(sysDb);
        }

        foreach (var db in scanResult.Databases.Where(d => d.Success && d.Tables.Count > 0))
        {
            if (systemDbs.Contains(db.DatabaseName)) continue;
            var cleanTables = db.Tables.Where(t => !t.Name.StartsWith("#")).Select(t => t.Name);
            var tableNames = string.Join(", ", cleanTables);
            if (!string.IsNullOrWhiteSpace(tableNames))
            {
                existingRouterLines[db.DatabaseName] = $"- **`{db.DatabaseName}`**: {tableNames}";
            }
        }

        var routerSb = new StringBuilder();
        routerSb.AppendLine($"# Database Tables Router: {scanResult.ServerAlias}");
        routerSb.AppendLine();
        routerSb.AppendLine($"> Ultra-compact skeleton index (~20KB) for low-token database routing.");
        routerSb.AppendLine($"> Flow: Locate database for table -> Open './databases/{{DB}}/schema.compact.md'.");
        routerSb.AppendLine($"> Fast Search: Call MCP tool `search_context(query: \"...\")` (<1ms via SQLite).");
        routerSb.AppendLine($"> Last updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        routerSb.AppendLine();
        foreach (var kv in existingRouterLines.OrderBy(k => k.Key))
        {
            routerSb.AppendLine(kv.Value);
        }
        await File.WriteAllTextAsync(routerPath, routerSb.ToString(), Encoding.UTF8, cancellationToken);
        createdFiles.Add(routerPath);

        // 4. Write / Merge Cross-Database Dependencies Map
        var crossDbPath = Path.Combine(serverDir, "CROSS_DB_DEPENDENCIES.compact.md");
        var existingDbSections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(crossDbPath))
        {
            var existingLines = await File.ReadAllLinesAsync(crossDbPath, cancellationToken);
            string? currentDb = null;
            var currentSection = new StringBuilder();

            foreach (var line in existingLines)
            {
                if (line.StartsWith("## Database `") && line.EndsWith("`"))
                {
                    if (currentDb != null && currentSection.Length > 0)
                    {
                        existingDbSections[currentDb] = currentSection.ToString().Trim();
                    }
                    currentDb = line.Substring(13, line.Length - 14);
                    currentSection.Clear();
                    currentSection.AppendLine(line);
                }
                else if (currentDb != null)
                {
                    currentSection.AppendLine(line);
                }
            }
            if (currentDb != null && currentSection.Length > 0)
            {
                existingDbSections[currentDb] = currentSection.ToString().Trim();
            }
        }

        // Update or remove sections for the scanned databases
        foreach (var db in scanResult.Databases)
        {
            if (!db.Success) continue;
            var validCrossDb = db.CrossDbDependencies
                .Where(d => !string.IsNullOrWhiteSpace(d.ReferencedDatabase) && !d.ReferencedDatabase.Equals(db.DatabaseName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (validCrossDb.Count > 0)
            {
                var dbSb = new StringBuilder();
                dbSb.AppendLine($"## Database `{db.DatabaseName}`");
                var grouped = validCrossDb.GroupBy(d => d.ReferencedDatabase, StringComparer.OrdinalIgnoreCase);
                foreach (var grp in grouped)
                {
                    dbSb.AppendLine($"- References **`{grp.Key}`**:");
                    foreach (var item in grp)
                    {
                        dbSb.AppendLine($"  - `{db.DatabaseName}.{item.ReferencingEntity}` -> `{grp.Key}.{item.ReferencedEntity}`");
                    }
                }
                existingDbSections[db.DatabaseName] = dbSb.ToString().Trim();
            }
            else
            {
                existingDbSections.Remove(db.DatabaseName);
            }
        }

        var crossDbSb = new StringBuilder();
        crossDbSb.AppendLine($"# Cross-Database Dependencies Map: {scanResult.ServerAlias}");
        crossDbSb.AppendLine();
        crossDbSb.AppendLine("> Inter-database references found in stored procedures, views, and functions.");
        crossDbSb.AppendLine($"> Last updated: {scanResult.ScannedAt:yyyy-MM-dd HH:mm:ss}");
        crossDbSb.AppendLine();

        if (existingDbSections.Count > 0)
        {
            foreach (var kv in existingDbSections.OrderBy(k => k.Key))
            {
                crossDbSb.AppendLine(kv.Value);
                crossDbSb.AppendLine();
            }
        }
        else
        {
            crossDbSb.AppendLine("*(No cross-database dependencies detected)*");
            crossDbSb.AppendLine();
        }

        await File.WriteAllTextAsync(crossDbPath, crossDbSb.ToString(), Encoding.UTF8, cancellationToken);
        createdFiles.Add(crossDbPath);

        // 4b. Write Cross-Database Graph (High-Level Summary Matrix ~3KB)
        var crossDbGraphPath = Path.Combine(serverDir, "CROSS_DB_GRAPH.compact.md");
        var graphSb = new StringBuilder();
        graphSb.AppendLine($"# Cross-Database Dependencies Graph: {scanResult.ServerAlias}");
        graphSb.AppendLine();
        graphSb.AppendLine("> High-level inter-database dependency matrix (~3KB).");
        graphSb.AppendLine("> For detailed entity references, inspect `./databases/{DB}/dependencies.compact.md` or call `search_context(query, target: \"dependency\")`.");
        graphSb.AppendLine($"> Last updated: {scanResult.ScannedAt:yyyy-MM-dd HH:mm:ss}");
        graphSb.AppendLine();
        graphSb.AppendLine("| Source Database | Target Database | References Count | Detail Link |");
        graphSb.AppendLine("|:---|:---|:---:|:---|");

        int totalCrossDbEdges = 0;
        foreach (var kv in existingDbSections.OrderBy(k => k.Key))
        {
            var dbName = kv.Key;
            var text = kv.Value;
            var lines = text.Split('\n');
            string? currentTarget = null;
            int count = 0;
            foreach (var l in lines)
            {
                var trimmed = l.Trim();
                if (trimmed.StartsWith("- References **`") && trimmed.Contains("`**:"))
                {
                    if (currentTarget != null && count > 0)
                    {
                        graphSb.AppendLine($"| **`{dbName}`** | `{currentTarget}` | {count} | [View `{dbName}` Dependencies](./databases/{dbName}/dependencies.compact.md) |");
                        totalCrossDbEdges += count;
                    }
                    var start = trimmed.IndexOf("`") + 1;
                    var end = trimmed.IndexOf("`**:");
                    currentTarget = trimmed.Substring(start, end - start);
                    count = 0;
                }
                else if (trimmed.StartsWith("- `") && trimmed.Contains("` -> `"))
                {
                    count++;
                }
            }
            if (currentTarget != null && count > 0)
            {
                graphSb.AppendLine($"| **`{dbName}`** | `{currentTarget}` | {count} | [View `{dbName}` Dependencies](./databases/{dbName}/dependencies.compact.md) |");
                totalCrossDbEdges += count;
            }
        }

        if (totalCrossDbEdges == 0)
        {
            graphSb.AppendLine("| *(None)* | *(None)* | 0 | *(No cross-database dependencies detected)* |");
        }

        await File.WriteAllTextAsync(crossDbGraphPath, graphSb.ToString(), Encoding.UTF8, cancellationToken);
        createdFiles.Add(crossDbGraphPath);

        // 5. Write / Merge Server Overview summary.md
        var summaryPath = Path.Combine(serverDir, "summary.md");
        var existingSummaryRows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(summaryPath))
        {
            var lines = await File.ReadAllLinesAsync(summaryPath, cancellationToken);
            foreach (var l in lines)
            {
                if (l.StartsWith("| **") && l.Contains("** |"))
                {
                    var endIdx = l.IndexOf("** |");
                    var db = l.Substring(4, endIdx - 4);
                    existingSummaryRows[db] = l;
                }
            }
        }

        foreach (var db in scanResult.Databases)
        {
            var status = db.Success ? "Success" : "Failed";
            var schemaLink = db.Success ? $"[databases/{db.DatabaseName}/schema.compact.md](./databases/{db.DatabaseName}/schema.compact.md)" : $"*(Error: {db.ErrorMessage})*";
            existingSummaryRows[db.DatabaseName] = $"| **{db.DatabaseName}** | {status} | {db.TableCount} | {db.ViewCount} | {db.ProcedureCount} | {db.FunctionCount} | {db.TriggerCount} | {schemaLink} |";
        }

        var summarySb = new StringBuilder();
        summarySb.AppendLine($"# Server Overview: {scanResult.ServerAlias}");
        summarySb.AppendLine();
        summarySb.AppendLine($"- **Host:** `{scanResult.ServerHost}`");
        summarySb.AppendLine($"- **SQL Version:** {scanResult.ServerVersion}");
        summarySb.AppendLine($"- **Scan Time:** {scanResult.ScannedAt:yyyy-MM-dd HH:mm:ss}");
        summarySb.AppendLine($"- **Execution Duration:** {scanResult.ElapsedMs} ms");
        summarySb.AppendLine($"- **Total Databases:** {existingSummaryRows.Count}");
        summarySb.AppendLine($"- **Fast Router Map (~20KB):** [TABLES_ROUTER.compact.md](./TABLES_ROUTER.compact.md)");
        summarySb.AppendLine($"- **Cross-DB Dependencies Graph (~3KB):** [CROSS_DB_GRAPH.compact.md](./CROSS_DB_GRAPH.compact.md)");
        summarySb.AppendLine($"- **Cross-DB Dependencies:** [CROSS_DB_DEPENDENCIES.compact.md](./CROSS_DB_DEPENDENCIES.compact.md)");
        summarySb.AppendLine($"- **SQLite Catalog (FTS5):** `../../catalog.db` (Searched instantly via tool `search_context`)");
        summarySb.AppendLine();
        summarySb.AppendLine("## Databases Overview");
        summarySb.AppendLine();
        summarySb.AppendLine("| Database | Status | Tables | Views | Procedures | Functions | Triggers | Schema Compact Link |");
        summarySb.AppendLine("|:---|:---:|:---:|:---:|:---:|:---:|:---:|:---|");
        foreach (var kv in existingSummaryRows.OrderBy(k => k.Key))
        {
            summarySb.AppendLine(kv.Value);
        }

        await File.WriteAllTextAsync(summaryPath, summarySb.ToString(), Encoding.UTF8, cancellationToken);
        createdFiles.Add(summaryPath);

        // 6. Update Registry & Master INDEX.md
        var migrationsMap = new Dictionary<string, DatabaseMigrationStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var db in scanResult.Databases)
        {
            if (db.Success)
            {
                migrationsMap[db.DatabaseName] = new DatabaseMigrationStatus(
                    db.DatabaseName,
                    db.LatestMigrationId,
                    db.MigrationCount,
                    db.LastObjectModifyDate
                );
            }
        }

        var registryItem = new ServerRegistryItem(
            ServerAlias: scanResult.ServerAlias,
            ServerHost: scanResult.ServerHost,
            ServerVersion: scanResult.ServerVersion,
            DatabaseCount: existingSummaryRows.Count,
            TotalTableCount: scanTotalTables,
            TotalViewCount: scanTotalViews,
            TotalProcedureCount: scanTotalProcedures,
            LastScannedAt: scanResult.ScannedAt,
            TotalFunctionCount: scanTotalFunctions,
            TotalTriggerCount: scanTotalTriggers,
            Migrations: migrationsMap
        );

        var indexPath = await UpdateMasterIndexAsync(baseDir, registryItem, cancellationToken);
        createdFiles.Add(indexPath);

        return createdFiles;
    }

    public static Task<string> UpdateCatalogDbAsync(
        string baseDir,
        string serverAlias,
        IEnumerable<DatabaseScanReport> databases,
        CancellationToken cancellationToken = default)
        => AiContextCatalog.UpdateCatalogDbAsync(baseDir, serverAlias, databases, cancellationToken);


    public static string RenderCompactSchema(string serverAlias, DatabaseScanReport db, int tableModularThreshold = TableModularThreshold)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Database Schema: {db.DatabaseName} (Server: {serverAlias})");
        sb.AppendLine();
        sb.AppendLine($"> Statistics: **{db.TableCount}** Tables | **{db.ViewCount}** Views | **{db.ProcedureCount}** Stored Procedures | **{db.FunctionCount}** Functions | **{db.TriggerCount}** Triggers");
        if (!string.IsNullOrEmpty(db.LatestMigrationId))
        {
            sb.AppendLine($"> Migration: Latest = `{db.LatestMigrationId}` ({db.MigrationCount} total)");
        }
        sb.AppendLine("> Token-optimized compact format for AI Agent schema lookup.");
        sb.AppendLine();

        sb.AppendLine("## 1. Tables");
        sb.AppendLine();
        if (db.Tables.Count == 0)
        {
            sb.AppendLine("*(No tables found or login lacks metadata permissions)*");
        }
        else if (db.Tables.Count > tableModularThreshold)
        {
            sb.AppendLine($"> 💡 **Modular Schema Notice**: This database contains **{db.Tables.Count}** tables. To eliminate token waste, detailed column specifications, constraints, and indexes are partitioned into individual files in `./tables/`.");
            sb.AppendLine($"> Fast Lookup: Call MCP tool `get_object_context(database: \"{db.DatabaseName}\", name: \"TableName\", type: \"table\")`.");
            sb.AppendLine();
            sb.AppendLine("| Table | Approx Rows | Approx Size | Primary Key | Detail File Link |");
            sb.AppendLine("|:---|:---:|:---:|:---|:---|");
            foreach (var table in db.Tables)
            {
                var rStr = "-";
                if (table.ApproxRowCount.HasValue)
                {
                    var r = table.ApproxRowCount.Value;
                    rStr = r >= 1_000_000
                        ? (r / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "M"
                        : r >= 1_000
                            ? (r / 1_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "K"
                            : $"{r}";
                }
                var sStr = table.ApproxSizeMb.HasValue
                    ? $"{table.ApproxSizeMb.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} MB"
                    : "-";
                var pkCols = table.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
                var pkStr = pkCols.Count > 0 ? string.Join(", ", pkCols) : "-";
                var safeTableName = SanitizeFileName($"{table.Schema}.{table.Name}.compact.md");
                sb.AppendLine($"| **`{table.FullName}`** | {rStr} | {sStr} | `{pkStr}` | [{safeTableName}](./tables/{safeTableName}) |");
            }
        }
        else
        {
            foreach (var table in db.Tables)
            {
                sb.Append(table.RenderCompactTableDefinition());
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
                var paramStr = proc.Parameters.Count > 0 ? $"({string.Join(", ", proc.Parameters.Take(3))}{(proc.Parameters.Count > 3 ? ", ..." : "")})" : "()";
                sb.AppendLine($"- **`{proc.FullName}`** `{paramStr}` -> [Procedure SQL definition](./procedures/{procFile})");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## 4. Functions Summary");
        sb.AppendLine();
        if (db.Functions.Count == 0)
        {
            sb.AppendLine("*(No functions found)*");
        }
        else
        {
            foreach (var func in db.Functions)
            {
                var funcFile = SanitizeFileName($"{func.Schema}.{func.Name}.sql");
                var paramStr = func.Parameters.Count > 0 ? $"({string.Join(", ", func.Parameters)})" : "()";
                sb.AppendLine($"- **`{func.FullName}`** `{paramStr}` ({func.TypeDesc}) -> [Function SQL definition](./functions/{funcFile})");
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

        // Merge migrations if existing item has older entries
        if (registry.TryGetValue(newItem.ServerAlias, out var existingItem) && existingItem.Migrations != null)
        {
            var merged = new Dictionary<string, DatabaseMigrationStatus>(existingItem.Migrations, StringComparer.OrdinalIgnoreCase);
            if (newItem.Migrations != null)
            {
                foreach (var kv in newItem.Migrations)
                {
                    merged[kv.Key] = kv.Value;
                }
            }
            newItem = newItem with { Migrations = merged };
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
        indexSb.AppendLine("> 💡 **CRITICAL TOKEN RULE**: Zero Token Waste. High-speed lookup via SQLite catalog.");
        indexSb.AppendLine();
        indexSb.AppendLine("1. **Precision Object Lookup (<1ms, ~50 tokens):** Call MCP tool `get_object_context(database: \"...\", name: \"...\", type: \"...\")` to fetch the exact schema of a single table, view, procedure, function, or trigger without loading large files.");
        indexSb.AppendLine("2. **Instant Search (<1ms, ~50 tokens):** Call MCP tool `search_context(query: \"...\")` to locate exact DB, Table, Column, Procedure, Function, Trigger, or Dependency from local SQLite `catalog.db`.");
        indexSb.AppendLine("3. **Low-Token Table Routing (~15KB):** If searching manually, open `./servers/{ALIAS}/TABLES_ROUTER.compact.md` (~15KB) to find the hosting database.");
        indexSb.AppendLine("4. **Inspect Compact Schema:** Open `./servers/{ALIAS}/databases/{DB_NAME}/schema.compact.md`. For large databases (>50 tables), schema.compact.md serves as a modular table skeleton index pointing to `./tables/*.compact.md`.");
        indexSb.AppendLine("5. **Inspect Cross-DB Relations:** Check `./servers/{ALIAS}/CROSS_DB_GRAPH.compact.md` for high-level relationship graph (~3KB), or `./databases/{DB}/dependencies.compact.md` for local dependencies.");
        indexSb.AppendLine("6. **Inspect Routine Logic:** Open targeted SQL files in `views/`, `procedures/`, `functions/`, or `triggers/`.");
        indexSb.AppendLine("7. **Detect Drift / Migrations:** Call MCP tool `check_schema_drift` or use CLI option `[3] Check DB Migration Drift`.");
        indexSb.AppendLine("8. **Query Live Data:** Call MCP tool `execute_query` with a read-only SELECT query to inspect real rows.");

        var masterIndexPath = Path.Combine(baseDir, "INDEX.md");
        await File.WriteAllTextAsync(masterIndexPath, indexSb.ToString(), Encoding.UTF8, cancellationToken);

        return masterIndexPath;
    }

    public static Task<SearchContextResult> SearchContextAsync(
        string baseDirectory,
        string query,
        string? database = null,
        string? target = "all",
        int limit = 10,
        string? serverAlias = null,
        bool includeDetails = false,
        CancellationToken cancellationToken = default)
        => AiContextCatalog.SearchContextAsync(baseDirectory, query, database, target, limit, serverAlias, includeDetails, cancellationToken);

    public static Task<string> GetObjectContextAsync(
        string baseDirectory,
        string serverAlias,
        string database,
        string objectName,
        string? objectType = null,
        CancellationToken cancellationToken = default)
        => ObjectContextReader.GetObjectContextAsync(baseDirectory, serverAlias, database, objectName, objectType, cancellationToken);

    private static void CleanupOrphanFiles(string directory, IEnumerable<string> activeFileNames, string searchPattern = "*.*")
    {
        if (!Directory.Exists(directory)) return;
        var activeSet = new HashSet<string>(activeFileNames, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(directory, searchPattern))
        {
            var fileName = Path.GetFileName(file);
            if (!activeSet.Contains(fileName))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    private static void CleanupOrphanSqlFiles(string directory, IEnumerable<string> activeFileNames)
        => CleanupOrphanFiles(directory, activeFileNames, "*.sql");

    internal static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(fileName.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
