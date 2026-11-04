using McpSqlServer.Investigation;
using McpSqlServer.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpSqlServer.Mcp;

/// <summary>Composition root of the MCP server (stdio transport). stdout is reserved for the protocol.</summary>
public static class McpHost
{
    // Kept under 1,500 characters (hub convention). Full playbook: docs/INVESTIGATION_PLAYBOOK.md
    public const string Instructions =
        "Read-only SQL Server investigation. Only validated single SELECT statements ever run; nothing can be changed. " +
        "Start with check_connection (also shows write_capable and config_source), then list_databases. " +
        "Workflow: search_context / find_columns to locate where an id or value is stored -> describe_table to learn columns and foreign keys -> " +
        "lookup_rows (no SQL needed) or get_related_rows to see a record and everything connected to it -> get_object_definition to read the procedure/trigger/view that produced it -> " +
        "explain_select when the problem is a slow query. Use run_select only for what the other tools cannot express. " +
        "Results are capped (rows, cell size, response size): filter by key or date instead of reading whole tables, and check 'truncated'. " +
        "Sensitive columns (password, token, secret, ...) are masked as '***'. " +
        "If a tool answers 'not configured', credentials are missing in the hub config (section 'mssql').";

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        // Hub contract: start and list every tool even without credentials; DB tools then answer "not configured".
        var options = ConnectionOptions.FromConfigOrEnvironment();
        Logger.SetActivePassword(options.Password);

        var configErrors = options.Validate();
        if (configErrors.Count > 0)
        {
            Logger.Warn($"SQL Server is not configured yet ({string.Join(" ", configErrors)}) - DB tools will report 'not configured'.");
        }

        Logger.Info("Starting mcp-sqlserver (read-only) over stdio...");

        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        var executor = new ReadOnlyQueryExecutor(options);
        builder.Services
            .AddSingleton(options)
            .AddSingleton(executor)
            .AddSingleton(new InvestigationService(executor))
            .AddSingleton(new SqlServerService());

        builder.Services
            .AddMcpServer(server =>
            {
                server.ServerInfo = new() { Name = "mcp-sqlserver", Version = typeof(McpHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" };
                server.ServerInstructions = Instructions;
            })
            .WithStdioServerTransport()
            .WithTools<SqlTools>();

        using var host = builder.Build();
        await host.RunAsync(cancellationToken);
    }
}
