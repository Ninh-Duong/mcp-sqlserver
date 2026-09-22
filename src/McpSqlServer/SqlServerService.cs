using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace McpSqlServer;

public record ConnectionCheckResult(
    bool Success,
    long ElapsedMs,
    string? ServerVersion = null,
    string? ErrorMessage = null
);

public record DatabaseItem(
    int DatabaseId,
    string Name,
    string State,
    bool? HasAccess,
    bool IsSystem
);

public record DatabaseListingResult(
    bool Success,
    int VisibleCount,
    int SystemCount,
    int OtherCount,
    IReadOnlyList<DatabaseItem> Databases,
    string Scope = "visible_to_current_login",
    string? ErrorMessage = null
);

public record TableItem(
    string Schema,
    string Name
);

public record TableListingResult(
    bool Success,
    string Database,
    int TableCount,
    IReadOnlyList<TableItem> Tables,
    string? ErrorMessage = null
);

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
    {
        var lower = typeName.ToLowerInvariant();
        return lower switch
        {
            "nvarchar" or "nchar" => maxLength == -1 ? $"{lower}(max)" : $"{lower}({maxLength / 2})",
            "varchar" or "char" or "varbinary" or "binary" => maxLength == -1 ? $"{lower}(max)" : $"{lower}({maxLength})",
            "decimal" or "numeric" => $"{lower}({precision},{scale})",
            "time" or "datetime2" or "datetimeoffset" => scale == 7 ? lower : $"{lower}({scale})",
            _ => lower
        };
    }

    public async Task<DatabaseScanReport> ScanDatabaseSchemaAsync(ConnectionOptions options, string databaseName, CancellationToken cancellationToken = default)
    {
        var dbName = databaseName.Trim();
        Logger.Process("SCAN", $"Scanning schema for database '{dbName}'...");

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

        try
        {
            var connectionString = targetOptions.BuildConnectionString();
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // 1. Scan Tables & Columns (including Data Types, PK, FK, Nullable, Identity, Description)
            var tableDict = new Dictionary<string, (string Schema, string Name, List<ColumnSchemaItem> Columns)>();
            const string queryTablesAndColumns = @"
SELECT
    s.name AS schema_name,
    t.name AS table_name,
    c.name AS column_name,
    ty.name AS type_name,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable,
    c.is_identity,
    ISNULL(pk.is_primary_key, 0) AS is_pk,
    fk.referenced_target,
    CAST(ep.value AS NVARCHAR(MAX)) AS column_description
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.columns c ON t.object_id = c.object_id
INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
LEFT JOIN (
    SELECT ic.object_id, ic.column_id, 1 AS is_primary_key
    FROM sys.index_columns ic
    INNER JOIN sys.indexes i ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    WHERE i.is_primary_key = 1
) pk ON t.object_id = pk.object_id AND c.column_id = pk.column_id
LEFT JOIN (
    SELECT 
        fkc.parent_object_id,
        fkc.parent_column_id,
        CONCAT(QUOTENAME(ref_s.name), '.', QUOTENAME(ref_t.name), '.', QUOTENAME(ref_c.name)) AS referenced_target
    FROM sys.foreign_key_columns fkc
    INNER JOIN sys.tables ref_t ON fkc.referenced_object_id = ref_t.object_id
    INNER JOIN sys.schemas ref_s ON ref_t.schema_id = ref_s.schema_id
    INNER JOIN sys.columns ref_c ON fkc.referenced_object_id = ref_c.object_id AND fkc.referenced_column_id = ref_c.column_id
) fk ON t.object_id = fk.parent_object_id AND c.column_id = fk.parent_column_id
LEFT JOIN sys.extended_properties ep ON t.object_id = ep.major_id AND c.column_id = ep.minor_id AND ep.name = 'MS_Description'
ORDER BY s.name, t.name, c.column_id;";

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = queryTablesAndColumns;
                cmd.CommandTimeout = options.QueryTimeout;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var schema = reader.GetString(0);
                    var table = reader.GetString(1);
                    var column = reader.GetString(2);
                    var typeName = reader.GetString(3);
                    var maxLen = reader.GetInt16(4);
                    var precision = reader.GetByte(5);
                    var scale = reader.GetByte(6);
                    var isNullable = reader.GetBoolean(7);
                    var isIdentity = reader.GetBoolean(8);
                    var isPk = reader.GetInt32(9) == 1;
                    var fkRef = reader.IsDBNull(10) ? null : reader.GetString(10);
                    var colDesc = reader.IsDBNull(11) ? null : reader.GetString(11);

                    var formattedType = FormatDataType(typeName, maxLen, precision, scale);
                    var colItem = new ColumnSchemaItem(column, formattedType, isNullable, isPk, isIdentity, fkRef, colDesc);

                    var tableKey = $"{schema}.{table}";
                    if (!tableDict.TryGetValue(tableKey, out var val))
                    {
                        val = (schema, table, new List<ColumnSchemaItem>());
                        tableDict[tableKey] = val;
                    }
                    val.Columns.Add(colItem);
                }
            }

            var tables = tableDict.Values
                .Select(t => new TableSchemaItem(t.Schema, t.Name, t.Columns))
                .ToList();

            // 2. Scan Views & SQL Logic
            var views = new List<ViewSchemaItem>();
            const string queryViews = @"
SELECT 
    s.name AS schema_name,
    v.name AS view_name,
    m.definition AS view_definition
FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
LEFT JOIN sys.sql_modules m ON v.object_id = m.object_id
ORDER BY s.name, v.name;
";

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = queryViews;
                cmd.CommandTimeout = options.QueryTimeout;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var schema = reader.GetString(0);
                    var viewName = reader.GetString(1);
                    var def = reader.IsDBNull(2) ? "-- [Definition hidden or restricted by permissions]" : reader.GetString(2);
                    views.Add(new ViewSchemaItem(schema, viewName, def));
                }
            }

            // 3. Scan Stored Procedures & Parameters & SQL Logic
            var procMap = new Dictionary<int, (string Schema, string Name, string? Def, List<string> Params)>();
            const string queryProcs = @"
SELECT 
    s.name AS schema_name,
    p.name AS proc_name,
    m.definition AS proc_definition,
    p.object_id
FROM sys.procedures p
INNER JOIN sys.schemas s ON p.schema_id = s.schema_id
LEFT JOIN sys.sql_modules m ON p.object_id = m.object_id
WHERE p.is_ms_shipped = 0
ORDER BY s.name, p.name;
";

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = queryProcs;
                cmd.CommandTimeout = options.QueryTimeout;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var schema = reader.GetString(0);
                    var procName = reader.GetString(1);
                    var def = reader.IsDBNull(2) ? "-- [Definition hidden or restricted by permissions]" : reader.GetString(2);
                    var objId = reader.GetInt32(3);
                    procMap[objId] = (schema, procName, def, new List<string>());
                }
            }

            // Scan Parameters of Stored Procedures
            if (procMap.Count > 0)
            {
                const string queryParams = @"
SELECT 
    p.object_id,
    pm.name AS param_name,
    ty.name AS type_name,
    pm.max_length,
    pm.precision,
    pm.scale,
    pm.is_output
FROM sys.parameters pm
INNER JOIN sys.types ty ON pm.user_type_id = ty.user_type_id
INNER JOIN sys.procedures p ON pm.object_id = p.object_id
WHERE p.is_ms_shipped = 0
ORDER BY p.object_id, pm.parameter_id;
";

                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = queryParams;
                    cmd.CommandTimeout = options.QueryTimeout;
                    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var objId = reader.GetInt32(0);
                        if (procMap.TryGetValue(objId, out var entry))
                        {
                            var paramName = reader.IsDBNull(1) ? "@return_value" : reader.GetString(1);
                            var typeName = reader.GetString(2);
                            var maxLen = reader.GetInt16(3);
                            var precision = reader.GetByte(4);
                            var scale = reader.GetByte(5);
                            var isOutput = reader.GetBoolean(6);

                            var formattedType = FormatDataType(typeName, maxLen, precision, scale);
                            var paramStr = $"{paramName} {formattedType}{(isOutput ? " OUTPUT" : "")}";
                            entry.Params.Add(paramStr);
                        }
                    }
                }
            }

            var procs = procMap.Values
                .Select(p => new ProcedureSchemaItem(p.Schema, p.Name, p.Params, p.Def))
                .ToList();

            Logger.Process("SCAN", $"Database '{dbName}' completed: {tables.Count} tables, {views.Count} views, {procs.Count} procedures.");

            return new DatabaseScanReport(
                DatabaseName: dbName,
                Success: true,
                TableCount: tables.Count,
                ViewCount: views.Count,
                ProcedureCount: procs.Count,
                Tables: tables,
                Views: views,
                Procedures: procs
            );
        }
        catch (SqlException ex)
        {
            var friendlyError = FormatSqlException(ex, targetOptions);
            Logger.Error($"Error scanning database '{dbName}' (Code {ex.Number})", ex);
            return new DatabaseScanReport(dbName, false, 0, 0, 0, [], [], [], friendlyError);
        }
        catch (Exception ex)
        {
            Logger.Error($"System error scanning database '{dbName}'", ex);
            return new DatabaseScanReport(dbName, false, 0, 0, 0, [], [], [], Logger.Sanitize(ex.Message));
        }
    }

    public async Task<ServerScanResult> ScanServerAsync(
        ConnectionOptions options,
        bool includeSystem = false,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var alias = string.IsNullOrWhiteSpace(options.ServerAlias) ? "DEV" : options.ServerAlias.Trim();

        onProgress?.Invoke($"Connecting to Server [{alias}] ({options.Server})...");
        var testResult = await TestConnectionAsync(options, cancellationToken);
        if (!testResult.Success)
        {
            throw new InvalidOperationException($"Unable to connect to server: {testResult.ErrorMessage}");
        }

        var serverVersion = testResult.ServerVersion ?? "Microsoft SQL Server (Unspecified version)";

        onProgress?.Invoke("Fetching database catalog...");
        var dbListResult = await ListDatabasesAsync(options, cancellationToken);
        if (!dbListResult.Success)
        {
            throw new InvalidOperationException($"Unable to fetch database list: {dbListResult.ErrorMessage}");
        }

        var candidateDbs = dbListResult.Databases
            .Where(d => includeSystem || !d.IsSystem)
            .Where(d => d.State.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
            .Where(d => d.HasAccess != false)
            .ToList();

        onProgress?.Invoke($"Found {candidateDbs.Count} databases ready to scan (Include system: {includeSystem}).");

        var reports = new List<DatabaseScanReport>();
        for (int i = 0; i < candidateDbs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var db = candidateDbs[i];
            onProgress?.Invoke($"[{i + 1}/{candidateDbs.Count}] Scanning database '{db.Name}'...");

            var report = await ScanDatabaseSchemaAsync(options, db.Name, cancellationToken);
            reports.Add(report);
        }

        sw.Stop();
        return new ServerScanResult(
            ServerAlias: alias,
            ServerHost: options.Server,
            ServerVersion: serverVersion,
            ScannedAt: DateTime.Now,
            ElapsedMs: sw.ElapsedMilliseconds,
            Databases: reports
        );
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

    private static string FormatSqlException(SqlException ex, ConnectionOptions options)
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
