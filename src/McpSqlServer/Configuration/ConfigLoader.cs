using System.Text.Json;

namespace McpSqlServer.Configuration;

/// <summary>
/// Hub-standard layered configuration (mcp-hub docs/child-mcp-compatibility.md).
/// Priority per value: environment (MSSQL_*) > hub section ("mssql" in CENTRAL_CONFIG_PATH) > local dbconfig.json > default.
/// An empty string counts as unset. A missing hub file is a warning, never a failure.
/// Reports where each value GROUP came from (never the values themselves).
/// </summary>
public static class ConfigLoader
{
    public const string CentralConfigEnvVar = "CENTRAL_CONFIG_PATH";
    public static readonly string[] HubSectionNames = ["mssql", "sqlserver"];

    private sealed record Key(string Canonical, string Group, string[] EnvVars, string[] JsonNames);

    private static readonly Key[] Keys =
    [
        new("server_alias", "server", ["MSSQL_SERVER_ALIAS"], ["serveralias", "alias"]),
        new("server", "server", ["MSSQL_SERVER"], ["server", "host"]),
        new("port", "server", ["MSSQL_PORT"], ["port"]),
        new("database", "server", ["MSSQL_DATABASE"], ["database", "initialcatalog"]),
        new("encrypt", "server", ["MSSQL_ENCRYPT"], ["encrypt"]),
        new("trust_server_certificate", "server", ["MSSQL_TRUST_SERVER_CERTIFICATE", "MSSQL_TRUST_CERTIFICATE", "MSSQL_TRUST_CERT"], ["trustservercertificate", "trustcertificate"]),
        new("username", "credentials", ["MSSQL_USERNAME"], ["username", "user", "userid"]),
        new("password", "credentials", ["MSSQL_PASSWORD"], ["password"]),
        new("connect_timeout", "limits", ["MSSQL_CONNECT_TIMEOUT"], ["connecttimeout", "connecttimeoutseconds"]),
        new("query_timeout", "limits", ["MSSQL_QUERY_TIMEOUT"], ["querytimeout", "querytimeoutseconds"]),
        new("allowed_databases", "limits", ["MSSQL_ALLOWED_DATABASES"], ["alloweddatabases"]),
        new("isolation", "limits", ["MSSQL_ISOLATION"], ["isolation"]),
        new("lock_timeout_ms", "limits", ["MSSQL_LOCK_TIMEOUT_MS"], ["locktimeoutms"]),
        new("max_rows", "limits", ["MSSQL_MAX_ROWS"], ["maxrows"]),
        new("masked_column_patterns", "limits", ["MSSQL_MASKED_COLUMN_PATTERNS"], ["maskedcolumnpatterns"]),
        new("context_directory", "limits", ["MSSQL_CONTEXT_DIRECTORY"], ["contextdirectory"]),
    ];

    /// <summary>Every supported key with its environment variables. Used by the docs consistency tests.</summary>
    internal static IReadOnlyList<(string Key, string[] EnvVars)> KnownKeys => Keys.Select(k => (k.Canonical, k.EnvVars)).ToList();

    private sealed class Layer(string name, Dictionary<string, string> values)
    {
        public string Name { get; } = name;
        public Dictionary<string, string> Values { get; } = values;
    }

    public sealed record LoadResult(ConnectionOptions Options, IReadOnlyList<string> Warnings);

    public static LoadResult Load(
        string localPath = "dbconfig.json",
        Func<string, string?>? getEnv = null,
        string? centralConfigPath = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var warnings = new List<string>();

        var layers = new List<Layer>
        {
            new("env", ReadEnvironment(getEnv)),
        };

        var central = centralConfigPath ?? getEnv(CentralConfigEnvVar);
        if (!string.IsNullOrWhiteSpace(central))
        {
            var path = ResolveRelativeToRepo(central.Trim());
            if (File.Exists(path))
            {
                var hub = ReadJsonSection(path, warnings);
                if (hub is not null) layers.Add(new Layer("hub", hub));
            }
            else
            {
                warnings.Add($"{CentralConfigEnvVar} is set but the file was not found; falling back to local configuration.");
            }
        }

        if (File.Exists(localPath))
        {
            var local = ReadJsonFile(localPath, warnings);
            if (local is not null) layers.Add(new Layer("local", local));
        }

        var options = new ConnectionOptions();
        var sources = new Dictionary<string, string>();

        foreach (var key in Keys)
        {
            var layer = layers.FirstOrDefault(l => key.JsonNames.Any(l.Values.ContainsKey) || l.Values.ContainsKey(key.Canonical));
            if (layer is null) continue;

            var raw = key.JsonNames.Where(layer.Values.ContainsKey).Select(n => layer.Values[n]).FirstOrDefault()
                      ?? layer.Values[key.Canonical];

            Apply(options, key.Canonical, raw, warnings);

            // The group's source is its highest-priority layer.
            if (!sources.TryGetValue(key.Group, out var current) || Rank(layer.Name) < Rank(current))
                sources[key.Group] = layer.Name;
        }

        foreach (var group in new[] { "server", "credentials", "limits" })
        {
            if (!sources.ContainsKey(group))
                sources[group] = group == "limits" ? "default" : "unset";
        }

        options.ConfigSources = sources;
        foreach (var warning in warnings) Logger.Warn(warning);
        return new LoadResult(options, warnings);
    }

