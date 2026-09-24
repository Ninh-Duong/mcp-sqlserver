using Microsoft.Data.SqlClient;

namespace McpSqlServer;

public record PreflightReport(
    bool Success,
    IReadOnlyList<string> PassedChecks,
    IReadOnlyList<string> Errors
);

public static class PreflightChecker
{
    public static PreflightReport RunPreflightChecks()
    {
        var passed = new List<string>();
        var errors = new List<string>();

        // 1. Verify runtime version (.NET 10+)
        if (Environment.Version.Major >= 10)
        {
            passed.Add($"Compatible .NET Runtime: v{Environment.Version}");
        }
        else
        {
            errors.Add($".NET 10 or higher required. Current runtime: {Environment.Version}");
        }

        // 2. Verify Microsoft.Data.SqlClient and Microsoft.Data.Sqlite packages
        try
        {
            var testConn = typeof(SqlConnection);
            passed.Add($"SqlClient library ready: {testConn.Assembly.GetName().Name} v{testConn.Assembly.GetName().Version}");
        }
        catch (Exception ex)
        {
            errors.Add($"Missing or faulty Microsoft.Data.SqlClient assembly: {ex.Message}. Please run 'dotnet restore'.");
        }

        try
        {
            var testSqlite = typeof(Microsoft.Data.Sqlite.SqliteConnection);
            passed.Add($"Sqlite library ready: {testSqlite.Assembly.GetName().Name} v{testSqlite.Assembly.GetName().Version}");
        }
        catch (Exception ex)
        {
            errors.Add($"Missing or faulty Microsoft.Data.Sqlite assembly: {ex.Message}. Please run 'dotnet restore'.");
        }

        // 3. Verify JSON serializer & Cryptography
        try
        {
            var jsonType = typeof(System.Text.Json.JsonSerializer);
            var cryptoType = typeof(System.Security.Cryptography.Aes);
            passed.Add("System.Text.Json and Cryptography libraries ready.");
        }
        catch (Exception ex)
        {
            errors.Add($"Core library error: {ex.Message}");
        }

        // 4. Verify write permission to relative ./logs directory
        try
        {
            var testDir = Path.Combine(".", "logs");
            if (!Directory.Exists(testDir))
            {
                Directory.CreateDirectory(testDir);
            }
            var testFile = Path.Combine(testDir, ".preflight_check");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
            passed.Add("Write access to relative directory ./logs verified.");
        }
        catch (Exception ex)
        {
            errors.Add($"Cannot write to relative directory ./logs: {ex.Message}");
        }

        return new PreflightReport(errors.Count == 0, passed, errors);
    }

    public static PreflightReport RunCoreSelfTests()
    {
        var passed = new List<string>();
        var errors = new List<string>();

        // Test 1: ConnectionOptions validation & builder with special characters
        try
        {
            var opts = new ConnectionOptions
            {
                Server = "localhost",
                Username = "sa",
                Password = "P@ssw;ord'\"=123",
                Port = 1433
            };
            var validation = opts.Validate();
            if (validation.Count > 0)
            {
                errors.Add($"SelfTest [ConnectionOptions.Validate] failed: {string.Join(", ", validation)}");
            }
            else
            {
                var connStr = opts.BuildConnectionString();
                if (!connStr.Contains("1433") || !connStr.Contains("sa"))
                {
                    errors.Add("SelfTest [ConnectionOptions.BuildConnectionString] returned invalid result.");
                }
                else
                {
                    passed.Add("SelfTest [ConnectionOptions]: Builder & Validation PASS.");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"SelfTest [ConnectionOptions] exception: {ex.Message}");
        }

        // Test 2: Logger sanitization
        try
        {
            Logger.SetActivePassword("SecretP@ss999");
            var testMsg = "Failed login for user admin with password SecretP@ss999 on server";
            var sanitized = Logger.Sanitize(testMsg);
            if (sanitized.Contains("SecretP@ss999"))
            {
                errors.Add("SelfTest [Logger.Sanitize] failed to redact password!");
            }
            else
            {
                passed.Add("SelfTest [Logger]: Password Sanitization PASS.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"SelfTest [Logger] exception: {ex.Message}");
        }

        // Test 3: System database categorization logic
        try
        {
            var sysDb = new DatabaseItem(1, "master", "ONLINE", true, true);
            var userDb = new DatabaseItem(5, "AppDb", "ONLINE", true, false);
            if (!sysDb.IsSystem || userDb.IsSystem)
            {
                errors.Add("SelfTest [DatabaseItem] categorization logic incorrect!");
            }
            else
            {
                passed.Add("SelfTest [DatabaseClassifier]: DB Categorization PASS.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"SelfTest [DatabaseItem] exception: {ex.Message}");
        }

        // Test 4: AI Context Schema Formatting & Data Type Formatter
        try
        {
            var formattedType = SqlServerService.FormatDataType("nvarchar", 100, 0, 0);
            var col = new ColumnSchemaItem("Username", formattedType, false, true, false, "dbo.Roles.Id");
            var compactStr = col.ToCompactString();

            if (formattedType != "nvarchar(50)" || !compactStr.Contains("PK") || !compactStr.Contains("FK -> dbo.Roles.Id"))
            {
                errors.Add($"SelfTest [AiContext.SchemaFormat] unexpected format: {compactStr}");
            }
            else
            {
                passed.Add("SelfTest [AiContext]: Schema Compact Formatting PASS.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"SelfTest [AiContext] exception: {ex.Message}");
        }

        // Test 5: Read-Only SQL Query Validator
        try
        {
            var validSelect = SqlServerService.ValidateReadOnlyQuery("SELECT TOP 10 * FROM dbo.Orders WHERE Status = 'DELETED'");
            var invalidDrop = SqlServerService.ValidateReadOnlyQuery("DROP TABLE dbo.Orders;");
            var invalidInsert = SqlServerService.ValidateReadOnlyQuery("INSERT INTO dbo.Orders (Id) VALUES (1)");

            if (!validSelect.IsValid || invalidDrop.IsValid || invalidInsert.IsValid)
            {
                errors.Add("SelfTest [ValidateReadOnlyQuery] read-only validator logic failed!");
            }
            else
            {
                passed.Add("SelfTest [SecurityGuard]: Read-Only SQL Validator PASS.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"SelfTest [ValidateReadOnlyQuery] exception: {ex.Message}");
        }

        // Test 6: In-Memory SQLite Catalog Functionality
        try
        {
            using var sqliteConn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            sqliteConn.Open();
            using var cmd = sqliteConn.CreateCommand();
            cmd.CommandText = "CREATE TABLE test (id INT); INSERT INTO test VALUES (42); SELECT id FROM test;";
            var scalar = cmd.ExecuteScalar();
            if (scalar == null || Convert.ToInt32(scalar) != 42)
            {
                errors.Add("SelfTest [SqliteCatalog] In-memory SQLite failed execution.");
            }
            else
            {
                passed.Add("SelfTest [SqliteCatalog]: SQLite Engine & In-Memory PASS.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"SelfTest [SqliteCatalog] exception: {ex.Message}");
        }

        return new PreflightReport(errors.Count == 0, passed, errors);
    }
}
