namespace McpSqlServer;

public record ColumnSchemaItem(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsIdentity,
    string? ForeignKeyReference = null,
    string? Description = null,
    string? DefaultValue = null,
    IReadOnlyList<string>? SampleValues = null
)
{
    public string ToCompactString()
    {
        var attributes = new List<string>();
        if (IsPrimaryKey) attributes.Add("PK");
        if (IsIdentity) attributes.Add("Identity");
        if (!string.IsNullOrEmpty(ForeignKeyReference)) attributes.Add($"FK -> {ForeignKeyReference}");
        if (!string.IsNullOrEmpty(DefaultValue)) attributes.Add($"Default: {DefaultValue.Trim()}");
        attributes.Add(IsNullable ? "Null" : "Not Null");
        if (!string.IsNullOrWhiteSpace(Description)) attributes.Add($"Description: {Description.Trim().Replace('\n', ' ')}");
        if (SampleValues != null && SampleValues.Count > 0)
        {
            var vals = string.Join(", ", SampleValues.Select(v => $"'{v}'"));
            attributes.Add($"Values: [{vals}]");
        }

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

public record IndexSchemaItem(
    string Name,
    bool IsUnique,
    bool IsPrimaryKey,
    string TypeDesc,
    IReadOnlyList<string> Columns,
    string? FilterDefinition = null
)
{
    public string ToCompactString()
    {
        var cols = string.Join(", ", Columns);
        var flags = new List<string>();
        if (IsPrimaryKey) flags.Add("PK");
        else if (IsUnique) flags.Add("UNIQUE");
        if (!string.IsNullOrEmpty(TypeDesc) && !TypeDesc.Equals("NONCLUSTERED", StringComparison.OrdinalIgnoreCase))
            flags.Add(TypeDesc);
        if (!string.IsNullOrEmpty(FilterDefinition))
            flags.Add($"WHERE {FilterDefinition}");

        var flagStr = flags.Count > 0 ? $" ({string.Join(", ", flags)})" : "";
        return $"{Name}: [{cols}]{flagStr}";
    }
}

public record CheckConstraintItem(
    string Name,
    string Definition
)
{
    public string ToCompactString() => $"{Name}: {Definition.Trim()}";
}

public record TriggerSchemaItem(
    string Schema,
    string Name,
    string TableSchema,
    string TableName,
    string Events,
    bool IsDisabled,
    string? Definition
)
{
    public string FullName => $"{Schema}.{Name}";
    public string TargetTable => $"{TableSchema}.{TableName}";
}

public record FunctionSchemaItem(
    string Schema,
    string Name,
    string TypeDesc,
    IReadOnlyList<string> Parameters,
    string? Definition
)
{
    public string FullName => $"{Schema}.{Name}";
}

public record CrossDbDependencyItem(
    string ReferencingEntity,
    string ReferencedDatabase,
    string ReferencedEntity
);

public record TableSchemaItem(
    string Schema,
    string Name,
    IReadOnlyList<ColumnSchemaItem> Columns,
    IReadOnlyList<IndexSchemaItem>? Indexes = null,
    IReadOnlyList<TriggerSchemaItem>? Triggers = null,
    IReadOnlyList<CheckConstraintItem>? CheckConstraints = null,
    long? ApproxRowCount = null,
    double? ApproxSizeMb = null
)
{
    public string FullName => $"{Schema}.{Name}";
    private readonly IReadOnlyList<IndexSchemaItem>? _indexes = Indexes;
    public IReadOnlyList<IndexSchemaItem> Indexes => _indexes ?? Array.Empty<IndexSchemaItem>();

    private readonly IReadOnlyList<TriggerSchemaItem>? _triggers = Triggers;
    public IReadOnlyList<TriggerSchemaItem> Triggers => _triggers ?? Array.Empty<TriggerSchemaItem>();

    private readonly IReadOnlyList<CheckConstraintItem>? _checkConstraints = CheckConstraints;
    public IReadOnlyList<CheckConstraintItem> CheckConstraints => _checkConstraints ?? Array.Empty<CheckConstraintItem>();
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
    string? ErrorMessage = null,
    int FunctionCount = 0,
    int TriggerCount = 0,
    IReadOnlyList<FunctionSchemaItem>? Functions = null,
    IReadOnlyList<TriggerSchemaItem>? Triggers = null,
    string? LatestMigrationId = null,
    int MigrationCount = 0,
    DateTime? LastObjectModifyDate = null,
    IReadOnlyList<CrossDbDependencyItem>? CrossDbDependencies = null
)
{
    private readonly IReadOnlyList<FunctionSchemaItem>? _functions = Functions;
    public IReadOnlyList<FunctionSchemaItem> Functions => _functions ?? Array.Empty<FunctionSchemaItem>();

    private readonly IReadOnlyList<TriggerSchemaItem>? _triggers = Triggers;
    public IReadOnlyList<TriggerSchemaItem> Triggers => _triggers ?? Array.Empty<TriggerSchemaItem>();

    private readonly IReadOnlyList<CrossDbDependencyItem>? _crossDbDependencies = CrossDbDependencies;
    public IReadOnlyList<CrossDbDependencyItem> CrossDbDependencies => _crossDbDependencies ?? Array.Empty<CrossDbDependencyItem>();
}

public record DatabaseMigrationStatus(
    string DatabaseName,
    string? LatestMigrationId,
    int MigrationCount,
    DateTime? LastObjectModifyDate
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