    private static int Rank(string layer) => layer switch { "env" => 0, "hub" => 1, "local" => 2, _ => 3 };

    private static Dictionary<string, string> ReadEnvironment(Func<string, string?> getEnv)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            foreach (var variable in key.EnvVars)
            {
                var value = getEnv(variable);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values[key.Canonical] = value.Trim();
                    break;
                }
            }
        }
        return values;
    }

    private static Dictionary<string, string>? ReadJsonFile(string path, List<string> warnings)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return Flatten(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warnings.Add($"Unable to read configuration file '{Path.GetFileName(path)}' ({ex.GetType().Name}).");
            return null;
        }
    }

    private static Dictionary<string, string>? ReadJsonSection(string path, List<string> warnings)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (HubSectionNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    return Flatten(property.Value);
                }
            }

            warnings.Add($"{CentralConfigEnvVar} has no '{HubSectionNames[0]}' section; falling back to local configuration.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warnings.Add($"Unable to read the hub configuration ({ex.GetType().Name}); falling back to local configuration.");
            return null;
        }
    }

    /// <summary>Normalizes keys (lowercase, no '_' or '-') so "trust_server_certificate" and "TrustServerCertificate" match; drops empty values.</summary>
    private static Dictionary<string, string> Flatten(JsonElement root)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object) return values;

        foreach (var property in root.EnumerateObject())
        {
            var text = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Array => string.Join(",", property.Value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString())),
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(text)) continue;
            values[Normalize(property.Name)] = text.Trim();
        }

        return values;
    }

    private static string Normalize(string name) => name.Replace("_", "").Replace("-", "").ToLowerInvariant();

    private static void Apply(ConnectionOptions options, string canonical, string raw, List<string> warnings)
    {
        switch (canonical)
        {
            case "server_alias": options.ServerAlias = raw.ToUpperInvariant(); break;
            case "server": options.Server = raw; break;
            case "database": options.Database = raw; break;
            case "username": options.Username = raw; break;
            case "password": options.Password = raw; break;
            case "isolation": options.Isolation = raw.ToLowerInvariant(); break;
            case "masked_column_patterns": options.MaskedColumnPatterns = raw; break;
            case "context_directory": options.ContextDirectory = raw; break;
            case "allowed_databases":
                options.AllowedDatabases = raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                break;
            case "port": SetInt(raw, canonical, v => options.Port = v, warnings); break;
            case "connect_timeout": SetInt(raw, canonical, v => options.ConnectTimeout = v, warnings); break;
            case "query_timeout": SetInt(raw, canonical, v => options.QueryTimeout = v, warnings); break;
            case "lock_timeout_ms": SetInt(raw, canonical, v => options.LockTimeoutMs = v, warnings); break;
            case "max_rows": SetInt(raw, canonical, v => options.MaxRows = Math.Clamp(v, 1, ConnectionOptions.HardMaxRows), warnings); break;
            case "encrypt": SetBool(raw, canonical, v => options.Encrypt = v, warnings); break;
            case "trust_server_certificate": SetBool(raw, canonical, v => options.TrustServerCertificate = v, warnings); break;
        }
    }

    private static void SetInt(string raw, string key, Action<int> set, List<string> warnings)
    {
        if (int.TryParse(raw, out var value)) set(value);
        else warnings.Add($"Ignored '{key}': not a number.");
    }

    private static void SetBool(string raw, string key, Action<bool> set, List<string> warnings)
    {
        if (bool.TryParse(raw, out var value)) set(value);
        else warnings.Add($"Ignored '{key}': not true/false.");
    }

    /// <summary>A relative CENTRAL_CONFIG_PATH resolves against the repo root (the hub starts the child there), not an arbitrary cwd.</summary>
    private static string ResolveRelativeToRepo(string path)
    {
        if (Path.IsPathRooted(path)) return path;

        var fromCwd = Path.GetFullPath(path);
        if (File.Exists(fromCwd)) return fromCwd;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "McpSqlServer.slnx")) || Directory.Exists(Path.Combine(dir.FullName, "src", "McpSqlServer")))
                return Path.GetFullPath(Path.Combine(dir.FullName, path));
        }

        return fromCwd;
    }
}
