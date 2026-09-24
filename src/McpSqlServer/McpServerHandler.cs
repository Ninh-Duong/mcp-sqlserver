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
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "test_connection",
                            ["description"] = "Verify connectivity to SQL Server instance with current configuration.",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject()
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "list_databases",
                            ["description"] = "Count and enumerate databases visible to current login on SQL Server.",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject()
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "list_tables",
                            ["description"] = "Count and list tables in a specific database.",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["database"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Target database name to inspect tables."
                                    }
                                },
                                ["required"] = new JsonArray { "database" }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "scan_server_context",
                            ["description"] = "Scan all databases on SQL Server (Tables, Columns, Data Types, PK, FK, Views, Stored Procedures, and SQL logic) and export token-optimized AI Context documentation.",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["server_alias"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Server or environment alias (e.g. DEV, UAT, PROD). Defaults to current configuration."
                                    },
                                    ["database"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Optional single database to scan (targeted fast scan in ~2s). If omitted, scans all accessible databases."
                                    },
                                    ["output_directory"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Directory to export AI Context documentation (default: ./ai-context)."
                                    },
                                    ["include_system"] = new JsonObject
                                    {
                                        ["type"] = "boolean",
                                        ["description"] = "Whether to scan system databases (master, msdb, etc.) (default: false)."
                                    }
                                }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "check_schema_drift",
                            ["description"] = "Check whether databases on SQL Server have newer EF Core migrations or DDL modifications compared to the cached ai-context snapshot.",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["base_directory"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Base directory of ai-context (default: ./ai-context)."
                                    }
                                }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "execute_query",
                            ["description"] = "Execute a safe, read-only SQL query (SELECT) on the database and return results in structured JSON. Automatically blocks modifying commands (INSERT, UPDATE, DELETE, DROP, etc.).",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["query"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "SQL SELECT or WITH ... SELECT statement to execute."
                                    },
                                    ["database"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Target database name to execute query against (defaults to configured initial database)."
                                    },
                                    ["max_rows"] = new JsonObject
                                    {
                                        ["type"] = "integer",
                                        ["description"] = "Maximum number of rows to return (default: 100, max: 1000)."
                                    }
                                },
                                ["required"] = new JsonArray { "query" }
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "search_context",
                            ["description"] = "Instant, token-optimized local search across all databases for tables, columns, procedures, views, functions, and triggers without reading large files.",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["query"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Keyword or name of table, column, procedure, view, function, or trigger to locate."
                                    },
                                    ["target"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Optional search filter: 'all' (default), 'table', 'column', 'routine', 'procedure', 'view', 'function', 'trigger'."
                                    },
                                    ["database"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Optional target database name to restrict search."
                                    },
                                    ["limit"] = new JsonObject
                                    {
                                        ["type"] = "integer",
                                        ["description"] = "Maximum number of matches to return (default: 20, max: 100)."
                                    }
                                },
                                ["required"] = new JsonArray { "query" }
                            }
                        }
                    }
                });

            case "tools/call":
                if (!root.TryGetProperty("params", out var paramsProp))
                {
                    Logger.Error("Invalid MCP request: Missing 'params' in tools/call.");
                    return CreateErrorResponse(idNode, -32602, "Missing 'params' in tools/call");
                }

                var toolName = paramsProp.TryGetProperty("name", out var toolNameProp) ? toolNameProp.GetString() : null;

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
                    var args = paramsProp.TryGetProperty("arguments", out var argsProp) ? argsProp : default;
                    var dbName = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("database", out var dbProp)
                        ? dbProp.GetString()
                        : _options.Database;

                    var tableResult = await _sqlService.ListTablesAsync(_options, dbName ?? _options.Database, cancellationToken);
                    var resultText = JsonSerializer.Serialize(tableResult, new JsonSerializerOptions { WriteIndented = true });
                    return CreateToolResponse(idNode, resultText, isError: !tableResult.Success);
                }
                else if (toolName == "scan_server_context")
                {
                    var args = paramsProp.TryGetProperty("arguments", out var argsProp) ? argsProp : default;
                    var alias = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("server_alias", out var aliasProp)
                        ? aliasProp.GetString()
                        : _options.ServerAlias;

                    var outDir = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("output_directory", out var outDirProp)
                        ? outDirProp.GetString()
                        : "./ai-context";

                    var includeSystem = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("include_system", out var sysProp) && sysProp.GetBoolean();

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

                    var targetDb = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("database", out var tdbProp)
                        ? tdbProp.GetString()
                        : null;

                    var scanResult = await _sqlService.ScanServerAsync(scanOptions, includeSystem: includeSystem, targetDatabase: targetDb, cancellationToken: cancellationToken);
                    var files = await AiContextRenderer.RenderAndExportAsync(scanResult, outDir ?? "./ai-context", cancellationToken);

                    var responseObj = new
                    {
                        server_alias = scanResult.ServerAlias,
                        server_host = scanResult.ServerHost,
                        database_count = scanResult.Databases.Count,
                        files_created_count = files.Count,
                        index_file = Path.Combine(outDir ?? "./ai-context", "INDEX.md"),
                        output_directory = Path.GetFullPath(outDir ?? "./ai-context"),
                        elapsed_ms = scanResult.ElapsedMs
                    };

                    var resultText = JsonSerializer.Serialize(responseObj, new JsonSerializerOptions { WriteIndented = true });
                    return CreateToolResponse(idNode, resultText, isError: false);
                }
                else if (toolName == "check_schema_drift")
                {
                    var args = paramsProp.TryGetProperty("arguments", out var argsProp) ? argsProp : default;
                    var baseDir = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("base_directory", out var bProp)
                        ? bProp.GetString()
                        : "./ai-context";

                    var fullBaseDir = Path.GetFullPath(baseDir ?? "./ai-context");
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
                    var args = paramsProp.TryGetProperty("arguments", out var argsProp) ? argsProp : default;
                    var query = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("query", out var queryProp)
                        ? queryProp.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(query))
                    {
                        Logger.Error("MCP tool 'execute_query' validation failure: Parameter 'query' cannot be empty.");
                        return CreateErrorResponse(idNode, -32602, "Parameter 'query' cannot be empty.");
                    }

                    var dbName = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("database", out var dbProp)
                        ? dbProp.GetString()
                        : _options.Database;

                    var maxRows = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("max_rows", out var maxRowsProp) && maxRowsProp.TryGetInt32(out var mr)
                        ? mr
                        : 100;

                    var queryResult = await _sqlService.ExecuteQueryAsync(_options, query, dbName, maxRows, cancellationToken);
                    var resultText = JsonSerializer.Serialize(queryResult, new JsonSerializerOptions { WriteIndented = true });
                    return CreateToolResponse(idNode, resultText, isError: !queryResult.Success);
                }
                else if (toolName == "search_context")
                {
                    var args = paramsProp.TryGetProperty("arguments", out var argsProp) ? argsProp : default;
                    var query = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("query", out var qProp)
                        ? qProp.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(query))
                    {
                        Logger.Error("MCP tool 'search_context' validation failure: Parameter 'query' cannot be empty.");
                        return CreateErrorResponse(idNode, -32602, "Parameter 'query' cannot be empty.");
                    }

                    var target = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("target", out var targetProp)
                        ? targetProp.GetString()
                        : "all";

                    var database = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("database", out var dbProp)
                        ? dbProp.GetString()
                        : null;

                    var limit = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("limit", out var limProp) && limProp.TryGetInt32(out var l)
                        ? Math.Clamp(l, 1, 100)
                        : 20;

                    var searchResult = await AiContextRenderer.SearchContextAsync("./ai-context", query, database, target, limit, cancellationToken);
                    var resultText = JsonSerializer.Serialize(searchResult, new JsonSerializerOptions { WriteIndented = true });
                    return CreateToolResponse(idNode, resultText, isError: false);
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
