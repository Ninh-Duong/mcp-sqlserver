namespace McpSqlServer;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Logger.Initialize(enableFileLogging: true);

        // 1. Kiểm tra môi trường, packages & dependencies trước khi chạy (Preflight Check)
        var preflight = PreflightChecker.RunPreflightChecks();
        if (!preflight.Success)
        {
            Console.Error.WriteLine("[FATAL] Preflight Dependency Check Thất Bại:");
            foreach (var err in preflight.Errors)
            {
                Console.Error.WriteLine($" - {err}");
            }
            return 1;
        }

        // 2. Chạy In-Process Core Self-Tests (Run Gate bắt buộc mọi lần run)
        var selfTests = PreflightChecker.RunCoreSelfTests();
        if (!selfTests.Success)
        {
            Console.Error.WriteLine("[FATAL] In-Process Self-Tests Thất Bại:");
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
            Logger.Info("Nhận tín hiệu hủy (Ctrl+C). Đang dừng tiến trình...");
            cts.Cancel();
        };

        var mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "menu";

        try
        {
            if (mode == "serve" || mode == "--serve")
            {
                await McpServerHandler.RunAsync(cts.Token);
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
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("Tiến trình kết thúc do ngoại lệ chưa được xử lý", ex);
            return 1;
        }
    }
}
