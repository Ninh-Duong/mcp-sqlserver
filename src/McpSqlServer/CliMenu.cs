namespace McpSqlServer;

public class CliMenu
{
    private readonly SqlServerService _sqlService = new();
    private ConnectionOptions _options = new();
    private string _connectionStatus = "Connection not tested";
    private bool _hasConfigured = false;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        // Auto-load config from dbconfig.json if available
        var (loaded, fileOptions) = ConnectionOptions.TryLoadFromFile("dbconfig.json");
        if (loaded && fileOptions != null)
        {
            _options = fileOptions;
            _hasConfigured = true;
            _connectionStatus = "Loaded from dbconfig.json (Not tested)";
            Logger.SetActivePassword(_options.Password);
            Logger.Info("Automatically loaded connection settings from dbconfig.json.");
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

            Console.WriteLine("1. Configure connection settings");
            Console.WriteLine("2. Test connection");
            Console.WriteLine("3. Check DB Migrations & Schema Drift");
            Console.WriteLine("4. List databases");
            Console.WriteLine("5. List tables in database");
            Console.WriteLine("6. Scan Server & Export AI Context Docs (DEV/UAT/PROD)");
            Console.WriteLine("0. Exit");
            Console.WriteLine();
            Console.Write("Select option (0-6): ");

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
                    await CheckDbMigrationsAndDriftAsync(cancellationToken);
                    break;
                case "4":
                    await ListDatabasesAsync(cancellationToken);
                    break;
                case "5":
                    await ListTablesInDatabaseAsync(cancellationToken);
                    break;
                case "6":
                    await ScanServerAndExportContextAsync(cancellationToken);
                    break;
                case "0":
                    Console.WriteLine("Goodbye!");
                    return;
                default:
                    Console.WriteLine("Invalid option. Press Enter to continue...");
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
        Console.WriteLine($"Status: {_connectionStatus}");
        if (_hasConfigured)
        {
            Console.WriteLine($"Config: {_options.GetDisplaySummary()}");
        }
        Console.WriteLine("----------------------------------------");
    }

    private void ConfigureConnection()
    {
        Console.WriteLine("--- CONFIGURE CONNECTION SETTINGS ---");

        Console.Write($"Server Alias (DEV, UAT, PROD...) [current: {(_hasConfigured ? _options.ServerAlias : "DEV")}]: ");
        var aliasInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(aliasInput))
        {
            _options.ServerAlias = aliasInput.ToUpperInvariant();
        }

