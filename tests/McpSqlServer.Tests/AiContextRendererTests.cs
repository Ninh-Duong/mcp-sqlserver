namespace McpSqlServer.Tests;

public class AiContextRendererTests
{
    [Theory]
    [InlineData("nvarchar", 100, 0, 0, "nvarchar(50)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", -1, 0, 0, "varchar(max)")]
    [InlineData("varchar", 50, 0, 0, "varchar(50)")]
    [InlineData("decimal", 0, 18, 2, "decimal(18,2)")]
    [InlineData("datetime2", 0, 0, 7, "datetime2")]
    [InlineData("datetime2", 0, 0, 3, "datetime2(3)")]
    [InlineData("int", 4, 10, 0, "int")]
    public void FormatDataType_FormatsTypesCorrectly(string typeName, short maxLen, byte prec, byte scale, string expected)
    {
        var result = SqlServerService.FormatDataType(typeName, maxLen, prec, scale);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ColumnSchemaItem_ToCompactString_FormatsAttributesCorrectly()
    {
        var col = new ColumnSchemaItem(
            Name: "CustomerId",
            DataType: "int",
            IsNullable: false,
            IsPrimaryKey: false,
            IsIdentity: false,
            ForeignKeyReference: "dbo.Customers.Id"
        );

        var compact = col.ToCompactString();
        Assert.Equal("- `CustomerId`: int (FK -> dbo.Customers.Id, Not Null)", compact);

        var pkCol = new ColumnSchemaItem(
            Name: "Id",
            DataType: "bigint",
            IsNullable: false,
            IsPrimaryKey: true,
            IsIdentity: true
        );

        var pkCompact = pkCol.ToCompactString();
        Assert.Equal("- `Id`: bigint (PK, Identity, Not Null)", pkCompact);
    }

    [Fact]
    public void RenderCompactSchema_GeneratesReadableMarkdown()
    {
        var cols = new List<ColumnSchemaItem>
        {
            new("Id", "int", false, true, true),
            new("Name", "nvarchar(100)", false, false, false)
        };
        var tables = new List<TableSchemaItem>
        {
            new("dbo", "Users", cols)
        };
        var views = new List<ViewSchemaItem>
        {
            new("dbo", "vw_ActiveUsers", "SELECT * FROM dbo.Users WHERE Active = 1")
        };
        var procs = new List<ProcedureSchemaItem>
        {
            new("dbo", "sp_GetUser", ["@Id int"], "SELECT * FROM dbo.Users WHERE Id = @Id")
        };

        var report = new DatabaseScanReport("TestDb", true, 1, 1, 1, tables, views, procs);
        var markdown = AiContextRenderer.RenderCompactSchema("DEV", report);

        Assert.Contains("# Database Schema: TestDb (Server: DEV)", markdown);
        Assert.Contains("### dbo.Users", markdown);
        Assert.Contains("- `Id`: int (PK, Identity, Not Null)", markdown);
        Assert.Contains("- `Name`: nvarchar(100) (Not Null)", markdown);
        Assert.Contains("vw_ActiveUsers", markdown);
        Assert.Contains("sp_GetUser", markdown);
    }

    [Fact]
    public async Task RenderAndExportAsync_MultiServer_MergesMasterIndex()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1. Scan DEV
            var devResult = new ServerScanResult(
                ServerAlias: "DEV",
                ServerHost: "sql-dev.local",
                ServerVersion: "SQL Server 2022",
                ScannedAt: DateTime.Now,
                ElapsedMs: 150,
                Databases:
                [
                    new DatabaseScanReport(
                        "SalesDb",
                        true,
                        1,
                        1,
                        1,
                        [new TableSchemaItem("dbo", "Orders", [new ColumnSchemaItem("Id", "int", false, true, true)])],
                        [new ViewSchemaItem("dbo", "vw_Orders", "SELECT * FROM dbo.Orders")],
                        [new ProcedureSchemaItem("dbo", "sp_GetOrder", ["@Id int"], "SELECT * FROM dbo.Orders")]
                    )
                ]
            );

            var devFiles = await AiContextRenderer.RenderAndExportAsync(devResult, tempDir);
            Assert.NotEmpty(devFiles);

            var indexPath = Path.Combine(tempDir, "INDEX.md");
            Assert.True(File.Exists(indexPath));
            var indexContent1 = await File.ReadAllTextAsync(indexPath);
            Assert.Contains("**DEV**", indexContent1);
            Assert.Contains("sql-dev.local", indexContent1);

            // Verify view and stored procedure files are created
            var viewFile = Path.Combine(tempDir, "servers", "DEV", "databases", "SalesDb", "views", "dbo.vw_Orders.sql");
            var procFile = Path.Combine(tempDir, "servers", "DEV", "databases", "SalesDb", "procedures", "dbo.sp_GetOrder.sql");
            Assert.True(File.Exists(viewFile));
            Assert.True(File.Exists(procFile));

            // Verify GLOBAL_TABLES_MAP.compact.md
            var globalMapFile = Path.Combine(tempDir, "servers", "DEV", "GLOBAL_TABLES_MAP.compact.md");
            Assert.True(File.Exists(globalMapFile));
            var globalMapContent = await File.ReadAllTextAsync(globalMapFile);
            Assert.Contains("SalesDb.dbo.Orders", globalMapContent);
            Assert.Contains("Id(PK)", globalMapContent);

            // 2. Scan PROD (verify Master Index preserves DEV and adds PROD)
            var prodResult = new ServerScanResult(
                ServerAlias: "PROD",
                ServerHost: "sql-prod.local",
                ServerVersion: "SQL Server 2022",
                ScannedAt: DateTime.Now,
                ElapsedMs: 200,
                Databases:
                [
                    new DatabaseScanReport(
                        "SalesDb_Prod",
                        true,
                        1,
                        0,
                        0,
                        [new TableSchemaItem("dbo", "Orders", [new ColumnSchemaItem("Id", "int", false, true, true)])],
                        [],
                        []
                    )
                ]
            );

            await AiContextRenderer.RenderAndExportAsync(prodResult, tempDir);

            var indexContent2 = await File.ReadAllTextAsync(indexPath);
            Assert.Contains("**DEV**", indexContent2);
            Assert.Contains("**PROD**", indexContent2);
            Assert.Contains("sql-dev.local", indexContent2);
            Assert.Contains("sql-prod.local", indexContent2);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Orders WHERE Status = 'DELETED'", true)]
    [InlineData("SELECT * FROM dbo.Logs WHERE Message LIKE '%drop table%'", true)]
    [InlineData("WITH Cte AS (SELECT 1 AS X) SELECT * FROM Cte", true)]
    [InlineData("/* comment */ SELECT TOP 10 * FROM Users", true)]
    [InlineData("DELETE FROM dbo.Orders WHERE Id = 1", false)]
    [InlineData("SELECT * FROM Users; DROP TABLE Users;", false)]
    [InlineData("INSERT INTO Logs (Msg) VALUES ('test')", false)]
    [InlineData("UPDATE Users SET Active = 0", false)]
    [InlineData("TRUNCATE TABLE Cache", false)]
    [InlineData("", false)]
    public void ValidateReadOnlyQuery_TestsSecurityBoundary(string query, bool expectedValid)
    {
        var (isValid, _) = SqlServerService.ValidateReadOnlyQuery(query);
        Assert.Equal(expectedValid, isValid);
    }

    [Fact]
    public void ColumnSchemaItem_WithDescription_RendersDescription()
    {
        var col = new ColumnSchemaItem(
            Name: "Status",
            DataType: "int",
            IsNullable: false,
            IsPrimaryKey: false,
            IsIdentity: false,
            Description: "1=Active, 2=Suspended, 3=Closed"
        );

        var compact = col.ToCompactString();
        Assert.Contains("Description: 1=Active, 2=Suspended, 3=Closed", compact);
    }
}
