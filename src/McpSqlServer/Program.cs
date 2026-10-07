namespace McpSqlServer;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Logger.Initialize(enableFileLogging: true);

        // 1. Environment, packages & dependency checks (Preflight Check)
        var preflight = PreflightChecker.RunPreflightChecks();
        if (!preflight.Success)
        {
            Console.Error.WriteLine("[FATAL] Preflight Dependency Check Failed:");
            foreach (var err in preflight.Errors)
            {
                Console.Error.WriteLine($" - {err}");
            }
            return 1;
        }

        // 2. Run In-Process Core Self-Tests (Run Gate mandatory for every run)
        var selfTests = PreflightChecker.RunCoreSelfTests();
        if (!selfTests.Success)
        {
            Console.Error.WriteLine("[FATAL] In-Process Self-Tests Failed:");
            foreach (var err in selfTests.Errors)
            {
                Console.Error.WriteLine($" - {err}");
            }
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Logger.Info("Cancellation signal received (Ctrl+C). Terminating process...");
            cts.Cancel();
        };

        var mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "menu";

        try
        {
            if (mode == "serve" || mode == "--serve")
            {
                await McpServerHandler.RunAsync(cts.Token);
            }
            else if (mode == "scan" || mode == "--scan")
            {
                var options = ConnectionOptions.FromConfigOrEnvironment();
                string outputDir = "./ai-context";
                bool includeSystem = false;
                bool resume = false;
                string? targetDatabase = null;

                for (int i = 1; i < args.Length; i++)
                {
                    var arg = args[i].Trim();
                    if ((arg == "--server-alias" || arg == "--alias" || arg == "--server-name") && i + 1 < args.Length)
                    {
                        options.ServerAlias = args[++i].Trim().ToUpperInvariant();
                    }
                    else if ((arg == "--database" || arg == "-d" || arg == "--db") && i + 1 < args.Length)
                    {
                        targetDatabase = args[++i].Trim();
                    }
                    else if ((arg == "--output" || arg == "-o") && i + 1 < args.Length)
                    {
                        outputDir = args[++i].Trim();
                    }
                    else if (arg == "--include-system")
                    {
                        includeSystem = true;
                    }
                    else if (arg == "--resume")
                    {
                        resume = true;
                    }
                }

                var errors = options.Validate();
                if (errors.Count > 0)
                {
                    Console.Error.WriteLine("[ERROR] Invalid configuration:");
                    foreach (var err in errors) Console.Error.WriteLine($" - {err}");
                    return 1;
                }

                Console.WriteLine($"[SCAN] Starting server scan for [{options.ServerAlias}] ({options.Server}){(targetDatabase != null ? $" [DB: {targetDatabase}]" : "")}...");
                var sqlService = new SqlServerService();
                var scanResult = await sqlService.ScanServerAsync(
                    options,
                    includeSystem: includeSystem,
                    onProgress: msg => Console.WriteLine($" - {msg}"),
                    targetDatabase: targetDatabase,
                    cancellationToken: cts.Token,
                    outputDirectory: outputDir,
                    resume: resume
                );

                Console.WriteLine("Rendering AI Context documentation...");
                var files = await AiContextRenderer.RenderAndExportAsync(scanResult, outputDir, cts.Token);
                var failed = scanResult.Databases.Where(db => !db.Success).ToArray();
                if (failed.Length > 0)
                {
                    Console.Error.WriteLine($"[PARTIAL] Completed {scanResult.Databases.Count - failed.Length}/{scanResult.Databases.Count} databases; generated {files.Count} files.");
                    foreach (var db in failed) Console.Error.WriteLine($" - {db.DatabaseName}: {db.ErrorMessage}");
                    Console.Error.WriteLine("Run again with --resume and the same --output to scan only these databases.");
                    return 2;
                }
                Console.WriteLine($"[SUCCESS] Successfully generated {files.Count} files in '{Path.GetFullPath(outputDir)}'.");
            }
            else
            {
                var menu = new CliMenu();
                await menu.RunAsync(cts.Token);
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex)
        {
            Logger.Error("Process terminated due to an unhandled exception", ex);
            Console.Error.WriteLine($"[ERROR] {Logger.Sanitize(ex.Message)}");
            return 1;
        }
    }
}
