namespace McpSqlServer.Tests;

public class ScanCheckpointTests
{
    [Fact]
    public async Task ScanReportsAsync_LimitsConcurrencyAndKeepsDatabaseOrder()
    {
        var started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var names = new[] { "A", "B", "C", "D", "E" };

        var reports = await DatabaseSchemaScanner.ScanReportsAsync(names, async (name, token) =>
        {
            if (Interlocked.Increment(ref started) == 4) release.SetResult();
            await release.Task.WaitAsync(token);
            return new DatabaseScanReport(name, true, 0, 0, 0, [], [], []);
        }, maxConcurrency: 4, cancellationToken: timeout.Token);

        Assert.Equal(5, started);
        Assert.Equal(names, reports.Select(r => r.DatabaseName));
    }

    [Fact]
    public async Task ScanReportsAsync_CancellationStopsTheBatch()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseSchemaScanner.ScanReportsAsync(["One", "Two"], (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<DatabaseScanReport>(token);
            }, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Resume_OnlyScansMissingAndFailedDatabases()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcp_scan_" + Guid.NewGuid().ToString("N"));
        try
        {
            var checkpoint = await ScanCheckpoint.OpenAsync(directory, "DEV", "sql.local", ["One", "Two", "Three"], resume: false);
            await checkpoint.SaveReportAsync(new DatabaseScanReport("One", true, 0, 0, 0, [], [], []));
            await checkpoint.SaveReportAsync(new DatabaseScanReport("Two", false, 0, 0, 0, [], [], [], "timeout"));

            var resumed = await ScanCheckpoint.OpenAsync(directory, "DEV", "sql.local", ["One", "Two", "Three"], resume: true);

            Assert.Equal(["Two", "Three"], resumed.PendingDatabases);
            Assert.Equal("One", resumed.GetCompletedReports().Single().DatabaseName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FreshScan_DoesNotReuseEarlierReports()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcp_scan_" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = await ScanCheckpoint.OpenAsync(directory, "DEV", "sql.local", ["One"], resume: false);
            await first.SaveReportAsync(new DatabaseScanReport("One", true, 0, 0, 0, [], [], []));

            var fresh = await ScanCheckpoint.OpenAsync(directory, "DEV", "sql.local", ["One"], resume: false);
            var resumed = await ScanCheckpoint.OpenAsync(directory, "DEV", "sql.local", ["One"], resume: true);

            Assert.Equal(["One"], fresh.PendingDatabases);
            Assert.Equal(["One"], resumed.PendingDatabases);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Resume_DetectsDatabasesNoLongerInCatalog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcp_scan_" + Guid.NewGuid().ToString("N"));
        try
        {
            await ScanCheckpoint.OpenAsync(directory, "DEV", "sql.local", ["One", "Two"], resume: false);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ScanCheckpoint.OpenAsync(directory, "DEV", "sql.local", ["One"], resume: true));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Resume_RestoresIndexedTableReports()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcp_scan_" + Guid.NewGuid().ToString("N"));
        try
        {
            var checkpoint = await ScanCheckpoint.OpenAsync(directory, "DEV", "sql.local", ["One"], resume: false);
            var report = new DatabaseScanReport("One", true, 1, 0, 0,
                [new TableSchemaItem("dbo", "Users", [new ColumnSchemaItem("Id", "int", false, true, true)],
                    [new IndexSchemaItem("PK_Users", true, true, "CLUSTERED", ["Id"])])], [], []);
            await checkpoint.SaveReportAsync(report);

            var resumed = await ScanCheckpoint.OpenAsync(directory, "DEV", "sql.local", ["One"], resume: true);

            Assert.Empty(resumed.PendingDatabases);
            Assert.Equal("PK_Users", resumed.GetCompletedReports().Single().Tables.Single().Indexes.Single().Name);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
