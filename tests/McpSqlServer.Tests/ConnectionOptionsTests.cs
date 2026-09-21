using Microsoft.Data.SqlClient;

namespace McpSqlServer.Tests;

public class ConnectionOptionsTests
{
    [Fact]
    public void Validate_WhenFieldsAreEmpty_ReturnsErrors()
    {
        var options = new ConnectionOptions();
        var errors = options.Validate();

        Assert.Contains(errors, e => e.Contains("Server name"));
        Assert.Contains(errors, e => e.Contains("Username"));
        Assert.Contains(errors, e => e.Contains("Password"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    [InlineData(-1)]
    public void Validate_WhenPortOutOfRange_ReturnsError(int invalidPort)
    {
        var options = new ConnectionOptions
        {
            Server = "localhost",
            Username = "sa",
            Password = "password",
            Port = invalidPort
        };

        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("Port"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void Validate_WhenTimeoutOutOfRange_ReturnsError(int invalidTimeout)
    {
        var options = new ConnectionOptions
        {
            Server = "localhost",
            Username = "sa",
            Password = "password",
            ConnectTimeout = invalidTimeout,
            QueryTimeout = invalidTimeout
        };

        var errors = options.Validate();
        Assert.Contains(errors, e => e.Contains("Connect timeout"));
        Assert.Contains(errors, e => e.Contains("Query timeout"));
    }

    [Fact]
    public void BuildConnectionString_WithSpecialCharsInPassword_EscapesProperly()
    {
        var options = new ConnectionOptions
        {
            Server = "sql.internal",
            Username = "myuser",
            Password = "P@ss;word'\"=special!#$%",
            Port = 1433,
            Database = "testdb",
            Encrypt = true,
            TrustServerCertificate = true
        };

        var connStr = options.BuildConnectionString();
        Assert.NotEmpty(connStr);

        // Verify SqlConnectionStringBuilder can round-trip and parse it safely
        var parsed = new SqlConnectionStringBuilder(connStr);
        Assert.Equal("sql.internal,1433", parsed.DataSource);
        Assert.Equal("myuser", parsed.UserID);
        Assert.Equal("P@ss;word'\"=special!#$%", parsed.Password);
        Assert.Equal("testdb", parsed.InitialCatalog);
        Assert.True(parsed.TrustServerCertificate);
    }

    [Fact]
    public void FromEnvironment_ReadsVariablesCorrectly()
    {
        Environment.SetEnvironmentVariable("MSSQL_SERVER", "env.server.internal");
        Environment.SetEnvironmentVariable("MSSQL_USERNAME", "envuser");
        Environment.SetEnvironmentVariable("MSSQL_PASSWORD", "envpass123");
        Environment.SetEnvironmentVariable("MSSQL_DATABASE", "envdb");
        Environment.SetEnvironmentVariable("MSSQL_PORT", "14333");
        Environment.SetEnvironmentVariable("MSSQL_TRUST_CERTIFICATE", "true");

        try
        {
            var options = ConnectionOptions.FromEnvironment();

            Assert.Equal("env.server.internal", options.Server);
            Assert.Equal("envuser", options.Username);
            Assert.Equal("envpass123", options.Password);
            Assert.Equal("envdb", options.Database);
            Assert.Equal(14333, options.Port);
            Assert.True(options.TrustServerCertificate);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSSQL_SERVER", null);
            Environment.SetEnvironmentVariable("MSSQL_USERNAME", null);
            Environment.SetEnvironmentVariable("MSSQL_PASSWORD", null);
            Environment.SetEnvironmentVariable("MSSQL_DATABASE", null);
            Environment.SetEnvironmentVariable("MSSQL_PORT", null);
            Environment.SetEnvironmentVariable("MSSQL_TRUST_CERTIFICATE", null);
        }
    }

    [Fact]
    public void GetDisplaySummary_DoesNotExposePassword()
    {
        var options = new ConnectionOptions
        {
            Server = "prod-db",
            Username = "admin",
            Password = "SuperSecretPassword123!",
            Database = "CRM"
        };

        var summary = options.GetDisplaySummary();
        Assert.Contains("prod-db", summary);
        Assert.Contains("admin", summary);
        Assert.DoesNotContain("SuperSecretPassword123!", summary);
    }

    [Fact]
    public void TryLoadFromFile_WhenFileExistsAndValid_LoadsOptionsSuccessfully()
    {
        var tempFile = Path.Combine(".", "temp_valid_config.json");
        var json = """
        {
            "Server": "test-server",
            "Username": "test-user",
            "Password": "test-pass-123",
            "Port": 1433,
            "Database": "testdb"
        }
        """;

        try
        {
            File.WriteAllText(tempFile, json);
            var (loaded, options) = ConnectionOptions.TryLoadFromFile(tempFile);

            Assert.True(loaded);
            Assert.NotNull(options);
            Assert.Equal("test-server", options.Server);
            Assert.Equal("test-user", options.Username);
            Assert.Equal("test-pass-123", options.Password);
            Assert.Equal("testdb", options.Database);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void TryLoadFromFile_WhenFileHasEmptyCredentials_ReturnsLoadedFalse()
    {
        var tempFile = Path.Combine(".", "temp_empty_config.json");
        var json = """
        {
            "Server": "",
            "Username": "",
            "Password": ""
        }
        """;

        try
        {
            File.WriteAllText(tempFile, json);
            var (loaded, options) = ConnectionOptions.TryLoadFromFile(tempFile);

            Assert.False(loaded);
            Assert.NotNull(options);
            Assert.False(options.HasRequiredCredentials());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void TryLoadFromFile_WhenFileDoesNotExist_ReturnsLoadedFalse()
    {
        var (loaded, options) = ConnectionOptions.TryLoadFromFile("non_existent_file_123.json");
        Assert.False(loaded);
        Assert.Null(options);
    }
}
