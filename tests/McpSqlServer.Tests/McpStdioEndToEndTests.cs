using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpSqlServer.Tests;

/// <summary>
/// Starts the real server (`McpSqlServer.dll serve`) and talks JSON-RPC over stdio, exactly like mcp-hub does.
/// No database is needed: these tests prove the hub contract and that dangerous input is rejected end to end.
/// </summary>
public sealed class McpStdioEndToEndTests : IAsyncLifetime
{
    private static readonly string[] ExpectedTools =
    [
        "check_connection", "list_databases", "list_tables", "describe_table", "find_columns",
        "lookup_rows", "get_related_rows", "run_select", "explain_select", "get_object_definition",
        "search_context", "get_object_context", "check_schema_drift",
    ];

    private Process? _process;
    private string _workDir = string.Empty;
    private int _nextId = 1;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
        try { if (Directory.Exists(_workDir)) Directory.Delete(_workDir, true); } catch { /* best effort */ }
    }

    private async Task<JsonElement> StartAndInitializeAsync(Dictionary<string, string>? env = null)
    {
        _workDir = Path.Combine(Path.GetTempPath(), "mcp-sqlserver-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);

        var dll = Path.Combine(AppContext.BaseDirectory, "McpSqlServer.dll");
        var info = new ProcessStartInfo("dotnet", $"\"{dll}\" serve")
        {
            WorkingDirectory = _workDir, // no dbconfig.json here
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var name in new[] { "MSSQL_SERVER", "MSSQL_USERNAME", "MSSQL_PASSWORD", "MSSQL_DATABASE", "MSSQL_PORT", "CENTRAL_CONFIG_PATH", "MSSQL_ALLOWED_DATABASES" })
            info.Environment.Remove(name);
        foreach (var (key, value) in env ?? new()) info.Environment[key] = value;

        _process = Process.Start(info)!;
        _process.ErrorDataReceived += (_, _) => { }; // drain stderr so the pipe never fills
        _process.BeginErrorReadLine();

        var init = await RequestAsync("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
        await SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
        return init;
    }

    private async Task SendAsync(object message)
    {
        await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await _process.StandardInput.FlushAsync();
    }

    private async Task<JsonElement> RequestAsync(string method, object? parameters = null)
    {
        var id = _nextId++;
        await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters });
        return await ReadResponseAsync(id);
    }

    private async Task<JsonElement> ReadResponseAsync(int? id)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var line = await _process!.StandardOutput.ReadLineAsync(cts.Token) ?? throw new InvalidOperationException("Server closed stdout.");
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();
            if (id is null || (root.TryGetProperty("id", out var got) && got.ValueKind == JsonValueKind.Number && got.GetInt32() == id))
                return root;
        }
    }

    private async Task<(bool IsError, string Text)> CallToolAsync(string name, object arguments)
    {
        var response = await RequestAsync("tools/call", new { name, arguments });
        var result = response.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
        return (isError, result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    [Fact]
    public async Task Initialize_StartsWithoutCredentials_AndAdvertisesReadOnlyInstructions()
    {
        var init = await StartAndInitializeAsync();

        var result = init.GetProperty("result");
        Assert.Equal("mcp-sqlserver", result.GetProperty("serverInfo").GetProperty("name").GetString());
        var instructions = result.GetProperty("instructions").GetString()!;
        Assert.Contains("Read-only", instructions);
        Assert.True(instructions.Length < 1500, "Server instructions must stay under 1,500 characters (hub convention).");
    }

    [Fact]
    public async Task ToolsList_HasExactlyTheDocumentedTools_AllReadOnly_WithSnakeCaseArguments()
    {
        await StartAndInitializeAsync();

        var tools = (await RequestAsync("tools/list")).GetProperty("result").GetProperty("tools");

        Assert.Equal(ExpectedTools.OrderBy(n => n), tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).OrderBy(n => n));
        foreach (var tool in tools.EnumerateArray())
        {
            var annotations = tool.GetProperty("annotations");
            Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean(), tool.GetProperty("name").GetString() + " must be readOnlyHint=true");
            foreach (var property in tool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject())
                Assert.Matches("^[a-z][a-z0-9_]*$", property.Name);
        }
    }

    [Fact]
    public async Task CheckConnection_WithoutCredentials_ReturnsNotConfigured_WithConfigSource()
    {
        await StartAndInitializeAsync();

        var (isError, text) = await CallToolAsync("check_connection", new { });

        Assert.True(isError);
        Assert.Contains("not configured", text);
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("unset", doc.RootElement.GetProperty("config_source").GetProperty("credentials").GetString());
        Assert.False(doc.RootElement.GetProperty("connected").GetBoolean());
    }

    [Theory]
    [InlineData("DELETE FROM dbo.Orders")]
    [InlineData("SELECT '--'; DROP TABLE dbo.Orders")]
    [InlineData("SELECT * INTO dbo.Copy FROM dbo.Orders")]
    [InlineData("EXEC sp_who")]
    [InlineData("TRUNCATE TABLE dbo.Orders")]
    public async Task RunSelect_RejectsWritesEndToEnd_WithoutReachingTheServer(string query)
    {
        // Server settings point at an address nothing listens on: a rejected query must not even try to connect.
        await StartAndInitializeAsync(new()
        {
            ["MSSQL_SERVER"] = "127.0.0.1", ["MSSQL_PORT"] = "1", ["MSSQL_USERNAME"] = "u", ["MSSQL_PASSWORD"] = "p", ["MSSQL_CONNECT_TIMEOUT"] = "1",
        });

        var (isError, text) = await CallToolAsync("run_select", new { database = "Crm", query });

        Assert.True(isError);
        using var doc = JsonDocument.Parse(text);
        var message = doc.RootElement.GetProperty("error_message").GetString()!;
        Assert.DoesNotContain("connect", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, doc.RootElement.GetProperty("elapsed_ms").GetInt32());
    }

    [Fact]
    public async Task SystemDatabase_IsRefused_ByDefault()
    {
        await StartAndInitializeAsync(new() { ["MSSQL_SERVER"] = "127.0.0.1", ["MSSQL_PORT"] = "1", ["MSSQL_USERNAME"] = "u", ["MSSQL_PASSWORD"] = "p" });

        var (isError, text) = await CallToolAsync("run_select", new { database = "master", query = "SELECT 1" });

        Assert.True(isError);
        Assert.Contains("not allowed", text);
    }

    [Fact]
    public async Task EmptyRequiredParameter_ReturnsToolError_NotAProtocolFailure()
    {
        await StartAndInitializeAsync();

        var (isError, text) = await CallToolAsync("run_select", new { database = "Crm", query = "  " });

        Assert.True(isError);
        Assert.Contains("'query' is required", text);
    }

    [Fact]
    public async Task GetObjectContext_RejectsPathTraversal()
    {
        await StartAndInitializeAsync();

        var (isError, text) = await CallToolAsync("get_object_context", new { database = @"..\..", name = "x" });

        Assert.True(isError);
        Assert.Contains("Invalid", text);
    }

    [Fact]
    public async Task UnknownTool_ReturnsAJsonRpcError()
    {
        await StartAndInitializeAsync();

        var response = await RequestAsync("tools/call", new { name = "execute_query", arguments = new { query = "SELECT 1" } });

        Assert.True(response.TryGetProperty("error", out _), "Removed/unknown tools must produce a JSON-RPC error.");
    }

    [Fact]
    public async Task MalformedRequestWithAnId_GetsAParseError_InsteadOfHanging()
    {
        await StartAndInitializeAsync();

        // Valid envelope, invalid JSON string escape inside the arguments.
        await _process!.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":777,\"method\":\"tools/call\",\"params\":{\"name\":\"run_select\",\"arguments\":{\"query\":\"a\\q\"}}}");
        await _process.StandardInput.FlushAsync();
        var response = await ReadResponseAsync(id: 777);

        Assert.Equal(-32700, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Garbage_OnStdin_DoesNotStopTheServer()
    {
        await StartAndInitializeAsync();

        await _process!.StandardInput.WriteLineAsync("{ this is not json");
        await _process.StandardInput.FlushAsync();

        var (isError, text) = await CallToolAsync("check_connection", new { });

        Assert.True(isError);
        Assert.Contains("not configured", text);
    }

    [Fact]
    public async Task ServerExits_WhenTheHubClosesStdin()
    {
        await StartAndInitializeAsync();

        _process!.StandardInput.Close();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await _process.WaitForExitAsync(cts.Token);

        Assert.True(_process.HasExited);
    }

    [Fact]
    public void EveryToolMethod_IsDeclaredReadOnly()
    {
        var methods = typeof(Mcp.SqlTools).GetMethods()
            .Select(m => (Method: m, Attribute: (McpServerToolAttribute?)Attribute.GetCustomAttribute(m, typeof(McpServerToolAttribute))))
            .Where(x => x.Attribute is not null)
            .ToList();

        Assert.Equal(ExpectedTools.Length, methods.Count);
        Assert.All(methods, x =>
        {
            Assert.True(x.Attribute!.ReadOnly, x.Method.Name + " must declare ReadOnly = true");
            Assert.False(x.Attribute.Destructive, x.Method.Name + " must not be destructive");
            Assert.False(string.IsNullOrWhiteSpace(x.Attribute.Name));
        });
    }
}
