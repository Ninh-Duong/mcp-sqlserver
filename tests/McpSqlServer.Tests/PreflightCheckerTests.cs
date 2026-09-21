namespace McpSqlServer.Tests;

public class PreflightCheckerTests
{
    [Fact]
    public void RunPreflightChecks_OnSupportedEnvironment_Succeeds()
    {
        var report = PreflightChecker.RunPreflightChecks();

        Assert.True(report.Success, $"Preflight checks failed with errors: {string.Join("; ", report.Errors)}");
        Assert.NotEmpty(report.PassedChecks);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void RunCoreSelfTests_PassesAllCoreAssertions()
    {
        var report = PreflightChecker.RunCoreSelfTests();

        Assert.True(report.Success, $"Core self-tests failed with errors: {string.Join("; ", report.Errors)}");
        Assert.Contains(report.PassedChecks, p => p.Contains("ConnectionOptions"));
        Assert.Contains(report.PassedChecks, p => p.Contains("Logger"));
        Assert.Contains(report.PassedChecks, p => p.Contains("DatabaseClassifier"));
        Assert.Empty(report.Errors);
    }
}
