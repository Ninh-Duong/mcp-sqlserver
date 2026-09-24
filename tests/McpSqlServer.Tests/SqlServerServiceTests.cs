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

    [Theory]
    [InlineData("Status", "varchar(20)", false, false, false, null, true)]
    [InlineData("StatusCode", "nvarchar(10)", false, false, false, null, true)]
    [InlineData("State", "int", false, false, false, null, true)]
    [InlineData("LeadType", "nvarchar(50)", false, false, false, null, true)]
    [InlineData("Priority", "int", false, false, false, null, true)]
    [InlineData("Stage", "varchar(30)", false, false, false, null, true)]
    [InlineData("IsActiveFlag", "bit", false, false, false, null, true)]
    [InlineData("CustomerName", "nvarchar(100)", false, false, false, null, false)]
    [InlineData("Description", "nvarchar(max)", false, false, false, null, false)]
    [InlineData("UserId", "int", false, false, false, null, false)]
    [InlineData("StatusId", "int", false, false, false, null, false)]
    [InlineData("Status", "int", true, false, false, null, false)] // PK
    [InlineData("Status", "int", false, true, false, null, false)] // Identity
    [InlineData("Status", "int", false, false, false, "dbo.StatusRef.Id", false)] // FK
    [InlineData("CreatedDate", "datetime2", false, false, false, null, false)]
    public void IsCandidateForSampling_EnforcesSmartHeuristicRules(
        string columnName,
        string dataType,
        bool isPk,
        bool isIdentity,
        bool isNullable,
        string? fkRef,
        bool expectedCandidate)
    {
        var col = new ColumnSchemaItem(
            Name: columnName,
            DataType: dataType,
            IsNullable: isNullable,
            IsPrimaryKey: isPk,
            IsIdentity: isIdentity,
            ForeignKeyReference: fkRef
        );

        var isCandidate = SqlServerService.IsCandidateForSampling(col);
        Assert.Equal(expectedCandidate, isCandidate);
    }

    [Fact]
    public void ColumnSchemaItem_WithSampleValues_FormatsCleanly()
    {
        var col = new ColumnSchemaItem(
            Name: "LeadStatus",
            DataType: "varchar(20)",
            IsNullable: false,
            IsPrimaryKey: false,
            IsIdentity: false,
            SampleValues: ["NEW", "CONTACTED", "QUALIFIED", "LOST"]
        );

        var compact = col.ToCompactString();
        Assert.Contains("Observed sample: ['NEW', 'CONTACTED', 'QUALIFIED', 'LOST'] (may be incomplete)", compact);
    }

    [Fact]
    public void CheckSchemaDrift_WhenEfMigrationsMatch_ButLastModifyDateIsNewer_FlagsDrift()
    {
        var snapshotDate = new DateTime(2026, 9, 20, 10, 0, 0);
        var manualDdlDate = new DateTime(2026, 9, 24, 11, 0, 0);
        var migrationId = "20260920000000_Initial";

        var isDrifted = SqlServerService.EvaluateDatabaseDrift(
            snapshotMigrationId: migrationId,
            currentMigrationId: migrationId,
            snapshotCount: 1,
            currentCount: 1,
            snapshotLastModify: snapshotDate,
            currentLastModify: manualDdlDate,
            out var driftReason
        );

        Assert.True(isDrifted);
        Assert.Contains("DDL Modified", driftReason);
    }

    [Fact]
    public void CheckSchemaDrift_WhenMigrationsAndDdlMatch_ReturnsUpToDate()
    {
        var now = new DateTime(2026, 9, 24, 10, 0, 0);
        var migrationId = "20260924000000_Update";

        var isDrifted = SqlServerService.EvaluateDatabaseDrift(
            snapshotMigrationId: migrationId,
            currentMigrationId: migrationId,
            snapshotCount: 2,
            currentCount: 2,
            snapshotLastModify: now,
            currentLastModify: now,
            out var driftReason
        );

        Assert.False(isDrifted);
        Assert.Equal("Up to date", driftReason);
    }
}
