using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpSqlServer;

public class McpServerHandler
{
    private readonly SqlServerService _sqlService = new();
    private readonly ConnectionOptions _options;

    public McpServerHandler(ConnectionOptions options)
    {
        _options = options;
    }

    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Logger.Info("Starting MCP SQL Server over STDIO protocol...");

        var options = ConnectionOptions.FromConfigOrEnvironment();
        var validationErrors = options.Validate();
        if (validationErrors.Count > 0)
        {
            Logger.Error("Missing connection settings for MCP Server:");
            foreach (var err in validationErrors)
            {
                Logger.Error($" - {err}");
            }
            Logger.Error("Please configure 'dbconfig.json' or set environment variables: MSSQL_SERVER, MSSQL_USERNAME, MSSQL_PASSWORD.");
            Environment.Exit(1);
            return;
        }

        var handler = new McpServerHandler(options);
        await handler.ProcessStdioAsync(cancellationToken);
    }

    public async Task ProcessStdioAsync(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Console.OpenStandardInput());

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break; // EOF from client

            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var responseJson = await HandleMessageAsync(line, cancellationToken);
                if (!string.IsNullOrEmpty(responseJson))
                {
                    // Strictly write JSON-RPC messages to stdout with newline
                    Console.Out.WriteLine(responseJson);
                    await Console.Out.FlushAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error processing MCP JSON-RPC message", ex);
            }
        }
    }

    public async Task<string?> HandleMessageAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(requestJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("method", out var methodProp))
        {
            return null;
        }

        var method = methodProp.GetString();
        var hasId = root.TryGetProperty("id", out var idProp);
        JsonNode? idNode = null;
        if (hasId)
        {
            if (idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt64(out var numId))
                idNode = JsonValue.Create(numId);
            else if (idProp.ValueKind == JsonValueKind.String)
                idNode = JsonValue.Create(idProp.GetString());
        }

        switch (method)
        {
            case "initialize":
                return CreateSuccessResponse(idNode, new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject()
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "mcp-sqlserver",
                        ["version"] = "1.0.0"
                    }
                });

            case "notifications/initialized":
                // MCP notification, no response required
                return null;

            case "ping":
                return CreateSuccessResponse(idNode, new JsonObject());

            case "tools/list":
                return CreateSuccessResponse(idNode, new JsonObject
                {
                    ["tools"] = McpToolDefinitions.GetToolDefinitions()
                });

            case "tools/call":
                if (!root.TryGetProperty("params", out var paramsProp))
                {
                    Logger.Error("Invalid MCP request: Missing 'params' in tools/call.");
                    return CreateErrorResponse(idNode, -32602, "Missing 'params' in tools/call");
                }

                var toolName = paramsProp.TryGetProperty("name", out var toolNameProp) ? toolNameProp.GetString() : null;
                var args = paramsProp.TryGetProperty("arguments", out var argsProp) ? argsProp : default;

                if (toolName == "test_connection")
                {
                    var testResult = await _sqlService.TestConnectionAsync(_options, cancellationToken);
                    var resultText = JsonSerializer.Serialize(testResult, new JsonSerializerOptions { WriteIndented = true });
                    return CreateToolResponse(idNode, resultText, isError: !testResult.Success);
                }
                else if (toolName == "list_databases")
                {
                    var listResult = await _sqlService.ListDatabasesAsync(_options, cancellationToken);
                    var resultText = JsonSerializer.Serialize(listResult, new JsonSerializerOptions { WriteIndented = true });
                    return CreateToolResponse(idNode, resultText, isError: !listResult.Success);
                }
                else if (toolName == "list_tables")
                {
                    var dbName = args.GetString("database", _options.Database) ?? _options.Database;
                    var tableResult = await _sqlService.ListTablesAsync(_options, dbName, cancellationToken);
                    var resultText = JsonSerializer.Serialize(tableResult, new JsonSerializerOptions { WriteIndented = true });
                    return CreateToolResponse(idNode, resultText, isError: !tableResult.Success);
                }
                else if (toolName == "scan_server_context")
                {
                    var alias = args.GetString("server_alias", _options.ServerAlias);
                    var outDir = args.GetString("output_directory", "./ai-context") ?? "./ai-context";
                    var includeSystem = args.GetBool("include_system", false);
                    var resume = args.GetBool("resume", false);
                    var targetDb = args.GetString("database");

                    var scanOptions = new ConnectionOptions
                    {
                        ServerAlias = string.IsNullOrWhiteSpace(alias) ? "DEV" : alias.Trim().ToUpperInvariant(),
                        Server = _options.Server,
                        Username = _options.Username,
                        Password = _options.Password,
                        Port = _options.Port,
                        Database = _options.Database,
                        Encrypt = _options.Encrypt,
                        TrustServerCertificate = _options.TrustServerCertificate,
                        ConnectTimeout = _options.ConnectTimeout,
                        QueryTimeout = _options.QueryTimeout
                    };

                    var scanResult = await _sqlService.ScanServerAsync(scanOptions, includeSystem: includeSystem, targetDatabase: targetDb, cancellationToken: cancellationToken, outputDirectory: outDir, resume: resume);
                    var files = await AiContextRenderer.RenderAndExportAsync(scanResult, outDir, cancellationToken);
                    var failed = scanResult.Databases.Where(db => !db.Success).Select(db => new { database = db.DatabaseName, error = db.ErrorMessage }).ToArray();

                    var responseObj = new
                    {
                        server_alias = scanResult.ServerAlias,
                        server_host = scanResult.ServerHost,
                        database_count = scanResult.Databases.Count,
                        files_created_count = files.Count,
                        index_file = Path.Combine(outDir, "INDEX.md"),
                        output_directory = Path.GetFullPath(outDir),
                        elapsed_ms = scanResult.ElapsedMs,
                        status = failed.Length == 0 ? "complete" : "partial",
                        failed_databases = failed,
                        resume_hint = failed.Length == 0 ? null : "Run scan_server_context again with resume=true and the same output_directory."
                    };

                    var resultText = JsonSerializer.Serialize(responseObj, new JsonSerializerOptions { WriteIndented = true });
                    return CreateToolResponse(idNode, resultText, isError: failed.Length > 0);
                }
                else if (toolName == "check_schema_drift")
                {
                    var baseDir = args.GetString("base_directory", "./ai-context") ?? "./ai-context";
                    var fullBaseDir = Path.GetFullPath(baseDir);
                    var registryPath = Path.Combine(fullBaseDir, "servers_registry.json");
                    IReadOnlyDictionary<string, DatabaseMigrationStatus>? cachedMigrations = null;

                    if (File.Exists(registryPath))
                    {
                        try
                        {
                            var json = await File.ReadAllTextAsync(registryPath, cancellationToken);
                            var items = JsonSerializer.Deserialize<List<ServerRegistryItem>>(json);
                            var serverItem = items?.FirstOrDefault(i => i.ServerAlias.Equals(_options.ServerAlias, StringComparison.OrdinalIgnoreCase))
                                             ?? items?.FirstOrDefault();
                            cachedMigrations = serverItem?.Migrations;
                        }
                        catch { }
                    }

                    var driftResult = await _sqlService.CheckSchemaDriftAsync(_options, cachedMigrations, cancellationToken);
                    var resultText = JsonSerializer.Serialize(driftResult, new JsonSerializerOptions { WriteIndented = true });
                    return CreateToolResponse(idNode, resultText, isError: !driftResult.Success);
                }
                else if (toolName == "execute_query")
                {
                    var query = args.GetString("query");
                    if (string.IsNullOrWhiteSpace(query))
                    {
                        Logger.Error("MCP tool 'execute_query' validation failure: Parameter 'query' cannot be empty.");
                        return CreateErrorResponse(idNode, -32602, "Parameter 'query' cannot be empty.");
                    }

                    var dbName = args.GetString("database", _options.Database);
                    var maxRows = args.GetInt32("max_rows", 100);

                    var queryResult = await _sqlService.ExecuteQueryAsync(_options, query, dbName, maxRows, cancellationToken);
                    var resultText = JsonSerializer.Serialize(queryResult, new JsonSerializerOptions { WriteIndented = true });
                    return CreateToolResponse(idNode, resultText, isError: !queryResult.Success);
                }
                else if (toolName == "search_context")
                {
                    var query = args.GetString("query");
                    if (string.IsNullOrWhiteSpace(query))
                    {
                        Logger.Error("MCP tool 'search_context' validation failure: Parameter 'query' cannot be empty.");
                        return CreateErrorResponse(idNode, -32602, "Parameter 'query' cannot be empty.");
                    }

                    var serverAlias = args.GetString("server_alias");
                    var target = args.GetString("target", "all");
                    var database = args.GetString("database");
                    var limit = Math.Clamp(args.GetInt32("limit", 10), 1, 50);
                    var includeDetails = args.GetBool("include_details", false);

                    var searchResult = await AiContextRenderer.SearchContextAsync("./ai-context", query, database, target, limit, serverAlias, includeDetails, cancellationToken);
                    var resultText = JsonSerializer.Serialize(searchResult, new JsonSerializerOptions { WriteIndented = true });
                    return CreateToolResponse(idNode, resultText, isError: false);
                }
                else if (toolName == "get_object_context")
                {
                    var db = args.GetString("database", _options.Database);
                    var name = args.GetString("name");
                    if (string.IsNullOrWhiteSpace(db) || string.IsNullOrWhiteSpace(name))
                    {
                        Logger.Error("MCP tool 'get_object_context' validation failure: 'database' and 'name' are required.");
                        return CreateErrorResponse(idNode, -32602, "Parameters 'database' and 'name' are required.");
                    }

                    var serverAlias = args.GetString("server_alias", _options.ServerAlias);
                    var type = args.GetString("type");

                    var contextResult = await AiContextRenderer.GetObjectContextAsync("./ai-context", serverAlias ?? _options.ServerAlias, db, name, type, cancellationToken);
                    return CreateToolResponse(idNode, contextResult, isError: false);
                }
                else
                {
                    Logger.Error($"MCP tool not found: '{toolName}'");
                    return CreateErrorResponse(idNode, -32601, $"Tool '{toolName}' not found.");
                }

            default:
                if (hasId)
                {
                    Logger.Error($"MCP method not supported: '{method}'");
                    return CreateErrorResponse(idNode, -32601, $"Method '{method}' not supported.");
                }
                return null;
        }
    }

    private static string CreateSuccessResponse(JsonNode? id, JsonNode result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result
        };
        return response.ToJsonString();
    }

    private static string CreateToolResponse(JsonNode? id, string textContent, bool isError)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = textContent
                    }
                },
                ["isError"] = isError
            }
        };
        return response.ToJsonString();
    }

    private static string CreateErrorResponse(JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        return response.ToJsonString();
    }
}
