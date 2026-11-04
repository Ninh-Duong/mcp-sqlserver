using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using McpSqlServer.Security;

namespace McpSqlServer.Investigation;

using Row = IReadOnlyDictionary<string, object?>;

/// <summary>
/// Investigation tools. Rules: every query goes through <see cref="ReadOnlyQueryExecutor"/>;
/// values are always parameters; identifiers are only interpolated after the catalog confirmed they exist.
/// This layer must not reference Microsoft.Data.SqlClient (enforced by ArchitectureRulesTests).
/// </summary>
public sealed class InvestigationService
{
    // ---- Fixed catalog SQL (each one is validated by SelectOnlyValidator in the tests) ----------

    internal const string ListObjectsSql = @"
SELECT s.name AS schema_name, o.name AS object_name, o.type AS object_type,
       ISNULL(SUM(CASE WHEN p.index_id IN (0, 1) THEN p.rows ELSE 0 END), 0) AS approx_rows
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN sys.partitions p ON p.object_id = o.object_id
WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0
  AND (@schema IS NULL OR s.name = @schema)
  AND (@like IS NULL OR o.name LIKE @like)
GROUP BY s.name, o.name, o.type
ORDER BY s.name, o.name";

    internal const string ResolveObjectSql = @"
SELECT s.name AS schema_name, o.name AS object_name, o.type AS object_type, o.object_id AS object_id
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('U', 'V') AND o.name = @name AND (@schema IS NULL OR s.name = @schema)
ORDER BY CASE WHEN s.name = 'dbo' THEN 0 ELSE 1 END, s.name";

    internal const string ColumnsSql = @"
SELECT c.column_id, c.name AS column_name, ty.name AS type_name, c.max_length, c.precision, c.scale,
       c.is_nullable, c.is_identity, c.is_computed,
       CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS is_pk,
       dc.definition AS default_definition
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
LEFT JOIN (SELECT ic.object_id, ic.column_id
           FROM sys.indexes i
           JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
           WHERE i.is_primary_key = 1) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE c.object_id = @oid
ORDER BY c.column_id";

    internal const string ForeignKeysSql = @"
SELECT fk.name AS fk_name,
       OBJECT_SCHEMA_NAME(fk.parent_object_id) AS parent_schema, OBJECT_NAME(fk.parent_object_id) AS parent_table, pc.name AS parent_column,
       OBJECT_SCHEMA_NAME(fk.referenced_object_id) AS ref_schema, OBJECT_NAME(fk.referenced_object_id) AS ref_table, rc.name AS ref_column,
       fkc.constraint_column_id AS ordinal
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
WHERE fk.parent_object_id = @oid OR fk.referenced_object_id = @oid
ORDER BY fk.name, fkc.constraint_column_id";

    internal const string IndexesSql = @"
SELECT i.name AS index_name, i.type_desc, i.is_unique, i.is_primary_key,
       STUFF((SELECT ', ' + c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
              FROM sys.index_columns ic
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
              ORDER BY ic.key_ordinal
              FOR XML PATH('')), 1, 2, '') AS key_columns
FROM sys.indexes i
WHERE i.object_id = @oid AND i.type > 0
ORDER BY i.index_id";

    internal const string TriggersSql = @"
SELECT name AS trigger_name, is_disabled, is_instead_of_trigger
FROM sys.triggers
WHERE parent_id = @oid
ORDER BY name";

    internal const string PrimaryKeySql = @"
SELECT c.name AS column_name
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = @oid AND i.is_primary_key = 1
ORDER BY ic.key_ordinal";

    internal const string RowCountSql = @"
SELECT ISNULL(SUM(p.rows), 0) AS approx_rows
FROM sys.partitions p
WHERE p.object_id = @oid AND p.index_id IN (0, 1)";

    internal const string ColumnTypesSql = @"
SELECT c.name AS column_name, ty.name AS type_name
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = @oid
ORDER BY c.column_id";

    internal const string SearchableDatabasesSql = @"
SELECT name FROM sys.databases
WHERE state_desc = 'ONLINE' AND HAS_DBACCESS(name) = 1
ORDER BY name";

