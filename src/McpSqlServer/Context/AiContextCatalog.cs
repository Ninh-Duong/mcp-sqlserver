using Microsoft.Data.Sqlite;

namespace McpSqlServer;

public static class AiContextCatalog
{
    public static async Task<string> UpdateCatalogDbAsync(
        string baseDir,
        string serverAlias,
        IEnumerable<DatabaseScanReport> databases,
        CancellationToken cancellationToken = default)
    {
        var dbPath = Path.Combine(baseDir, "catalog.db");
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(cancellationToken);

        const string initSql = @"
CREATE TABLE IF NOT EXISTS catalog_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    server_alias TEXT NOT NULL,
    database_name TEXT NOT NULL,
    item_type TEXT NOT NULL,
    name TEXT NOT NULL,
    details TEXT,
    relative_path TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_cat_alias_db ON catalog_items(server_alias, database_name);
CREATE INDEX IF NOT EXISTS idx_cat_server_db_type ON catalog_items(server_alias, database_name, item_type);
CREATE INDEX IF NOT EXISTS idx_cat_type ON catalog_items(item_type);
CREATE INDEX IF NOT EXISTS idx_cat_name ON catalog_items(name COLLATE NOCASE);
";
        await using (var initCmd = conn.CreateCommand())
        {
            initCmd.CommandText = initSql;
            await initCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = conn.BeginTransaction();

        foreach (var db in databases)
        {
            if (!db.Success) continue;

            // Delete old items for this server & DB
            await using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = transaction;
                delCmd.CommandText = "DELETE FROM catalog_items WHERE server_alias = @alias AND database_name = @db;";
                delCmd.Parameters.AddWithValue("@alias", serverAlias);
                delCmd.Parameters.AddWithValue("@db", db.DatabaseName);
                await delCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Insert tables & columns
            foreach (var t in db.Tables)
            {
                var colsSummary = string.Join(", ", t.Columns.Select(c => c.ToQuickMapString()));
                var volInfo = t.ApproxRowCount.HasValue ? $" (~{t.ApproxRowCount.Value} rows)" : "";

                await using (var insCmd = conn.CreateCommand())
                {
                    insCmd.Transaction = transaction;
                    insCmd.CommandText = @"
INSERT INTO catalog_items (server_alias, database_name, item_type, name, details, relative_path)
VALUES (@alias, @db, 'table', @name, @details, @path);";
                    insCmd.Parameters.AddWithValue("@alias", serverAlias);
                    insCmd.Parameters.AddWithValue("@db", db.DatabaseName);
                    insCmd.Parameters.AddWithValue("@name", t.FullName);
                    insCmd.Parameters.AddWithValue("@details", $"{colsSummary}{volInfo}");
                    insCmd.Parameters.AddWithValue("@path", $"servers/{serverAlias}/databases/{db.DatabaseName}/schema.compact.md");
                    await insCmd.ExecuteNonQueryAsync(cancellationToken);
                }

                foreach (var col in t.Columns)
                {
                    await using var insColCmd = conn.CreateCommand();
                    insColCmd.Transaction = transaction;
                    insColCmd.CommandText = @"
INSERT INTO catalog_items (server_alias, database_name, item_type, name, details, relative_path)
VALUES (@alias, @db, 'column', @name, @details, @path);";
                    insColCmd.Parameters.AddWithValue("@alias", serverAlias);
                    insColCmd.Parameters.AddWithValue("@db", db.DatabaseName);
                    insColCmd.Parameters.AddWithValue("@name", $"{t.FullName}.{col.Name}");
                    var sampleStr = col.SampleValues != null && col.SampleValues.Count > 0 ? $" | Observed sample: [{string.Join(", ", col.SampleValues)}] (may be incomplete)" : "";
                    insColCmd.Parameters.AddWithValue("@details", $"{col.DataType} ({(col.IsPrimaryKey ? "PK, " : "")}{(col.IsNullable ? "Null" : "Not Null")}){sampleStr}");
                    insColCmd.Parameters.AddWithValue("@path", $"servers/{serverAlias}/databases/{db.DatabaseName}/schema.compact.md");
                    await insColCmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            // Insert views
            foreach (var v in db.Views)
            {
                var fileName = AiContextRenderer.SanitizeFileName($"{v.Schema}.{v.Name}.sql");
                await using var insViewCmd = conn.CreateCommand();
                insViewCmd.Transaction = transaction;
                insViewCmd.CommandText = @"
INSERT INTO catalog_items (server_alias, database_name, item_type, name, details, relative_path)
VALUES (@alias, @db, 'view', @name, @details, @path);";
                insViewCmd.Parameters.AddWithValue("@alias", serverAlias);
                insViewCmd.Parameters.AddWithValue("@db", db.DatabaseName);
                insViewCmd.Parameters.AddWithValue("@name", v.FullName);
                insViewCmd.Parameters.AddWithValue("@details", $"View in {db.DatabaseName}");
                insViewCmd.Parameters.AddWithValue("@path", $"servers/{serverAlias}/databases/{db.DatabaseName}/views/{fileName}");
                await insViewCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Insert procedures
            foreach (var p in db.Procedures)
            {
                var fileName = AiContextRenderer.SanitizeFileName($"{p.Schema}.{p.Name}.sql");
                var paramsStr = p.Parameters.Count > 0 ? string.Join(", ", p.Parameters) : "No params";
                await using var insProcCmd = conn.CreateCommand();
                insProcCmd.Transaction = transaction;
                insProcCmd.CommandText = @"
INSERT INTO catalog_items (server_alias, database_name, item_type, name, details, relative_path)
VALUES (@alias, @db, 'procedure', @name, @details, @path);";
                insProcCmd.Parameters.AddWithValue("@alias", serverAlias);
                insProcCmd.Parameters.AddWithValue("@db", db.DatabaseName);
                insProcCmd.Parameters.AddWithValue("@name", p.FullName);
                insProcCmd.Parameters.AddWithValue("@details", paramsStr);
                insProcCmd.Parameters.AddWithValue("@path", $"servers/{serverAlias}/databases/{db.DatabaseName}/procedures/{fileName}");
                await insProcCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Insert functions
            foreach (var f in db.Functions)
            {
                var fileName = AiContextRenderer.SanitizeFileName($"{f.Schema}.{f.Name}.sql");
                await using var insFuncCmd = conn.CreateCommand();
                insFuncCmd.Transaction = transaction;
                insFuncCmd.CommandText = @"
INSERT INTO catalog_items (server_alias, database_name, item_type, name, details, relative_path)
VALUES (@alias, @db, 'function', @name, @details, @path);";
                insFuncCmd.Parameters.AddWithValue("@alias", serverAlias);
                insFuncCmd.Parameters.AddWithValue("@db", db.DatabaseName);
                insFuncCmd.Parameters.AddWithValue("@name", f.FullName);
                insFuncCmd.Parameters.AddWithValue("@details", f.TypeDesc);
                insFuncCmd.Parameters.AddWithValue("@path", $"servers/{serverAlias}/databases/{db.DatabaseName}/functions/{fileName}");
                await insFuncCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Insert triggers
            foreach (var trg in db.Triggers)
            {
                var fileName = AiContextRenderer.SanitizeFileName($"{trg.Schema}.{trg.Name}.sql");
                await using var insTrgCmd = conn.CreateCommand();
                insTrgCmd.Transaction = transaction;
                insTrgCmd.CommandText = @"
INSERT INTO catalog_items (server_alias, database_name, item_type, name, details, relative_path)
VALUES (@alias, @db, 'trigger', @name, @details, @path);";
                insTrgCmd.Parameters.AddWithValue("@alias", serverAlias);
                insTrgCmd.Parameters.AddWithValue("@db", db.DatabaseName);
                insTrgCmd.Parameters.AddWithValue("@name", trg.FullName);
                insTrgCmd.Parameters.AddWithValue("@details", $"Trigger on [{trg.TargetTable}] ({trg.Events})");
                insTrgCmd.Parameters.AddWithValue("@path", $"servers/{serverAlias}/databases/{db.DatabaseName}/triggers/{fileName}");
                await insTrgCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        try
        {
            await using var syncFts = conn.CreateCommand();
            syncFts.Transaction = transaction;
            syncFts.CommandText = "INSERT INTO catalog_fts(catalog_fts) VALUES('rebuild');";
            await syncFts.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            // Ignore if rebuild command unsupported
        }

        await transaction.CommitAsync(cancellationToken);
        return dbPath;
    }

    public static async Task<SearchContextResult> SearchContextAsync(
        string baseDirectory,
        string query,
        string? database = null,
        string? target = "all",
        int limit = 10,
        string? serverAlias = null,
        bool includeDetails = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchContextResult(query, 0, Array.Empty<SearchContextMatch>(), "Query string cannot be empty.");
        }

        var fullBaseDir = Path.GetFullPath(baseDirectory);
        var catalogPath = Path.Combine(fullBaseDir, "catalog.db");
        if (!File.Exists(catalogPath))
        {
            return new SearchContextResult(query, 0, Array.Empty<SearchContextMatch>(), "No ai-context catalog found. Please run 'scan_server_context' tool first.");
        }

        var q = query.Trim();
        var targetType = string.IsNullOrWhiteSpace(target) ? "all" : target.Trim().ToLowerInvariant();
        var matches = new List<SearchContextMatch>();

        try
        {
            await using var conn = new SqliteConnection($"Data Source={catalogPath}");
            await conn.OpenAsync(cancellationToken);

            var querySql = @"
SELECT server_alias, database_name, item_type, name, details, relative_path
FROM catalog_items
WHERE (@server IS NULL OR server_alias = @server COLLATE NOCASE)
  AND (@db IS NULL OR database_name = @db COLLATE NOCASE)
  AND (
    @type = 'all' 
    OR item_type = @type 
    OR (@type = 'routine' AND item_type IN ('procedure', 'view', 'function', 'trigger'))
  )
  AND (name LIKE @pattern ESCAPE '\' OR details LIKE @pattern ESCAPE '\')
ORDER BY 
  CASE WHEN name = @exact COLLATE NOCASE THEN 1
       WHEN name LIKE @prefix COLLATE NOCASE THEN 2
       ELSE 3 END,
  item_type, name
LIMIT @limit;";

            var escapedQ = q.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
            var pattern = $"%{escapedQ}%";
            var prefix = $"{escapedQ}%";

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = querySql;
                cmd.Parameters.AddWithValue("@server", (object?)serverAlias ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@db", (object?)database ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@type", targetType);
                cmd.Parameters.AddWithValue("@pattern", pattern);
                cmd.Parameters.AddWithValue("@prefix", prefix);
                cmd.Parameters.AddWithValue("@exact", q);
                cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 50));

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    matches.Add(new SearchContextMatch(
                        ServerAlias: reader.GetString(0),
                        Database: reader.GetString(1),
                        Type: reader.GetString(2),
                        Name: reader.GetString(3),
                        Details: includeDetails && !reader.IsDBNull(4) ? reader.GetString(4) : "",
                        RelativePath: reader.GetString(5)
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            return new SearchContextResult(q, 0, Array.Empty<SearchContextMatch>(), $"Error querying catalog: {ex.Message}");
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
}
