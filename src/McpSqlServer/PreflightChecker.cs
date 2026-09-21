using Microsoft.Data.SqlClient;

namespace McpSqlServer;

public record PreflightReport(
    bool Success,
    IReadOnlyList<string> PassedChecks,
    IReadOnlyList<string> Errors
);

public static class PreflightChecker
{
    public static PreflightReport RunPreflightChecks()
    {
        var passed = new List<string>();
        var errors = new List<string>();

        // 1. Kiểm tra runtime version (.NET 10+)
        if (Environment.Version.Major >= 10)
        {
            passed.Add($"Runtime .NET tương thích: v{Environment.Version}");
        }
        else
        {
            errors.Add($"Yêu cầu .NET 10 trở lên. Runtime hiện tại: {Environment.Version}");
        }

        // 2. Kiểm tra package Microsoft.Data.SqlClient
        try
        {
            var testConn = typeof(SqlConnection);
            passed.Add($"Thư viện SqlClient sẵn sàng: {testConn.Assembly.GetName().Name} v{testConn.Assembly.GetName().Version}");
        }
        catch (Exception ex)
        {
            errors.Add($"Thiếu hoặc lỗi thư viện Microsoft.Data.SqlClient: {ex.Message}. Vui lòng chạy 'dotnet restore'.");
        }

        // 3. Kiểm tra JSON serializer & Cryptography
        try
        {
            var jsonType = typeof(System.Text.Json.JsonSerializer);
            var cryptoType = typeof(System.Security.Cryptography.Aes);
            passed.Add("Thư viện System.Text.Json & Cryptography sẵn sàng.");
        }
        catch (Exception ex)
        {
            errors.Add($"Lỗi thư viện hệ thống: {ex.Message}");
        }

        // 4. Kiểm tra quyền ghi thư mục tương đối ./logs
        try
        {
            var testDir = Path.Combine(".", "logs");
            if (!Directory.Exists(testDir))
            {
                Directory.CreateDirectory(testDir);
            }
            var testFile = Path.Combine(testDir, ".preflight_check");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
            passed.Add("Quyền truy cập thư mục tương đối ./logs hoạt động tốt.");
        }
        catch (Exception ex)
        {
            errors.Add($"Không thể ghi vào thư mục tương đối ./logs: {ex.Message}");
        }

        return new PreflightReport(errors.Count == 0, passed, errors);
    }

    public static PreflightReport RunCoreSelfTests()
    {
        var passed = new List<string>();
        var errors = new List<string>();

        // Test 1: ConnectionOptions validation & builder with special characters
        try
        {
            var opts = new ConnectionOptions
            {
                Server = "localhost",
                Username = "sa",
                Password = "P@ssw;ord'\"=123",
                Port = 1433
            };
            var validation = opts.Validate();
            if (validation.Count > 0)
            {
                errors.Add($"SelfTest [ConnectionOptions.Validate] thất bại: {string.Join(", ", validation)}");
            }
            else
            {
                var connStr = opts.BuildConnectionString();
                if (!connStr.Contains("1433") || !connStr.Contains("sa"))
                {
                    errors.Add("SelfTest [ConnectionOptions.BuildConnectionString] kết quả không đúng.");
                }
                else
                {
                    passed.Add("SelfTest [ConnectionOptions]: Builder & Validation PASS.");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"SelfTest [ConnectionOptions] ngoại lệ: {ex.Message}");
        }

        // Test 2: Logger sanitization
        try
        {
            Logger.SetActivePassword("SecretP@ss999");
            var testMsg = "Failed login for user admin with password SecretP@ss999 on server";
            var sanitized = Logger.Sanitize(testMsg);
            if (sanitized.Contains("SecretP@ss999"))
            {
                errors.Add("SelfTest [Logger.Sanitize] không lọc được mật khẩu!");
            }
            else
            {
                passed.Add("SelfTest [Logger]: Password Sanitization PASS.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"SelfTest [Logger] ngoại lệ: {ex.Message}");
        }

        // Test 3: System database categorization logic
        try
        {
            var sysDb = new DatabaseItem(1, "master", "ONLINE", true, true);
            var userDb = new DatabaseItem(5, "AppDb", "ONLINE", true, false);
            if (!sysDb.IsSystem || userDb.IsSystem)
            {
                errors.Add("SelfTest [DatabaseItem] logic phân loại hệ thống/người dùng sai!");
            }
            else
            {
                passed.Add("SelfTest [DatabaseClassifier]: DB Categorization PASS.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"SelfTest [DatabaseItem] ngoại lệ: {ex.Message}");
        }

        return new PreflightReport(errors.Count == 0, passed, errors);
    }
}
