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
        var fullBaseDir = Path.GetFullPath(baseDirectory);
        var dbDir = Path.Combine(fullBaseDir, "servers", serverAlias, "databases", database);
        if (!Directory.Exists(dbDir))
        {
            return $"Database '{database}' on server '{serverAlias}' not found in ai-context snapshot.";
        }

        var normalizedName = objectName.Trim().Trim('[', ']');
        var pureName = normalizedName.Contains('.') ? normalizedName.Substring(normalizedName.LastIndexOf('.') + 1) : normalizedName;
        var type = objectType?.Trim().ToLowerInvariant();

        // 1. If table or unspecified, search in schema.compact.md
        if (type == "table" || string.IsNullOrEmpty(type))
        {
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

        // 3. Check cross-db dependencies
        if (type == "dependency" || string.IsNullOrEmpty(type))
        {
            var crossDbPath = Path.Combine(fullBaseDir, "servers", serverAlias, "CROSS_DB_DEPENDENCIES.compact.md");
            if (File.Exists(crossDbPath))
            {
                var lines = await File.ReadAllLinesAsync(crossDbPath, cancellationToken);
                var matchingLines = lines.Where(l => l.Contains(pureName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matchingLines.Count > 0)
                {
                    return $"### Cross-Database Dependencies for '{pureName}':\n" + string.Join("\n", matchingLines);
                }
            }
        }

        return $"Object '{objectName}' not found in ai-context snapshot for {serverAlias}/{database}.";
    }
}
