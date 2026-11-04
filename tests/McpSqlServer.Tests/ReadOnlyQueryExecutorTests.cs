using McpSqlServer.Security;
using Microsoft.Data.SqlClient;

namespace McpSqlServer.Tests;

/// <summary>Runs only when a real SQL Server is configured through MSSQL_SERVER / MSSQL_USERNAME / MSSQL_PASSWORD.</summary>
public sealed class FactIfSqlServerAttribute : FactAttribute
{
    public FactIfSqlServerAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSSQL_SERVER")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSSQL_USERNAME")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSSQL_PASSWORD")))
        {
            Skip = "Integration test: set MSSQL_SERVER, MSSQL_USERNAME and MSSQL_PASSWORD to run against a real server.";
        }
    }
}

public class ReadOnlyQueryExecutorTests
{
    // An address that can never be reached: any test using it proves the code returned BEFORE connecting.
    private static ConnectionOptions Unreachable(Action<ConnectionOptions>? tweak = null)
    {
        var options = new ConnectionOptions
        {
            Server = "127.0.0.1",
            Port = 1,
            Username = "u",
            Password = "p",
            Database = "AppDb",
            ConnectTimeout = 1,
        };
        tweak?.Invoke(options);
        return options;
    }

    [Fact]
    public async Task NotConfigured_ReturnsNotConfiguredError()
    {
        var executor = new ReadOnlyQueryExecutor(new ConnectionOptions());

        var result = await executor.RunSelectAsync("SELECT 1", "AppDb");

        Assert.False(result.Success);
        Assert.Contains("not configured", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DELETE FROM dbo.Orders")]
    [InlineData("SELECT '--'; DROP TABLE dbo.Orders")]
    [InlineData("SELECT * INTO dbo.Copy FROM dbo.Orders")]
    [InlineData("SELECT 1; SELECT 2")]
    public async Task ValidatorRejection_NeverReachesTheServer(string sql)
    {
        var executor = new ReadOnlyQueryExecutor(Unreachable());

        var result = await executor.RunSelectAsync(sql, "AppDb");

        Assert.False(result.Success);
        // A connection attempt would surface as a timeout / cannot-connect error instead.
        Assert.DoesNotContain("connect", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.ElapsedMs);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("msdb")]
    [InlineData("tempdb")]
    [InlineData("rdsadmin")]
    public async Task SystemDatabases_AreBlockedByDefault(string database)
    {
        var executor = new ReadOnlyQueryExecutor(Unreachable());

        var result = await executor.RunSelectAsync("SELECT 1", database);

        Assert.False(result.Success);
        Assert.Contains("not allowed", result.ErrorMessage);
    }

    [Fact]
    public async Task AllowList_RestrictsTargetDatabase()
    {
        var executor = new ReadOnlyQueryExecutor(Unreachable(o => o.AllowedDatabases = ["Crm"]));

        var other = await executor.RunSelectAsync("SELECT 1", "Billing");

        Assert.False(other.Success);
        Assert.Contains("not allowed", other.ErrorMessage);
    }

    [Fact]
    public async Task AllowList_RestrictsCrossDatabaseReferencesInsideTheQuery()
    {
        var executor = new ReadOnlyQueryExecutor(Unreachable(o => o.AllowedDatabases = ["Crm"]));

        var result = await executor.RunSelectAsync("SELECT * FROM Billing.dbo.Invoices", "Crm");

        Assert.False(result.Success);
        Assert.Contains("Billing", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyDatabaseName_FallsBackToConfiguredDatabaseAndStillValidates(string database)
    {
        var executor = new ReadOnlyQueryExecutor(Unreachable(o => o.Database = "master"));

        var result = await executor.RunSelectAsync("SELECT 1", database);

        Assert.False(result.Success);
        Assert.Contains("not allowed", result.ErrorMessage);
    }

    [Fact]
    public void ConvertValue_TruncatesLongStrings_AndHexEncodesBinary()
    {
        var longText = new string('x', ReadOnlyQueryExecutor.MaxCellChars + 50);

        var text = Assert.IsType<string>(ReadOnlyQueryExecutor.ConvertValue(longText));
        var bytes = Assert.IsType<string>(ReadOnlyQueryExecutor.ConvertValue(new byte[] { 0xAB, 0xCD }));

        Assert.Contains("truncated", text);
        Assert.True(text.Length < longText.Length);
        Assert.Equal("0xABCD", bytes);
        Assert.Null(ReadOnlyQueryExecutor.ConvertValue(null));
    }

    [Fact]
    public void ConnectionString_IsReadOnlyIntentAndNamed()
    {
        var parsed = new SqlConnectionStringBuilder(Unreachable().BuildConnectionString());

        Assert.Equal(ApplicationIntent.ReadOnly, parsed.ApplicationIntent);
        Assert.Equal(ConnectionOptions.ApplicationName, parsed.ApplicationName);
    }

    // ---- Integration (skipped without a server) -------------------------------------------------

    private static ConnectionOptions FromEnv() => ConnectionOptions.FromEnvironment();

    [FactIfSqlServer]
    public async Task Integration_RunsInsideATransactionThatIsRolledBack()
    {
        var executor = new ReadOnlyQueryExecutor(FromEnv());

        var result = await executor.RunCatalogSelectAsync("SELECT @@TRANCOUNT AS tran_count");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, Convert.ToInt32(result.Rows[0]["tran_count"]));
    }

    [FactIfSqlServer]
    public async Task Integration_UsesReadUncommittedByDefault_AndNamedApplication()
    {
        var executor = new ReadOnlyQueryExecutor(FromEnv());

        var result = await executor.RunCatalogSelectAsync(
            "SELECT transaction_isolation_level AS iso, APP_NAME() AS app FROM sys.dm_exec_sessions WHERE session_id = @@SPID");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, Convert.ToInt32(result.Rows[0]["iso"])); // 1 = READ UNCOMMITTED
        Assert.Equal(ConnectionOptions.ApplicationName, result.Rows[0]["app"]);
    }

    [FactIfSqlServer]
    public async Task Integration_RowCap_IsEnforcedAndReportedAsTruncated()
    {
        var executor = new ReadOnlyQueryExecutor(FromEnv());

        var result = await executor.RunCatalogSelectAsync(
            "SELECT TOP 50 object_id FROM sys.all_objects", maxRows: 5);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(5, result.RowCount);
        Assert.True(result.IsTruncated);
    }

    [FactIfSqlServer]
    public async Task Integration_ParameterAndDuplicateColumnNames_AreHandled()
    {
        var executor = new ReadOnlyQueryExecutor(FromEnv());

        var result = await executor.RunCatalogSelectAsync(
            "SELECT @v AS value, @v AS value, 1",
            parameters: [new QueryParameter("v", "hello")]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["value", "value_2", "column_3"], result.Columns);
        Assert.Equal("hello", result.Rows[0]["value"]);
    }
}