    internal const string FindColumnsSql = @"
SELECT TOP (@limit) s.name AS schema_name, o.name AS table_name, c.name AS column_name, ty.name AS type_name, c.is_nullable
FROM sys.columns c
JOIN sys.objects o ON o.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 AND c.name LIKE @like
ORDER BY o.name, c.column_id";

    internal const string ObjectDefinitionSql = @"
SELECT TOP (5) s.name AS schema_name, o.name AS object_name, o.type_desc AS type_desc,
       CONVERT(varchar(19), o.modify_date, 120) AS modified, m.definition AS definition
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE o.name = @name AND (@schema IS NULL OR s.name = @schema)
ORDER BY s.name, o.name";

    internal static IReadOnlyList<string> AllFixedSql =>
    [
        ListObjectsSql, ResolveObjectSql, ColumnsSql, ForeignKeysSql, IndexesSql, TriggersSql,
        PrimaryKeySql, RowCountSql, ColumnTypesSql, SearchableDatabasesSql, FindColumnsSql, ObjectDefinitionSql,
    ];

    public const int MaxFilters = 10;
    public const int MaxInValues = 50;
    public const int MaxRelatedQueries = 24;
    private const int MaxSearchDatabases = 50;
    private const int ObjectDefinitionCellChars = 60_000;

    private readonly ReadOnlyQueryExecutor _executor;

    public InvestigationService(ReadOnlyQueryExecutor executor) => _executor = executor;

    internal sealed record ObjectRef(string Schema, string Name, string Type, int ObjectId)
    {
        public string Quoted => SqlIdentifier.Quote(Schema, Name);
        public string Display => $"{Schema}.{Name}";
    }

    // ---- list_tables ---------------------------------------------------------------------------

    public async Task<ListTablesResult> ListTablesAsync(string database, string? schema, string? nameLike, int limit, CancellationToken ct)
    {
        var result = await _executor.RunSelectAsync(
            ListObjectsSql, database, Math.Clamp(limit, 1, 500),
            [new("schema", NullIfEmpty(schema)), new("like", SqlIdentifier.ToLikePattern(nameLike))], ct);

        if (!result.Success)
            return new ListTablesResult(false, database, 0, false, [], result.ErrorMessage);

        var tables = result.Rows
            .Select(r => new TableSummary(Str(r, "schema_name"), Str(r, "object_name"), Str(r, "object_type") == "V" ? "view" : "table", Long(r, "approx_rows")))
            .ToList();

        return new ListTablesResult(true, result.Database, tables.Count, result.IsTruncated, tables);
    }

    // ---- describe_table ------------------------------------------------------------------------

    public async Task<TableDescription> DescribeTableAsync(string database, string table, CancellationToken ct)
    {
        var (obj, error) = await ResolveObjectAsync(database, table, ct);
        if (obj is null) return Empty(database, error);

        var oid = new QueryParameter("oid", obj.ObjectId);

        var columnsTask = _executor.RunSelectAsync(ColumnsSql, database, 500, [oid], ct);
        var fkTask = _executor.RunSelectAsync(ForeignKeysSql, database, 500, [oid], ct);
        var indexTask = _executor.RunSelectAsync(IndexesSql, database, 200, [oid], ct);
        var triggerTask = _executor.RunSelectAsync(TriggersSql, database, 100, [oid], ct);
        var rowsTask = _executor.RunSelectAsync(RowCountSql, database, 1, [oid], ct);
        await Task.WhenAll(columnsTask, fkTask, indexTask, triggerTask, rowsTask);

        var columnsResult = columnsTask.Result;
        if (!columnsResult.Success) return Empty(database, columnsResult.ErrorMessage);

        var columns = columnsResult.Rows.Select(r => new ColumnInfo(
            Str(r, "column_name"), FormatType(r), Bool(r, "is_nullable"), Bool(r, "is_pk"),
            Bool(r, "is_identity"), Bool(r, "is_computed"), NullIfEmpty(Str(r, "default_definition")))).ToList();

        var foreignKeys = fkTask.Result.Success ? BuildForeignKeys(fkTask.Result.Rows, obj) : [];

        var indexes = indexTask.Result.Success
            ? indexTask.Result.Rows.Select(r => new IndexInfo(Str(r, "index_name"), Str(r, "type_desc"), Bool(r, "is_unique"), Bool(r, "is_primary_key"), NullIfEmpty(Str(r, "key_columns")))).ToList()
            : [];

        var triggers = triggerTask.Result.Success
            ? triggerTask.Result.Rows.Select(r => new TriggerInfo(Str(r, "trigger_name"), Bool(r, "is_disabled"), Bool(r, "is_instead_of_trigger"))).ToList()
            : [];

        var approx = rowsTask.Result.Success && rowsTask.Result.Rows.Count > 0 ? Long(rowsTask.Result.Rows[0], "approx_rows") : 0;

        return new TableDescription(true, database, obj.Schema, obj.Name, obj.Type == "V" ? "view" : "table", approx, columns, foreignKeys, indexes, triggers);
    }

