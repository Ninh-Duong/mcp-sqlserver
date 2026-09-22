namespace McpSqlServer.Tests;

public class LoggerTests
{
    [Fact]
    public void Sanitize_MasksActivePassword()
    {
        Logger.SetActivePassword("MySecretPass#2026");
        var input = "Connecting to sql.example.internal with password MySecretPass#2026 failed.";
        var sanitized = Logger.Sanitize(input);

        Assert.DoesNotContain("MySecretPass#2026", sanitized);
        Assert.Contains("***", sanitized);
    }

    [Theory]
    [InlineData("Server=myServer;Database=myDataBase;Uid=myUsername;Pwd=myPassword;", "Pwd=***")]
    [InlineData("Server=myServer;Database=myDataBase;User Id=myUsername;Password=myPassword;", "Password=***")]
    [InlineData("password = SuperSecretPass123 ; OtherParam=1", "password=***")]
    public void Sanitize_MasksConnectionStringPasswords(string connStr, string expectedMask)
    {
        var sanitized = Logger.Sanitize(connStr);
        Assert.DoesNotContain("myPassword", sanitized);
        Assert.DoesNotContain("SuperSecretPass123", sanitized);
        Assert.Contains(expectedMask, sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LogMethods_DoNotThrowExceptions()
    {
        var ex = Record.Exception(() =>
        {
            Logger.Info("Test info message");
            Logger.Warn("Test warning message");
            Logger.Error("Test error message", new InvalidOperationException("Inner error"));
            Logger.Process("STEP", "Test process step message");
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Error_WritesToDedicatedErrorLogFile_WithSanitization()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mcp_sql_logger_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            Logger.Initialize(enableFileLogging: true, customLogDir: tempDir);
            Logger.SetActivePassword("SecretKey123");

            var testException = new InvalidOperationException("Connection failed for SecretKey123");
            Logger.Error("Failed to connect with SecretKey123", testException);

            var errorFile = Path.Combine(tempDir, "error.log");
            var processFile = Path.Combine(tempDir, "process.log");

            Assert.True(File.Exists(errorFile), "error.log should be created on error.");
            Assert.True(File.Exists(processFile), "process.log should also be created.");

            var errorContent = File.ReadAllText(errorFile);
            Assert.Contains("[ERROR]", errorContent);
            Assert.Contains("Failed to connect with ***", errorContent);
            Assert.Contains("InvalidOperationException: Connection failed for ***", errorContent);
            Assert.DoesNotContain("SecretKey123", errorContent);
        }
        finally
        {
            // Restore default logger configuration
            Logger.SetActivePassword(null);
            Logger.Initialize(enableFileLogging: true);
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
