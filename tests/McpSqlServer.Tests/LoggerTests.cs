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
}
