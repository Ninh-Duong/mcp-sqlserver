using System.Text.Json;
using McpSqlServer.Investigation;
using McpSqlServer.Security;

namespace McpSqlServer.Tests;

public class InvestigationServiceTests
{
    private static readonly InvestigationService.ObjectRef Orders = new("dbo", "Orders", "U", 42);

    private static readonly Dictionary<string, string> OrderColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Id"] = "int",
        ["Status"] = "varchar",
        ["Note"] = "nvarchar",
        ["CreatedDate"] = "datetime2",
    };

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static InvestigationService.BuiltQuery Build(
        IReadOnlyList<LookupFilter>? filters = null, IReadOnlyList<LookupOrder>? order = null,
        IReadOnlyList<string>? columns = null, int limit = 10)
        => InvestigationService.BuildSelect(Orders, OrderColumns, filters ?? [], order, columns, limit);

    // ---- every fixed catalog query must itself pass the read-only validator ---------------------

    [Fact]
    public void AllFixedCatalogQueries_PassTheSelectOnlyValidator()
    {
        foreach (var sql in InvestigationService.AllFixedSql)
        {
            var result = SelectOnlyValidator.Validate(sql);
            Assert.True(result.IsValid, $"{result.Error}\n{sql}");
        }
    }

    // ---- lookup_rows SQL builder ---------------------------------------------------------------

    [Fact]
    public void Build_SimpleEquality_UsesParameters_AndPassesTheValidator()
    {
        var built = Build([new LookupFilter("id", "=", J("42"))]);

        Assert.Null(built.ErrorMessage);
        Assert.Equal("SELECT TOP (@n) * FROM [dbo].[Orders] WHERE [Id] = @f0", built.Sql);
        Assert.Contains(built.Parameters, p => p.Name == "f0" && Equals(p.Value, 42L));
        Assert.True(SelectOnlyValidator.Validate(built.Sql).IsValid);
    }

    [Fact]
    public void Build_ValueInjection_StaysInsideTheParameter()
    {
        var evil = "x'; DROP TABLE dbo.Orders; --";

        var built = Build([new LookupFilter("Note", "=", J(JsonSerializer.Serialize(evil)))]);

        Assert.DoesNotContain("DROP", built.Sql);
        Assert.Contains(built.Parameters, p => p.Name == "f0" && (string?)p.Value == evil);
    }

    [Theory]
    [InlineData("Id]; DROP TABLE dbo.Orders; --")]
    [InlineData("Id] = 1 OR [1")]
    [InlineData("NoSuchColumn")]
    [InlineData("")]
    public void Build_UnknownOrMaliciousColumnName_IsRejectedBeforeAnySql(string column)
    {
        var built = Build([new LookupFilter(column, "=", J("1"))]);

        Assert.Null(built.Sql);
        Assert.NotNull(built.ErrorMessage);
    }

    [Fact]
    public void Build_UnknownOrderByAndSelectColumns_AreRejected()
    {
        Assert.Null(Build(order: [new LookupOrder("Id]; DROP TABLE x;--")]).Sql);
        Assert.Null(Build(columns: ["Id", "Nope"]).Sql);
    }

    [Fact]
    public void Build_OrderByAndColumns_AreQuotedFromTheCatalogNames()
    {
        var built = Build(order: [new LookupOrder("createddate", Desc: true)], columns: ["id", "STATUS"]);

        Assert.Equal("SELECT TOP (@n) [Id], [Status] FROM [dbo].[Orders] ORDER BY [CreatedDate] DESC", built.Sql);
    }

    [Theory]
    [InlineData("=", "=")]
    [InlineData("eq", "=")]
    [InlineData("!=", "<>")]
    [InlineData("ne", "<>")]
    [InlineData(">", ">")]
    [InlineData("gte", ">=")]
    [InlineData("<", "<")]
    [InlineData("lte", "<=")]
    [InlineData("like", "LIKE")]
    public void Build_ComparisonOperators_AreMapped(string op, string expected)
    {
        var built = Build([new LookupFilter("Id", op, J("1"))]);

        Assert.Contains($"[Id] {expected} @f0", built.Sql);
        Assert.True(SelectOnlyValidator.Validate(built.Sql).IsValid);
    }

    [Fact]
    public void Build_Contains_EscapesWildcards()
    {
        var built = Build([new LookupFilter("Note", "contains", J("\"50%_off[x]\""))]);

        Assert.Contains("[Note] LIKE @f0", built.Sql);
        Assert.Equal("%50[%][_]off[[]x]%", built.Parameters.Single(p => p.Name == "f0").Value);
    }

    [Fact]
    public void Build_VarcharColumns_BindStringsAsAnsi_SoIndexesCanBeUsed()
    {
        var varchar = Build([new LookupFilter("Status", "=", J("\"OPEN\""))]).Parameters.Single(p => p.Name == "f0");
        var nvarchar = Build([new LookupFilter("Note", "=", J("\"hello\""))]).Parameters.Single(p => p.Name == "f0");

        Assert.True(varchar.AnsiString);
        Assert.False(nvarchar.AnsiString);
    }

    [Fact]
    public void Build_In_ExpandsToOneParameterPerItem()
    {
        var built = Build([new LookupFilter("Id", "in", J("[1, 2, 3]"))]);

        Assert.Contains("[Id] IN (@f0_0, @f0_1, @f0_2)", built.Sql);
        Assert.Equal(1 + 3, built.Parameters.Count); // @n + three values
        Assert.True(SelectOnlyValidator.Validate(built.Sql).IsValid);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"not-an-array\"")]
    [InlineData("[{\"a\":1}]")]
    public void Build_In_RejectsBadValues(string value)
    {
        Assert.Null(Build([new LookupFilter("Id", "in", J(value))]).Sql);
    }

    [Fact]
    public void Build_In_RejectsTooManyItems()
    {
        var items = string.Join(",", Enumerable.Range(0, InvestigationService.MaxInValues + 1));

        Assert.Null(Build([new LookupFilter("Id", "in", J($"[{items}]"))]).Sql);
    }

    [Fact]
    public void Build_NullChecks_NeedNoValue()
    {
        var built = Build([new LookupFilter("Note", "is_null"), new LookupFilter("Status", "is_not_null")]);

        Assert.Contains("[Note] IS NULL AND [Status] IS NOT NULL", built.Sql);
        Assert.Single(built.Parameters); // only @n
    }

    [Fact]
    public void Build_EqualsNull_BecomesIsNull()
    {
        var built = Build([new LookupFilter("Note", "=", J("null"))]);

        Assert.Contains("[Note] IS NULL", built.Sql);
    }

    [Theory]
    [InlineData("drop")]
    [InlineData("; DELETE")]
    [InlineData("regexp")]
    public void Build_UnknownOperator_IsRejected(string op)
    {
        Assert.Null(Build([new LookupFilter("Id", op, J("1"))]).Sql);
    }

    [Fact]
    public void Build_MissingValue_IsRejected()
    {
        Assert.Null(Build([new LookupFilter("Id", "=")]).Sql);
        Assert.Null(Build([new LookupFilter("Id", "=", J("{\"a\":1}"))]).Sql);
    }

    [Fact]
    public void Build_TableNameWithBracketsIsEscaped()
    {
        var weird = new InvestigationService.ObjectRef("dbo", "Odd]Name", "U", 1);

        var built = InvestigationService.BuildSelect(weird, OrderColumns, [], null, null, 5);

        Assert.Equal("SELECT TOP (@n) * FROM [dbo].[Odd]]Name]", built.Sql);
        Assert.True(SelectOnlyValidator.Validate(built.Sql).IsValid);
    }

    // ---- identifiers ---------------------------------------------------------------------------

    [Theory]
    [InlineData("Orders", null, "Orders")]
    [InlineData("dbo.Orders", "dbo", "Orders")]
    [InlineData("[dbo].[Orders]", "dbo", "Orders")]
    [InlineData("\"sales\".\"Order Items\"", "sales", "Order Items")]
    [InlineData("[dbo].[Odd]]Name]", "dbo", "Odd]Name")]
    public void ParseObjectName_AcceptsCommonForms(string input, string? schema, string name)
    {
        Assert.True(SqlIdentifier.TryParseObjectName(input, out var s, out var n, out var error), error);
        Assert.Equal(schema, s);
        Assert.Equal(name, n);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("db.dbo.Orders")]
    [InlineData("a.b.c.d")]
    public void ParseObjectName_RejectsEmptyAndDatabaseQualifiedNames(string input)
    {
        Assert.False(SqlIdentifier.TryParseObjectName(input, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Quote_DoublesClosingBrackets()
    {
        Assert.Equal("[a]]b]", SqlIdentifier.Quote("a]b"));
        Assert.Equal("[dbo].[T]", SqlIdentifier.Quote("dbo", "T"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("order", "%order%")]
    [InlineData("Order%", "Order%")]
    public void ToLikePattern_WrapsOnlyPlainTerms(string? input, string? expected)
    {
        Assert.Equal(expected, SqlIdentifier.ToLikePattern(input));
    }

    // ---- explain plan summary ------------------------------------------------------------------

    private const string SamplePlan = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.6">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementSubTreeCost="1.25" StatementEstRows="500">
              <QueryPlan>
                <MissingIndexes>
                  <MissingIndexGroup Impact="80">
                    <MissingIndex Database="[Crm]" Schema="[dbo]" Table="[Orders]">
                      <ColumnGroup Usage="EQUALITY"><Column Name="[Status]" /></ColumnGroup>
                    </MissingIndex>
                  </MissingIndexGroup>
                </MissingIndexes>
                <Warnings><NoJoinPredicate /></Warnings>
                <RelOp PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="500" EstimatedTotalSubtreeCost="1.25">
                  <RelOp PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimateRows="90000" EstimatedTotalSubtreeCost="1.1">
                    <IndexScan><Object Table="[Orders]" Index="[PK_Orders]" /></IndexScan>
                  </RelOp>
                  <RelOp PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="1" EstimatedTotalSubtreeCost="0.01">
                    <IndexScan><Object Table="[Customers]" Index="[PK_Customers]" /></IndexScan>
                  </RelOp>
                </RelOp>
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public void ParsePlan_SummarisesCostScansWarningsAndMissingIndexes()
    {
        var plan = InvestigationService.ParsePlan("Crm", SamplePlan);

        Assert.True(plan.Success);
        Assert.Equal(1.25, plan.EstimatedCost);
        Assert.Equal("Nested Loops", plan.Operators[0].Physical); // most expensive first
        Assert.Contains(plan.Warnings, w => w.Contains("Clustered Index Scan") && w.Contains("[Orders]"));
        Assert.Contains("NoJoinPredicate", plan.Warnings);
        Assert.DoesNotContain(plan.Warnings, w => w.StartsWith("Index Seek"));
        Assert.Single(plan.MissingIndexes);
        Assert.Contains("EQUALITY([Status])", plan.MissingIndexes[0]);
    }

    [Fact]
    public void ParsePlan_RejectsDtd_SoUntrustedXmlCannotExpandEntities()
    {
        var xml = "<!DOCTYPE x [<!ENTITY a \"b\">]><ShowPlanXML xmlns=\"http://schemas.microsoft.com/sqlserver/2004/07/showplan\" />";

        Assert.ThrowsAny<System.Xml.XmlException>(() => InvestigationService.ParsePlan("Crm", xml));
    }
}

public class ObjectContextReaderSecurityTests
{
    [Theory]
    [InlineData("..", "Db", "Orders")]
    [InlineData("DEV", "..", "Orders")]
    [InlineData("DEV", @"..\..\secrets", "Orders")]
    [InlineData(@"..\..", "Db", "Orders")]
    [InlineData(@"C:\Windows", "Db", "Orders")]
    [InlineData("DEV", "Db", "*")]
    [InlineData("DEV", "Db", "Orders?")]
    [InlineData("DEV", "Db", @"..\..\..\Windows\win")]
    [InlineData("", "Db", "Orders")]
    public async Task PathTraversalAndWildcards_AreRejected(string alias, string database, string name)
    {
        var result = await ObjectContextReader.GetObjectContextAsync(Path.GetTempPath(), alias, database, name);

        Assert.Contains("Invalid", result);
    }

    [Theory]
    [InlineData("CRM_Order")]
    [InlineData("My DB")]
    [InlineData("db-1.v2")]
    public void OrdinaryNames_AreAllowed(string name) => Assert.True(ObjectContextReader.IsSafeSegment(name));
}
