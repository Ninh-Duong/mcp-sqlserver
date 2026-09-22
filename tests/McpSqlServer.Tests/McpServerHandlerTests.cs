using System.Text.Json;

namespace McpSqlServer.Tests;

public class McpServerHandlerTests
{
    private readonly ConnectionOptions _dummyOptions = new()
    {
        Server = "127.0.0.1",
        Username = "dummy",
        Password = "dummypassword",
        Port = 1433
    };

    [Fact]
    public async Task HandleMessageAsync_Initialize_ReturnsProtocolAndServerInfo()
    {
        var handler = new McpServerHandler(_dummyOptions);
        var request = """{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}}""";

        var response = await handler.HandleMessageAsync(request);

        Assert.NotNull(response);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, root.GetProperty("id").GetInt64());

        var result = root.GetProperty("result");
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("mcp-sqlserver", result.GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task HandleMessageAsync_ToolsList_ReturnsTestConnectionAndListDatabases()
    {
        var handler = new McpServerHandler(_dummyOptions);
        var request = """{"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}""";

        var response = await handler.HandleMessageAsync(request);

        Assert.NotNull(response);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("id").GetInt64());
        var tools = root.GetProperty("result").GetProperty("tools");

        Assert.Equal(5, tools.GetArrayLength());

        var toolNames = new List<string>();
        foreach (var tool in tools.EnumerateArray())
        {
            toolNames.Add(tool.GetProperty("name").GetString()!);
        }

        Assert.Contains("test_connection", toolNames);
        Assert.Contains("list_databases", toolNames);
        Assert.Contains("list_tables", toolNames);
        Assert.Contains("scan_server_context", toolNames);
        Assert.Contains("execute_query", toolNames);
    }

    [Fact]
    public async Task HandleMessageAsync_ExecuteQuery_WithForbiddenKeyword_ReturnsError()
    {
        var handler = new McpServerHandler(_dummyOptions);
        var request = """
        {
            "jsonrpc": "2.0",
            "id": 99,
            "method": "tools/call",
            "params": {
                "name": "execute_query",
                "arguments": {
                    "query": "DROP TABLE Users;"
                }
            }
        }
        """;

        var response = await handler.HandleMessageAsync(request);
        Assert.NotNull(response);

        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        var result = root.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task HandleMessageAsync_UnknownTool_ReturnsMethodNotFound()
    {
        var handler = new McpServerHandler(_dummyOptions);
        var request = """{"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "non_existent_tool"}}""";

        var response = await handler.HandleMessageAsync(request);

        Assert.NotNull(response);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out var errorProp));
        Assert.Equal(-32601, errorProp.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task HandleMessageAsync_InitializedNotification_ReturnsNull()
    {
        var handler = new McpServerHandler(_dummyOptions);
        var request = """{"jsonrpc": "2.0", "method": "notifications/initialized"}""";

        var response = await handler.HandleMessageAsync(request);

        Assert.Null(response);
    }

    [Fact]
    public async Task HandleMessageAsync_Ping_ReturnsEmptyResult()
    {
        var handler = new McpServerHandler(_dummyOptions);
        var request = """{"jsonrpc": "2.0", "id": 99, "method": "ping"}""";

        var response = await handler.HandleMessageAsync(request);

        Assert.NotNull(response);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        Assert.Equal(99, root.GetProperty("id").GetInt64());
        Assert.True(root.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task HandleMessageAsync_CallListTables_WithEmptyDb_ReturnsErrorToolResult()
    {
        var handler = new McpServerHandler(_dummyOptions);
        var request = """{"jsonrpc": "2.0", "id": 50, "method": "tools/call", "params": {"name": "list_tables", "arguments": {"database": ""}}}""";

        var response = await handler.HandleMessageAsync(request);

        Assert.NotNull(response);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        Assert.Equal(50, root.GetProperty("id").GetInt64());
        var result = root.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task HandleMessageAsync_ToolsCall_MissingParams_ReturnsInvalidParamsError()
    {
        var handler = new McpServerHandler(_dummyOptions);
        var request = """{"jsonrpc": "2.0", "id": 51, "method": "tools/call"}""";

        var response = await handler.HandleMessageAsync(request);

        Assert.NotNull(response);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out var errorProp));
        Assert.Equal(-32602, errorProp.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task HandleMessageAsync_ExecuteQuery_MissingQuery_ReturnsInvalidParamsError()
    {
        var handler = new McpServerHandler(_dummyOptions);
        var request = """{"jsonrpc": "2.0", "id": 52, "method": "tools/call", "params": {"name": "execute_query", "arguments": {}}}""";

        var response = await handler.HandleMessageAsync(request);

        Assert.NotNull(response);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("error", out var errorProp));
        Assert.Equal(-32602, errorProp.GetProperty("code").GetInt32());
    }
}