        Console.Write($"Server name [current: {(_hasConfigured ? _options.Server : "none")}]: ");
        var serverInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(serverInput) || !_hasConfigured)
        {
            _options.Server = serverInput ?? string.Empty;
        }

        Console.Write($"Username [current: {(_hasConfigured ? _options.Username : "none")}]: ");
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

        Console.Write("Advanced configuration? [y/N]: ");
        var advChoice = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (advChoice == "y" || advChoice == "yes")
        {
            Console.Write($"Port [default: {_options.Port?.ToString() ?? "1433"}]: ");
            var portInput = Console.ReadLine()?.Trim();
            if (int.TryParse(portInput, out var p)) _options.Port = p;

            Console.Write($"Initial Database [default: {_options.Database}]: ");
            var dbInput = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(dbInput)) _options.Database = dbInput;

            Console.Write("Trust Server Certificate? (enable for self-signed SSL) [y/N]: ");
            var trustInput = Console.ReadLine()?.Trim().ToLowerInvariant();
            _options.TrustServerCertificate = (trustInput == "y" || trustInput == "yes");

            Console.Write($"Connect Timeout (seconds) [default: {_options.ConnectTimeout}]: ");
            var timeoutInput = Console.ReadLine()?.Trim();
            if (int.TryParse(timeoutInput, out var t)) _options.ConnectTimeout = t;
        }

        var validationErrors = _options.Validate();
        if (validationErrors.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Invalid configuration:");
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
            _connectionStatus = "Configuration updated (Not tested)";
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Configuration updated successfully!");
            Console.ResetColor();

            Console.Write("\nSave configuration to 'dbconfig.json' for future sessions? [y/N]: ");
            var saveChoice = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (saveChoice == "y" || saveChoice == "yes")
            {
                try
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(_options, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText("dbconfig.json", json);
                    Console.WriteLine("Saved to dbconfig.json successfully (protected by .gitignore).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Unable to save file: {ex.Message}");
                }
            }
        }

        Console.WriteLine("\nPress Enter to return to menu...");
        Console.ReadLine();
    }

    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        if (!EnsureConfigured()) return;

        Console.WriteLine("Testing connection...");
        var result = await _sqlService.TestConnectionAsync(_options, cancellationToken);

        if (result.Success)
        {
            _connectionStatus = $"Connected successfully ({result.ElapsedMs} ms)";
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[SUCCESS] Connected in {result.ElapsedMs} ms.");
            if (!string.IsNullOrEmpty(result.ServerVersion))
            {
                Console.WriteLine($"Version: {result.ServerVersion}");
            }
            Console.ResetColor();
        }
        else
        {
            _connectionStatus = "Connection failed";
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[FAILED] {result.ErrorMessage}");
            Console.ResetColor();
        }

        Console.WriteLine("\nPress Enter to continue...");
        Console.ReadLine();
    }

    private async Task ListDatabasesAsync(CancellationToken cancellationToken)
    {
        if (!EnsureConfigured()) return;

        Console.WriteLine("Fetching database list...");
        var result = await _sqlService.ListDatabasesAsync(_options, cancellationToken);

        if (result.Success)
        {
            _connectionStatus = "Database list retrieved successfully";
            Console.WriteLine();
            Console.WriteLine($"Databases visible to account: {result.VisibleCount}");
            Console.WriteLine($"  System: {result.SystemCount} | User: {result.OtherCount}");
            Console.WriteLine();

            Console.WriteLine("{0,-30} {1,-15} {2,-15}", "Name", "State", "Access");
            Console.WriteLine(new string('-', 60));

            foreach (var db in result.Databases)
            {
                var accessText = db.HasAccess.HasValue ? (db.HasAccess.Value ? "Yes" : "No") : "Unknown";
                Console.WriteLine("{0,-30} {1,-15} {2,-15}", db.Name, db.State, accessText);
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("* Note: This listing reflects permissions of the current login on SQL Server.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] {result.ErrorMessage}");
            Console.ResetColor();
        }

        Console.WriteLine("\nPress Enter to return to menu...");
        Console.ReadLine();
    }

    private async Task ListTablesInDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!EnsureConfigured()) return;

        Console.Write($"Enter database name [default: {_options.Database}]: ");
        var inputDb = Console.ReadLine()?.Trim();
        var targetDb = string.IsNullOrWhiteSpace(inputDb) ? _options.Database : inputDb;

        Console.WriteLine($"Fetching table list for database '{targetDb}'...");
        var result = await _sqlService.ListTablesAsync(_options, targetDb, cancellationToken);

        if (result.Success)
        {
            _connectionStatus = $"Retrieved tables ({targetDb})";
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Total tables in database '{result.Database}': {result.TableCount}");
            Console.ResetColor();
            Console.WriteLine();

            if (result.TableCount == 0)
            {
                Console.WriteLine("No tables found or login lacks metadata SELECT permissions.");
            }
            else
            {
                Console.WriteLine("{0,-20} {1,-40}", "Schema", "Table Name");
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
            Console.WriteLine($"[ERROR] {result.ErrorMessage}");
            Console.ResetColor();
        }

        Console.WriteLine("\nPress Enter to return to menu...");
        Console.ReadLine();
    }

    private async Task ScanServerAndExportContextAsync(CancellationToken cancellationToken)
    {
        if (!EnsureConfigured()) return;

        Console.WriteLine("==================================================");
        Console.WriteLine("     FULL SERVER SCAN & AI CONTEXT GENERATOR");
        Console.WriteLine("==================================================");
        Console.WriteLine($"Server: {_options.Server} (Host: {_options.Server})");
        Console.Write($"Confirm Server Alias (DEV, UAT, PROD...) [current: {_options.ServerAlias}]: ");
        var aliasInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(aliasInput))
        {
            _options.ServerAlias = aliasInput.ToUpperInvariant();
        }

        Console.Write("Include system databases (master, msdb, etc.)? [y/N]: ");
        var sysChoice = Console.ReadLine()?.Trim().ToLowerInvariant();
        var includeSystem = sysChoice == "y" || sysChoice == "yes";

        Console.Write("Export directory [default: ./ai-context]: ");
        var outDir = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(outDir)) outDir = "./ai-context";
        var resume = false;
        if (ScanCheckpoint.HasCheckpoint(outDir, _options.ServerAlias, _options.Server))
        {
            Console.Write("Found unfinished scan checkpoint. Resume previous scan? [Y/n]: ");
            var resumeInput = Console.ReadLine()?.Trim();
            resume = string.IsNullOrEmpty(resumeInput) ||
                     resumeInput.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                     resumeInput.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[START] Scanning server '{_options.ServerAlias}'...");
        Console.ResetColor();

        try
        {
            var scanResult = await _sqlService.ScanServerAsync(
                _options,
                includeSystem: includeSystem,
                onProgress: msg => Console.WriteLine($" - {msg}"),
                cancellationToken: cancellationToken,
                outputDirectory: outDir,
                resume: resume
            );

            Console.WriteLine();
            Console.WriteLine("Exporting AI Context documentation (Progressive Disclosure)...");
            var createdFiles = await AiContextRenderer.RenderAndExportAsync(scanResult, outDir, cancellationToken);
            var failed = scanResult.Databases.Where(db => !db.Success).ToArray();
            if (failed.Length > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[PARTIAL] Completed {scanResult.Databases.Count - failed.Length}/{scanResult.Databases.Count} databases.");
                foreach (var db in failed) Console.WriteLine($" - {db.DatabaseName}: {db.ErrorMessage}");
                Console.WriteLine("Run menu scan again and choose Resume to scan only these databases.");
                Console.ResetColor();
            }
            else
            {
                ScanCheckpoint.DeleteCheckpoint(outDir, _options.ServerAlias, _options.Server);
                _connectionStatus = $"Scanned server {_options.ServerAlias} ({scanResult.Databases.Count} DBs)";
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine();
                Console.WriteLine("==================================================");
                Console.WriteLine($"[SUCCESS] Finished scanning Server '{scanResult.ServerAlias}' in {scanResult.ElapsedMs} ms!");
                Console.WriteLine($" - Total Databases scanned: {scanResult.Databases.Count}");
                Console.WriteLine($" - Total files created: {createdFiles.Count}");
                Console.WriteLine($" - Root directory: {Path.GetFullPath(outDir)}");
                Console.WriteLine($" - Central Index: {Path.Combine(outDir, "INDEX.md")}");
                Console.WriteLine("==================================================");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[FAILED] Scan process interrupted: {Logger.Sanitize(ex.Message)}");
            Console.ResetColor();
        }

        Console.WriteLine("\nPress Enter to return to menu...");
        Console.ReadLine();
    }

    private async Task CheckDbMigrationsAndDriftAsync(CancellationToken cancellationToken)
    {
        if (!EnsureConfigured()) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==================================================");
        Console.WriteLine("    CHECK DATABASE MIGRATIONS & SCHEMA DRIFT");
        Console.WriteLine("==================================================");
        Console.ResetColor();

        Console.WriteLine($"Inspecting live databases on [{_options.ServerAlias}] ({_options.Server})...\n");

        var outDir = "./ai-context";
        var registryPath = Path.Combine(outDir, "servers_registry.json");
        IReadOnlyDictionary<string, DatabaseMigrationStatus>? cachedMigrations = null;

        if (File.Exists(registryPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(registryPath, cancellationToken);
                var items = System.Text.Json.JsonSerializer.Deserialize<List<ServerRegistryItem>>(json);
                var serverItem = items?.FirstOrDefault(i => i.ServerAlias.Equals(_options.ServerAlias, StringComparison.OrdinalIgnoreCase))
                                 ?? items?.FirstOrDefault();
                cachedMigrations = serverItem?.Migrations;
            }
            catch { }
        }

        var driftResult = await _sqlService.CheckSchemaDriftAsync(_options, cachedMigrations, cancellationToken);
        if (!driftResult.Success)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] Check failed: {driftResult.ErrorMessage}");
            Console.ResetColor();
            Console.WriteLine("\nPress Enter to return to menu...");
            Console.ReadLine();
            return;
        }

        Console.WriteLine($"Found {driftResult.DriftedDatabases.Count + driftResult.UpToDateDatabases.Count} accessible databases.\n");

        if (driftResult.DriftedDatabases.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✔ All {driftResult.UpToDateDatabases.Count} databases are UP TO DATE with ai-context snapshot.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠ Schema Drift detected in {driftResult.DriftedDatabases.Count} database(s):\n");
            Console.ResetColor();

            foreach (var d in driftResult.DriftedDatabases)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($" - [{d.DatabaseName}]: {d.Reason}");
                Console.ResetColor();
                if (!string.IsNullOrEmpty(d.CurrentMigrationId) || !string.IsNullOrEmpty(d.SnapshotMigrationId))
                {
                    Console.WriteLine($"     Current DB Migration : {d.CurrentMigrationId ?? "(none)"} ({d.CurrentCount} total)");
                    Console.WriteLine($"     Snapshot Migration   : {d.SnapshotMigrationId ?? "(none)"} ({d.SnapshotCount} total)");
                }
            }

            Console.WriteLine();
            Console.Write("Enter database name to re-scan (or 'all' to re-scan all / Enter to cancel): ");
            var target = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(target))
            {
                var targetDb = target.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : target;
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"\nStarting targeted scan for {(targetDb ?? "ALL databases")}...");
                Console.ResetColor();

                try
                {
                    var scanResult = await _sqlService.ScanServerAsync(
                        _options,
                        includeSystem: false,
                        onProgress: msg => Console.WriteLine($" [PROGRESS] {msg}"),
                        targetDatabase: targetDb,
                        cancellationToken: cancellationToken);

                    var files = await AiContextRenderer.RenderAndExportAsync(scanResult, outDir, cancellationToken);
                    var failed = scanResult.Databases.Where(db => !db.Success).ToArray();
                    Console.ForegroundColor = failed.Length == 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
                    if (failed.Length == 0)
                        Console.WriteLine($"\n[SUCCESS] Re-scan finished in {scanResult.ElapsedMs} ms ({files.Count} files updated)!");
                    else
                    {
                        Console.WriteLine($"\n[PARTIAL] Re-scan completed {scanResult.Databases.Count - failed.Length}/{scanResult.Databases.Count} databases.");
                        foreach (var db in failed) Console.WriteLine($" - {db.DatabaseName}: {db.ErrorMessage}");
                    }
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[FAILED] Re-scan error: {Logger.Sanitize(ex.Message)}");
                    Console.ResetColor();
                }
            }
        }

        Console.WriteLine("\nPress Enter to return to menu...");
        Console.ReadLine();
    }

    private bool EnsureConfigured()
    {
        if (!_hasConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Please select option '1' to configure connection settings first!");
            Console.ResetColor();
            Console.WriteLine("\nPress Enter to continue...");
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
