using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace McpSqlServer;

public sealed class ScanCheckpoint
{
    private sealed record Metadata(string ServerAlias, string ServerHost, string Generation, string[] DatabaseNames);
    private sealed record SavedReport(string Generation, DatabaseScanReport Report);

    private readonly string _directory;
    private readonly Metadata _metadata;
    private readonly ConcurrentDictionary<string, DatabaseScanReport> _reports = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<string> _databaseNames;

    private ScanCheckpoint(string directory, Metadata metadata, IReadOnlyList<string> databaseNames)
    {
        _directory = directory;
        _metadata = metadata;
        _databaseNames = databaseNames;
    }

    public IReadOnlyList<string> PendingDatabases => _databaseNames.Where(name => !_reports.TryGetValue(name, out var report) || !report.Success).ToArray();

    public IReadOnlyList<DatabaseScanReport> GetCompletedReports() =>
        _databaseNames.Where(name => _reports.TryGetValue(name, out var report) && report.Success)
            .Select(name => _reports[name]).ToArray();

    public DatabaseScanReport? GetReport(string databaseName) =>
        _reports.GetValueOrDefault(databaseName);

    public static string GetCheckpointDirectory(string outputDirectory, string serverAlias, string serverHost)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{serverAlias.ToUpperInvariant()}\n{serverHost.ToUpperInvariant()}")));
        return Path.Combine(Path.GetFullPath(outputDirectory), ".scan-state", identity);
    }

    public static bool HasCheckpoint(string outputDirectory, string serverAlias, string serverHost)
    {
        var metadataPath = Path.Combine(GetCheckpointDirectory(outputDirectory, serverAlias, serverHost), "metadata.json");
        return File.Exists(metadataPath);
    }

    public static void DeleteCheckpoint(string outputDirectory, string serverAlias, string serverHost)
    {
        var directory = GetCheckpointDirectory(outputDirectory, serverAlias, serverHost);
        if (Directory.Exists(directory))
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    public static async Task<ScanCheckpoint> OpenAsync(
        string outputDirectory,
        string serverAlias,
        string serverHost,
        IReadOnlyList<string> databaseNames,
        bool resume,
        CancellationToken cancellationToken = default)
    {
        var directory = GetCheckpointDirectory(outputDirectory, serverAlias, serverHost);
        Directory.CreateDirectory(directory);
        var metadataPath = Path.Combine(directory, "metadata.json");

        Metadata metadata;
        if (resume)
        {
            if (!File.Exists(metadataPath))
                throw new InvalidOperationException($"No scan checkpoint found for server '{serverAlias}'. Run a full scan first.");
            metadata = JsonSerializer.Deserialize<Metadata>(await File.ReadAllTextAsync(metadataPath, cancellationToken))
                ?? throw new InvalidOperationException("Scan checkpoint metadata is invalid.");
            if (!string.Equals(metadata.ServerAlias, serverAlias, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(metadata.ServerHost, serverHost, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Scan checkpoint belongs to another server.");
            var unavailable = (metadata.DatabaseNames ?? []).Except(databaseNames, StringComparer.OrdinalIgnoreCase).ToArray();
            if (unavailable.Length > 0)
                throw new InvalidOperationException($"Cannot resume because these databases are no longer accessible: {string.Join(", ", unavailable)}. Run a full scan to reset the checkpoint.");
        }
        else
        {
            metadata = new Metadata(serverAlias, serverHost, Guid.NewGuid().ToString("N"), databaseNames.ToArray());
            await WriteAtomicallyAsync(metadataPath, JsonSerializer.Serialize(metadata), cancellationToken);
        }

        var checkpoint = new ScanCheckpoint(directory, metadata, databaseNames);
        if (resume)
        {
            foreach (var name in databaseNames)
            {
                var path = checkpoint.ReportPath(name);
                if (!File.Exists(path)) continue;
                try
                {
                    var saved = JsonSerializer.Deserialize<SavedReport>(await File.ReadAllTextAsync(path, cancellationToken));
                    if (saved?.Report is { } report && saved.Generation == metadata.Generation &&
                        string.Equals(report.DatabaseName, name, StringComparison.OrdinalIgnoreCase))
                        checkpoint._reports[name] = report;
                }
                catch (JsonException)
                {
                    // A partial or damaged checkpoint is retried.
                }
            }
        }
        return checkpoint;
    }

    public async Task SaveReportAsync(DatabaseScanReport report, CancellationToken cancellationToken = default)
    {
        if (!_databaseNames.Contains(report.DatabaseName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Database '{report.DatabaseName}' is not part of this scan.");
        await WriteAtomicallyAsync(ReportPath(report.DatabaseName),
            JsonSerializer.Serialize(new SavedReport(_metadata.Generation, report)), cancellationToken);
        _reports[report.DatabaseName] = report;
    }

    private string ReportPath(string name) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant()))) + ".json");

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