    private static TableDescription Empty(string database, string? error) =>
        new(false, database, string.Empty, string.Empty, string.Empty, 0, [], [], [], [], error);

    private static List<ForeignKeyInfo> BuildForeignKeys(IReadOnlyList<Row> rows, ObjectRef self)
    {
        return rows.GroupBy(r => Str(r, "fk_name")).Select(g =>
        {
            var first = g.First();
            var parent = $"{Str(first, "parent_schema")}.{Str(first, "parent_table")}";
            var referenced = $"{Str(first, "ref_schema")}.{Str(first, "ref_table")}";
            var direction = parent.Equals(self.Display, StringComparison.OrdinalIgnoreCase) ? "references" : "referenced_by";
            return new ForeignKeyInfo(g.Key, direction, parent,
                g.Select(r => Str(r, "parent_column")).ToList(), referenced, g.Select(r => Str(r, "ref_column")).ToList());
        }).ToList();
    }

    // ---- find_columns --------------------------------------------------------------------------

    public async Task<FindColumnsResult> FindColumnsAsync(string nameLike, string? database, int limit, CancellationToken ct)
    {
        var like = SqlIdentifier.ToLikePattern(nameLike);
        if (like is null)
            return new FindColumnsResult(false, 0, false, [], [], [], "name_like cannot be empty.");

        limit = Math.Clamp(limit, 1, 200);
        var errors = new List<string>();
        List<string> databases;

        if (!string.IsNullOrWhiteSpace(database))
        {
            databases = [database.Trim()];
        }
        else
        {
            var listing = await _executor.RunCatalogSelectAsync(SearchableDatabasesSql, maxRows: 1000, cancellationToken: ct);
            if (!listing.Success)
                return new FindColumnsResult(false, 0, false, [], [], [], listing.ErrorMessage);

            databases = listing.Rows.Select(r => Str(r, "name")).Where(_executor.Options.IsDatabaseAllowed).Take(MaxSearchDatabases).ToList();
            if (listing.Rows.Count > databases.Count)
                errors.Add($"Searched the first {databases.Count} allowed databases only; pass 'database' to narrow the search.");
        }

        var matches = new List<ColumnMatch>();
        using var gate = new SemaphoreSlim(4);
        var tasks = databases.Select(async db =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await _executor.RunSelectAsync(FindColumnsSql, db, limit, [new("limit", limit), new("like", like)], ct);
                lock (matches)
                {
                    if (!r.Success) errors.Add($"{db}: {r.ErrorMessage}");
                    else matches.AddRange(r.Rows.Select(row => new ColumnMatch(db, Str(row, "schema_name"), Str(row, "table_name"), Str(row, "column_name"), Str(row, "type_name"), Bool(row, "is_nullable"))));
                }
            }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);

        var ordered = matches.OrderBy(m => m.Database, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Schema).ThenBy(m => m.Table).ThenBy(m => m.Column).ToList();
        var truncated = ordered.Count > limit;
        if (truncated) ordered = ordered.Take(limit).ToList();

