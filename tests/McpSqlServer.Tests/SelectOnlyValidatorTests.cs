using McpSqlServer.Security;

namespace McpSqlServer.Tests;

/// <summary>
/// Security boundary tests. Every rejected case below is a known or plausible bypass.
/// RULES.md: these tests must never be deleted, only added to.
/// </summary>
public class SelectOnlyValidatorTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT TOP 10 * FROM dbo.Orders WHERE Status = 'DELETED'")]
    [InlineData("SELECT * FROM dbo.Orders WHERE Note = 'DROP TABLE x; -- really'")]
    [InlineData("SELECT * FROM dbo.Orders WHERE Note = '/* UPDATE */'")]
    [InlineData("WITH c AS (SELECT Id FROM dbo.Orders) SELECT * FROM c")]
    [InlineData("SELECT o.Id, c.Name FROM dbo.Orders o JOIN dbo.Customers c ON c.Id = o.CustomerId")]
    [InlineData("SELECT * FROM dbo.Orders WITH (NOLOCK) WHERE Id = @id")]
    [InlineData("SELECT * FROM dbo.Orders WITH (READUNCOMMITTED, INDEX(0))")]
    [InlineData("SELECT COUNT(*) FROM sys.tables")]
    [InlineData("SELECT * FROM dbo.Orders ORDER BY Id OFFSET 10 ROWS FETCH NEXT 10 ROWS ONLY")]
    [InlineData("SELECT * FROM dbo.Orders FOR JSON AUTO")]
    [InlineData("SELECT UpdatedDate, DeletedFlag, InsertedBy FROM dbo.Orders")]
    [InlineData("-- comment first\nSELECT 1")]
    [InlineData("/* block */ SELECT 1")]
    [InlineData("SELECT * FROM dbo.sp_archive_log")]
    public void Valid_SelectIsAccepted(string sql)
    {
        var result = SelectOnlyValidator.Validate(sql);
        Assert.True(result.IsValid, result.Error);
        Assert.Equal(sql, result.Sql);
    }

    [Theory]
    // classic statements
    [InlineData("DELETE FROM dbo.Orders")]
    [InlineData("UPDATE dbo.Orders SET Status = 1")]
    [InlineData("INSERT INTO dbo.Orders (Id) VALUES (1)")]
    [InlineData("TRUNCATE TABLE dbo.Orders")]
    [InlineData("DROP TABLE dbo.Orders")]
    [InlineData("ALTER TABLE dbo.Orders ADD x int")]
    [InlineData("CREATE TABLE t (id int)")]
    [InlineData("MERGE dbo.A AS a USING dbo.B AS b ON a.Id = b.Id WHEN MATCHED THEN DELETE;")]
    [InlineData("EXEC sp_who")]
    [InlineData("EXECUTE ('DELETE FROM t')")]
    [InlineData("GRANT SELECT ON dbo.Orders TO public")]
    [InlineData("SHUTDOWN")]
    // statements the old regex blocklist missed
    [InlineData("DBCC CHECKDB")]
    [InlineData("WAITFOR DELAY '00:00:30'")]
    [InlineData("BACKUP DATABASE x TO DISK = 'c:\\x.bak'")]
    [InlineData("KILL 55")]
    [InlineData("USE master")]
    [InlineData("SET IDENTITY_INSERT dbo.Orders ON")]
    [InlineData("DECLARE @a int = 1")]
    [InlineData("BEGIN TRAN")]
    [InlineData("DISABLE TRIGGER ALL ON dbo.Orders")]
    [InlineData("RECONFIGURE")]
    [InlineData("WRITETEXT dbo.t.c @p 'x'")]
    [InlineData("UPDATETEXT dbo.t.c @p 0 0 'x'")]
    // comment-marker-inside-string bypass (the original regex hole)
    [InlineData("SELECT '--'; DROP TABLE t")]
    [InlineData("SELECT '/*'; DELETE FROM t; SELECT '*/'")]
    [InlineData("SELECT 'a' -- x\n; DELETE FROM dbo.Orders")]
    // multi-statement
    [InlineData("SELECT 1; UPDATE dbo.Orders SET Status = 1")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 1\nGO\nDELETE FROM t")]
    // CTE + DML
    [InlineData("WITH c AS (SELECT Id FROM dbo.Orders) DELETE FROM c")]
    [InlineData("WITH c AS (SELECT Id FROM dbo.Orders) UPDATE c SET Id = 1")]
    // SELECT INTO
    [InlineData("SELECT * INTO dbo.NewTbl FROM dbo.Orders")]
    [InlineData("SELECT * INTO #tmp FROM dbo.Orders")]
    // pass-through / external data
    [InlineData("SELECT * FROM OPENQUERY(Linked, 'DELETE FROM t')")]
    [InlineData("SELECT * FROM OPENROWSET(BULK 'C:\\f.txt', SINGLE_CLOB) AS r")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;', 'SELECT 1') AS r")]
    [InlineData("SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x').db.dbo.t")]
    [InlineData("SELECT * FROM Linked.db.dbo.Orders")]
    [InlineData("SELECT * FROM dbo.xp_cmdshell_fn('dir')")]
    [InlineData("SELECT * FROM sys.fn_xe_file_target_read_file('x*.xel', NULL, NULL, NULL)")]
    [InlineData("SELECT * FROM sys.fn_dblog(NULL, NULL)")]
    // side effects without DML
    [InlineData("SELECT NEXT VALUE FOR dbo.Seq")]
    [InlineData("SELECT @a = 1")]
    [InlineData("SELECT * FROM dbo.Orders WITH (UPDLOCK)")]
    [InlineData("SELECT * FROM dbo.Orders WITH (XLOCK, ROWLOCK)")]
    [InlineData("SELECT * FROM dbo.Orders WITH (TABLOCKX)")]
    [InlineData("SELECT * FROM dbo.Orders WITH (HOLDLOCK)")]
    [InlineData("SELECT * FROM dbo.Orders OPTION (QUERYTRACEON 9481)")]
    // junk / empty
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- only a comment")]
    [InlineData("SELEC 1")]
    [InlineData("SELECT * FROM")]
    public void Dangerous_OrInvalid_IsRejected(string sql)
    {
        var result = SelectOnlyValidator.Validate(sql);
        Assert.False(result.IsValid, $"Should have been rejected: {sql}");
        Assert.NotNull(result.Error);
        Assert.Null(result.Sql);
    }

    [Fact]
    public void TooLong_IsRejected()
    {
        var sql = "SELECT " + new string('1', SelectOnlyValidator.MaxSqlLength);
        Assert.False(SelectOnlyValidator.Validate(sql).IsValid);
    }

    [Fact]
    public void Null_IsRejected()
    {
        Assert.False(SelectOnlyValidator.Validate(null).IsValid);
    }

    [Fact]
    public void ReferencedDatabases_AreReported_AndEnforced()
    {
        var sql = "SELECT * FROM Other.dbo.Orders o JOIN Main.dbo.Customers c ON c.Id = o.CustomerId";

        var open = SelectOnlyValidator.Validate(sql);
        Assert.True(open.IsValid, open.Error);
        Assert.Contains("Other", open.ReferencedDatabases);
        Assert.Contains("Main", open.ReferencedDatabases);

        var restricted = SelectOnlyValidator.Validate(sql, db => db.Equals("Main", StringComparison.OrdinalIgnoreCase));
        Assert.False(restricted.IsValid);
        Assert.Contains("Other", restricted.Error);
    }

    [Fact]
    public void ThreePartFunction_DatabaseIsEnforced()
    {
        var result = SelectOnlyValidator.Validate("SELECT Secret.dbo.fn_x(1)", db => db == "Main");
        Assert.False(result.IsValid);
    }
}
