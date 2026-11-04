namespace McpSqlServer;

public record ConnectionCheckResult(
    bool Success,
    long ElapsedMs,
    string? ServerVersion = null,
    string? ErrorMessage = null,
    bool? WriteCapable = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyDictionary<string, string>? ConfigSource = null
)
{
    /// <summary>Field the hub smoke check looks for: {"connected": true, ...}.</summary>
    public bool Connected => Success;
}

public record DatabaseItem(
    int DatabaseId,
    string Name,
    string State,
    bool? HasAccess,
    bool IsSystem
);

public record DatabaseListingResult(
    bool Success,
    int VisibleCount,
    int SystemCount,
    int OtherCount,
    IReadOnlyList<DatabaseItem> Databases,
    string Scope = "visible_to_current_login",
    string? ErrorMessage = null
);

public record TableItem(
    string Schema,
    string Name
);

public record TableListingResult(
    bool Success,
    string Database,
    int TableCount,
    IReadOnlyList<TableItem> Tables,
    string? ErrorMessage = null
);

public record QueryExecutionResult(
    bool Success,
    string Database,
    int RowCount,
    [property: System.Text.Json.Serialization.JsonPropertyName("truncated")] bool IsTruncated,
    long ElapsedMs,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    string? ErrorMessage = null,
    string? Sql = null
);

public record DatabaseDriftInfo(
    string DatabaseName,
    bool HasDrift,
    string? SnapshotMigrationId,
    string? CurrentMigrationId,
    int SnapshotCount,
    int CurrentCount,
    string Reason
);

public record DriftCheckResult(
    bool Success,
    string ServerAlias,
    IReadOnlyList<DatabaseDriftInfo> DriftedDatabases,
    IReadOnlyList<DatabaseDriftInfo> UpToDateDatabases,
    string? ErrorMessage = null
);

public record SearchContextMatch(
    string ServerAlias,
    string Database,
    string Type,
    string Name,
    string Details,
    string RelativePath
);

public record SearchContextResult(
    string Query,
    int TotalMatches,
    IReadOnlyList<SearchContextMatch> Matches,
    string Message,
    bool Success = true
);

/// <summary>One object from the offline snapshot. <see cref="Found"/> is false for invalid input or a missing object; the tool then sets the MCP error flag.</summary>
public record ObjectContextResult(bool Found, string Text);

public record ServerRegistryItem(
    string ServerAlias,
    string ServerHost,
    string ServerVersion,
    int DatabaseCount,
    int TotalTableCount,
    int TotalViewCount,
    int TotalProcedureCount,
    DateTime LastScannedAt,
    int TotalFunctionCount = 0,
    int TotalTriggerCount = 0,
    IReadOnlyDictionary<string, DatabaseMigrationStatus>? Migrations = null
);
