using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace McpSqlServer.Security;

/// <summary>A named query parameter. <see cref="AnsiString"/> binds a string as varchar so it can seek varchar indexes.</summary>
public sealed record QueryParameter(string Name, object? Value, bool AnsiString = false);

/// <summary>
/// The ONLY gateway from MCP tools to SQL Server (RULES.md #1). Every statement is:
///  1. validated as a single SELECT by <see cref="SelectOnlyValidator"/> (the validated text is what is sent),
///  2. run inside a transaction that is ALWAYS rolled back,
///  3. run with session guards (isolation, lock timeout, ROWCOUNT, NOCOUNT) and ApplicationIntent=ReadOnly,
///  4. limited in rows, cell size and total response size, with sensitive columns masked.
/// </summary>
public sealed class ReadOnlyQueryExecutor
{
    public const int MaxCellChars = 2_000;
    public const int MaxLargeCellChars = 190_000;
    public const int MaxResponseChars = 200_000;
    private const int MaxDatabaseNameLength = 128;

    private readonly ConnectionOptions _options;
    private readonly Regex? _maskedColumns;

    public ReadOnlyQueryExecutor(ConnectionOptions options)
    {
        _options = options;
        try
        {
            _maskedColumns = string.IsNullOrWhiteSpace(options.MaskedColumnPatterns)
                ? null
                : new Regex(options.MaskedColumnPatterns, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            _maskedColumns = new Regex("password|token|secret", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    public ConnectionOptions Options => _options;

    /// <summary>Run a SELECT against an allowed database. Use for any text that came from a caller.</summary>
    /// <param name="maxCellChars">Per-cell text cap; raise it (up to <see cref="MaxLargeCellChars"/>) only for source-code style columns.</param>
    public Task<QueryExecutionResult> RunSelectAsync(
        string sql, string? database, int? maxRows = null,
        IReadOnlyList<QueryParameter>? parameters = null, CancellationToken cancellationToken = default,
        int maxCellChars = MaxCellChars)
        => ExecuteAsync(sql, database, maxRows, parameters, enforceDatabaseAllowList: true, explain: false, maxCellChars, cancellationToken);

    /// <summary>
    /// Run a fixed catalog SELECT written in this code base (for example sys.databases on master).
    /// Same validation, rollback and limits; only the target-database allow-list is skipped.
    /// </summary>
    public Task<QueryExecutionResult> RunCatalogSelectAsync(
        string sql, string? database = null, int? maxRows = null,
        IReadOnlyList<QueryParameter>? parameters = null, CancellationToken cancellationToken = default,
        int maxCellChars = MaxCellChars)
        => ExecuteAsync(sql, database, maxRows, parameters, enforceDatabaseAllowList: false, explain: false, maxCellChars, cancellationToken);

    /// <summary>Return the estimated plan (SHOWPLAN_XML) of a validated SELECT without executing it.</summary>
    public Task<QueryExecutionResult> ExplainAsync(
        string sql, string? database, CancellationToken cancellationToken = default)
        => ExecuteAsync(sql, database, 1, null, enforceDatabaseAllowList: true, explain: true, MaxLargeCellChars, cancellationToken);

    private async Task<QueryExecutionResult> ExecuteAsync(
        string sql, string? database, int? maxRows, IReadOnlyList<QueryParameter>? parameters,
        bool enforceDatabaseAllowList, bool explain, int maxCellChars, CancellationToken cancellationToken)
    {
        maxCellChars = Math.Clamp(maxCellChars, 100, MaxLargeCellChars);
        var targetDb = string.IsNullOrWhiteSpace(database) ? _options.Database : database.Trim();

        var configErrors = _options.Validate();
        if (configErrors.Count > 0)
            return Fail(targetDb, 0, $"SQL Server is not configured: {string.Join(" ", configErrors)}");

        if (string.IsNullOrWhiteSpace(targetDb) || targetDb.Length > MaxDatabaseNameLength || targetDb.Any(char.IsControl))
            return Fail(targetDb, 0, "Invalid database name.");

        if (enforceDatabaseAllowList && !_options.IsDatabaseAllowed(targetDb))
            return Fail(targetDb, 0, $"Database '{targetDb}' is not allowed. Use list_databases to see the allowed databases.");

        var validation = SelectOnlyValidator.Validate(sql, db => _options.IsDatabaseAllowed(db) || !enforceDatabaseAllowList);
        if (!validation.IsValid)
        {
            // Never log the SQL text (RULES.md #6): only the fact and the reason.
            Logger.Process("REJECT", $"Query rejected on '{targetDb}': {validation.Error}");
            return Fail(targetDb, 0, validation.Error!);
        }

        var cap = Math.Clamp(maxRows ?? _options.MaxRows, 1, ConnectionOptions.HardMaxRows);
        var sw = Stopwatch.StartNew();

        try
        {
            var connStr = _options.WithDatabase(targetDb).BuildConnectionString();
            await using var connection = new SqlConnection(connStr);
            await connection.OpenAsync(cancellationToken);

            try
            {
                await RunGuardAsync(connection, cap, cancellationToken);

                var isolation = _options.Isolation.Equals("read_committed", StringComparison.OrdinalIgnoreCase)
                    ? IsolationLevel.ReadCommitted
                    : IsolationLevel.ReadUncommitted;

                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(isolation, cancellationToken);
                try
                {
                    if (explain)
                    {
                        await using var showplanOn = new SqlCommand("SET SHOWPLAN_XML ON;", connection, transaction);
                        await showplanOn.ExecuteNonQueryAsync(cancellationToken);
                    }

                    try
                    {
                        return await ReadAsync(connection, transaction, validation.Sql!, parameters, targetDb, cap, maxCellChars, sw, cancellationToken);
                    }
                    finally
                    {
                        // Also on failure: SHOWPLAN must never stay on for a connection that returns to the pool.
                        if (explain)
                        {
                            try
                            {
                                await using var showplanOff = new SqlCommand("SET SHOWPLAN_XML OFF;", connection, transaction);
                                await showplanOff.ExecuteNonQueryAsync(CancellationToken.None);
                            }
                            catch (Exception) { /* connection already broken; it will not be reused */ }
                        }
                    }
                }
                finally
                {
                    // Unconditional ROLLBACK: even a successful read never commits anything.
                    try { await transaction.RollbackAsync(CancellationToken.None); }
                    catch (Exception) { /* transaction already aborted by the server */ }
                }
            }
            finally
            {
                try
                {
                    await using var reset = new SqlCommand("SET ROWCOUNT 0;", connection);
                    await reset.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (Exception) { /* connection already broken; it will not be reused */ }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            Logger.Process("ERROR", $"SQL error {ex.Number} on '{targetDb}'.");
            return Fail(targetDb, sw.ElapsedMilliseconds, FormatSqlException(ex, _options));
        }
        catch (Exception ex)
        {
            Logger.Process("ERROR", $"{ex.GetType().Name} on '{targetDb}'.");
            return Fail(targetDb, sw.ElapsedMilliseconds, $"Execution error ({ex.GetType().Name}).");
        }
    }

    private async Task RunGuardAsync(SqlConnection connection, int cap, CancellationToken cancellationToken)
    {
        var lockTimeout = Math.Clamp(_options.LockTimeoutMs, 0, 60_000);
        var guard =
            "SET NOCOUNT ON; SET XACT_ABORT ON; SET DEADLOCK_PRIORITY LOW; " +
            $"SET LOCK_TIMEOUT {lockTimeout}; SET ROWCOUNT {cap + 1};";

        await using var cmd = new SqlCommand(guard, connection) { CommandTimeout = _options.QueryTimeout };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<QueryExecutionResult> ReadAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        IReadOnlyList<QueryParameter>? parameters, string targetDb, int cap, int maxCellChars, Stopwatch sw,
        CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(sql, connection, transaction) { CommandTimeout = _options.QueryTimeout };

        if (parameters is not null)
        {
            foreach (var p in parameters)
            {
                var name = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
                var parameter = cmd.Parameters.Add(name, p.AnsiString ? SqlDbType.VarChar : InferType(p.Value));
                parameter.Value = p.Value ?? DBNull.Value;
            }
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var columns = UniqueColumnNames(reader);
        var masked = columns.Select(c => _maskedColumns is not null && SafeIsMatch(_maskedColumns, c)).ToArray();

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var truncated = false;
        var budget = MaxResponseChars;

        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= cap || budget <= 0)
            {
                truncated = true;
                break;
            }

            var row = new Dictionary<string, object?>(columns.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
            {
                object? value = masked[i] && !reader.IsDBNull(i) ? "***" : ConvertValue(reader.IsDBNull(i) ? null : reader.GetValue(i), maxCellChars);
                row[columns[i]] = value;
                budget -= columns[i].Length + (value is string s ? s.Length : 12);
            }
            rows.Add(row);
        }

        sw.Stop();
        Logger.Process("QUERY", $"{rows.Count} row(s) from '{targetDb}' in {sw.ElapsedMilliseconds} ms{(truncated ? " (truncated)" : "")}.");

        return new QueryExecutionResult(
            Success: true,
            Database: targetDb,
            RowCount: rows.Count,
            IsTruncated: truncated,
            ElapsedMs: sw.ElapsedMilliseconds,
            Columns: columns,
            Rows: rows);
    }

    private static List<string> UniqueColumnNames(SqlDataReader reader)
    {
        var names = new List<string>(reader.FieldCount);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (string.IsNullOrEmpty(name)) name = $"column_{i + 1}";
            var unique = name;
            for (var n = 2; !seen.Add(unique); n++) unique = $"{name}_{n}";
            names.Add(unique);
        }
        return names;
    }

    private static bool SafeIsMatch(Regex regex, string input)
    {
        try { return regex.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return true; } // fail closed: mask when unsure
    }

    private static SqlDbType InferType(object? value) => value switch
    {
        null => SqlDbType.NVarChar,
        bool => SqlDbType.Bit,
        byte or short or int => SqlDbType.Int,
        long => SqlDbType.BigInt,
        decimal => SqlDbType.Decimal,
        float or double => SqlDbType.Float,
        DateTime => SqlDbType.DateTime2,
        DateTimeOffset => SqlDbType.DateTimeOffset,
        Guid => SqlDbType.UniqueIdentifier,
        _ => SqlDbType.NVarChar,
    };

    internal static object? ConvertValue(object? value, int maxCellChars = MaxCellChars)
    {
        switch (value)
        {
            case null:
                return null;
            case byte[] bytes:
                var hex = Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, 256));
                return "0x" + hex + (bytes.Length > 256 ? $"... [{bytes.Length} bytes]" : string.Empty);
            case DateTime dt:
                return dt.ToString("yyyy-MM-dd HH:mm:ss.fff");
            case DateTimeOffset dto:
                return dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
            case TimeSpan ts:
                return ts.ToString();
            case Guid g:
                return g.ToString();
            case string s when s.Length > maxCellChars:
                return s[..maxCellChars] + $"... [truncated, {s.Length} chars]";
            default:
                return value;
        }
    }

    private static QueryExecutionResult Fail(string database, long elapsedMs, string message) => new(
        Success: false,
        Database: database,
        RowCount: 0,
        IsTruncated: false,
        ElapsedMs: elapsedMs,
        Columns: Array.Empty<string>(),
        Rows: Array.Empty<IReadOnlyDictionary<string, object?>>(),
        ErrorMessage: message);

    internal static string FormatSqlException(SqlException ex, ConnectionOptions options) => ex.Number switch
    {
        18456 => "Login failed. Please verify the username and password.",
        53 => $"Cannot connect to server '{options.Server}'. Verify the server address, port, and firewall rules.",
        4060 => "Cannot open the requested database. The login may not have access permissions.",
        -2 => "The query timed out. Narrow the query (filter by key or date) or raise query_timeout_seconds.",
        1222 => "Lock request timed out: another session holds a lock on this data. Retry, or use isolation read_uncommitted.",
        -2146893019 => "TLS/SSL certificate error: the server certificate is untrusted. Set trust_server_certificate if the server uses a self-signed certificate.",
        _ => $"SQL Server error ({ex.Number}): {Logger.Sanitize(ex.Message)}",
    };
}
