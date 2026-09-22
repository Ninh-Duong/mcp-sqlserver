namespace McpSqlServer.Tests;

public class SqlServerServiceTests
{
    [Fact]
    public void DatabaseItem_SystemVsUserClassification_WorksCorrectly()
    {
        var master = new DatabaseItem(1, "master", "ONLINE", true, true);
        var tempdb = new DatabaseItem(2, "tempdb", "ONLINE", true, true);
        var model = new DatabaseItem(3, "model", "ONLINE", true, true);
        var msdb = new DatabaseItem(4, "msdb", "ONLINE", true, true);
        var appDb = new DatabaseItem(5, "CustomerData", "ONLINE", true, false);
        var reportingDb = new DatabaseItem(6, "Reporting", "OFFLINE", null, false);

        Assert.True(master.IsSystem);
        Assert.True(tempdb.IsSystem);
        Assert.True(model.IsSystem);
        Assert.True(msdb.IsSystem);
        Assert.False(appDb.IsSystem);
        Assert.False(reportingDb.IsSystem);

        Assert.Equal("ONLINE", appDb.State);
        Assert.Equal("OFFLINE", reportingDb.State);
        Assert.Null(reportingDb.HasAccess);
    }

    [Fact]
    public void DatabaseListingResult_CountsComputedConsistently()
    {
        var list = new List<DatabaseItem>
        {
            new(1, "master", "ONLINE", true, true),
            new(2, "tempdb", "ONLINE", true, true),
            new(3, "model", "ONLINE", true, true),
            new(4, "msdb", "ONLINE", true, true),
            new(5, "CRM", "ONLINE", true, false),
            new(6, "Sales", "ONLINE", true, false),
            new(7, "Archive", "OFFLINE", false, false)
        };

        var result = new DatabaseListingResult(
            Success: true,
            VisibleCount: list.Count,
            SystemCount: list.Count(d => d.IsSystem),
            OtherCount: list.Count(d => !d.IsSystem),
            Databases: list
        );

        Assert.True(result.Success);
        Assert.Equal(7, result.VisibleCount);
        Assert.Equal(4, result.SystemCount);
        Assert.Equal(3, result.OtherCount);
        Assert.Equal("visible_to_current_login", result.Scope);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ConnectionCheckResult_WhenFailed_IncludesErrorMessage()
    {
        var result = new ConnectionCheckResult(
            Success: false,
            ElapsedMs: 120,
            ErrorMessage: "Login failed. Please verify your Username and Password."
        );

        Assert.False(result.Success);
        Assert.Equal(120, result.ElapsedMs);
        Assert.Contains("Login failed", result.ErrorMessage);
        Assert.Null(result.ServerVersion);
    }

    [Fact]
    public void TableListingResult_ComputesCountAndStoresTablesCorrectly()
    {
        var tables = new List<TableItem>
        {
            new("dbo", "Users"),
            new("dbo", "Orders"),
            new("sales", "Invoices")
        };

        var result = new TableListingResult(
            Success: true,
            Database: "AppDb",
            TableCount: tables.Count,
            Tables: tables
        );

        Assert.True(result.Success);
        Assert.Equal("AppDb", result.Database);
        Assert.Equal(3, result.TableCount);
        Assert.Equal(3, result.Tables.Count);
        Assert.Equal("dbo", result.Tables[0].Schema);
        Assert.Equal("Users", result.Tables[0].Name);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void TableListingResult_WhenEmpty_ReturnsZeroCount()
    {
        var result = new TableListingResult(
            Success: true,
            Database: "EmptyDb",
            TableCount: 0,
            Tables: Array.Empty<TableItem>()
        );

        Assert.True(result.Success);
        Assert.Equal(0, result.TableCount);
        Assert.Empty(result.Tables);
    }
}
