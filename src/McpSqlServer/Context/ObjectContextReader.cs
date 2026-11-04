using System.Text;

namespace McpSqlServer;

public static class ObjectContextReader
{
    public static async Task<string> GetObjectContextAsync(
        string baseDirectory,
        string serverAlias,
        string database,
        string objectName,
        string? objectType = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = (objectName ?? string.Empty).Trim().Trim('[', ']');
        var pureName = normalizedName.Contains('.') ? normalizedName.Substring(normalizedName.LastIndexOf('.') + 1) : normalizedName;
        var type = objectType?.Trim().ToLowerInvariant();

        // Every caller-supplied value becomes part of a path or a glob pattern: allow plain names only.
        if (!IsSafeSegment(serverAlias) || !IsSafeSegment(database) || !IsSafeSegment(pureName))
        {
            return "Invalid server_alias, database or name: path separators, '..' and wildcards are not allowed.";
        }

        var fullBaseDir = Path.GetFullPath(baseDirectory);
        var dbDir = Path.GetFullPath(Path.Combine(fullBaseDir, "servers", serverAlias, "databases", database));
        var serversRoot = Path.GetFullPath(Path.Combine(fullBaseDir, "servers")) + Path.DirectorySeparatorChar;
        if (!dbDir.StartsWith(serversRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(dbDir))
        {
            return $"Database '{database}' on server '{serverAlias}' not found in ai-context snapshot.";
        }

        // 1. If table or unspecified, check individual table files first, then fallback to schema.compact.md
        if (type == "table" || string.IsNullOrEmpty(type))
        {
            var tablesDir = Path.Combine(dbDir, "tables");
            if (Directory.Exists(tablesDir))
            {
                var tableMatch = Directory.GetFiles(tablesDir, $"*.{pureName}.compact.md")
                    .Concat(Directory.GetFiles(tablesDir, $"{pureName}.compact.md"))
                    .FirstOrDefault();

                if (tableMatch != null)
                {
                    return await File.ReadAllTextAsync(tableMatch, cancellationToken);
                }
            }

            var schemaFile = Path.Combine(dbDir, "schema.compact.md");
            if (File.Exists(schemaFile))
            {
                var lines = await File.ReadAllLinesAsync(schemaFile, cancellationToken);
                var section = new StringBuilder();
                bool recording = false;
                foreach (var line in lines)
                {
                    if (line.StartsWith("### ") && (line.Contains($".{pureName} ") || line.EndsWith($".{pureName}") || line.Contains($".{pureName}(")))
                    {
                        recording = true;
                        section.AppendLine(line);
                        continue;
                    }
                    if (recording)
                    {
                        if (line.StartsWith("### ") || line.StartsWith("## ")) break;
                        section.AppendLine(line);
                    }
                }
                if (section.Length > 0) return section.ToString().TrimEnd();
            }
        }

        // 2. Check routine SQL files (views, procedures, functions, triggers)
        var routineFolders = new[] { "procedures", "views", "functions", "triggers" };
        foreach (var folder in routineFolders)
        {
            if (!string.IsNullOrEmpty(type) && !folder.StartsWith(type)) continue;

            var folderPath = Path.Combine(dbDir, folder);
            if (Directory.Exists(folderPath))
            {
                var match = Directory.GetFiles(folderPath, $"*.{pureName}.sql")
                    .Concat(Directory.GetFiles(folderPath, $"{pureName}.sql"))
                    .FirstOrDefault();

                if (match != null)
                {
                    return await File.ReadAllTextAsync(match, cancellationToken);
                }
            }
        }

        // 3. Check cross-db dependencies (check local DB dependencies first, then server-wide)
        if (type == "dependency" || string.IsNullOrEmpty(type))
        {
            var localDepPath = Path.Combine(dbDir, "dependencies.compact.md");
            if (File.Exists(localDepPath))
            {
                var lines = await File.ReadAllLinesAsync(localDepPath, cancellationToken);
                var matchingLines = lines.Where(l => l.Contains(pureName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matchingLines.Count > 0)
                {
                    return $"### Cross-Database Dependencies for '{pureName}':\n" + string.Join("\n", matchingLines);
                }
            }

        }

        return $"Object '{objectName}' not found in ai-context snapshot for {serverAlias}/{database}.";
    }

    internal static bool IsSafeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value == "." || value == "..") return false;
        return value.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0']) < 0 && !value.Any(char.IsControl);
    }
}
