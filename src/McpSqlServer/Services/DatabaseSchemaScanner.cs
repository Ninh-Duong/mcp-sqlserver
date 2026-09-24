using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace McpSqlServer;

public static class DatabaseSchemaScanner
{
    public static string FormatDataType(string typeName, short maxLength, byte precision, byte scale)
    {
        var lower = typeName.ToLowerInvariant();
        return lower switch
        {
            "nvarchar" or "nchar" => maxLength == -1 ? $"{lower}(max)" : $"{lower}({maxLength / 2})",
            "varchar" or "char" or "varbinary" or "binary" => maxLength == -1 ? $"{lower}(max)" : $"{lower}({maxLength})",
            "decimal" or "numeric" => $"{lower}({precision},{scale})",
            "datetime2" or "datetimeoffset" or "time" => scale == 7 ? lower : $"{lower}({scale})",
            _ => lower
        };
    }

    public static bool IsCandidateForSampling(ColumnSchemaItem col)
    {
        if (col.IsPrimaryKey || col.IsIdentity || !string.IsNullOrEmpty(col.ForeignKeyReference))
            return false;

        var name = col.Name;
        var dt = col.DataType.ToLowerInvariant();
        if (dt.Contains("date") || dt.Contains("time") || dt.Contains("binary") || dt.Contains("image") || dt.Contains("uniqueidentifier"))
            return false;

        if (name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Guid", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Name", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Description", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Title", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Comment", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Note", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Address", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Json", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Payload", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Xml", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Url", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Hash", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Token", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] whitelist = { "status", "statuscode", "state", "statecode", "type", "typecode", "category", "mode", "priority", "stage", "disposition", "flag" };
        return whitelist.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<DatabaseScanReport> ScanDatabaseSchemaAsync(
        ConnectionOptions options,
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        var targetOptions = new ConnectionOptions
        {
            ServerAlias = options.ServerAlias,
            Server = options.Server,
            Username = options.Username,
            Password = options.Password,
            Port = options.Port,
            Database = databaseName,
            Encrypt = options.Encrypt,
            TrustServerCertificate = options.TrustServerCertificate,
            ConnectTimeout = options.ConnectTimeout,
            QueryTimeout = options.QueryTimeout
        };

        var dbName = databaseName;
        try
        {
            var connStr = targetOptions.BuildConnectionString();
            await using var connection = new SqlConnection(connStr);
            await connection.OpenAsync(cancellationToken);

            // 0. Query Latest EF Core Migration History & Last Object Modify Date
            string? latestMigrationId = null;
            int migrationCount = 0;
            DateTime? lastObjectModifyDate = null;

            const string queryMigrationAndModify = @"
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

            try
            {
                await using var migCmd = connection.CreateCommand();
                migCmd.CommandText = queryMigrationAndModify;
                migCmd.CommandTimeout = options.QueryTimeout;
                await using var migReader = await migCmd.ExecuteReaderAsync(cancellationToken);
                if (await migReader.ReadAsync(cancellationToken))
                {
                    latestMigrationId = migReader.IsDBNull(0) ? null : migReader.GetString(0);
                    migrationCount = migReader.IsDBNull(1) ? 0 : migReader.GetInt32(1);
                    lastObjectModifyDate = migReader.IsDBNull(2) ? null : migReader.GetDateTime(2);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not fetch migration history for '{dbName}': {ex.Message}");
            }

            // 0b. Query Approximate Row Count & Size in MB via sys.dm_db_partition_stats (Zero IO Overhead)
            var partitionStats = new Dictionary<string, (long RowCount, double SizeMb)>(StringComparer.OrdinalIgnoreCase);
            const string queryPartitionStats = @"
SELECT 
    s.name AS schema_name,
    t.name AS table_name,
    SUM(p.record_count) AS approx_row_count,
    CAST(ROUND(SUM(p.used_page_count) * 8.0 / 1024.0, 2) AS float) AS approx_size_mb
FROM sys.dm_db_partition_stats p
JOIN sys.tables t ON p.object_id = t.object_id
JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE p.index_id IN (0, 1)
GROUP BY s.name, t.name;";

            try
            {
                await using var partCmd = connection.CreateCommand();
                partCmd.CommandText = queryPartitionStats;
                partCmd.CommandTimeout = options.QueryTimeout;
                await using var partReader = await partCmd.ExecuteReaderAsync(cancellationToken);
                while (await partReader.ReadAsync(cancellationToken))
                {
                    var pSchema = partReader.GetString(0);
                    var pTable = partReader.GetString(1);
                    var pRowCount = partReader.GetInt64(2);
                    var pSizeMb = partReader.GetDouble(3);
                    partitionStats[$"{pSchema}.{pTable}"] = (pRowCount, pSizeMb);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not fetch partition stats for '{dbName}': {ex.Message}");
            }

            // 1. Scan Tables & Columns
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
    CAST(ep.value AS NVARCHAR(MAX)) AS column_description,
    dc.definition AS default_value
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
LEFT JOIN sys.default_constraints dc ON c.default_object_id = dc.object_id
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
                    var defVal = reader.IsDBNull(12) ? null : reader.GetString(12);

                    var formattedType = FormatDataType(typeName, maxLen, precision, scale);
                    var colItem = new ColumnSchemaItem(column, formattedType, isNullable, isPk, isIdentity, fkRef, colDesc, defVal);

                    var tableKey = $"{schema}.{table}";
                    if (!tableDict.TryGetValue(tableKey, out var val))
                    {
                        val = (schema, table, new List<ColumnSchemaItem>());
                        tableDict[tableKey] = val;
                    }
                    val.Columns.Add(colItem);
                }
            }

            // 1b. Smart Heuristic Sampling for candidate status/code columns
            foreach (var entry in tableDict.Values)
            {
                for (int c = 0; c < entry.Columns.Count; c++)
                {
                    var col = entry.Columns[c];
                    if (!IsCandidateForSampling(col)) continue;

                    try
                    {
                        await using var sampleCmd = connection.CreateCommand();
                        sampleCmd.CommandText = $"SELECT DISTINCT TOP 16 [{col.Name}] FROM [{entry.Schema}].[{entry.Name}] WITH (NOLOCK) WHERE [{col.Name}] IS NOT NULL;";
                        sampleCmd.CommandTimeout = 3;
                        await using var sReader = await sampleCmd.ExecuteReaderAsync(cancellationToken);
                        var sampleList = new List<string>();
                        while (await sReader.ReadAsync(cancellationToken))
                        {
                            if (!sReader.IsDBNull(0))
                            {
                                sampleList.Add(sReader.GetValue(0)?.ToString() ?? "");
                            }
                        }

                        if (sampleList.Count > 0 && sampleList.Count <= 15)
                        {
                            entry.Columns[c] = col with { SampleValues = sampleList };
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Ignore sampling errors
                    }
                }
            }

            // 2. Scan Check Constraints
            var checkConstraintsMap = new Dictionary<string, List<CheckConstraintItem>>(StringComparer.OrdinalIgnoreCase);
            const string queryCheckConstraints = @"
SELECT 
    s.name AS schema_name,
    t.name AS table_name,
    cc.name AS constraint_name,
    cc.definition
FROM sys.check_constraints cc
INNER JOIN sys.tables t ON cc.parent_object_id = t.object_id
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE cc.is_ms_shipped = 0
ORDER BY s.name, t.name, cc.name;";

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = queryCheckConstraints;
                cmd.CommandTimeout = options.QueryTimeout;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var schema = reader.GetString(0);
                    var table = reader.GetString(1);
                    var name = reader.GetString(2);
                    var def = reader.GetString(3);

                    var tableKey = $"{schema}.{table}";
                    if (!checkConstraintsMap.TryGetValue(tableKey, out var list))
                    {
                        list = new List<CheckConstraintItem>();
                        checkConstraintsMap[tableKey] = list;
                    }
                    list.Add(new CheckConstraintItem(name, def));
                }
            }

            // 3. Scan Indexes (Separating KeyColumns and IncludedColumns)
            var indexColMap = new Dictionary<string, Dictionary<string, (bool IsUnique, bool IsPk, string TypeDesc, string? FilterDef, List<string> KeyColumns, List<string> IncludedColumns)>>(StringComparer.OrdinalIgnoreCase);
            const string queryIndexes = @"
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
ORDER BY s.name, t.name, i.name, ic.is_included_column, ic.key_ordinal;";

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = queryIndexes;
                cmd.CommandTimeout = options.QueryTimeout;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var schema = reader.GetString(0);
                    var table = reader.GetString(1);
                    var indexName = reader.GetString(2);
                    var isUnique = reader.GetBoolean(3);
                    var isPk = reader.GetBoolean(4);
                    var typeDesc = reader.GetString(5);
                    var colName = reader.GetString(6);
                    var filterDef = reader.IsDBNull(7) ? null : reader.GetString(7);
                    var isIncluded = !reader.IsDBNull(8) && reader.GetBoolean(8);

                    var tableKey = $"{schema}.{table}";
                    if (!indexColMap.TryGetValue(tableKey, out var indices))
                    {
                        indices = new Dictionary<string, (bool, bool, string, string?, List<string>, List<string>)>(StringComparer.OrdinalIgnoreCase);
                        indexColMap[tableKey] = indices;
                    }

                    if (!indices.TryGetValue(indexName, out var idxData))
                    {
                        idxData = (isUnique, isPk, typeDesc, filterDef, new List<string>(), new List<string>());
                        indices[indexName] = idxData;
                    }

                    if (isIncluded)
                    {
                        idxData.IncludedColumns.Add(colName);
                    }
                    else
                    {
                        idxData.KeyColumns.Add(colName);
                    }
                }
            }

            // 4. Scan Triggers
            var triggersMap = new Dictionary<string, List<TriggerSchemaItem>>(StringComparer.OrdinalIgnoreCase);
            var allTriggers = new List<TriggerSchemaItem>();
            const string queryTriggers = @"
SELECT 
    ts.name AS schema_name,
    tr.name AS trigger_name,
    ts.name AS table_schema,
    t.name AS table_name,
    OBJECTPROPERTY(tr.object_id, 'ExecIsInsertTrigger') AS is_insert,
    OBJECTPROPERTY(tr.object_id, 'ExecIsUpdateTrigger') AS is_update,
    OBJECTPROPERTY(tr.object_id, 'ExecIsDeleteTrigger') AS is_delete,
    tr.is_instead_of_trigger AS is_instead_of,
    tr.is_disabled,
    m.definition AS trigger_definition
FROM sys.triggers tr
INNER JOIN sys.tables t ON tr.parent_id = t.object_id
INNER JOIN sys.schemas ts ON t.schema_id = ts.schema_id
LEFT JOIN sys.sql_modules m ON tr.object_id = m.object_id
WHERE tr.is_ms_shipped = 0
ORDER BY ts.name, t.name, tr.name;";

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = queryTriggers;
                cmd.CommandTimeout = options.QueryTimeout;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var triggerSchema = reader.GetString(0);
                    var triggerName = reader.GetString(1);
                    var tableSchema = reader.GetString(2);
                    var tableName = reader.GetString(3);
                    var isInsert = !reader.IsDBNull(4) && reader.GetInt32(4) == 1;
                    var isUpdate = !reader.IsDBNull(5) && reader.GetInt32(5) == 1;
                    var isDelete = !reader.IsDBNull(6) && reader.GetInt32(6) == 1;
                    var isInsteadOf = reader.GetBoolean(7);
                    var isDisabled = reader.GetBoolean(8);
                    var def = reader.IsDBNull(9) ? null : reader.GetString(9);

                    var eventList = new List<string>();
                    if (isInsert) eventList.Add("INSERT");
                    if (isUpdate) eventList.Add("UPDATE");
                    if (isDelete) eventList.Add("DELETE");
                    var timing = isInsteadOf ? "INSTEAD OF" : "AFTER";
                    var eventsStr = eventList.Count > 0 ? $"{timing} {string.Join(", ", eventList)}" : timing;

                    var trgItem = new TriggerSchemaItem(triggerSchema, triggerName, tableSchema, tableName, eventsStr, isDisabled, def);
                    allTriggers.Add(trgItem);

                    var tableKey = $"{tableSchema}.{tableName}";
                    if (!triggersMap.TryGetValue(tableKey, out var trgList))
                    {
                        trgList = new List<TriggerSchemaItem>();
                        triggersMap[tableKey] = trgList;
                    }
                    trgList.Add(trgItem);
                }
            }

            // Build Tables with Indexes, Triggers, and Check Constraints
            var tables = new List<TableSchemaItem>();
            foreach (var kv in tableDict)
            {
                var tableKey = kv.Key;
                var t = kv.Value;

                var tableIndices = new List<IndexSchemaItem>();
                if (indexColMap.TryGetValue(tableKey, out var idxDict))
                {
                    foreach (var idx in idxDict)
                    {
                        tableIndices.Add(new IndexSchemaItem(
                            Name: idx.Key,
                            IsUnique: idx.Value.IsUnique,
                            IsPrimaryKey: idx.Value.IsPk,
                            TypeDesc: idx.Value.TypeDesc,
                            KeyColumns: idx.Value.KeyColumns,
                            IncludedColumns: idx.Value.IncludedColumns,
                            FilterDefinition: idx.Value.FilterDef
                        ));
                    }
                }

                IReadOnlyList<TriggerSchemaItem> tableTrgs = triggersMap.TryGetValue(tableKey, out var trgs) ? trgs : Array.Empty<TriggerSchemaItem>();
                IReadOnlyList<CheckConstraintItem> tableCks = checkConstraintsMap.TryGetValue(tableKey, out var cks) ? cks : Array.Empty<CheckConstraintItem>();

                long? approxRows = null;
                double? approxMb = null;
                if (partitionStats.TryGetValue(tableKey, out var pStat))
                {
                    approxRows = pStat.RowCount;
                    approxMb = pStat.SizeMb;
                }

                tables.Add(new TableSchemaItem(t.Schema, t.Name, t.Columns, tableIndices, tableTrgs, tableCks, approxRows, approxMb));
            }

            // 5. Scan Views & SQL Logic
            var views = new List<ViewSchemaItem>();
            const string queryViews = @"
