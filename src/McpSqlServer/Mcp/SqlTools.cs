#pragma warning disable IDE1006 // Parameter names are snake_case on purpose: they are the MCP tool argument names (hub standard).

using System.ComponentModel;
using System.Text.Json;
using McpSqlServer.Investigation;
using McpSqlServer.Security;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpSqlServer.Mcp;

/// <summary>
/// The MCP surface. Rules (docs/RULES.md): every tool is ReadOnly = true; no tool touches SqlClient directly;
/// database access goes only through <see cref="InvestigationService"/> / <see cref="SqlServerService"/> -> <see cref="ReadOnlyQueryExecutor"/>.
/// </summary>
[McpServerToolType]
public sealed class SqlTools(
    ConnectionOptions options,
    ReadOnlyQueryExecutor executor,
    InvestigationService investigation,
    SqlServerService catalog)
{
    // ---- server / catalog ----------------------------------------------------------------------

    [McpServerTool(Name = "check_connection", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Check connectivity and report the server version, whether the login could write (write_capable), and where each config group came from (config_source: env|hub|local|default|unset). Call this first.")]
    public async Task<CallToolResult> CheckConnection(CancellationToken cancellationToken)
    {
        var result = await catalog.TestConnectionAsync(options, cancellationToken);
        return ToolResults.From(result, result.Success);
    }

    [McpServerTool(Name = "config_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Report where the SQL login settings come from (groups.mssql_login: env|hub|local|default|unset) without connecting. Names only, never values. Used by mcp-hub.")]
    public CallToolResult ConfigStatus() =>
        ToolResults.From(new { groups = new Dictionary<string, string> { ["mssql_login"] = Configuration.ConfigLoader.LoginSource(options.ConfigSources) } }, true);

    [McpServerTool(Name = "list_databases", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("List the databases this MCP is allowed to query (system databases are excluded unless allowed_databases says otherwise), with state and access.")]
    public async Task<CallToolResult> ListDatabases(CancellationToken cancellationToken)
    {
        var result = await catalog.ListDatabasesAsync(options, cancellationToken);
        return ToolResults.From(result, result.Success);
    }

    [McpServerTool(Name = "list_tables", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("List tables and views of a database with approximate row counts. Filter by schema and/or a name fragment (e.g. 'order' matches %order%).")]
    public async Task<CallToolResult> ListTables(
        [Description("Database name (see list_databases).")] string database,
        [Description("Optional schema filter, e.g. 'dbo'.")] string? schema = null,
        [Description("Optional name fragment or LIKE pattern.")] string? name_like = null,
        [Description("Max items (1-500, default 100).")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database)) return Required("database");
        var result = await investigation.ListTablesAsync(database.Trim(), schema, name_like, limit, cancellationToken);
        return ToolResults.From(result, result.Success);
    }

    [McpServerTool(Name = "describe_table", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Live structure of a table or view: columns (type, nullable, PK, identity, default), foreign keys in both directions, indexes and triggers. Use it to learn how tables relate before querying.")]
    public async Task<CallToolResult> DescribeTable(
        [Description("Database name.")] string database,
        [Description("'Orders' or 'dbo.Orders'.")] string table,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database)) return Required("database");
        if (string.IsNullOrWhiteSpace(table)) return Required("table");
        var result = await investigation.DescribeTableAsync(database.Trim(), table, cancellationToken);
        return ToolResults.From(result, result.Success);
    }

    [McpServerTool(Name = "find_columns", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Find which tables hold a column, e.g. 'OrderId' or 'customer'. Searches one database, or every allowed database when 'database' is omitted. Use it to follow an id from a log or ticket to the tables that store it.")]
    public async Task<CallToolResult> FindColumns(
        [Description("Column name fragment or LIKE pattern.")] string name_like,
        [Description("Optional database; omit to search all allowed databases.")] string? database = null,
        [Description("Max matches (1-200, default 50).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name_like)) return Required("name_like");
        var result = await investigation.FindColumnsAsync(name_like, database, limit, cancellationToken);
        return ToolResults.From(result, result.Success);
    }

    // ---- reading data ---------------------------------------------------------------------------

    [McpServerTool(Name = "lookup_rows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Read rows from one table or view WITHOUT writing SQL. filters is an AND list of {column, op, value}; op is one of = != > >= < <= like contains in is_null is_not_null ('in' takes a value array). Example: filters=[{\"column\":\"OrderId\",\"op\":\"=\",\"value\":123}], order_by=[{\"column\":\"CreatedDate\",\"desc\":true}]. Values are always sent as parameters. The generated SQL is returned in 'sql'.")]
    public async Task<CallToolResult> LookupRows(
        [Description("Database name.")] string database,
        [Description("'Orders' or 'dbo.Orders'.")] string table,
        [Description("Conditions combined with AND.")] LookupFilter[]? filters = null,
        [Description("Sort: [{column, desc}].")] LookupOrder[]? order_by = null,
        [Description("Columns to return; default all.")] string[]? columns = null,
        [Description("Max rows (1-1000, default 20).")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database)) return Required("database");
        if (string.IsNullOrWhiteSpace(table)) return Required("table");
        var result = await investigation.LookupRowsAsync(database.Trim(), table, filters, order_by, columns, limit, cancellationToken);
        return ToolResults.From(result, result.Success);
    }

    [McpServerTool(Name = "get_related_rows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Given one row (by primary key or key_column), follow foreign keys in both directions and return the parent and child rows around it (depth 1 or 2). Use it to see everything connected to a failing record. Capped at 24 queries.")]
    public async Task<CallToolResult> GetRelatedRows(
        [Description("Database name.")] string database,
        [Description("'Orders' or 'dbo.Orders'.")] string table,
        [Description("Value of the key column (string or number).")] JsonElement key_value,
        [Description("Key column; default is the single-column primary key.")] string? key_column = null,
        [Description("1 (default) or 2.")] int depth = 1,
        [Description("Rows fetched per relation (1-20, default 5).")] int rows_per_relation = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database)) return Required("database");
        if (string.IsNullOrWhiteSpace(table)) return Required("table");
        var result = await investigation.GetRelatedRowsAsync(database.Trim(), table, key_column, key_value, depth, rows_per_relation, cancellationToken);
        return ToolResults.From(result, result.Success);
    }

    [McpServerTool(Name = "run_select", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Run ONE read-only SELECT (CTEs allowed). Anything else is rejected before it reaches the server: INSERT/UPDATE/DELETE/TRUNCATE/DROP/EXEC, multiple statements, SELECT INTO, OPENROWSET/OPENQUERY, linked servers, lock hints. Runs in a transaction that is always rolled back, with row/size limits; password/token/secret columns are masked. Prefer lookup_rows when a simple filter is enough.")]
    public async Task<CallToolResult> RunSelect(
        [Description("Database name.")] string database,
        [Description("A single SELECT statement.")] string query,
        [Description("Max rows (1-1000, default from config, usually 100).")] int? max_rows = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database)) return Required("database");
        if (string.IsNullOrWhiteSpace(query)) return Required("query");
        var result = await executor.RunSelectAsync(query, database.Trim(), max_rows, cancellationToken: cancellationToken);
        return ToolResults.From(result, result.Success);
    }

    [McpServerTool(Name = "explain_select", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Return the ESTIMATED execution plan of a SELECT without running it: total cost, most expensive operators, table/index scans, plan warnings and missing-index suggestions. Use it when the issue is a slow query.")]
    public async Task<CallToolResult> ExplainSelect(
        [Description("Database name.")] string database,
        [Description("A single SELECT statement.")] string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database)) return Required("database");
        if (string.IsNullOrWhiteSpace(query)) return Required("query");
        var result = await investigation.ExplainSelectAsync(database.Trim(), query, cancellationToken);
        return ToolResults.From(result, result.Success);
    }

    [McpServerTool(Name = "get_object_definition", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Read the live source of a view, stored procedure, function or trigger (up to 60,000 characters). Use it to understand the logic that produced a bad value.")]
    public async Task<CallToolResult> GetObjectDefinition(
        [Description("Database name.")] string database,
        [Description("'sp_GetOrder' or 'dbo.sp_GetOrder'.")] string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database)) return Required("database");
        if (string.IsNullOrWhiteSpace(name)) return Required("name");
        var result = await investigation.GetObjectDefinitionAsync(database.Trim(), name, cancellationToken);
        return ToolResults.From(result, result.Success);
    }

    // ---- offline snapshot (written by the CLI `scan`; never touches the database) ---------------

    [McpServerTool(Name = "search_context", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Instant search of the OFFLINE schema snapshot (tables, columns, procedures, views, functions, triggers, cross-database dependencies) across all scanned databases. Cheap: try it before querying the live server.")]
    public async Task<CallToolResult> SearchContext(
        [Description("Name or keyword to locate.")] string query,
        [Description("Optional server alias, e.g. DEV.")] string? server_alias = null,
        [Description("Optional database filter.")] string? database = null,
        [Description("all (default) | table | column | routine | procedure | view | function | trigger | dependency")] string target = "all",
        [Description("Max matches (1-50, default 10).")] int limit = 10,
        [Description("Include columns/parameters in each match (default false).")] bool include_details = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Required("query");
        var result = await AiContextRenderer.SearchContextAsync(
            options.ContextDirectory, query, database, target, Math.Clamp(limit, 1, 50), server_alias, include_details, cancellationToken);
        return ToolResults.From(result, result.Success);
    }

    [McpServerTool(Name = "get_object_context", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Exact compact definition of one table, view, procedure, function or trigger from the OFFLINE snapshot, without loading large files.")]
    public async Task<CallToolResult> GetObjectContext(
        [Description("Database name.")] string database,
        [Description("Object name, e.g. 'Orders' or 'dbo.sp_GetOrder'.")] string name,
        [Description("Optional server alias; defaults to the configured one.")] string? server_alias = null,
        [Description("Optional: table | view | procedure | function | trigger | dependency")] string? type = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(database)) return Required("database");
        if (string.IsNullOrWhiteSpace(name)) return Required("name");
        var result = await AiContextRenderer.GetObjectContextAsync(
            options.ContextDirectory, string.IsNullOrWhiteSpace(server_alias) ? options.ServerAlias : server_alias.Trim(), database.Trim(), name, type, cancellationToken);
        return result.Found ? ToolResults.Text(result.Text) : ToolResults.Error(result.Text);
    }

    [McpServerTool(Name = "check_schema_drift", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Compare the live databases with the offline snapshot (new EF migrations or DDL changes). Tells you whether search_context / get_object_context can still be trusted.")]
    public async Task<CallToolResult> CheckSchemaDrift(CancellationToken cancellationToken)
    {
        var cached = await ServerRegistryReader.LoadMigrationsAsync(options.ContextDirectory, options.ServerAlias, cancellationToken);
        var result = await catalog.CheckSchemaDriftAsync(options, cached, cancellationToken);
        return ToolResults.From(result, result.Success);
    }

    private static CallToolResult Required(string parameter) => ToolResults.Error($"Parameter '{parameter}' is required.");
}
