using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace McpSqlServer;

public record ConnectionCheckResult(
    bool Success,
    long ElapsedMs,
    string? ServerVersion = null,
    string? ErrorMessage = null
);

public record DatabaseItem(
    int DatabaseId,
    string Name,
    string State,
    bool? HasAccess,
    bool IsSystem
);

public record DatabaseListingResult(
    bool Success,
    int VisibleCount,
    int SystemCount,
    int OtherCount,
    IReadOnlyList<DatabaseItem> Databases,
    string Scope = "visible_to_current_login",
    string? ErrorMessage = null
);

public record TableItem(
    string Schema,
    string Name
);

public record TableListingResult(
    bool Success,
    string Database,
    int TableCount,
    IReadOnlyList<TableItem> Tables,
    string? ErrorMessage = null
);

public class SqlServerService
{
    private const string ListDatabasesQuery = @"
SELECT
    database_id,
    name,
    state_desc,
    HAS_DBACCESS(name) AS has_access,
    CASE WHEN database_id BETWEEN 1 AND 4 THEN 1 ELSE 0 END AS is_system
FROM sys.databases
ORDER BY name;";

    public async Task<ConnectionCheckResult> TestConnectionAsync(ConnectionOptions options, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Logger.Process("CONNECT", $"Đang thử kết nối tới {options.Server} (User: {options.Username})...");

        try
        {
            var connectionString = options.BuildConnectionString();
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT @@VERSION;";
            cmd.CommandTimeout = options.QueryTimeout;

            var versionObj = await cmd.ExecuteScalarAsync(cancellationToken);
            var versionStr = versionObj?.ToString()?.Split('\n')[0].Trim();

            stopwatch.Stop();
            Logger.Process("CONNECT", $"Kết nối thành công trong {stopwatch.ElapsedMilliseconds} ms ({versionStr}).");

            return new ConnectionCheckResult(
                Success: true,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                ServerVersion: versionStr
            );
        }
        catch (SqlException ex)
        {
            stopwatch.Stop();
            var friendlyError = FormatSqlException(ex, options);
            Logger.Error($"Lỗi kết nối SQL Server (Mã lỗi {ex.Number})", ex);

            return new ConnectionCheckResult(
                Success: false,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                ErrorMessage: friendlyError
            );
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.Error("Lỗi kết nối không xác định", ex);

            return new ConnectionCheckResult(
                Success: false,
                ElapsedMs: stopwatch.ElapsedMilliseconds,
                ErrorMessage: $"Lỗi kết nối: {Logger.Sanitize(ex.Message)}"
            );
        }
    }

