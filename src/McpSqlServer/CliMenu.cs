namespace McpSqlServer;

public class CliMenu
{
    private readonly SqlServerService _sqlService = new();
    private ConnectionOptions _options = new();
    private string _connectionStatus = "Chưa kiểm tra kết nối";
    private bool _hasConfigured = false;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        // Tự động kiểm tra file config dbconfig.json
        var (loaded, fileOptions) = ConnectionOptions.TryLoadFromFile("dbconfig.json");
        if (loaded && fileOptions != null)
        {
            _options = fileOptions;
            _hasConfigured = true;
            _connectionStatus = "Đã nạp từ dbconfig.json (Chưa kiểm tra)";
            Logger.SetActivePassword(_options.Password);
            Logger.Info("Tự động nạp thông tin kết nối từ file dbconfig.json thành công.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!Console.IsOutputRedirected && !Console.IsInputRedirected)
                {
                    Console.Clear();
                }
            }
            catch
            {
                // Ignore if not a valid interactive console buffer
            }

            PrintHeader();

            Console.WriteLine("1. Nhập / đổi thông tin kết nối");
            Console.WriteLine("2. Kiểm tra kết nối");
            Console.WriteLine("3. Đếm và liệt kê database");
            Console.WriteLine("4. Đếm và liệt kê table trong database");
            Console.WriteLine("0. Thoát");
            Console.WriteLine();
            Console.Write("Chọn chức năng (0-4): ");

            var choice = Console.ReadLine()?.Trim();
            if (choice == null) break; // Clean EOF
            Console.WriteLine();

            switch (choice)
            {
                case "1":
                    ConfigureConnection();
                    break;
                case "2":
                    await TestConnectionAsync(cancellationToken);
                    break;
                case "3":
                    await ListDatabasesAsync(cancellationToken);
                    break;
                case "4":
                    await ListTablesInDatabaseAsync(cancellationToken);
                    break;
                case "0":
                    Console.WriteLine("Tạm biệt!");
                    return;
                default:
                    Console.WriteLine("Lựa chọn không hợp lệ. Nhấn Enter để tiếp tục...");
                    Console.ReadLine();
                    break;
            }
        }
    }

    private void PrintHeader()
    {
        Console.WriteLine("========================================");
        Console.WriteLine("         MCP SQL SERVER CLI");
        Console.WriteLine("========================================");
        Console.WriteLine($"Trạng thái: {_connectionStatus}");
        if (_hasConfigured)
        {
            Console.WriteLine($"Cấu hình:   {_options.GetDisplaySummary()}");
        }
        Console.WriteLine("----------------------------------------");
    }

    private void ConfigureConnection()
    {
        Console.WriteLine("--- NHẬP THÔNG TIN KẾT NỐI ---");

        Console.Write($"Server name [hiện tại: {(_hasConfigured ? _options.Server : "chưa có")}]: ");
        var serverInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(serverInput) || !_hasConfigured)
        {
            _options.Server = serverInput ?? string.Empty;
        }

        Console.Write($"Username [hiện tại: {(_hasConfigured ? _options.Username : "chưa có")}]: ");
        var userInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(userInput) || !_hasConfigured)
        {
            _options.Username = userInput ?? string.Empty;
        }

        Console.Write("Password: ");
        var passwordInput = ReadPasswordMasked();
        if (!string.IsNullOrEmpty(passwordInput) || !_hasConfigured)
        {
            _options.Password = passwordInput;
            Logger.SetActivePassword(passwordInput);
        }

        Console.Write("Cấu hình nâng cao? [y/N]: ");
        var advChoice = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (advChoice == "y" || advChoice == "yes")
        {
            Console.Write($"Port [mặc định: {_options.Port?.ToString() ?? "1433"}]: ");
            var portInput = Console.ReadLine()?.Trim();
            if (int.TryParse(portInput, out var p)) _options.Port = p;

            Console.Write($"Initial Database [mặc định: {_options.Database}]: ");
            var dbInput = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(dbInput)) _options.Database = dbInput;

            Console.Write($"Trust Server Certificate? (bật nếu dùng self-signed cert) [y/N]: ");
            var trustInput = Console.ReadLine()?.Trim().ToLowerInvariant();
            _options.TrustServerCertificate = (trustInput == "y" || trustInput == "yes");

            Console.Write($"Connect Timeout (giây) [mặc định: {_options.ConnectTimeout}]: ");
            var timeoutInput = Console.ReadLine()?.Trim();
            if (int.TryParse(timeoutInput, out var t)) _options.ConnectTimeout = t;
        }

        var validationErrors = _options.Validate();
        if (validationErrors.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Cấu hình chưa hợp lệ:");
            foreach (var err in validationErrors)
            {
                Console.WriteLine($" - {err}");
            }
            Console.ResetColor();
            _hasConfigured = false;
        }
        else
        {
            _hasConfigured = true;
            _connectionStatus = "Đã cập nhật thông tin (Chưa kiểm tra)";
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Cập nhật thông tin thành công!");
            Console.ResetColor();

            Console.Write("\nLưu cấu hình vào file 'dbconfig.json' để tự động nạp lần sau? [y/N]: ");
            var saveChoice = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (saveChoice == "y" || saveChoice == "yes")
            {
                try
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(_options, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText("dbconfig.json", json);
                    Console.WriteLine("Đã lưu vào file dbconfig.json thành công (file này đã được .gitignore bảo vệ).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Không thể lưu file: {ex.Message}");
                }
            }
        }

        Console.WriteLine("\nNhấn Enter để quay lại menu...");
        Console.ReadLine();
    }

    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        if (!EnsureConfigured()) return;

        Console.WriteLine("Đang kiểm tra kết nối...");
        var result = await _sqlService.TestConnectionAsync(_options, cancellationToken);

        if (result.Success)
        {
            _connectionStatus = $"Đã kết nối thành công ({result.ElapsedMs} ms)";
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[THÀNH CÔNG] Đã kết nối trong {result.ElapsedMs} ms.");
            if (!string.IsNullOrEmpty(result.ServerVersion))
            {
                Console.WriteLine($"Phiên bản: {result.ServerVersion}");
            }
            Console.ResetColor();
        }
        else
        {
            _connectionStatus = "Kết nối thất bại";
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[THẤT BẠI] {result.ErrorMessage}");
            Console.ResetColor();
        }

        Console.WriteLine("\nNhấn Enter để tiếp tục...");
        Console.ReadLine();
    }

    private async Task ListDatabasesAsync(CancellationToken cancellationToken)
    {
        if (!EnsureConfigured()) return;

        Console.WriteLine("Đang lấy danh sách database...");
        var result = await _sqlService.ListDatabasesAsync(_options, cancellationToken);

        if (result.Success)
        {
            _connectionStatus = "Đã lấy danh sách DB thành công";
            Console.WriteLine();
            Console.WriteLine($"Database tài khoản nhìn thấy: {result.VisibleCount}");
            Console.WriteLine($"  Hệ thống: {result.SystemCount} | Khác: {result.OtherCount}");
            Console.WriteLine();

            Console.WriteLine("{0,-30} {1,-15} {2,-15}", "Tên", "Trạng thái", "Truy cập DB");
            Console.WriteLine(new string('-', 60));

            foreach (var db in result.Databases)
            {
                var accessText = db.HasAccess.HasValue ? (db.HasAccess.Value ? "Có" : "Không") : "Không xác định";
                Console.WriteLine("{0,-30} {1,-15} {2,-15}", db.Name, db.State, accessText);
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("* Lưu ý: Đây là danh sách theo quyền của tài khoản hiện tại trên SQL Server.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[LỖI] {result.ErrorMessage}");
            Console.ResetColor();
        }

        Console.WriteLine("\nNhấn Enter để quay lại menu...");
        Console.ReadLine();
    }

    private async Task ListTablesInDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!EnsureConfigured()) return;

        Console.Write($"Nhập tên database [mặc định: {_options.Database}]: ");
        var inputDb = Console.ReadLine()?.Trim();
        var targetDb = string.IsNullOrWhiteSpace(inputDb) ? _options.Database : inputDb;

        Console.WriteLine($"Đang lấy danh sách table trong database '{targetDb}'...");
        var result = await _sqlService.ListTablesAsync(_options, targetDb, cancellationToken);

        if (result.Success)
        {
            _connectionStatus = $"Đã lấy danh sách table ({targetDb})";
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Tổng số table trong database '{result.Database}': {result.TableCount}");
            Console.ResetColor();
            Console.WriteLine();

            if (result.TableCount == 0)
            {
                Console.WriteLine("Database không có table nào hoặc tài khoản chưa được cấp quyền SELECT metadata.");
            }
            else
            {
                Console.WriteLine("{0,-20} {1,-40}", "Schema", "Tên Table");
                Console.WriteLine(new string('-', 60));

                foreach (var table in result.Tables)
                {
                    Console.WriteLine("{0,-20} {1,-40}", table.Schema, table.Name);
                }
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[LỖI] {result.ErrorMessage}");
            Console.ResetColor();
        }

        Console.WriteLine("\nNhấn Enter để quay lại menu...");
        Console.ReadLine();
    }

    private bool EnsureConfigured()
    {
        if (!_hasConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Vui lòng chọn mục '1' để nhập thông tin kết nối trước!");
            Console.ResetColor();
            Console.WriteLine("\nNhấn Enter để tiếp tục...");
            Console.ReadLine();
            return false;
        }
        return true;
    }

    public static string ReadPasswordMasked()
    {
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Remove(password.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        return password.ToString();
    }
}
