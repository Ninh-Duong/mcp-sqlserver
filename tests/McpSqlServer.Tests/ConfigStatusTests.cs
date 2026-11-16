using System.Text.Json;
using McpSqlServer.Configuration;
using McpSqlServer.Investigation;
using McpSqlServer.Mcp;
using McpSqlServer.Security;
using ModelContextProtocol.Protocol;

namespace McpSqlServer.Tests;

/// <summary>config_status: where the SQL login comes from, without connecting and without values (mcp-hub reads it).</summary>
public class ConfigStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcp-sqlserver-status-" + Guid.NewGuid().ToString("N"));

    public ConfigStatusTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private string Write(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private static Func<string, string?> Env(Dictionary<string, string> values)
        => name => values.TryGetValue(name, out var v) ? v : null;

    private static string CallConfigStatus(ConnectionOptions options)
    {
        var executor = new ReadOnlyQueryExecutor(options);
        var tools = new SqlTools(options, executor, new InvestigationService(executor), new SqlServerService());
        var result = tools.ConfigStatus();

        Assert.False(result.IsError);
        var block = Assert.Single(result.Content);
        return Assert.IsType<TextContentBlock>(block).Text;
    }

    private static string LoginSource(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal(["groups"], root.EnumerateObject().Select(p => p.Name));
        var groups = root.GetProperty("groups");
        Assert.Equal(["mssql_login"], groups.EnumerateObject().Select(p => p.Name));
        return groups.GetProperty("mssql_login").GetString()!;
    }

    [Fact]
    public void Hub_WhenTheHubSectionHoldsTheLogin()
    {
        var hub = Write("hub.json", """{ "mssql": { "server": "hub-srv", "username": "hub-user", "password": "hub-secret-pw" } }""");
        var options = ConfigLoader.Load(Path.Combine(_dir, "none.json"), Env(new()), hub).Options;

        var text = CallConfigStatus(options);

        Assert.Equal("""{"groups":{"mssql_login":"hub"}}""", text);
        Assert.DoesNotContain("hub-secret-pw", text);
        Assert.DoesNotContain("hub-srv", text);
    }

    [Fact]
    public void Local_WhenOnlyTheLocalFileHoldsTheLogin_AndTheHubSectionIsEmpty()
    {
        var local = Write("dbconfig.json", """{ "Server": "local-srv", "Username": "u", "Password": "local-secret-pw" }""");
        var hub = Write("hub.json", """{ "mssql": { "server": "", "username": "", "password": "" } }""");
        var options = ConfigLoader.Load(local, Env(new()), hub).Options;

        var text = CallConfigStatus(options);

        Assert.Equal("local", LoginSource(text));
        Assert.DoesNotContain("local-secret-pw", text);
        Assert.DoesNotContain(_dir.Replace("\\", "\\\\"), text);
    }

    [Fact]
    public void Env_WhenTheEnvironmentHoldsTheCredentials()
    {
        var local = Write("dbconfig.json", """{ "Server": "local-srv" }""");
        var env = new Dictionary<string, string> { ["MSSQL_USERNAME"] = "env-user", ["MSSQL_PASSWORD"] = "env-secret-pw" };
        var options = ConfigLoader.Load(local, Env(env), "").Options;

        var text = CallConfigStatus(options);

        Assert.Equal("env", LoginSource(text));
        Assert.DoesNotContain("env-secret-pw", text);
    }

    [Fact]
    public void Unset_WhenNothingIsConfigured()
    {
        var options = ConfigLoader.Load(Path.Combine(_dir, "none.json"), Env(new()), "").Options;

        Assert.Equal("unset", LoginSource(CallConfigStatus(options)));
    }

    [Fact]
    public void Unset_WhenCredentialsExistButTheServerIsUnset()
    {
        var env = new Dictionary<string, string> { ["MSSQL_USERNAME"] = "u", ["MSSQL_PASSWORD"] = "p" };
        var options = ConfigLoader.Load(Path.Combine(_dir, "none.json"), Env(env), "").Options;

        Assert.Equal("unset", LoginSource(CallConfigStatus(options)));
    }

    [Fact]
    public void Unset_WhenTheServerExistsButCredentialsAreUnset()
    {
        var env = new Dictionary<string, string> { ["MSSQL_SERVER"] = "srv" };
        var options = ConfigLoader.Load(Path.Combine(_dir, "none.json"), Env(env), "").Options;

        Assert.Equal("unset", LoginSource(CallConfigStatus(options)));
    }
}