    public async Task<DatabaseListingResult> ListDatabasesAsync(ConnectionOptions options, CancellationToken cancellationToken = default)
    {
        Logger.Process("QUERY", "Đang truy vấn danh mục sys.databases...");

        try
        {
            var connectionString = options.BuildConnectionString();
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = ListDatabasesQuery;
            cmd.CommandTimeout = options.QueryTimeout;

            var list = new List<DatabaseItem>();

            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var dbId = reader.GetInt32(0);
                var name = reader.GetString(1);
                var state = reader.IsDBNull(2) ? "UNKNOWN" : reader.GetString(2);

                bool? hasAccess = null;
                if (!reader.IsDBNull(3))
                {
                    var accessInt = reader.GetInt32(3);
                    hasAccess = accessInt == 1;
                }

                var isSystem = reader.GetInt32(4) == 1;

                list.Add(new DatabaseItem(dbId, name, state, hasAccess, isSystem));
            }

            var systemCount = list.Count(d => d.IsSystem);
            var otherCount = list.Count(d => !d.IsSystem);

            Logger.Process("QUERY", $"Truy vấn hoàn tất: {list.Count} database (Hệ thống: {systemCount}, Khác: {otherCount}).");

            return new DatabaseListingResult(
                Success: true,
                VisibleCount: list.Count,
                SystemCount: systemCount,
                OtherCount: otherCount,
                Databases: list
            );
        }
        catch (SqlException ex)
        {
            var friendlyError = FormatSqlException(ex, options);
            Logger.Error($"Lỗi khi truy vấn catalog sys.databases (Mã {ex.Number})", ex);

            return new DatabaseListingResult(
                Success: false,
                VisibleCount: 0,
                SystemCount: 0,
                OtherCount: 0,
                Databases: Array.Empty<DatabaseItem>(),
                ErrorMessage: friendlyError
            );
        }
        catch (Exception ex)
        {
            Logger.Error("Lỗi hệ thống khi liệt kê database", ex);

            return new DatabaseListingResult(
                Success: false,
                VisibleCount: 0,
                SystemCount: 0,
                OtherCount: 0,
                Databases: Array.Empty<DatabaseItem>(),
                ErrorMessage: $"Lỗi: {Logger.Sanitize(ex.Message)}"
            );
        }
    }

    public async Task<TableListingResult> ListTablesAsync(ConnectionOptions options, string databaseName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return new TableListingResult(false, string.Empty, 0, Array.Empty<TableItem>(), "Tên database không được để trống.");
        }

        var dbName = databaseName.Trim();
        Logger.Process("QUERY", $"Đang truy vấn danh sách table trong database '{dbName}'...");

        try
        {
            var targetOptions = new ConnectionOptions
            {
                Server = options.Server,
                Username = options.Username,
                Password = options.Password,
                Port = options.Port,
                Database = dbName,
                Encrypt = options.Encrypt,
                TrustServerCertificate = options.TrustServerCertificate,
                ConnectTimeout = options.ConnectTimeout,
                QueryTimeout = options.QueryTimeout
            };

            var connectionString = targetOptions.BuildConnectionString();
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT 
    s.name AS schema_name,
    t.name AS table_name
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
ORDER BY s.name, t.name;";
            cmd.CommandTimeout = options.QueryTimeout;

            var tables = new List<TableItem>();
            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString(0);
                var tableName = reader.GetString(1);
                tables.Add(new TableItem(schema, tableName));
            }

            Logger.Process("QUERY", $"Tìm thấy {tables.Count} table trong database '{dbName}'.");

            return new TableListingResult(
                Success: true,
                Database: dbName,
                TableCount: tables.Count,
                Tables: tables
            );
        }
        catch (SqlException ex)
        {
            var friendlyError = FormatSqlException(ex, options);
            Logger.Error($"Lỗi khi truy vấn table trong database '{dbName}' (Mã {ex.Number})", ex);

            return new TableListingResult(
                Success: false,
                Database: dbName,
                TableCount: 0,
                Tables: Array.Empty<TableItem>(),
                ErrorMessage: friendlyError
            );
        }
        catch (Exception ex)
        {
            Logger.Error($"Lỗi hệ thống khi lấy danh sách table trong database '{dbName}'", ex);

            return new TableListingResult(
                Success: false,
                Database: dbName,
                TableCount: 0,
                Tables: Array.Empty<TableItem>(),
                ErrorMessage: $"Lỗi: {Logger.Sanitize(ex.Message)}"
            );
        }
    }

    private static string FormatSqlException(SqlException ex, ConnectionOptions options)
    {
        return ex.Number switch
        {
            18456 => "Đăng nhập thất bại. Vui lòng kiểm tra lại Username và Password.",
            53 => $"Không thể kết nối đến server '{options.Server}'. Vui lòng kiểm tra địa chỉ server, port hoặc cấu hình tường lửa.",
            4060 => $"Không thể mở initial database '{options.Database}'. Tài khoản có thể chưa được cấp quyền truy cập database này.",
            -2 => "Hết thời gian chờ kết nối (Connection Timeout). Server không phản hồi kịp thời.",
            -2146893019 => "Lỗi chứng chỉ TLS/SSL: Chứng chỉ server không được tin cậy. Hãy bật 'Trust Server Certificate' nếu dùng Self-signed cert.",
            _ => $"Lỗi SQL Server ({ex.Number}): {Logger.Sanitize(ex.Message)}"
        };
    }
}