SELECT 
    s.name AS schema_name,
    v.name AS view_name,
    m.definition AS view_definition
FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
LEFT JOIN sys.sql_modules m ON v.object_id = m.object_id
ORDER BY s.name, v.name;";

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = queryViews;
                cmd.CommandTimeout = options.QueryTimeout;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var schema = reader.GetString(0);
                    var viewName = reader.GetString(1);
                    var def = reader.IsDBNull(2) ? null : reader.GetString(2);
                    views.Add(new ViewSchemaItem(schema, viewName, def));
                }
            }

            // 6. Scan Functions & Parameters
            var funcMap = new Dictionary<int, (string Schema, string Name, string TypeDesc, string? Def, List<string> Params)>();
            const string queryFuncs = @"
SELECT 
    s.name AS schema_name,
    o.name AS func_name,
    o.type_desc,
    m.definition AS func_definition,
    o.object_id
FROM sys.objects o
INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
LEFT JOIN sys.sql_modules m ON o.object_id = m.object_id
WHERE o.type IN ('FN', 'IF', 'TF') AND o.is_ms_shipped = 0
ORDER BY s.name, o.name;";

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = queryFuncs;
                cmd.CommandTimeout = options.QueryTimeout;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var schema = reader.GetString(0);
                    var funcName = reader.GetString(1);
                    var typeDesc = reader.GetString(2);
                    var def = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var objId = reader.GetInt32(4);
                    funcMap[objId] = (schema, funcName, typeDesc, def, new List<string>());
                }
            }

            // Populate Function parameters using shared routine parameter query
            if (funcMap.Count > 0)
            {
                await PopulateRoutineParametersAsync(
                    connection,
                    "o.type IN ('FN', 'IF', 'TF')",
                    funcMap.ToDictionary(k => k.Key, v => v.Value.Params),
                    options.QueryTimeout,
                    cancellationToken
                );
            }

            var functions = funcMap.Values
                .Select(f => new FunctionSchemaItem(f.Schema, f.Name, f.TypeDesc, f.Params, f.Def))
                .ToList();

            // 7. Scan Stored Procedures & Parameters
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
ORDER BY s.name, p.name;";

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = queryProcs;
                cmd.CommandTimeout = options.QueryTimeout;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var schema = reader.GetString(0);
                    var procName = reader.GetString(1);
                    var def = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var objId = reader.GetInt32(3);
                    procMap[objId] = (schema, procName, def, new List<string>());
                }
            }

            // Populate Stored Procedure parameters using shared routine parameter query
            if (procMap.Count > 0)
            {
                await PopulateRoutineParametersAsync(
                    connection,
                    "o.type = 'P'",
                    procMap.ToDictionary(k => k.Key, v => v.Value.Params),
                    options.QueryTimeout,
                    cancellationToken
                );
            }

            var procs = procMap.Values
                .Select(p => new ProcedureSchemaItem(p.Schema, p.Name, p.Params, p.Def))
                .ToList();

            // 8. Scan Cross-Database Dependencies
            var crossDbDeps = new List<CrossDbDependencyItem>();
            const string queryCrossDbDeps = @"
