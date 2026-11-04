using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace McpSqlServer.Security;

/// <summary>Outcome of validating one SQL text. <see cref="Sql"/> is the exact text that was parsed.</summary>
public sealed record SelectValidation(bool IsValid, string? Error, string? Sql, IReadOnlyCollection<string> ReferencedDatabases)
{
    public static SelectValidation Reject(string error) => new(false, error, null, Array.Empty<string>());
}

/// <summary>
/// Allowlist validator. Accepts exactly one T-SQL batch holding exactly one plain SELECT statement
/// (CTEs allowed) and rejects everything else. Works on the ScriptDom AST, never on regex-cleaned text.
/// The caller must send <see cref="SelectValidation.Sql"/> (the validated text) to the server, unchanged.
/// </summary>
public static class SelectOnlyValidator
{
    public const int MaxSqlLength = 20_000;

    // AST node types that read from outside the database, pass text through to another engine, or have side effects.
    private static readonly HashSet<string> ForbiddenNodeTypes = new(StringComparer.Ordinal)
    {
        "OpenRowsetTableReference", "BulkOpenRowset", "InternalOpenRowset", "OpenRowsetCosmos",
        "OpenQueryTableReference", "AdHocTableReference",
        "NextValueForExpression",
        "SelectSetVariable",
        "BrowseForClause",
    };

    // Table hints that take or hold locks stronger than a plain read.
    private static readonly HashSet<string> ForbiddenTableHints = new(StringComparer.OrdinalIgnoreCase)
    {
        "UpdLock", "XLock", "TabLock", "TabLockX", "HoldLock", "PagLock", "Serializable", "RepeatableRead",
    };

    // Built-in table functions that expose files, traces or log internals.
    private static readonly HashSet<string> ForbiddenFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "fn_xe_file_target_read_file", "fn_get_audit_file", "fn_trace_gettable", "fn_dump_dblog", "fn_dblog", "fn_dblog_xtp",
    };

    public static SelectValidation Validate(string? sql, Func<string, bool>? databaseAllowed = null)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return SelectValidation.Reject("SQL query cannot be empty.");

        if (sql.Length > MaxSqlLength)
            return SelectValidation.Reject($"SQL query is longer than {MaxSqlLength} characters.");

        TSqlFragment fragment;
        IList<ParseError> errors;
        try
        {
            var parser = new TSql170Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(sql);
            fragment = parser.Parse(reader, out errors);
        }
        catch (Exception ex)
        {
            return SelectValidation.Reject($"SQL could not be parsed ({ex.GetType().Name}).");
        }

        if (errors.Count > 0)
        {
            var first = errors[0];
            return SelectValidation.Reject($"SQL syntax error at line {first.Line}, column {first.Column}: {first.Message}");
        }

        if (fragment is not TSqlScript script || script.Batches.Count != 1)
            return SelectValidation.Reject("Exactly one SQL batch is allowed.");

        var statements = script.Batches[0].Statements;
        if (statements.Count != 1)
            return SelectValidation.Reject("Exactly one statement is allowed (no multi-statement batches).");

        if (statements[0] is not SelectStatement select)
            return SelectValidation.Reject("Only a single SELECT statement (optionally with CTEs) is allowed. Modifying and administrative statements are prohibited.");

        if (select.Into is not null)
            return SelectValidation.Reject("SELECT ... INTO is not allowed because it creates a table.");

        var visitor = new GuardVisitor();
        select.Accept(visitor);

        if (visitor.Violation is not null)
            return SelectValidation.Reject(visitor.Violation);

        if (databaseAllowed is not null)
        {
            foreach (var db in visitor.Databases)
            {
                if (!databaseAllowed(db))
                    return SelectValidation.Reject($"Database '{db}' is not allowed.");
            }
        }

        return new SelectValidation(true, null, sql, visitor.Databases);
    }

    private sealed class GuardVisitor : TSqlFragmentVisitor
    {
        public string? Violation { get; private set; }
        public HashSet<string> Databases { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(TSqlFragment fragment)
        {
            if (Violation is not null) return;

            var typeName = fragment.GetType().Name;
            if (ForbiddenNodeTypes.Contains(typeName))
            {
                Violation = $"'{typeName}' is not allowed in a read-only query.";
                return;
            }

            switch (fragment)
            {
                case SchemaObjectName name:
                    CheckSchemaObjectName(name);
                    break;

                case TableHint hint when ForbiddenTableHints.Contains(hint.HintKind.ToString()):
                    Violation = $"Table hint '{hint.HintKind}' is not allowed because it holds locks.";
                    break;

                case OptimizerHint optimizer when optimizer.HintKind.ToString().Contains("QueryTraceOn", StringComparison.OrdinalIgnoreCase):
                    Violation = "QUERYTRACEON is not allowed.";
                    break;

                case FunctionCall call:
                    CheckFunctionCall(call);
                    break;

                case SchemaObjectFunctionTableReference tableFunction:
                    CheckFunctionName(tableFunction.SchemaObject?.BaseIdentifier?.Value);
                    break;
            }
        }

        private void CheckSchemaObjectName(SchemaObjectName name)
        {
            if (name.ServerIdentifier is not null)
            {
                Violation = "Linked-server (four-part) names are not allowed.";
                return;
            }

            if (name.DatabaseIdentifier is not null)
                Databases.Add(name.DatabaseIdentifier.Value);
        }

        private void CheckFunctionCall(FunctionCall call)
        {
            CheckFunctionName(call.FunctionName?.Value);

            if (call.CallTarget is MultiPartIdentifierCallTarget target)
            {
                // The call target holds the prefix only (the function name is separate): schema | database.schema | server.database.schema
                var parts = target.MultiPartIdentifier.Identifiers;
                if (parts.Count >= 3)
                    Violation = "Linked-server (four-part) names are not allowed.";
                else if (parts.Count == 2)
                    Databases.Add(parts[0].Value);
            }
        }

        private void CheckFunctionName(string? name)
        {
            if (Violation is not null || string.IsNullOrEmpty(name)) return;

            if (name.StartsWith("xp_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) ||
                ForbiddenFunctions.Contains(name))
            {
                Violation = $"'{name}' is not allowed in a read-only query.";
            }
        }
    }
}
