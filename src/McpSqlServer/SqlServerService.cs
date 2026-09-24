using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace McpSqlServer;

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
ORDER BY name;";

    public async Task<ConnectionCheckResult> TestConnectionAsync(ConnectionOptions options, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Logger.Process("CONNECT", $"Attempting connection to {options.Server} (User: {options.Username})...");

        try
        {
            var connectionString = options.BuildConnectionString();
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT @@VERSION;";
            cmd.CommandTimeout = options.QueryTimeout;

            var versionObj = await cmd.ExecuteScalarAsync(cancellationToken);
            var versionStr = versionObj?.ToString()?.Split('\n')[0].Trim();

            stopwatch.Stop();
            Logger.Process("CONNECT", $"Connected successfully in {stopwatch.ElapsedMilliseconds} ms ({versionStr}).");

            return new ConnectionCheckResult(
                Success: true,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                ServerVersion: versionStr
            );
        }
        catch (SqlException ex)
        {
            stopwatch.Stop();
            var friendlyError = FormatSqlException(ex, options);
            Logger.Error($"SQL Server connection error (Error code {ex.Number})", ex);

            return new ConnectionCheckResult(
                Success: false,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                ErrorMessage: friendlyError
            );
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.Error("Unknown connection error", ex);

            return new ConnectionCheckResult(
                Success: false,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                ErrorMessage: $"Connection error: {Logger.Sanitize(ex.Message)}"
            );
        }
    }

    public async Task<DatabaseListingResult> ListDatabasesAsync(ConnectionOptions options, CancellationToken cancellationToken = default)
    {
        Logger.Process("QUERY", "Querying catalog sys.databases...");

        try
        {
            var connectionString = options.BuildConnectionString();
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = ListDatabasesQuery;
            cmd.CommandTimeout = options.QueryTimeout;

            var list = new List<DatabaseItem>();

            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var dbId = reader.GetInt32(0);
                var name = reader.GetString(1);
                var state = reader.IsDBNull(2) ? "UNKNOWN" : reader.GetString(2);

                bool? hasAccess = null;
                if (!reader.IsDBNull(3))
                {
                    var accessInt = reader.GetInt32(3);
                    hasAccess = accessInt == 1;
                }

                var isSystem = reader.GetInt32(4) == 1;

                list.Add(new DatabaseItem(dbId, name, state, hasAccess, isSystem));
            }

            var systemCount = list.Count(d => d.IsSystem);
            var otherCount = list.Count(d => !d.IsSystem);

            Logger.Process("QUERY", $"Query completed: {list.Count} databases (System: {systemCount}, Other: {otherCount}).");

            return new DatabaseListingResult(
                Success: true,
                VisibleCount: list.Count,
                SystemCount: systemCount,
                OtherCount: otherCount,
                Databases: list
            );
        }
        catch (SqlException ex)
        {
            var friendlyError = FormatSqlException(ex, options);
            Logger.Error($"Error querying catalog sys.databases (Code {ex.Number})", ex);

            return new DatabaseListingResult(
                Success: false,
                VisibleCount: 0,
                SystemCount: 0,
                OtherCount: 0,
                Databases: Array.Empty<DatabaseItem>(),
                ErrorMessage: friendlyError
            );
        }
        catch (Exception ex)
        {
            Logger.Error("System error while listing databases", ex);

            return new DatabaseListingResult(
                Success: false,
                VisibleCount: 0,
                SystemCount: 0,
                OtherCount: 0,
                Databases: Array.Empty<DatabaseItem>(),
                ErrorMessage: $"Error: {Logger.Sanitize(ex.Message)}"
            );
        }
    }

    public async Task<TableListingResult> ListTablesAsync(ConnectionOptions options, string databaseName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            Logger.Error("Database name cannot be empty for ListTablesAsync.");
            return new TableListingResult(false, string.Empty, 0, Array.Empty<TableItem>(), "Database name cannot be empty.");
        }

        var dbName = databaseName.Trim();
        Logger.Process("QUERY", $"Querying table list in database '{dbName}'...");

        try
        {
            var targetOptions = new ConnectionOptions
            {
                ServerAlias = options.ServerAlias,
                Server = options.Server,
                Username = options.Username,
                Password = options.Password,
                Port = options.Port,
                Database = dbName,
                Encrypt = options.Encrypt,
                TrustServerCertificate = options.TrustServerCertificate,
                ConnectTimeout = options.ConnectTimeout,
                QueryTimeout = options.QueryTimeout
            };

            var connectionString = targetOptions.BuildConnectionString();
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT 
    s.name AS schema_name,
    t.name AS table_name
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
ORDER BY s.name, t.name;";
            cmd.CommandTimeout = options.QueryTimeout;

            var tables = new List<TableItem>();
            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString(0);
                var tableName = reader.GetString(1);
                tables.Add(new TableItem(schema, tableName));
            }

            Logger.Process("QUERY", $"Found {tables.Count} tables in database '{dbName}'.");

            return new TableListingResult(
                Success: true,
                Database: dbName,
                TableCount: tables.Count,
                Tables: tables
            );
        }
        catch (SqlException ex)
        {
            var friendlyError = FormatSqlException(ex, options);
            Logger.Error($"Error querying tables in database '{dbName}' (Code {ex.Number})", ex);

            return new TableListingResult(
                Success: false,
                Database: dbName,
                TableCount: 0,
                Tables: Array.Empty<TableItem>(),
                ErrorMessage: friendlyError
            );
        }
        catch (Exception ex)
        {
            Logger.Error($"System error getting table list in database '{dbName}'", ex);

            return new TableListingResult(
                Success: false,
                Database: dbName,
                TableCount: 0,
                Tables: Array.Empty<TableItem>(),
                ErrorMessage: $"Error: {Logger.Sanitize(ex.Message)}"
            );
        }
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

            foreach (var db in candidateDbs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var targetOptions = new ConnectionOptions
                {
                    ServerAlias = options.ServerAlias,
                    Server = options.Server,
                    Username = options.Username,
                    Password = options.Password,
                    Port = options.Port,
                    Database = db.Name,
                    Encrypt = options.Encrypt,
                    TrustServerCertificate = options.TrustServerCertificate,
                    ConnectTimeout = options.ConnectTimeout,
                    QueryTimeout = 5
                };

                string? currentMigrationId = null;
                int currentCount = 0;
                DateTime? currentLastModify = null;

                try
                {
                    var connStr = targetOptions.BuildConnectionString();
                    await using var conn = new SqlConnection(connStr);
                    await conn.OpenAsync(cancellationToken);

                    const string checkSql = @"
IF OBJECT_ID('dbo.__EFMigrationsHistory') IS NOT NULL
BEGIN
    SELECT 
        (SELECT TOP 1 MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC) AS LatestMigrationId,
        (SELECT COUNT(1) FROM dbo.__EFMigrationsHistory) AS MigrationCount,
        (SELECT MAX(modify_date) FROM sys.objects WHERE is_ms_shipped = 0) AS LastModifyDate;
END
ELSE
BEGIN
    SELECT 
        NULL AS LatestMigrationId,
        0 AS MigrationCount,
        (SELECT MAX(modify_date) FROM sys.objects WHERE is_ms_shipped = 0) AS LastModifyDate;
END";

                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = checkSql;
                    cmd.CommandTimeout = 5;
                    await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
                    if (await rdr.ReadAsync(cancellationToken))
                    {
                        currentMigrationId = rdr.IsDBNull(0) ? null : rdr.GetString(0);
                        currentCount = rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1);
                        currentLastModify = rdr.IsDBNull(2) ? null : rdr.GetDateTime(2);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Could not inspect migration state for '{db.Name}': {ex.Message}");
                    continue;
                }

                DatabaseMigrationStatus? cached = null;
                cachedMigrations?.TryGetValue(db.Name, out cached);

                if (cached == null)
                {
                    drifted.Add(new DatabaseDriftInfo(
                        DatabaseName: db.Name,
                        HasDrift: true,
                        SnapshotMigrationId: null,
                        CurrentMigrationId: currentMigrationId,
                        SnapshotCount: 0,
                        CurrentCount: currentCount,
                        Reason: "Not in snapshot"
                    ));
                }
                else
                {
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
                }
            }

            return new DriftCheckResult(true, alias, drifted, upToDate);
        }
        catch (Exception ex)
        {
            return new DriftCheckResult(false, alias, drifted, upToDate, ex.Message);
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

    public static (bool IsValid, string? ErrorMessage) ValidateReadOnlyQuery(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return (false, "SQL query cannot be empty.");
        }

        var trimmed = sql.Trim();

        // 1. Remove block and line comments before inspection
        var cleanSql = System.Text.RegularExpressions.Regex.Replace(trimmed, @"/\*.*?\*/", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
        cleanSql = System.Text.RegularExpressions.Regex.Replace(cleanSql, @"--.*$", " ", System.Text.RegularExpressions.RegexOptions.Multiline).Trim();

        if (string.IsNullOrWhiteSpace(cleanSql))
        {
            return (false, "SQL query does not contain valid executable syntax.");
        }

        // 2. Must begin with SELECT or WITH (CTE)
        if (!System.Text.RegularExpressions.Regex.IsMatch(cleanSql, @"^(?i)(SELECT|WITH)\b"))
        {
            return (false, "Only read-only queries (SELECT or WITH ... SELECT) are allowed. Modifying operations are prohibited.");
        }

        // 3. Remove string literals '...' to prevent false positives on string values (e.g. WHERE Status = 'DELETED')
        var withoutStrings = System.Text.RegularExpressions.Regex.Replace(cleanSql, @"'([^']|'')*'", "''");

        // 4. Block modifying or administrative keywords
        var forbiddenPattern = @"\b(?i)(INSERT|UPDATE|DELETE|DROP|ALTER|TRUNCATE|CREATE|MERGE|GRANT|REVOKE|DENY|EXEC|EXECUTE|SHUTDOWN)\b";
        if (System.Text.RegularExpressions.Regex.IsMatch(withoutStrings, forbiddenPattern))
        {
            return (false, "Forbidden modifying or administrative keyword detected in query. Only pure SELECT queries are permitted.");
        }

        return (true, null);
    }

    public async Task<QueryExecutionResult> ExecuteQueryAsync(
        ConnectionOptions options,
        string sql,
        string? database = null,
        int maxRows = 100,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateReadOnlyQuery(sql);
        var targetDb = string.IsNullOrWhiteSpace(database) ? options.Database : database.Trim();

        if (!validation.IsValid)
        {
            Logger.Error($"Query validation failed on database '{targetDb}': {validation.ErrorMessage} (SQL: {Logger.Sanitize(sql)})");
            return new QueryExecutionResult(
                Success: false,
                Database: targetDb,
                RowCount: 0,
                IsTruncated: false,
                ElapsedMs: 0,
                Columns: Array.Empty<string>(),
                Rows: Array.Empty<IReadOnlyDictionary<string, object?>>(),
                ErrorMessage: validation.ErrorMessage
            );
        }

        var targetOptions = new ConnectionOptions
        {
            ServerAlias = options.ServerAlias,
            Server = options.Server,
            Username = options.Username,
            Password = options.Password,
            Port = options.Port,
            Database = targetDb,
            Encrypt = options.Encrypt,
            TrustServerCertificate = options.TrustServerCertificate,
            ConnectTimeout = options.ConnectTimeout,
            QueryTimeout = options.QueryTimeout
        };

        var sw = Stopwatch.StartNew();
        int safeMaxRows = Math.Clamp(maxRows, 1, 1000);

        try
        {
            var connStr = targetOptions.BuildConnectionString();
            await using var connection = new SqlConnection(connStr);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = options.QueryTimeout;

            var rows = new List<Dictionary<string, object?>>();
            var columns = new List<string>();
            bool isTruncated = false;

            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection, cancellationToken);

            for (int i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }

            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= safeMaxRows)
                {
                    isTruncated = true;
                    break;
                }

                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var colName = columns[i];
                    var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    if (val is byte[] bytes)
                    {
                        val = "0x" + Convert.ToHexString(bytes);
                    }
                    else if (val is DateTime dt)
                    {
                        val = dt.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    }
                    else if (val is DateTimeOffset dto)
                    {
                        val = dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
                    }
                    row[colName] = val;
                }
                rows.Add(row);
            }

            sw.Stop();
            return new QueryExecutionResult(
                Success: true,
                Database: targetDb,
                RowCount: rows.Count,
                IsTruncated: isTruncated,
                ElapsedMs: sw.ElapsedMilliseconds,
                Columns: columns,
                Rows: rows
            );
        }
        catch (SqlException ex)
        {
            sw.Stop();
            var friendlyError = FormatSqlException(ex, targetOptions);
            Logger.Error($"SQL execution error on database '{targetDb}' (Code {ex.Number}): {friendlyError}", ex);
            return new QueryExecutionResult(
                Success: false,
                Database: targetDb,
                RowCount: 0,
                IsTruncated: false,
                ElapsedMs: sw.ElapsedMilliseconds,
                Columns: Array.Empty<string>(),
                Rows: Array.Empty<IReadOnlyDictionary<string, object?>>(),
                ErrorMessage: friendlyError
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            Logger.Error($"Unexpected execution error on database '{targetDb}': {ex.Message}", ex);
            return new QueryExecutionResult(
                Success: false,
                Database: targetDb,
                RowCount: 0,
                IsTruncated: false,
                ElapsedMs: sw.ElapsedMilliseconds,
                Columns: Array.Empty<string>(),
                Rows: Array.Empty<IReadOnlyDictionary<string, object?>>(),
                ErrorMessage: $"Execution error: {Logger.Sanitize(ex.Message)}"
            );
        }
    }

    internal static string FormatSqlException(SqlException ex, ConnectionOptions options)
    {
        return ex.Number switch
        {
            18456 => "Login failed. Please verify your Username and Password.",
            53 => $"Cannot connect to server '{options.Server}'. Please verify the server address, port, and firewall rules.",
            4060 => $"Cannot open initial database '{options.Database}'. The login may not have access permissions.",
            -2 => "Connection timed out. The server did not respond in time.",
            -2146893019 => "TLS/SSL certificate error: Server certificate is untrusted. Enable 'Trust Server Certificate' if using self-signed certificates.",
            _ => $"SQL Server error ({ex.Number}): {Logger.Sanitize(ex.Message)}"
        };
    }
}