SELECT 
    OBJECT_SCHEMA_NAME(referencing_id) + '.' + OBJECT_NAME(referencing_id) AS referencing_entity,
    referenced_database_name,
    ISNULL(referenced_schema_name, 'dbo') + '.' + referenced_entity_name AS referenced_entity
FROM sys.sql_expression_dependencies
WHERE referenced_database_name IS NOT NULL
  AND referenced_database_name <> DB_NAME()
ORDER BY referencing_entity, referenced_database_name;";

            try
            {
                await using var depCmd = connection.CreateCommand();
                depCmd.CommandText = queryCrossDbDeps;
                depCmd.CommandTimeout = options.QueryTimeout;
                await using var depReader = await depCmd.ExecuteReaderAsync(cancellationToken);
                while (await depReader.ReadAsync(cancellationToken))
                {
                    var referencing = depReader.GetString(0);
                    var refDb = depReader.GetString(1);
                    var referenced = depReader.GetString(2);
                    crossDbDeps.Add(new CrossDbDependencyItem(referencing, refDb, referenced));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Could not fetch cross-database dependencies for '{dbName}': {ex.Message}");
            }

            Logger.Process("SCAN", $"Database '{dbName}' completed: {tables.Count} tables, {views.Count} views, {procs.Count} procedures, {functions.Count} functions, {allTriggers.Count} triggers, {crossDbDeps.Count} cross-db references.");

            return new DatabaseScanReport(
                DatabaseName: dbName,
                Success: true,
                TableCount: tables.Count,
                ViewCount: views.Count,
                ProcedureCount: procs.Count,
                Tables: tables,
                Views: views,
                Procedures: procs,
                ErrorMessage: null,
                FunctionCount: functions.Count,
                TriggerCount: allTriggers.Count,
                Functions: functions,
                Triggers: allTriggers,
                LatestMigrationId: latestMigrationId,
                MigrationCount: migrationCount,
                LastObjectModifyDate: lastObjectModifyDate,
                CrossDbDependencies: crossDbDeps
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqlException ex)
        {
            var friendlyError = SqlServerService.FormatSqlException(ex, targetOptions);
            Logger.Error($"Error scanning database '{dbName}' (Code {ex.Number})", ex);
            return new DatabaseScanReport(dbName, false, 0, 0, 0, [], [], [], friendlyError);
        }
        catch (Exception ex)
        {
            Logger.Error($"System error scanning database '{dbName}'", ex);
            return new DatabaseScanReport(dbName, false, 0, 0, 0, [], [], [], Logger.Sanitize(ex.Message));
        }
    }

    /// <summary>
    /// Shared helper to populate parameters for both stored procedures and functions, deduplicating sys.parameters queries.
    /// </summary>
    private static async Task PopulateRoutineParametersAsync(
        SqlConnection connection,
        string objectTypeCondition,
        Dictionary<int, List<string>> paramListsByObjectId,
        int queryTimeout,
        CancellationToken cancellationToken)
    {
        var queryParams = $@"
SELECT 
    pm.object_id,
    pm.name AS param_name,
    ty.name AS type_name,
    pm.max_length,
    pm.precision,
    pm.scale,
    pm.is_output,
    pm.parameter_id
FROM sys.parameters pm
INNER JOIN sys.types ty ON pm.user_type_id = ty.user_type_id
INNER JOIN sys.objects o ON pm.object_id = o.object_id
WHERE {objectTypeCondition} AND o.is_ms_shipped = 0
ORDER BY pm.object_id, pm.parameter_id;";

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = queryParams;
        cmd.CommandTimeout = queryTimeout;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var objId = reader.GetInt32(0);
            var paramId = reader.GetInt32(7);
            if (paramId == 0) continue; // Skip scalar function return value

            if (paramListsByObjectId.TryGetValue(objId, out var paramList))
            {
                var paramName = reader.IsDBNull(1) ? "@param" : reader.GetString(1);
                var typeName = reader.GetString(2);
                var maxLen = reader.GetInt16(3);
                var precision = reader.GetByte(4);
                var scale = reader.GetByte(5);
                var isOutput = reader.GetBoolean(6);

                var formattedType = FormatDataType(typeName, maxLen, precision, scale);
                var paramStr = $"{paramName} {formattedType}{(isOutput ? " OUTPUT" : "")}";
                paramList.Add(paramStr);
            }
        }
    }

    public static async Task<ServerScanResult> ScanServerAsync(
        SqlServerService sqlService,
        ConnectionOptions options,
        bool includeSystem = false,
        Action<string>? onProgress = null,
        string? targetDatabase = null,
        CancellationToken cancellationToken = default,
        string? outputDirectory = null,
        bool resume = false)
    {
        if (resume && (outputDirectory == null || targetDatabase != null))
            throw new InvalidOperationException("Resume requires a full-server scan and an output directory.");
        var sw = Stopwatch.StartNew();
        var alias = string.IsNullOrWhiteSpace(options.ServerAlias) ? "DEV" : options.ServerAlias.Trim();

        onProgress?.Invoke($"Connecting to Server [{alias}] ({options.Server})...");
        var testResult = await sqlService.TestConnectionAsync(options, cancellationToken);
        if (!testResult.Success)
        {
            throw new InvalidOperationException($"Unable to connect to server: {testResult.ErrorMessage}");
        }

        var serverVersion = testResult.ServerVersion ?? "Microsoft SQL Server (Unspecified version)";

        onProgress?.Invoke("Fetching database catalog...");
        var dbListResult = await sqlService.ListDatabasesAsync(options, cancellationToken);
        if (!dbListResult.Success)
        {
            throw new InvalidOperationException($"Unable to fetch database list: {dbListResult.ErrorMessage}");
        }

        var candidateDbs = dbListResult.Databases
            .Where(d => includeSystem || !d.IsSystem)
            .Where(d => d.State.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
            .Where(d => d.HasAccess != false)
            .ToList();

        if (!string.IsNullOrWhiteSpace(targetDatabase))
        {
            var targetName = targetDatabase.Trim();
            candidateDbs = candidateDbs
                .Where(d => d.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidateDbs.Count == 0)
            {
                throw new InvalidOperationException($"Target database '{targetName}' was not found or is offline.");
            }
        }

        onProgress?.Invoke($"Found {candidateDbs.Count} databases ready to scan (Include system: {includeSystem}).");

        var names = candidateDbs.Select(db => db.Name).ToArray();
        ScanCheckpoint? checkpoint = null;
        if (outputDirectory != null && targetDatabase == null)
        {
            checkpoint = await ScanCheckpoint.OpenAsync(outputDirectory, alias, options.Server, names, resume, cancellationToken);
            if (resume)
                onProgress?.Invoke($"Resuming: {checkpoint.GetCompletedReports().Count} cached reports, {checkpoint.PendingDatabases.Count} databases to scan.");
        }

        var pending = checkpoint?.PendingDatabases ?? names;
        var freshReports = await ScanReportsAsync(pending, async (name, token) =>
        {
            var report = await ScanDatabaseSchemaAsync(options, name, token);
            if (checkpoint != null) await checkpoint.SaveReportAsync(report, token);
            return report;
        }, 4, onProgress, cancellationToken);
        var freshByName = freshReports.ToDictionary(report => report.DatabaseName, StringComparer.OrdinalIgnoreCase);
        var reports = names.Select(name => freshByName.TryGetValue(name, out var report)
            ? report
            : checkpoint?.GetReport(name) ?? throw new InvalidOperationException($"Missing scan report for '{name}'.")).ToArray();

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

    public static async Task<DatabaseScanReport[]> ScanReportsAsync(
        IReadOnlyList<string> databaseNames,
        Func<string, CancellationToken, Task<DatabaseScanReport>> scan,
        int maxConcurrency = 4,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var reports = new DatabaseScanReport[databaseNames.Count];
        var progressLock = new object();
        await Parallel.ForEachAsync(Enumerable.Range(0, databaseNames.Count),
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var name = databaseNames[index];
                lock (progressLock) onProgress?.Invoke($"[{index + 1}/{databaseNames.Count}] Scanning database '{name}'...");
                reports[index] = await scan(name, token);
            });
        return reports;
    }
}