        var success = databases.Count > 0 && (matches.Count > 0 || errors.Count < databases.Count);
        return new FindColumnsResult(success, ordered.Count, truncated, databases, ordered, errors,
            success ? null : string.Join(" ", errors.DefaultIfEmpty("No searchable database.")));
    }

    // ---- lookup_rows ---------------------------------------------------------------------------

    public async Task<QueryExecutionResult> LookupRowsAsync(
        string database, string table, IReadOnlyList<LookupFilter>? filters, IReadOnlyList<LookupOrder>? orderBy,
        IReadOnlyList<string>? columns, int limit, CancellationToken ct)
    {
        filters ??= [];
        if (filters.Count > MaxFilters)
            return Fail(database, $"At most {MaxFilters} filters are allowed.");

        var (obj, error) = await ResolveObjectAsync(database, table, ct);
        if (obj is null) return Fail(database, error);

        var typeResult = await _executor.RunSelectAsync(ColumnTypesSql, database, 1000, [new("oid", obj.ObjectId)], ct);
        if (!typeResult.Success) return Fail(database, typeResult.ErrorMessage);

        var columnTypes = typeResult.Rows.ToDictionary(r => Str(r, "column_name"), r => Str(r, "type_name"), StringComparer.OrdinalIgnoreCase);

        return await SelectByConditionsAsync(database, obj, columnTypes, filters, orderBy, columns, limit, ct);
    }

    internal sealed record BuiltQuery(string? Sql, List<QueryParameter> Parameters, string? ErrorMessage)
    {
        public static BuiltQuery Error(string message) => new(null, [], message);
    }

    private async Task<QueryExecutionResult> SelectByConditionsAsync(
        string database, ObjectRef obj, Dictionary<string, string> columnTypes,
        IReadOnlyList<LookupFilter> filters, IReadOnlyList<LookupOrder>? orderBy, IReadOnlyList<string>? columns,
        int limit, CancellationToken ct)
    {
        var built = BuildSelect(obj, columnTypes, filters, orderBy, columns, limit);
        if (built.Sql is null) return Fail(database, built.ErrorMessage);

        var result = await _executor.RunSelectAsync(built.Sql, database, limit, built.Parameters, ct);
        return result with { Sql = built.Sql };
    }

    /// <summary>Pure SQL builder (no I/O). Column and table names must already come from the catalog; values become parameters.</summary>
    internal static BuiltQuery BuildSelect(
        ObjectRef obj, Dictionary<string, string> columnTypes,
        IReadOnlyList<LookupFilter> filters, IReadOnlyList<LookupOrder>? orderBy, IReadOnlyList<string>? columns, int limit)
    {
        string CanonicalColumn(string name) => columnTypes.Keys.FirstOrDefault(k => k.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

        var select = "*";
        if (columns is { Count: > 0 })
        {
            var names = new List<string>();
            foreach (var c in columns)
            {
                var canonical = CanonicalColumn(c);
                if (canonical.Length == 0) return BuiltQuery.Error($"Column '{c}' does not exist in {obj.Display}.");
                names.Add(SqlIdentifier.Quote(canonical));
            }
            select = string.Join(", ", names);
        }

        var parameters = new List<QueryParameter> { new("n", Math.Clamp(limit, 1, ConnectionOptions.HardMaxRows)) };
        var conditions = new List<string>();

        for (var i = 0; i < filters.Count; i++)
        {
            var f = filters[i];
            var column = CanonicalColumn(f.Column);
            if (column.Length == 0) return BuiltQuery.Error($"Column '{f.Column}' does not exist in {obj.Display}.");

            var quoted = SqlIdentifier.Quote(column);
            var ansi = IsAnsiType(columnTypes[column]);
            var op = NormalizeOp(f.Op);
            var value = f.Value;

            switch (op)
            {
                case "is_null": conditions.Add($"{quoted} IS NULL"); break;
                case "is_not_null": conditions.Add($"{quoted} IS NOT NULL"); break;

                case "in":
                    if (value is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() == 0 || list.GetArrayLength() > MaxInValues)
                        return BuiltQuery.Error($"Filter on '{column}': 'in' needs a value array with 1-{MaxInValues} items.");
                    var names = new List<string>();
                    foreach (var item in list.EnumerateArray())
                    {
                        if (!TryScalar(item, out var scalar)) return BuiltQuery.Error($"Filter on '{column}': array items must be strings, numbers or booleans.");
                        var name = $"f{i}_{names.Count}";
                        parameters.Add(new QueryParameter(name, scalar, ansi && scalar is string));
                        names.Add("@" + name);
                    }
                    conditions.Add($"{quoted} IN ({string.Join(", ", names)})");
                    break;

                case "=" or "!=" or ">" or ">=" or "<" or "<=" or "like" or "contains":
                    if (value is null || !TryScalar(value.Value, out var one))
                    {
                        if (value is { ValueKind: JsonValueKind.Null } && op is "=" or "!=")
                        {
                            conditions.Add($"{quoted} IS {(op == "=" ? "" : "NOT ")}NULL");
                            break;
                        }
                        return BuiltQuery.Error($"Filter on '{column}': a string, number or boolean 'value' is required for '{op}'.");
                    }

                    if (op == "contains") one = "%" + EscapeLike(one?.ToString() ?? string.Empty) + "%";
                    var pname = $"f{i}";
                    parameters.Add(new QueryParameter(pname, one, ansi && one is string));
                    var sqlOp = op switch { "contains" => "LIKE", "like" => "LIKE", "!=" => "<>", _ => op };
                    conditions.Add($"{quoted} {sqlOp} @{pname}");
                    break;

                default:
                    return BuiltQuery.Error($"Unsupported operator '{f.Op}'. Use = != > >= < <= like contains in is_null is_not_null.");
            }
        }

        var order = string.Empty;
        if (orderBy is { Count: > 0 })
        {
            var parts = new List<string>();
            foreach (var o in orderBy)
            {
                var canonical = CanonicalColumn(o.Column);
                if (canonical.Length == 0) return BuiltQuery.Error($"order_by column '{o.Column}' does not exist in {obj.Display}.");
                parts.Add(SqlIdentifier.Quote(canonical) + (o.Desc ? " DESC" : " ASC"));
            }
            order = " ORDER BY " + string.Join(", ", parts);
        }

        var sql = $"SELECT TOP (@n) {select} FROM {obj.Quoted}" +
                  (conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty) + order;

        return new BuiltQuery(sql, parameters, null);
    }

    // ---- get_related_rows ----------------------------------------------------------------------

    public async Task<RelatedRowsResult> GetRelatedRowsAsync(
        string database, string table, string? keyColumn, JsonElement keyValue, int depth, int rowsPerRelation, CancellationToken ct)
    {
        depth = Math.Clamp(depth, 1, 2);
        rowsPerRelation = Math.Clamp(rowsPerRelation, 1, 20);

        RelatedRowsResult Failed(string? message) => new(false, database, table, null, [], 0, false, message);

        var (root, error) = await ResolveObjectAsync(database, table, ct);
        if (root is null) return Failed(error);

        var types = await GetColumnTypesAsync(database, root, ct);
        if (types is null) return Failed("Could not read the columns of the table.");

        if (string.IsNullOrWhiteSpace(keyColumn))
        {
            var pk = await _executor.RunSelectAsync(PrimaryKeySql, database, 10, [new("oid", root.ObjectId)], ct);
            if (!pk.Success) return Failed(pk.ErrorMessage);
            if (pk.Rows.Count != 1) return Failed("The table has no single-column primary key; pass 'key_column'.");
            keyColumn = Str(pk.Rows[0], "column_name");
        }

        var queries = 0;
        var rootResult = await SelectByConditionsAsync(database, root, types, [new LookupFilter(keyColumn, "=", keyValue)], null, null, 1, ct);
        queries++;
        if (!rootResult.Success) return Failed(rootResult.ErrorMessage);
        if (rootResult.Rows.Count == 0) return Failed($"No row in {root.Display} where {keyColumn} matches the given value.");

        var related = new List<RelatedGroup>();
        var budgetReached = false;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fkCache = new Dictionary<int, List<(string Name, ObjectRef Parent, ObjectRef Referenced, List<(string P, string R)> Cols)>>();
        var refCache = new Dictionary<string, ObjectRef?>(StringComparer.OrdinalIgnoreCase);
        var typeCache = new Dictionary<int, Dictionary<string, string>> { [root.ObjectId] = types };

        var queue = new Queue<(ObjectRef Table, Row Row, int Depth)>();
        queue.Enqueue((root, rootResult.Rows[0], 1));

        while (queue.Count > 0 && !budgetReached)
        {
            var (current, row, level) = queue.Dequeue();

            if (!fkCache.TryGetValue(current.ObjectId, out var fks))
            {
                var fkResult = await _executor.RunSelectAsync(ForeignKeysSql, database, 500, [new("oid", current.ObjectId)], ct);
                queries++;
                fks = fkResult.Success ? await ParseForeignKeysAsync(database, fkResult.Rows, refCache, ct) : [];
                fkCache[current.ObjectId] = fks;
            }

            foreach (var fk in fks)
            {
                var outgoing = fk.Parent.ObjectId == current.ObjectId;
                var incoming = fk.Referenced.ObjectId == current.ObjectId;

                foreach (var direction in new[] { (outgoing, "references"), (incoming, "referenced_by") })
                {
                    if (!direction.Item1) continue;

                    var target = direction.Item2 == "references" ? fk.Referenced : fk.Parent;
                    var sourceColumns = direction.Item2 == "references" ? fk.Cols.Select(c => c.P) : fk.Cols.Select(c => c.R);
                    var targetColumns = direction.Item2 == "references" ? fk.Cols.Select(c => c.R) : fk.Cols.Select(c => c.P);

                    var values = sourceColumns.Select(c => row.TryGetValue(c, out var v) ? v : null).ToList();
                    if (values.Any(v => v is null)) continue;

                    var visitKey = $"{target.ObjectId}|{fk.Name}|{string.Join("|", values)}";
                    if (!visited.Add(visitKey)) continue;

                    if (queries >= MaxRelatedQueries) { budgetReached = true; break; }

                    if (!typeCache.TryGetValue(target.ObjectId, out var targetTypes))
                    {
                        targetTypes = await GetColumnTypesAsync(database, target, ct) ?? new();
                        typeCache[target.ObjectId] = targetTypes;
                        queries++;
                    }

                    var filters = targetColumns.Zip(values, (col, val) => new LookupFilter(col, "=", JsonSerializer.SerializeToElement(val))).ToList();
                    var fetched = await SelectByConditionsAsync(database, target, targetTypes, filters, null, null, rowsPerRelation, ct);
                    queries++;
                    if (!fetched.Success || fetched.Rows.Count == 0) continue;

                    related.Add(new RelatedGroup(level, direction.Item2, fk.Name, current.Display, target.Display,
                        fetched.RowCount, fetched.IsTruncated, fetched.Rows));

                    if (level < depth)
                    {
                        foreach (var child in fetched.Rows.Take(2)) queue.Enqueue((target, child, level + 1));
                    }
                }
            }
        }

        return new RelatedRowsResult(true, database, root.Display, rootResult.Rows[0], related, queries, budgetReached);
    }

    private async Task<List<(string Name, ObjectRef Parent, ObjectRef Referenced, List<(string P, string R)> Cols)>> ParseForeignKeysAsync(
        string database, IReadOnlyList<Row> rows, Dictionary<string, ObjectRef?> refCache, CancellationToken ct)
    {
        var list = new List<(string, ObjectRef, ObjectRef, List<(string, string)>)>();
        foreach (var group in rows.GroupBy(r => Str(r, "fk_name")))
        {
            var first = group.First();
            var parent = await ResolveCachedAsync(database, Str(first, "parent_schema"), Str(first, "parent_table"), refCache, ct);
            var referenced = await ResolveCachedAsync(database, Str(first, "ref_schema"), Str(first, "ref_table"), refCache, ct);
            if (parent is null || referenced is null) continue;

            list.Add((group.Key, parent, referenced, group.Select(r => (Str(r, "parent_column"), Str(r, "ref_column"))).ToList()));
        }
        return list;
    }

    private async Task<ObjectRef?> ResolveCachedAsync(string database, string schema, string name, Dictionary<string, ObjectRef?> cache, CancellationToken ct)
    {
        var key = $"{schema}.{name}";
        if (cache.TryGetValue(key, out var cached)) return cached;
        var (obj, _) = await ResolveObjectAsync(database, key, ct);
        cache[key] = obj;
        return obj;
    }

    private async Task<Dictionary<string, string>?> GetColumnTypesAsync(string database, ObjectRef obj, CancellationToken ct)
    {
        var result = await _executor.RunSelectAsync(ColumnTypesSql, database, 1000, [new("oid", obj.ObjectId)], ct);
        return result.Success
            ? result.Rows.ToDictionary(r => Str(r, "column_name"), r => Str(r, "type_name"), StringComparer.OrdinalIgnoreCase)
            : null;
    }

    // ---- explain_select ------------------------------------------------------------------------

    public async Task<ExplainResult> ExplainSelectAsync(string database, string sql, CancellationToken ct)
    {
        var result = await _executor.ExplainAsync(sql, database, ct);
        if (!result.Success)
            return new ExplainResult(false, database, null, [], [], [], result.ErrorMessage);

        var planXml = result.Rows.Count > 0 && result.Columns.Count > 0 ? result.Rows[0][result.Columns[0]]?.ToString() : null;
        if (string.IsNullOrWhiteSpace(planXml))
            return new ExplainResult(false, database, null, [], [], [], "The server returned no plan.");

        try
        {
            return ParsePlan(database, planXml);
        }
        catch (XmlException)
        {
            return new ExplainResult(false, database, null, [], [], [], "The execution plan was too large or malformed to summarise.");
        }
    }

    internal static ExplainResult ParsePlan(string database, string planXml)
    {
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        using var reader = XmlReader.Create(new StringReader(planXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader);

        double? cost = doc.Descendants(ns + "StmtSimple").Select(s => (double?)ParseDouble(s.Attribute("StatementSubTreeCost")?.Value)).FirstOrDefault();

        var operators = doc.Descendants(ns + "RelOp").Select(op => new PlanOperator(
                op.Attribute("PhysicalOp")?.Value ?? "?",
                op.Attribute("LogicalOp")?.Value ?? "?",
                ParseDouble(op.Attribute("EstimateRows")?.Value),
                ParseDouble(op.Attribute("EstimatedTotalSubtreeCost")?.Value),
                op.Descendants(ns + "Object").Select(o => (o.Attribute("Table")?.Value ?? "") + (o.Attribute("Index") is { } ix ? $" ({ix.Value})" : string.Empty)).FirstOrDefault(s => s.Length > 0)))
            .OrderByDescending(o => o.Cost)
            .Take(12)
            .ToList();

        var warnings = new List<string>();
        foreach (var o in operators.Where(o => o.Physical.Contains("Scan", StringComparison.OrdinalIgnoreCase) && !o.Physical.Contains("Seek", StringComparison.OrdinalIgnoreCase)))
            warnings.Add($"{o.Physical} on {o.Object ?? "?"} (~{o.EstimatedRows:0} rows): consider a filter that can use an index.");
        foreach (var w in doc.Descendants(ns + "Warnings").Elements())
            warnings.Add(w.Name.LocalName);

        var missing = doc.Descendants(ns + "MissingIndex").Select(m =>
            $"{m.Attribute("Schema")?.Value}.{m.Attribute("Table")?.Value}: " +
            string.Join("; ", m.Elements(ns + "ColumnGroup").Select(g => g.Attribute("Usage")?.Value + "(" + string.Join(",", g.Elements(ns + "Column").Select(c => c.Attribute("Name")?.Value)) + ")"))).ToList();

        return new ExplainResult(true, database, cost, operators, warnings.Distinct().ToList(), missing);
    }

    // ---- get_object_definition -----------------------------------------------------------------

    public async Task<ObjectDefinitionResult> GetObjectDefinitionAsync(string database, string name, CancellationToken ct)
    {
        if (!SqlIdentifier.TryParseObjectName(name, out var schema, out var objectName, out var error))
            return new ObjectDefinitionResult(false, database, [], error);

        var result = await _executor.RunSelectAsync(
            ObjectDefinitionSql, database, 5, [new("name", objectName), new("schema", schema)], ct, ObjectDefinitionCellChars);

        if (!result.Success) return new ObjectDefinitionResult(false, database, [], result.ErrorMessage);
        if (result.Rows.Count == 0)
            return new ObjectDefinitionResult(false, database, [], $"No view, procedure, function or trigger named '{name}' was found in {database}.");

        var objects = result.Rows.Select(r =>
        {
            var definition = r["definition"]?.ToString();
            return new ObjectDefinition(Str(r, "schema_name"), Str(r, "object_name"), Str(r, "type_desc"), r["modified"]?.ToString(),
                definition ?? "(definition is encrypted or unavailable)", definition?.Contains("[truncated,", StringComparison.Ordinal) == true);
        }).ToList();

        return new ObjectDefinitionResult(true, database, objects);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<(ObjectRef? Obj, string? Error)> ResolveObjectAsync(string database, string table, CancellationToken ct)
    {
        if (!SqlIdentifier.TryParseObjectName(table, out var schema, out var name, out var parseError))
            return (null, parseError);

        var result = await _executor.RunSelectAsync(ResolveObjectSql, database, 5, [new("name", name), new("schema", schema)], ct);
        if (!result.Success) return (null, result.ErrorMessage);
        if (result.Rows.Count == 0) return (null, $"Table or view '{table}' was not found in {database}. Use list_tables to search.");

        var first = result.Rows[0];
        if (result.Rows.Count > 1 && schema is null && !Str(first, "schema_name").Equals("dbo", StringComparison.OrdinalIgnoreCase))
        {
            var options = string.Join(", ", result.Rows.Select(r => $"{Str(r, "schema_name")}.{Str(r, "object_name")}"));
            return (null, $"'{table}' is ambiguous ({options}). Qualify it with the schema.");
        }

        return (new ObjectRef(Str(first, "schema_name"), Str(first, "object_name"), Str(first, "object_type").Trim(), Convert.ToInt32(first["object_id"])), null);
    }

    private static QueryExecutionResult Fail(string database, string? message) => new(
        false, database, 0, false, 0, [], [], message);

    private static string NormalizeOp(string? op) => (op ?? "=").Trim().ToLowerInvariant() switch
    {
        "eq" or "==" or "=" => "=",
        "ne" or "<>" or "!=" => "!=",
        "gt" => ">",
        "gte" or "ge" => ">=",
        "lt" => "<",
        "lte" or "le" => "<=",
        "isnull" or "is_null" => "is_null",
        "isnotnull" or "is_not_null" => "is_not_null",
        var other => other,
    };

    private static bool TryScalar(JsonElement element, out object? value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String: value = element.GetString(); return true;
            case JsonValueKind.True: value = true; return true;
            case JsonValueKind.False: value = false; return true;
            case JsonValueKind.Number:
                value = element.TryGetInt64(out var l) ? l : element.TryGetDecimal(out var d) ? d : element.GetDouble();
                return true;
            default: value = null; return false;
        }
    }

    private static bool IsAnsiType(string type) => type is "varchar" or "char" or "text";

    private static string EscapeLike(string value) => value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    private static string FormatType(Row r)
    {
        var type = Str(r, "type_name");
        var length = Convert.ToInt32(r["max_length"] ?? 0);
        return type switch
        {
            "nvarchar" or "nchar" => $"{type}({(length < 0 ? "max" : (length / 2).ToString())})",
            "varchar" or "char" or "varbinary" or "binary" => $"{type}({(length < 0 ? "max" : length.ToString())})",
            "decimal" or "numeric" => $"{type}({r["precision"]},{r["scale"]})",
            "datetime2" or "time" or "datetimeoffset" => $"{type}({r["scale"]})",
            _ => type,
        };
    }

    private static double ParseDouble(string? text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Str(Row r, string key) => r.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? string.Empty : string.Empty;
    private static long Long(Row r, string key) => r.TryGetValue(key, out var v) && v is not null ? Convert.ToInt64(v) : 0;
    private static bool Bool(Row r, string key) => r.TryGetValue(key, out var v) && v is not null && (v is bool b ? b : Convert.ToInt32(v) != 0);
}
