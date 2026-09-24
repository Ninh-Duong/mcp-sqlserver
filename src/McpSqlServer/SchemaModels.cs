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
            attributes.Add($"Observed sample: [{vals}] (may be incomplete)");
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

public record IndexSchemaItem
{
    public string Name { get; init; }
    public bool IsUnique { get; init; }
    public bool IsPrimaryKey { get; init; }
    public string TypeDesc { get; init; }
    public IReadOnlyList<string> KeyColumns { get; init; }
    public IReadOnlyList<string> IncludedColumns { get; init; }
    public string? FilterDefinition { get; init; }

    [System.Text.Json.Serialization.JsonConstructor]
    public IndexSchemaItem(string Name, bool IsUnique, bool IsPrimaryKey, string TypeDesc,
        IReadOnlyList<string> KeyColumns, IReadOnlyList<string>? IncludedColumns = null, string? FilterDefinition = null)
    {
        this.Name = Name;
        this.IsUnique = IsUnique;
        this.IsPrimaryKey = IsPrimaryKey;
        this.TypeDesc = TypeDesc;
        this.KeyColumns = KeyColumns;
        this.IncludedColumns = IncludedColumns ?? Array.Empty<string>();
        this.FilterDefinition = FilterDefinition;
    }

    public IndexSchemaItem(string name, bool isUnique, bool isPrimaryKey, string typeDesc, IReadOnlyList<string> keyColumns, string? filterDefinition)
        : this(name, isUnique, isPrimaryKey, typeDesc, keyColumns, null, filterDefinition)
    {
    }

    public IReadOnlyList<string> Columns => KeyColumns;

    public string ToCompactString()
    {
        var cols = IncludedColumns.Count > 0
            ? $"Key: {string.Join(", ", KeyColumns)} | Inc: {string.Join(", ", IncludedColumns)}"
            : string.Join(", ", KeyColumns);

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

    public string RenderCompactTableDefinition()
    {
        var sb = new System.Text.StringBuilder();
        var volSuffix = "";
        if (ApproxRowCount.HasValue)
        {
            var r = ApproxRowCount.Value;
            var rStr = r >= 1_000_000
                ? (r / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "M"
                : r >= 1_000
                    ? (r / 1_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "K"
                    : $"{r}";
            var sStr = ApproxSizeMb.HasValue
                ? $" | {ApproxSizeMb.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} MB"
                : "";
            var highVol = r >= 100_000 ? " [HIGH VOLUME]" : "";
            volSuffix = $" (~{rStr} rows{sStr}){highVol}";
        }
        else
        {
            volSuffix = " (Stats: unavailable)";
        }

        sb.AppendLine($"### {FullName}{volSuffix}");
        foreach (var col in Columns)
        {
            sb.AppendLine(col.ToCompactString());
        }

        if (CheckConstraints.Count > 0)
        {
            var checksStr = string.Join("; ", CheckConstraints.Select(c => c.ToCompactString()));
            sb.AppendLine($"- Check Constraints: {checksStr}");
        }

        if (Indexes.Count > 0)
        {
            var idxStr = string.Join("; ", Indexes.Select(i => i.ToCompactString()));
            sb.AppendLine($"- Indexes: {idxStr}");
        }

        if (Triggers.Count > 0)
        {
            var trgStr = string.Join("; ", Triggers.Select(t => $"{t.Name} ({t.Events})"));
            sb.AppendLine($"- Triggers: {trgStr}");
        }

        return sb.ToString();
    }
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

public record ServerScanResult(
    string ServerAlias,
    string ServerHost,
    string ServerVersion,
    DateTime ScannedAt,
    long ElapsedMs,
    IReadOnlyList<DatabaseScanReport> Databases
);
