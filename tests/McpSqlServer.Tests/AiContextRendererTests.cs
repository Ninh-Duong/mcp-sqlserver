namespace McpSqlServer.Tests;

public class AiContextRendererTests
{
    [Fact]
    public async Task FailedScan_PreservesPriorCrossDatabaseDependencySection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcp_scan_" + Guid.NewGuid().ToString("N"));
        try
        {
            var success = new DatabaseScanReport("One", true, 0, 0, 0, [], [], [],
                CrossDbDependencies: [new CrossDbDependencyItem("dbo.View", "Two", "dbo.Table")]);
            await AiContextRenderer.RenderAndExportAsync(new ServerScanResult("DEV", "sql.local", "SQL", DateTime.Now, 1, [success]), directory);
            var failed = new DatabaseScanReport("One", false, 0, 0, 0, [], [], [], "timeout");

            await AiContextRenderer.RenderAndExportAsync(new ServerScanResult("DEV", "sql.local", "SQL", DateTime.Now, 1, [failed]), directory);

            var dbDep = await File.ReadAllTextAsync(Path.Combine(directory, "servers", "DEV", "databases", "One", "dependencies.compact.md"));
            Assert.Contains("## References **`Two`**", dbDep);
            Assert.Contains("dbo.View", dbDep);

            var crossDbGraph = await File.ReadAllTextAsync(Path.Combine(directory, "servers", "DEV", "CROSS_DB_GRAPH.compact.md"));
            Assert.Contains("`One`", crossDbGraph);
            Assert.Contains("`Two`", crossDbGraph);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            }
        }
    }

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

            // Verify catalog.db
            var catalogDbFile = Path.Combine(tempDir, "catalog.db");
            Assert.True(File.Exists(catalogDbFile));

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

    [Fact]
    public void ColumnSchemaItem_WithDefaultValue_RendersDefault()
    {
        var col = new ColumnSchemaItem(
            Name: "CreatedAt",
            DataType: "datetime2",
            IsNullable: false,
            IsPrimaryKey: false,
            IsIdentity: false,
            DefaultValue: "(getutcdate())"
        );

        var compact = col.ToCompactString();
        Assert.Contains("Default: (getutcdate())", compact);
    }

    [Fact]
    public void IndexSchemaItem_ToCompactString_FormatsCorrectly()
    {
        var idx = new IndexSchemaItem("IX_User_Email", true, false, "NONCLUSTERED", ["Email"], "IsDeleted = 0");
        var compact = idx.ToCompactString();
        Assert.Equal("IX_User_Email: [Email] (UNIQUE, WHERE IsDeleted = 0)", compact);
    }

    [Fact]
    public void IndexSchemaItem_WithIncludedColumns_FormatsCorrectly()
    {
        var idx = new IndexSchemaItem(
            Name: "IX_Orders_CustomerId",
            IsUnique: false,
            IsPrimaryKey: false,
            TypeDesc: "NONCLUSTERED",
            KeyColumns: new[] { "CustomerId", "OrderDate" },
            IncludedColumns: new[] { "TotalAmount", "Status" },
            FilterDefinition: "[Status] <> 'DELETED'"
        );

        var compact = idx.ToCompactString();
        Assert.Equal("IX_Orders_CustomerId: [Key: CustomerId, OrderDate | Inc: TotalAmount, Status] (WHERE [Status] <> 'DELETED')", compact);
    }

    [Fact]
    public void RenderCompactSchema_WhenRowCountUnavailable_OutputsExplicitStatsUnavailable()
    {
        var table = new TableSchemaItem("dbo", "Metrics", new[] { new ColumnSchemaItem("Id", "int", false, true, false) }, ApproxRowCount: null);
        var report = new DatabaseScanReport("TestDb", true, 1, 0, 0, new[] { table }, Array.Empty<ViewSchemaItem>(), Array.Empty<ProcedureSchemaItem>());
        var md = AiContextRenderer.RenderCompactSchema("DEV", report);

        Assert.Contains("### dbo.Metrics (Stats: unavailable)", md);
    }

    [Fact]
    public void RenderCompactSchema_RendersFunctionsTriggersIndexesAndConstraints()
    {
        var cols = new List<ColumnSchemaItem>
        {
            new("Id", "int", false, true, true),
            new("Email", "nvarchar(100)", false, false, false, DefaultValue: "('')")
        };
        var indexes = new List<IndexSchemaItem>
        {
            new("UQ_User_Email", true, false, "NONCLUSTERED", ["Email"])
        };
        var checkConstraints = new List<CheckConstraintItem>
        {
            new("CK_User_Email", "([Email]<>'')")
        };
        var triggers = new List<TriggerSchemaItem>
        {
            new("dbo", "trg_User_Audit", "dbo", "Users", "AFTER INSERT, UPDATE", false, "CREATE TRIGGER trg_User_Audit ...")
        };

        var tables = new List<TableSchemaItem>
        {
            new("dbo", "Users", cols, indexes, triggers, checkConstraints)
        };
        var views = new List<ViewSchemaItem>();
        var procs = new List<ProcedureSchemaItem>();
        var funcs = new List<FunctionSchemaItem>
        {
            new("dbo", "fn_GetActiveUsers", "TABLE", ["@TenantId int"], "CREATE FUNCTION ...")
        };

        var report = new DatabaseScanReport(
            DatabaseName: "TestDb",
            Success: true,
            TableCount: 1,
            ViewCount: 0,
            ProcedureCount: 0,
            Tables: tables,
            Views: views,
            Procedures: procs,
            FunctionCount: 1,
            TriggerCount: 1,
            Functions: funcs,
            Triggers: triggers
        );

        var markdown = AiContextRenderer.RenderCompactSchema("DEV", report);

        Assert.Contains("Indexes: UQ_User_Email: [Email] (UNIQUE)", markdown);
        Assert.Contains("Check Constraints: CK_User_Email: ([Email]<>'')", markdown);
        Assert.Contains("Triggers: trg_User_Audit (AFTER INSERT, UPDATE)", markdown);
        Assert.Contains("Default: ('')", markdown);
        Assert.Contains("## 4. Functions Summary", markdown);
        Assert.Contains("fn_GetActiveUsers", markdown);
        Assert.Contains("## 5. Triggers Summary", markdown);
        Assert.Contains("trg_User_Audit", markdown);
    }

    [Fact]
    public async Task SearchContextAsync_FindsTablesColumnsAndRoutines()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_search_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var scanResult = new ServerScanResult(
                ServerAlias: "DEV",
                ServerHost: "sql-dev.local",
                ServerVersion: "SQL Server 2022",
                ScannedAt: DateTime.Now,
                ElapsedMs: 100,
                Databases:
                [
                    new DatabaseScanReport(
                        DatabaseName: "CrmDb",
                        Success: true,
                        TableCount: 1,
                        ViewCount: 0,
                        ProcedureCount: 1,
                        Tables:
                        [
                            new TableSchemaItem("dbo", "Customers", [
                                new ColumnSchemaItem("Id", "int", false, true, true),
                                new ColumnSchemaItem("Phone", "varchar(20)", true, false, false)
                            ])
                        ],
                        Views: [],
                        Procedures:
                        [
                            new ProcedureSchemaItem("dbo", "sp_FindCustomer", ["@Phone varchar(20)"], "SELECT 1")
                        ]
                    )
                ]
            );

            await AiContextRenderer.RenderAndExportAsync(scanResult, tempDir);

            // Test search by table name
            var tableSearch = await AiContextRenderer.SearchContextAsync(tempDir, "Customer", target: "table");
            Assert.True(tableSearch.TotalMatches > 0);
            Assert.Contains(tableSearch.Matches, m => m.Type == "table" && m.Name.Contains("Customer"));

            // Test search by column name
            var colSearch = await AiContextRenderer.SearchContextAsync(tempDir, "Phone", target: "column");
            Assert.True(colSearch.TotalMatches > 0);
            Assert.Contains(colSearch.Matches, m => m.Type == "column" && m.Name.Contains("Phone"));

            // Test search by procedure name
            var procSearch = await AiContextRenderer.SearchContextAsync(tempDir, "FindCustomer", target: "routine");
            Assert.True(procSearch.TotalMatches > 0);
            Assert.Contains(procSearch.Matches, m => m.Type == "procedure" && m.Name.Contains("FindCustomer"));

            // Verify TABLES_ROUTER.compact.md was created
            var routerPath = Path.Combine(tempDir, "servers", "DEV", "TABLES_ROUTER.compact.md");
            Assert.True(File.Exists(routerPath));
            var routerContent = await File.ReadAllTextAsync(routerPath);
            Assert.Contains("Customers", routerContent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void RenderCompactSchema_RendersVolumeStatsAndSampleValues()
    {
        var col = new ColumnSchemaItem("Status", "int", false, false, false, SampleValues: ["1", "2", "3"]);
        var table = new TableSchemaItem("dbo", "Orders", [col], ApproxRowCount: 1_250_000, ApproxSizeMb: 450.2);

        var report = new DatabaseScanReport(
            DatabaseName: "SalesDb",
            Success: true,
            TableCount: 1,
            ViewCount: 0,
            ProcedureCount: 0,
            Tables: [table],
            Views: [],
            Procedures: [],
            LatestMigrationId: "20260924_Init",
            MigrationCount: 5
        );

        var markdown = AiContextRenderer.RenderCompactSchema("DEV", report);

        Assert.Contains("dbo.Orders (~1.3M rows | 450.2 MB) [HIGH VOLUME]", markdown);
        Assert.Contains("Observed sample: ['1', '2', '3'] (may be incomplete)", markdown);
        Assert.Contains("Migration: Latest = `20260924_Init` (5 total)", markdown);
    }

    [Fact]
    public async Task RenderAndExportAsync_GeneratesCrossDbDependenciesMap()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_crossdb_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var deps = new List<CrossDbDependencyItem>
            {
                new("dbo.GetLeadOverview", "CRM_Tenant", "dbo.Customer"),
                new("dbo.vw_ActiveLeads", "CRM_Master", "dbo.Tenant")
            };

            var scanResult = new ServerScanResult(
                ServerAlias: "DEV",
                ServerHost: "sql-dev.local",
                ServerVersion: "SQL Server 2022",
                ScannedAt: DateTime.Now,
                ElapsedMs: 120,
                Databases:
                [
                    new DatabaseScanReport(
                        DatabaseName: "CRM_Lead",
                        Success: true,
                        TableCount: 1,
                        ViewCount: 1,
                        ProcedureCount: 1,
                        Tables: [new TableSchemaItem("dbo", "Lead", [new ColumnSchemaItem("Id", "int", false, true, true)])],
                        Views: [new ViewSchemaItem("dbo", "vw_ActiveLeads", "SELECT 1")],
                        Procedures: [new ProcedureSchemaItem("dbo", "GetLeadOverview", [], "SELECT 1")],
                        CrossDbDependencies: deps
                    )
                ]
            );

            await AiContextRenderer.RenderAndExportAsync(scanResult, tempDir);

            var dbDepPath = Path.Combine(tempDir, "servers", "DEV", "databases", "CRM_Lead", "dependencies.compact.md");
            Assert.True(File.Exists(dbDepPath));
            var content = await File.ReadAllTextAsync(dbDepPath);
            Assert.Contains("## References **`CRM_Tenant`**", content);
            Assert.Contains("## References **`CRM_Master`**", content);
            Assert.Contains("`CRM_Lead.dbo.GetLeadOverview` -> `CRM_Tenant.dbo.Customer`", content);

            var crossDbGraphPath = Path.Combine(tempDir, "servers", "DEV", "CROSS_DB_GRAPH.compact.md");
            Assert.True(File.Exists(crossDbGraphPath));
            var graphContent = await File.ReadAllTextAsync(crossDbGraphPath);
            Assert.Contains("`CRM_Tenant`", graphContent);
            Assert.Contains("`CRM_Master`", graphContent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task RenderAndExportAsync_SingleDbScan_DeletesOrphanRoutineFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_clean_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var dbDir = Path.Combine(tempDir, "servers", "DEV", "databases", "AppDb");
            var procsDir = Path.Combine(dbDir, "procedures");
            Directory.CreateDirectory(procsDir);
            var orphanFile = Path.Combine(procsDir, "dbo.OldDeletedProc.sql");
            await File.WriteAllTextAsync(orphanFile, "-- Old proc");

            var scanResult = new ServerScanResult(
                ServerAlias: "DEV",
                ServerHost: "localhost",
                ServerVersion: "SQL Server 2022",
                ScannedAt: DateTime.Now,
                ElapsedMs: 50,
                Databases:
                [
                    new DatabaseScanReport(
                        "AppDb",
                        true,
                        0, 0, 1,
                        Array.Empty<TableSchemaItem>(),
                        Array.Empty<ViewSchemaItem>(),
                        [new ProcedureSchemaItem("dbo", "NewProc", Array.Empty<string>(), "SELECT 1")]
                    )
                ]
            );

            await AiContextRenderer.RenderAndExportAsync(scanResult, tempDir);

            Assert.False(File.Exists(orphanFile), "Orphan routine file should have been deleted");
            Assert.True(File.Exists(Path.Combine(procsDir, "dbo.NewProc.sql")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task RenderAndExportAsync_SingleDbScan_PreservesExistingCrossDbDependenciesOfOtherDbs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_crossdb_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var serverDir = Path.Combine(tempDir, "servers", "DEV");
            Directory.CreateDirectory(serverDir);
            var existingDbDir = Path.Combine(serverDir, "databases", "ExistingDb");
            Directory.CreateDirectory(existingDbDir);
            await File.WriteAllTextAsync(Path.Combine(existingDbDir, "dependencies.compact.md"),
                "# Database Dependencies: ExistingDb\n\n## References **`OtherDb`**\n- `ExistingDb.dbo.sp_Call` -> `OtherDb.dbo.Target`\n");

            var scanResult = new ServerScanResult(
                ServerAlias: "DEV",
                ServerHost: "localhost",
                ServerVersion: "SQL Server 2022",
                ScannedAt: DateTime.Now,
                ElapsedMs: 50,
                Databases:
                [
                    new DatabaseScanReport(
                        "NewDb",
                        true,
                        0, 0, 0,
                        Array.Empty<TableSchemaItem>(),
                        Array.Empty<ViewSchemaItem>(),
                        Array.Empty<ProcedureSchemaItem>(),
                        CrossDbDependencies: [new CrossDbDependencyItem("dbo.Proc1", "TargetDb", "dbo.TargetTable")]
                    )
                ]
            );

            await AiContextRenderer.RenderAndExportAsync(scanResult, tempDir);

            var crossDbGraphPath = Path.Combine(serverDir, "CROSS_DB_GRAPH.compact.md");
            var content = await File.ReadAllTextAsync(crossDbGraphPath);
            Assert.Contains("`ExistingDb`", content);
            Assert.Contains("`OtherDb`", content);
            Assert.Contains("`NewDb`", content);
            Assert.Contains("`TargetDb`", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task SearchContextAsync_WithServerFilter_ReturnsOnlyMatchingServer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_search_srv_" + Guid.NewGuid().ToString("N"));
        try
        {
            var resDev = new ServerScanResult("DEV", "localhost", "2022", DateTime.Now, 10, [
                new DatabaseScanReport("Db1", true, 1, 0, 0, [new TableSchemaItem("dbo", "Users", [])], [], [])
            ]);
            var resProd = new ServerScanResult("PROD", "localhost", "2022", DateTime.Now, 10, [
                new DatabaseScanReport("Db1", true, 1, 0, 0, [new TableSchemaItem("dbo", "Users", [])], [], [])
            ]);
            await AiContextRenderer.RenderAndExportAsync(resDev, tempDir);
            await AiContextRenderer.RenderAndExportAsync(resProd, tempDir);

            var searchResult = await AiContextRenderer.SearchContextAsync(tempDir, "Users", serverAlias: "DEV");

            Assert.NotEmpty(searchResult.Matches);
            Assert.All(searchResult.Matches, m => Assert.Equal("DEV", m.ServerAlias));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task SearchContextAsync_DefaultIncludeDetailsFalse_ReturnsConciseDetails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_search_concise_" + Guid.NewGuid().ToString("N"));
        try
        {
            var res = new ServerScanResult("DEV", "localhost", "2022", DateTime.Now, 10, [
                new DatabaseScanReport("Db1", true, 1, 0, 0, [
                    new TableSchemaItem("dbo", "Users", [
                        new ColumnSchemaItem("Id", "int", false, true, true),
                        new ColumnSchemaItem("Email", "nvarchar(100)", false, false, false)
                    ])
                ], [], [])
            ]);
            await AiContextRenderer.RenderAndExportAsync(res, tempDir);

            var searchResult = await AiContextRenderer.SearchContextAsync(tempDir, "Users", includeDetails: false);

            var tableMatch = searchResult.Matches.First(m => m.Type == "table");
            Assert.Empty(tableMatch.Details);

            var detailedResult = await AiContextRenderer.SearchContextAsync(tempDir, "Users", includeDetails: true);
            var detailedMatch = detailedResult.Matches.First(m => m.Type == "table");
            Assert.NotEmpty(detailedMatch.Details);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task GetObjectContextAsync_Table_ReturnsOnlyTargetTableMarkdown()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_get_obj_" + Guid.NewGuid().ToString("N"));
        try
        {
            var res = new ServerScanResult("DEV", "localhost", "2022", DateTime.Now, 10, [
                new DatabaseScanReport("Db1", true, 2, 0, 0, [
                    new TableSchemaItem("dbo", "Users", [new ColumnSchemaItem("Id", "int", false, true, true)]),
                    new TableSchemaItem("dbo", "Orders", [new ColumnSchemaItem("OrderId", "bigint", false, true, true)])
                ], [], [])
            ]);
            await AiContextRenderer.RenderAndExportAsync(res, tempDir);

            var tableMd = await AiContextRenderer.GetObjectContextAsync(tempDir, "DEV", "Db1", "Orders", "table");

            Assert.Contains("### dbo.Orders", tableMd);
            Assert.Contains("OrderId", tableMd);
            Assert.DoesNotContain("### dbo.Users", tableMd);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task GetObjectContextAsync_Procedure_ReturnsSqlContent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_get_proc_" + Guid.NewGuid().ToString("N"));
        try
        {
            var res = new ServerScanResult("DEV", "localhost", "2022", DateTime.Now, 10, [
                new DatabaseScanReport("Db1", true, 0, 0, 1, [], [], [
                    new ProcedureSchemaItem("dbo", "sp_GetOrder", ["@Id int"], "SELECT * FROM Orders WHERE Id = @Id")
                ])
            ]);
            await AiContextRenderer.RenderAndExportAsync(res, tempDir);

            var sql = await AiContextRenderer.GetObjectContextAsync(tempDir, "DEV", "Db1", "sp_GetOrder", "procedure");

            Assert.Contains("sp_GetOrder", sql);
            Assert.Contains("SELECT * FROM Orders", sql);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task RenderAndExportAsync_LargeTableCount_PartitionsIntoIndividualTableFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_mod_tbl_" + Guid.NewGuid().ToString("N"));
        try
        {
            var tables = Enumerable.Range(1, 55).Select(i =>
                new TableSchemaItem("dbo", $"Table{i}", [new ColumnSchemaItem("Id", "int", false, true, true)], ApproxRowCount: 1000 * i)
            ).ToList();

            var scanResult = new ServerScanResult("DEV", "localhost", "SQL 2022", DateTime.Now, 10, [
                new DatabaseScanReport("LargeDb", true, tables.Count, 0, 0, tables, [], [])
            ]);

            await AiContextRenderer.RenderAndExportAsync(scanResult, tempDir);

            // 1. Verify tables directory was created
            var tablesDir = Path.Combine(tempDir, "servers", "DEV", "databases", "LargeDb", "tables");
            Assert.True(Directory.Exists(tablesDir));

            // 2. Verify individual table compact files exist
            var table1File = Path.Combine(tablesDir, "dbo.Table1.compact.md");
            Assert.True(File.Exists(table1File));
            var table1Content = await File.ReadAllTextAsync(table1File);
            Assert.Contains("# Table: dbo.Table1", table1Content);
            Assert.Contains("`Id`: int (PK, Identity, Not Null)", table1Content);

            // 3. Verify schema.compact.md has modular notice and table skeleton
            var schemaFile = Path.Combine(tempDir, "servers", "DEV", "databases", "LargeDb", "schema.compact.md");
            var schemaContent = await File.ReadAllTextAsync(schemaFile);
            Assert.Contains("Modular Schema Notice", schemaContent);
            Assert.Contains("`dbo.Table1`", schemaContent);
            Assert.Contains("./tables/dbo.Table1.compact.md", schemaContent);

            // 4. Verify GetObjectContextAsync resolves table directly from tables directory
            var table50Md = await AiContextRenderer.GetObjectContextAsync(tempDir, "DEV", "LargeDb", "Table50", "table");
            Assert.Contains("### dbo.Table50", table50Md);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task RenderAndExportAsync_ExportsLocalDependenciesAndCrossDbGraph()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_graph_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var deps = new List<CrossDbDependencyItem>
            {
                new("dbo.sp_ProcessOrder", "CRM_Billing", "dbo.Invoices"),
                new("dbo.vw_UserOrders", "CRM_Identity", "dbo.Users")
            };

            var scanResult = new ServerScanResult("DEV", "localhost", "SQL 2022", DateTime.Now, 10, [
                new DatabaseScanReport("CRM_Order", true, 0, 1, 1, [], [], [], CrossDbDependencies: deps)
            ]);

            await AiContextRenderer.RenderAndExportAsync(scanResult, tempDir);

            // 1. Verify local dependencies.compact.md
            var localDepFile = Path.Combine(tempDir, "servers", "DEV", "databases", "CRM_Order", "dependencies.compact.md");
            Assert.True(File.Exists(localDepFile));
            var localContent = await File.ReadAllTextAsync(localDepFile);
            Assert.Contains("## References **`CRM_Billing`**", localContent);
            Assert.Contains("## References **`CRM_Identity`**", localContent);

            // 2. Verify server-level CROSS_DB_GRAPH.compact.md
            var graphFile = Path.Combine(tempDir, "servers", "DEV", "CROSS_DB_GRAPH.compact.md");
            Assert.True(File.Exists(graphFile));
            var graphContent = await File.ReadAllTextAsync(graphFile);
            Assert.Contains("| **`CRM_Order`** | `CRM_Billing` | 1 |", graphContent);
            Assert.Contains("| **`CRM_Order`** | `CRM_Identity` | 1 |", graphContent);

            // 3. Verify GetObjectContextAsync finds dependency from local dependencies.compact.md
            var depCtx = await AiContextRenderer.GetObjectContextAsync(tempDir, "DEV", "CRM_Order", "sp_ProcessOrder", "dependency");
            Assert.Contains("sp_ProcessOrder", depCtx);
            Assert.Contains("CRM_Billing", depCtx);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task RenderAndExportAsync_FiltersSystemDatabasesAndHashTablesFromRouter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_router_filter_" + Guid.NewGuid().ToString("N"));
        try
        {
            var scanResult = new ServerScanResult("DEV", "localhost", "SQL 2022", DateTime.Now, 10, [
                new DatabaseScanReport("CRM_Core", true, 2, 0, 0, [
                    new TableSchemaItem("dbo", "ValidTable", [new ColumnSchemaItem("Id", "int", false, true, true)]),
                    new TableSchemaItem("dbo", "#TempHash", [new ColumnSchemaItem("Id", "int", false, true, true)])
                ], [], []),
                new DatabaseScanReport("tempdb", true, 1, 0, 0, [
                    new TableSchemaItem("dbo", "#A072FA71", [new ColumnSchemaItem("Id", "int", false, true, true)])
                ], [], []),
                new DatabaseScanReport("master", true, 1, 0, 0, [
                    new TableSchemaItem("dbo", "spt_fallback", [new ColumnSchemaItem("Id", "int", false, true, true)])
                ], [], [])
            ]);

            await AiContextRenderer.RenderAndExportAsync(scanResult, tempDir);

            var routerPath = Path.Combine(tempDir, "servers", "DEV", "TABLES_ROUTER.compact.md");
            var routerContent = await File.ReadAllTextAsync(routerPath);

            Assert.Contains("- **`CRM_Core`**: ValidTable", routerContent);
            Assert.DoesNotContain("#TempHash", routerContent);
            Assert.DoesNotContain("`tempdb`", routerContent);
            Assert.DoesNotContain("`master`", routerContent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task SearchContextAsync_FindsCrossDbDependencies()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_search_dep_" + Guid.NewGuid().ToString("N"));
        try
        {
            var deps = new List<CrossDbDependencyItem>
            {
                new("dbo.sp_ProcessInvoice", "CRM_Finance", "dbo.Accounts")
            };

            var scanResult = new ServerScanResult("DEV", "localhost", "SQL 2022", DateTime.Now, 10, [
                new DatabaseScanReport("CRM_Billing", true, 0, 0, 1, [], [], [], CrossDbDependencies: deps)
            ]);

            await AiContextRenderer.RenderAndExportAsync(scanResult, tempDir);

            var searchResult = await AiContextRenderer.SearchContextAsync(tempDir, "Accounts", target: "dependency");
            Assert.True(searchResult.TotalMatches > 0);
            Assert.Equal("dependency", searchResult.Matches[0].Type);
            Assert.Contains("CRM_Finance.dbo.Accounts", searchResult.Matches[0].Name);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
