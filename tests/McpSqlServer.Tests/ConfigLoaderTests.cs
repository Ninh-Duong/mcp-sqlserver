using McpSqlServer.Configuration;

namespace McpSqlServer.Tests;

public class ConfigLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcp-sqlserver-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfigLoaderTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void NothingConfigured_StillLoads_AndReportsUnset()
    {
        var result = ConfigLoader.Load(Path.Combine(_dir, "missing.json"), Env(new()), centralConfigPath: "");

        Assert.False(result.Options.HasRequiredCredentials());
        Assert.Equal("unset", result.Options.ConfigSources["server"]);
        Assert.Equal("unset", result.Options.ConfigSources["credentials"]);
        Assert.Equal("default", result.Options.ConfigSources["limits"]);
        Assert.Equal(100, result.Options.MaxRows);
        Assert.Equal("read_uncommitted", result.Options.Isolation);
    }

    [Fact]
    public void Priority_EnvBeatsHubBeatsLocal()
    {
        var local = Write("local.json", """{ "Server": "local-srv", "Username": "local-user", "Password": "local-pw", "Database": "LocalDb" }""");
        var hub = Write("hub.json", """{ "mssql": { "server": "hub-srv", "username": "hub-user", "password": "hub-pw" } }""");
        var env = new Dictionary<string, string> { ["MSSQL_SERVER"] = "env-srv" };

        var result = ConfigLoader.Load(local, Env(env), hub);

        Assert.Equal("env-srv", result.Options.Server);       // env wins
        Assert.Equal("hub-user", result.Options.Username);    // hub beats local
        Assert.Equal("LocalDb", result.Options.Database);     // only local has it
        Assert.Equal("env", result.Options.ConfigSources["server"]);
        Assert.Equal("hub", result.Options.ConfigSources["credentials"]);
    }

    [Fact]
    public void EmptyString_CountsAsUnset_SoLowerLayerWins()
    {
        var local = Write("local.json", """{ "Server": "local-srv", "Username": "u", "Password": "p" }""");
        var hub = Write("hub.json", """{ "mssql": { "server": "", "username": "", "password": "" } }""");

        var result = ConfigLoader.Load(local, Env(new()), hub);

        Assert.Equal("local-srv", result.Options.Server);
        Assert.Equal("local", result.Options.ConfigSources["server"]);
    }

    [Fact]
    public void HubSection_AcceptsSnakeCaseKeys_AndLists()
    {
        var hub = Write("hub.json", """
        { "mssql": {
            "server": "s", "username": "u", "password": "p", "port": 14330,
            "trust_server_certificate": true, "query_timeout_seconds": 45,
            "allowed_databases": ["Crm", "Billing"], "isolation": "READ_COMMITTED", "max_rows": 5000
        } }
        """);

        var o = ConfigLoader.Load(Path.Combine(_dir, "none.json"), Env(new()), hub).Options;

        Assert.Equal(14330, o.Port);
        Assert.True(o.TrustServerCertificate);
        Assert.Equal(45, o.QueryTimeout);
        Assert.Equal(["Crm", "Billing"], o.AllowedDatabases);
        Assert.Equal("read_committed", o.Isolation);
        Assert.Equal(ConnectionOptions.HardMaxRows, o.MaxRows); // clamped
    }

    [Fact]
    public void MissingHubFile_IsAWarning_NotAFailure_AndFallsBackToLocal()
    {
        var local = Write("local.json", """{ "Server": "s", "Username": "u", "Password": "p" }""");

        var result = ConfigLoader.Load(local, Env(new()), Path.Combine(_dir, "no-such-hub.json"));

        Assert.True(result.Options.HasRequiredCredentials());
        Assert.Contains(result.Warnings, w => w.Contains("not found"));
    }

    [Fact]
    public void HubFileWithoutSection_FallsBackWithWarning()
    {
        var hub = Write("hub.json", """{ "somethingElse": { "x": 1 } }""");
        var local = Write("local.json", """{ "Server": "s", "Username": "u", "Password": "p" }""");

        var result = ConfigLoader.Load(local, Env(new()), hub);

        Assert.True(result.Options.HasRequiredCredentials());
        Assert.Contains(result.Warnings, w => w.Contains("section"));
    }

    [Fact]
    public void InvalidNumbers_AreIgnoredWithWarning()
    {
        var env = new Dictionary<string, string> { ["MSSQL_PORT"] = "abc", ["MSSQL_TRUST_CERTIFICATE"] = "maybe" };

        var result = ConfigLoader.Load(Path.Combine(_dir, "none.json"), Env(env), "");

        Assert.Equal(1433, result.Options.Port);
        Assert.False(result.Options.TrustServerCertificate);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void LegacyEnvName_TrustCertificate_StillWorks()
    {
        var env = new Dictionary<string, string> { ["MSSQL_TRUST_CERTIFICATE"] = "true" };

        Assert.True(ConfigLoader.Load(Path.Combine(_dir, "none.json"), Env(env), "").Options.TrustServerCertificate);
    }

    [Fact]
    public void ConfigSources_NeverContainValues()
    {
        var env = new Dictionary<string, string> { ["MSSQL_SERVER"] = "secret-host", ["MSSQL_USERNAME"] = "secret-user", ["MSSQL_PASSWORD"] = "secret-pw" };

        var sources = ConfigLoader.Load(Path.Combine(_dir, "none.json"), Env(env), "").Options.ConfigSources;
        var flattened = string.Join("|", sources.Select(kv => kv.Key + "=" + kv.Value));

        Assert.DoesNotContain("secret", flattened);
        Assert.All(sources.Values, v => Assert.Contains(v, new[] { "env", "hub", "local", "default", "unset" }));
    }

    [Fact]
    public void BadJson_IsWarning_NotException()
    {
        var local = Write("local.json", "{ not json");

        var result = ConfigLoader.Load(local, Env(new()), "");

        Assert.False(result.Options.HasRequiredCredentials());
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void IsDatabaseAllowed_DefaultsBlockSystemDatabases_AndAllowListIsExclusive()
    {
        var open = new ConnectionOptions();
        Assert.False(open.IsDatabaseAllowed("master"));
        Assert.False(open.IsDatabaseAllowed("RDSADMIN"));
        Assert.True(open.IsDatabaseAllowed("CRM_Main"));
        Assert.False(open.IsDatabaseAllowed(" "));

        var listed = new ConnectionOptions { AllowedDatabases = ["Crm"] };
        Assert.True(listed.IsDatabaseAllowed("crm"));
        Assert.False(listed.IsDatabaseAllowed("Billing"));
    }
}
