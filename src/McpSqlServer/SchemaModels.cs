namespace McpSqlServer;

public record ColumnSchemaItem(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsIdentity,
    string? ForeignKeyReference = null,
    string? Description = null
)
{
    public string ToCompactString()
    {
        var attributes = new List<string>();
        if (IsPrimaryKey) attributes.Add("PK");
        if (IsIdentity) attributes.Add("Identity");
        if (!string.IsNullOrEmpty(ForeignKeyReference)) attributes.Add($"FK -> {ForeignKeyReference}");
        attributes.Add(IsNullable ? "Null" : "Not Null");
        if (!string.IsNullOrWhiteSpace(Description)) attributes.Add($"Description: {Description.Trim().Replace('\n', ' ')}");

        return $"- `{Name}`: {DataType} ({string.Join(", ", attributes)})";
    }

    public string ToQuickMapString()
    {
        var flags = new List<string>();
        if (IsPrimaryKey) flags.Add("PK");
        if (!string.IsNullOrEmpty(ForeignKeyReference)) flags.Add("FK");
        var flagStr = flags.Count > 0 ? $"({string.Join(",", flags)})" : "";
        return $"{Name}{flagStr}";
    }
}

public record TableSchemaItem(
    string Schema,
    string Name,
    IReadOnlyList<ColumnSchemaItem> Columns
)
{
    public string FullName => $"{Schema}.{Name}";
}

public record ViewSchemaItem(
    string Schema,
    string Name,
    string? Definition
)
{
    public string FullName => $"{Schema}.{Name}";
}

public record ProcedureSchemaItem(
    string Schema,
    string Name,
    IReadOnlyList<string> Parameters,
    string? Definition
)
{
    public string FullName => $"{Schema}.{Name}";
}

public record DatabaseScanReport(
    string DatabaseName,
    bool Success,
    int TableCount,
    int ViewCount,
    int ProcedureCount,
    IReadOnlyList<TableSchemaItem> Tables,
    IReadOnlyList<ViewSchemaItem> Views,
    IReadOnlyList<ProcedureSchemaItem> Procedures,
    string? ErrorMessage = null
);

public record ServerScanResult(
    string ServerAlias,
    string ServerHost,
    string ServerVersion,
    DateTime ScannedAt,
    long ElapsedMs,
    IReadOnlyList<DatabaseScanReport> Databases
);

public record QueryExecutionResult(
    bool Success,
    string Database,
    int RowCount,
    bool IsTruncated,
    long ElapsedMs,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    string? ErrorMessage = null
);
