using System.Diagnostics;
using McpSqlServer.Security;

namespace McpSqlServer;

/// <summary>
/// Catalog-level operations (connection check, database/table listing, drift check).
/// Every query goes through <see cref="ReadOnlyQueryExecutor"/>; this class never opens a SqlCommand itself.
/// </summary>
public class SqlServerService
{
    private const string ListDatabasesQuery = @"
SELECT
    database_id,
    name,
    state_desc,
    HAS_DBACCESS(name) AS has_access,
    CASE WHEN database_id BETWEEN 1 AND 4 THEN 1 ELSE 0 END AS is_system
FROM sys.databases
ORDER BY name";

    private const string ConnectionCheckQuery = @"
SELECT
    @@VERSION AS server_version,
    ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0) AS is_sysadmin,
    ISNULL(IS_MEMBER('db_owner'), 0) AS is_db_owner,
    ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT'), 0) AS can_insert,
    ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'UPDATE'), 0) AS can_update,
    ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'DELETE'), 0) AS can_delete";

    private const string ListTablesQuery = @"
SELECT s.name AS schema_name, t.name AS table_name
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
ORDER BY s.name, t.name";

    public async Task<ConnectionCheckResult> TestConnectionAsync(ConnectionOptions options, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var executor = new ReadOnlyQueryExecutor(options);
        var result = await executor.RunCatalogSelectAsync(ConnectionCheckQuery, cancellationToken: cancellationToken);
        stopwatch.Stop();

        if (!result.Success || result.Rows.Count == 0)
        {
            return new ConnectionCheckResult(
                Success: false,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                ErrorMessage: result.ErrorMessage ?? "No result returned.",
                ConfigSource: options.ConfigSources);
        }

        var row = result.Rows[0];
        var version = row["server_version"]?.ToString()?.Split('\n')[0].Trim();
        var writeCapable =
            AsInt(row["is_sysadmin"]) == 1 || AsInt(row["is_db_owner"]) == 1 ||
            AsInt(row["can_insert"]) == 1 || AsInt(row["can_update"]) == 1 || AsInt(row["can_delete"]) == 1;

        var warnings = new List<string>();
        if (writeCapable)
        {
            warnings.Add("The configured login CAN write to this server. This MCP still only runs validated SELECT statements inside a rolled-back transaction, but a read-only login (docs/readonly-login.sql) is strongly recommended.");
        }

        Logger.Process("CONNECT", $"Connected in {stopwatch.ElapsedMilliseconds} ms ({version}). write_capable={writeCapable}");

        return new ConnectionCheckResult(
            Success: true,
            ElapsedMs: stopwatch.ElapsedMilliseconds,
            ServerVersion: version,
            WriteCapable: writeCapable,
            Warnings: warnings,
            ConfigSource: options.ConfigSources);
    }

    /// <param name="respectAllowList">False only for the offline CLI scanner, which may scan system databases on request.</param>
    public async Task<DatabaseListingResult> ListDatabasesAsync(
        ConnectionOptions options, CancellationToken cancellationToken = default, bool respectAllowList = true)
    {
        var executor = new ReadOnlyQueryExecutor(options);
        var result = await executor.RunCatalogSelectAsync(ListDatabasesQuery, maxRows: ConnectionOptions.HardMaxRows, cancellationToken: cancellationToken);

        if (!result.Success)
        {
            return new DatabaseListingResult(false, 0, 0, 0, Array.Empty<DatabaseItem>(), ErrorMessage: result.ErrorMessage);
        }

        var list = new List<DatabaseItem>();
        foreach (var row in result.Rows)
        {
            var name = row["name"]?.ToString() ?? string.Empty;
            if (respectAllowList && !options.IsDatabaseAllowed(name)) continue;

            bool? hasAccess = row["has_access"] is null ? null : AsInt(row["has_access"]) == 1;
            list.Add(new DatabaseItem(
                AsInt(row["database_id"]),
                name,
                row["state_desc"]?.ToString() ?? "UNKNOWN",
                hasAccess,
                AsInt(row["is_system"]) == 1));
        }

        var systemCount = list.Count(d => d.IsSystem);
        return new DatabaseListingResult(true, list.Count, systemCount, list.Count - systemCount, list);
    }

    public async Task<TableListingResult> ListTablesAsync(ConnectionOptions options, string databaseName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return new TableListingResult(false, string.Empty, 0, Array.Empty<TableItem>(), "Database name cannot be empty.");
        }

        var dbName = databaseName.Trim();
        var executor = new ReadOnlyQueryExecutor(options);
        var result = await executor.RunSelectAsync(ListTablesQuery, dbName, ConnectionOptions.HardMaxRows, cancellationToken: cancellationToken);

        if (!result.Success)
        {
            return new TableListingResult(false, dbName, 0, Array.Empty<TableItem>(), result.ErrorMessage);
        }

        var tables = result.Rows
            .Select(r => new TableItem(r["schema_name"]?.ToString() ?? string.Empty, r["table_name"]?.ToString() ?? string.Empty))
            .ToList();

        return new TableListingResult(true, dbName, tables.Count, tables);
    }

    public static string FormatDataType(string typeName, short maxLength, byte precision, byte scale)
        => DatabaseSchemaScanner.FormatDataType(typeName, maxLength, precision, scale);

    public static bool IsCandidateForSampling(ColumnSchemaItem col)
        => DatabaseSchemaScanner.IsCandidateForSampling(col);

    public Task<DatabaseScanReport> ScanDatabaseSchemaAsync(ConnectionOptions options, string databaseName, CancellationToken cancellationToken = default)
        => DatabaseSchemaScanner.ScanDatabaseSchemaAsync(options, databaseName, cancellationToken);

    public Task<ServerScanResult> ScanServerAsync(
        ConnectionOptions options,
        bool includeSystem = false,
        Action<string>? onProgress = null,
        string? targetDatabase = null,
        CancellationToken cancellationToken = default,
        string? outputDirectory = null,
        bool resume = false)
        => DatabaseSchemaScanner.ScanServerAsync(this, options, includeSystem, onProgress, targetDatabase, cancellationToken, outputDirectory, resume);

    public async Task<DriftCheckResult> CheckSchemaDriftAsync(
        ConnectionOptions options,
        IReadOnlyDictionary<string, DatabaseMigrationStatus>? cachedMigrations,
        CancellationToken cancellationToken = default)
    {
        var alias = string.IsNullOrWhiteSpace(options.ServerAlias) ? "DEV" : options.ServerAlias.Trim();
        var drifted = new List<DatabaseDriftInfo>();
        var upToDate = new List<DatabaseDriftInfo>();

        try
        {
            var dbListResult = await ListDatabasesAsync(options, cancellationToken);
            if (!dbListResult.Success)
            {
                return new DriftCheckResult(false, alias, drifted, upToDate, dbListResult.ErrorMessage);
            }

            var candidateDbs = dbListResult.Databases
                .Where(d => !d.IsSystem && d.State.Equals("ONLINE", StringComparison.OrdinalIgnoreCase) && d.HasAccess != false)
                .ToList();

            var fastOptions = options.WithDatabase(options.Database);
            fastOptions.QueryTimeout = 5;
            var executor = new ReadOnlyQueryExecutor(fastOptions);

            foreach (var db in candidateDbs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? currentMigrationId = null;
                int currentCount = 0;
                DateTime? currentLastModify = null;

                var state = await executor.RunSelectAsync(
                    "SELECT CASE WHEN OBJECT_ID('dbo.__EFMigrationsHistory') IS NULL THEN 0 ELSE 1 END AS has_history, " +
                    "(SELECT MAX(modify_date) FROM sys.objects WHERE is_ms_shipped = 0) AS last_modify",
                    db.Name, 1, cancellationToken: cancellationToken);

                if (!state.Success || state.Rows.Count == 0)
                {
                    Logger.Warning($"Could not inspect migration state for '{db.Name}'.");
                    continue;
                }

                var stateRow = state.Rows[0];
                currentLastModify = TryParseDate(stateRow["last_modify"]);

                if (AsInt(stateRow["has_history"]) == 1)
                {
                    var history = await executor.RunSelectAsync(
                        "SELECT TOP 1 MigrationId AS latest_id, (SELECT COUNT(1) FROM dbo.__EFMigrationsHistory) AS migration_count " +
                        "FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC",
                        db.Name, 1, cancellationToken: cancellationToken);

                    if (history.Success && history.Rows.Count > 0)
                    {
                        currentMigrationId = history.Rows[0]["latest_id"]?.ToString();
                        currentCount = AsInt(history.Rows[0]["migration_count"]);
                    }
                }

                DatabaseMigrationStatus? cached = null;
                cachedMigrations?.TryGetValue(db.Name, out cached);

                if (cached == null)
                {
                    drifted.Add(new DatabaseDriftInfo(db.Name, true, null, currentMigrationId, 0, currentCount, "Not in snapshot"));
                    continue;
                }

                var hasDrift = EvaluateDatabaseDrift(
                    cached.LatestMigrationId, currentMigrationId,
                    cached.MigrationCount, currentCount,
                    cached.LastObjectModifyDate, currentLastModify,
                    out var driftReason);

                var info = new DatabaseDriftInfo(
                    db.Name, hasDrift, cached.LatestMigrationId, currentMigrationId,
                    cached.MigrationCount, currentCount, hasDrift ? driftReason : "Up to date");

                (hasDrift ? drifted : upToDate).Add(info);
            }

            return new DriftCheckResult(true, alias, drifted, upToDate);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DriftCheckResult(false, alias, drifted, upToDate, Logger.Sanitize(ex.Message));
        }
    }

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

        // 2. Check DDL modification drift for ALL databases (including EF Core databases)
        if (currentLastModify.HasValue && snapshotLastModify.HasValue &&
            currentLastModify.Value > snapshotLastModify.Value.AddSeconds(5))
        {
            driftReason = $"DDL Modified: {currentLastModify:yyyy-MM-dd HH:mm}";
            return true;
        }

        driftReason = "Up to date";
        return false;
    }

    /// <summary>Run one validated SELECT. Kept for the offline CLI menu; MCP tools use <see cref="ReadOnlyQueryExecutor"/> directly.</summary>
    public Task<QueryExecutionResult> ExecuteQueryAsync(
        ConnectionOptions options,
        string sql,
        string? database = null,
        int maxRows = 100,
        CancellationToken cancellationToken = default)
        => new ReadOnlyQueryExecutor(options).RunSelectAsync(sql, database, maxRows, cancellationToken: cancellationToken);

    private static int AsInt(object? value) => value is null ? 0 : Convert.ToInt32(value);

    private static DateTime? TryParseDate(object? value) => value switch
    {
        null => null,
        DateTime dt => dt,
        string s when DateTime.TryParse(s, out var parsed) => parsed,
        _ => null,
    };
}
