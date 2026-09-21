using Microsoft.Data.SqlClient;

namespace McpSqlServer;

public class ConnectionOptions
{
    public string Server { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int? Port { get; set; } = 1433;
    public string Database { get; set; } = "master";
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; } = false;
    public int ConnectTimeout { get; set; } = 10;
    public int QueryTimeout { get; set; } = 15;

    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Server))
        {
            errors.Add("Server name không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            errors.Add("Username không được để trống.");
        }

        if (string.IsNullOrEmpty(Password))
        {
            errors.Add("Password không được để trống.");
        }

        if (Port.HasValue && (Port.Value < 1 || Port.Value > 65535))
        {
            errors.Add("Port phải nằm trong khoảng từ 1 đến 65535.");
        }

        if (ConnectTimeout < 1 || ConnectTimeout > 120)
        {
            errors.Add("Connect timeout phải nằm trong khoảng từ 1 đến 120 giây.");
        }

        if (QueryTimeout < 1 || QueryTimeout > 120)
        {
            errors.Add("Query timeout phải nằm trong khoảng từ 1 đến 120 giây.");
        }

        return errors;
    }

    public string BuildConnectionString()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Cấu hình không hợp lệ: {string.Join(", ", errors)}");
        }

        var builder = new SqlConnectionStringBuilder();

        // Server format: if port is explicitly specified and not already in Server string
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

        return builder.ConnectionString;
    }

    public static ConnectionOptions FromEnvironment()
    {
        var options = new ConnectionOptions
        {
            Server = Environment.GetEnvironmentVariable("MSSQL_SERVER") ?? string.Empty,
            Username = Environment.GetEnvironmentVariable("MSSQL_USERNAME") ?? string.Empty,
            Password = Environment.GetEnvironmentVariable("MSSQL_PASSWORD") ?? string.Empty,
            Database = Environment.GetEnvironmentVariable("MSSQL_DATABASE") ?? "master"
        };

        var portStr = Environment.GetEnvironmentVariable("MSSQL_PORT");
        if (int.TryParse(portStr, out var port))
        {
            options.Port = port;
        }

        var trustCertStr = Environment.GetEnvironmentVariable("MSSQL_TRUST_CERTIFICATE");
        if (bool.TryParse(trustCertStr, out var trustCert))
        {
            options.TrustServerCertificate = trustCert;
        }

        var timeoutStr = Environment.GetEnvironmentVariable("MSSQL_CONNECT_TIMEOUT");
        if (int.TryParse(timeoutStr, out var timeout))
        {
            options.ConnectTimeout = timeout;
        }

        return options;
    }

    public bool HasRequiredCredentials()
    {
        return !string.IsNullOrWhiteSpace(Server) &&
               !string.IsNullOrWhiteSpace(Username) &&
               !string.IsNullOrEmpty(Password);
    }

    public static (bool loaded, ConnectionOptions? options) TryLoadFromFile(string relativePath = "dbconfig.json")
    {
        try
        {
            if (!File.Exists(relativePath))
            {
                return (false, null);
            }

            var json = File.ReadAllText(relativePath);
            var options = System.Text.Json.JsonSerializer.Deserialize<ConnectionOptions>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (options != null && options.HasRequiredCredentials())
            {
                return (true, options);
            }

            return (false, options);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Không thể đọc file config '{relativePath}': {ex.Message}");
            return (false, null);
        }
    }

    public static ConnectionOptions FromConfigOrEnvironment(string relativePath = "dbconfig.json")
    {
        var (loaded, fileOptions) = TryLoadFromFile(relativePath);
        if (loaded && fileOptions != null)
        {
            Logger.Info($"Đã nạp cấu hình database từ '{relativePath}'.");
            return fileOptions;
        }

        return FromEnvironment();
    }

    public string GetDisplaySummary()
    {
        var trust = TrustServerCertificate ? "Bật (Trust)" : "Tắt (Verify CA)";
        var db = string.IsNullOrWhiteSpace(Database) ? "master" : Database;
        return $"Server: {Server} | User: {Username} | Initial DB: {db} | Port: {Port?.ToString() ?? "mặc định"} | TrustCert: {trust}";
    }
}
