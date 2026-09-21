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
        Logger.Info("Khởi động MCP SQL Server qua giao thức STDIO...");

        var options = ConnectionOptions.FromConfigOrEnvironment();
        var validationErrors = options.Validate();
        if (validationErrors.Count > 0)
        {
            Logger.Error("Thiếu thông tin kết nối cho MCP Server:");
            foreach (var err in validationErrors)
            {
                Logger.Error($" - {err}");
            }
            Logger.Error("Vui lòng cấu hình file 'dbconfig.json' hoặc đặt biến môi trường: MSSQL_SERVER, MSSQL_USERNAME, MSSQL_PASSWORD.");
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
                Logger.Error("Lỗi xử lý JSON-RPC MCP", ex);
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
                            ["description"] = "Kiểm tra kết nối tới SQL Server instance theo cấu hình hiện tại.",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject()
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "list_databases",
                            ["description"] = "Đếm và liệt kê danh sách database mà tài khoản đăng nhập hiện tại được phép nhìn thấy trên SQL Server.",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject()
                            }
                        },
                        new JsonObject
                        {
                            ["name"] = "list_tables",
                            ["description"] = "Đếm và liệt kê danh sách table trong một database cụ thể.",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["database"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Tên database cần kiểm tra table."
                                    }
                                },
                                ["required"] = new JsonArray { "database" }
                            }
                        }
                    }
                });

            case "tools/call":
                if (!root.TryGetProperty("params", out var paramsProp))
                {
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
                else
                {
                    return CreateErrorResponse(idNode, -32601, $"Tool '{toolName}' không tồn tại.");
                }

            default:
                if (hasId)
                {
                    return CreateErrorResponse(idNode, -32601, $"Phương thức '{method}' không được hỗ trợ.");
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
