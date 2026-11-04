using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpSqlServer.Investigation;

using Row = IReadOnlyDictionary<string, object?>;

public record TableSummary(string Schema, string Name, string Type, long ApproxRows);

public record ListTablesResult(
    bool Success, string Database, int Count, bool Truncated,
    IReadOnlyList<TableSummary> Tables, string? ErrorMessage = null);

public record ColumnInfo(string Name, string Type, bool Nullable, bool IsPrimaryKey, bool IsIdentity, bool IsComputed, string? Default);

/// <summary>Direction is relative to the described table: "references" (this -> other) or "referenced_by" (other -> this).</summary>
public record ForeignKeyInfo(
    string Name, string Direction,
    string FromTable, IReadOnlyList<string> FromColumns,
    string ToTable, IReadOnlyList<string> ToColumns);

public record IndexInfo(string Name, string Type, bool IsUnique, bool IsPrimaryKey, string? KeyColumns);

public record TriggerInfo(string Name, bool IsDisabled, bool InsteadOf);

public record TableDescription(
    bool Success, string Database, string Schema, string Name, string Type, long ApproxRows,
    IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<ForeignKeyInfo> ForeignKeys,
    IReadOnlyList<IndexInfo> Indexes, IReadOnlyList<TriggerInfo> Triggers, string? ErrorMessage = null);

public record ColumnMatch(string Database, string Schema, string Table, string Column, string Type, bool Nullable);

public record FindColumnsResult(
    bool Success, int Count, bool Truncated, IReadOnlyList<string> SearchedDatabases,
    IReadOnlyList<ColumnMatch> Matches, IReadOnlyList<string> Errors, string? ErrorMessage = null);

/// <summary>One condition of lookup_rows. Op: = != &gt; &gt;= &lt; &lt;= like contains in is_null is_not_null.</summary>
public sealed record LookupFilter(
    [property: JsonPropertyName("column")] string Column,
    [property: JsonPropertyName("op")] string? Op = "=",
    [property: JsonPropertyName("value")] JsonElement? Value = null);

public sealed record LookupOrder(
    [property: JsonPropertyName("column")] string Column,
    [property: JsonPropertyName("desc")] bool Desc = false);

public record RelatedGroup(
    int Depth, string Direction, string ForeignKey, string FromTable, string Table,
    int RowCount, bool Truncated, IReadOnlyList<Row> Rows);

public record RelatedRowsResult(
    bool Success, string Database, string Table, Row? Root,
    IReadOnlyList<RelatedGroup> Related, int QueriesUsed, bool BudgetReached, string? ErrorMessage = null);

public record PlanOperator(string Physical, string Logical, double EstimatedRows, double Cost, string? Object);

public record ExplainResult(
    bool Success, string Database, double? EstimatedCost, IReadOnlyList<PlanOperator> Operators,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> MissingIndexes, string? ErrorMessage = null);

public record ObjectDefinition(string Schema, string Name, string Type, string? Modified, string? Definition, bool Truncated);

public record ObjectDefinitionResult(
    bool Success, string Database, IReadOnlyList<ObjectDefinition> Objects, string? ErrorMessage = null);
