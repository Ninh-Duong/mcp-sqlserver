using System.Text.RegularExpressions;

namespace McpSqlServer.Investigation;

/// <summary>
/// Identifier helpers. Names are only ever put into SQL text AFTER they were confirmed to exist in the
/// catalog, and always through <see cref="Quote"/> (the C# equivalent of QUOTENAME). Values never go into SQL text.
/// </summary>
public static class SqlIdentifier
{
    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    public static string Quote(string schema, string name) => Quote(schema) + "." + Quote(name);

    /// <summary>Parses "Orders", "dbo.Orders", "[dbo].[Orders]". A database prefix is rejected: pass the database separately.</summary>
    public static bool TryParseObjectName(string? input, out string? schema, out string name, out string? error)
    {
        schema = null;
        name = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Object name cannot be empty.";
            return false;
        }

        var parts = Regex.Matches(input.Trim(), @"\[((?:[^\]]|\]\])+)\]|""([^""]+)""|([^.\[\]""]+)")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value.Replace("]]", "]") : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (parts.Count is 0 or > 2)
        {
            error = "Use 'table' or 'schema.table'. Pass the database in the 'database' parameter.";
            return false;
        }

        if (parts.Any(p => p.Length > 128 || p.Any(char.IsControl)))
        {
            error = "Invalid object name.";
            return false;
        }

        name = parts[^1];
        schema = parts.Count == 2 ? parts[0] : null;
        return true;
    }

    /// <summary>Wraps a search term as a LIKE pattern unless the caller already supplied wildcards.</summary>
    public static string? ToLikePattern(string? term)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;
        var trimmed = term.Trim();
        return trimmed.Contains('%') ? trimmed : "%" + trimmed + "%";
    }
}
