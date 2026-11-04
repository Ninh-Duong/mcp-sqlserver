using Microsoft.Data.SqlClient;

namespace McpSqlServer;

public class ConnectionOptions
{
    public string ServerAlias { get; set; } = "DEV";
    public string Server { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int? Port { get; set; } = 1433;
    public string Database { get; set; } = "master";
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; } = false;
    public int ConnectTimeout { get; set; } = 10;
    public int QueryTimeout { get; set; } = 30;

    // --- Read-only investigation settings (see docs/CONFIGURATION.md) ---

    /// <summary>Databases the investigation tools may touch. Empty = every database except <see cref="DefaultBlockedDatabases"/>.</summary>
    public List<string> AllowedDatabases { get; set; } = new();

    /// <summary>"read_uncommitted" (default, never blocks production writers) or "read_committed".</summary>
    public string Isolation { get; set; } = "read_uncommitted";

    public int LockTimeoutMs { get; set; } = 3000;

    /// <summary>Default row cap per query. Hard cap is <see cref="HardMaxRows"/>.</summary>
    public int MaxRows { get; set; } = 100;

    /// <summary>Regex of column names whose values are replaced with '***'.</summary>
    public string MaskedColumnPatterns { get; set; } = "password|passwd|pwd|token|secret|salt|hash|api_?key";

    /// <summary>Where each config value group came from: env | hub | local | default | unset. Never holds values.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, string> ConfigSources { get; set; } = new();

    /// <summary>Folder of the offline schema snapshot (written by the CLI `scan`, read by search_context / get_object_context).</summary>
    public string ContextDirectory { get; set; } = "./ai-context";

    public const int HardMaxRows = 1000;
    public const string ApplicationName = "mcp-sqlserver-readonly";
    public static readonly string[] DefaultBlockedDatabases = ["master", "msdb", "model", "tempdb", "rdsadmin"];

    public bool IsDatabaseAllowed(string? database)
    {
        if (string.IsNullOrWhiteSpace(database)) return false;
        var name = database.Trim();
        return AllowedDatabases.Count > 0
            ? AllowedDatabases.Contains(name, StringComparer.OrdinalIgnoreCase)
            : !DefaultBlockedDatabases.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Copy of these options pointing at another initial database.</summary>
    public ConnectionOptions WithDatabase(string database)
    {
        var copy = (ConnectionOptions)MemberwiseClone();
        copy.Database = database;
        copy.AllowedDatabases = new List<string>(AllowedDatabases);
        copy.ConfigSources = new Dictionary<string, string>(ConfigSources);
        return copy;
    }

    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Server))
        {
            errors.Add("Server name cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            errors.Add("Username cannot be empty.");
        }

        if (string.IsNullOrEmpty(Password))
        {
            errors.Add("Password cannot be empty.");
        }

        if (Port.HasValue && (Port.Value < 1 || Port.Value > 65535))
        {
            errors.Add("Port must be between 1 and 65535.");
        }

        if (ConnectTimeout < 1 || ConnectTimeout > 120)
        {
            errors.Add("Connect timeout must be between 1 and 120 seconds.");
        }

        if (QueryTimeout < 1 || QueryTimeout > 120)
        {
            errors.Add("Query timeout must be between 1 and 120 seconds.");
        }

        return errors;
    }

    public string BuildConnectionString()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid configuration: {string.Join(", ", errors)}");
        }

        var builder = new SqlConnectionStringBuilder();

        var dataSource = Server.Trim();
        if (Port.HasValue && !dataSource.Contains(',') && !dataSource.Contains('\\'))
        {
            dataSource = $"{dataSource},{Port.Value}";
        }

        builder.DataSource = dataSource;
        builder.UserID = Username;
        builder.Password = Password;
        builder.InitialCatalog = string.IsNullOrWhiteSpace(Database) ? "master" : Database.Trim();
        builder.Encrypt = Encrypt;
        builder.TrustServerCertificate = TrustServerCertificate;
        builder.ConnectTimeout = ConnectTimeout;
        builder.Pooling = true;
        builder.ApplicationIntent = ApplicationIntent.ReadOnly;
        builder.ApplicationName = ApplicationName;

        return builder.ConnectionString;
    }

    /// <summary>Environment variables only (MSSQL_*). No file is read.</summary>
    public static ConnectionOptions FromEnvironment()
        => Configuration.ConfigLoader.Load(localPath: string.Empty, centralConfigPath: string.Empty).Options;

    public bool HasRequiredCredentials()
    {
        return !string.IsNullOrWhiteSpace(Server) &&
               !string.IsNullOrWhiteSpace(Username) &&
               !string.IsNullOrEmpty(Password);
    }

    /// <summary>Reads one local config file only (no environment, no hub). Used by the interactive menu.</summary>
    public static (bool loaded, ConnectionOptions? options) TryLoadFromFile(string relativePath = "dbconfig.json")
    {
        if (!File.Exists(relativePath))
        {
            return (false, null);
        }

        var options = Configuration.ConfigLoader.Load(relativePath, getEnv: _ => null, centralConfigPath: string.Empty).Options;
        return (options.HasRequiredCredentials(), options);
    }

    /// <summary>Hub-standard layered load: env > CENTRAL_CONFIG_PATH "mssql" section > local file > defaults.</summary>
    public static ConnectionOptions FromConfigOrEnvironment(string relativePath = "dbconfig.json")
        => Configuration.ConfigLoader.Load(relativePath).Options;

    public string GetDisplaySummary()
    {
        var alias = string.IsNullOrWhiteSpace(ServerAlias) ? "DEV" : ServerAlias;
        var trust = TrustServerCertificate ? "Enabled (Trust)" : "Disabled (Verify CA)";
        var db = string.IsNullOrWhiteSpace(Database) ? "master" : Database;
        return $"Alias: {alias} | Server: {Server} | User: {Username} | Initial DB: {db} | Port: {Port?.ToString() ?? "default"} | TrustCert: {trust}";
    }
}
