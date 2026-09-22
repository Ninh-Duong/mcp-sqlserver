using System.Text.RegularExpressions;

namespace McpSqlServer;

public static class Logger
{
    private static readonly Lock LockObj = new();
    private static string? _logFilePath;
    private static string? _errorLogFilePath;
    private static string? _activePassword;

    public static string? LogFilePath => _logFilePath;
    public static string? ErrorLogFilePath => _errorLogFilePath;

    public static void Initialize(bool enableFileLogging = true, string? customLogDir = null)
    {
        if (enableFileLogging)
        {
            // Relative path: ./logs/process.log and ./logs/error.log
            var logDir = customLogDir ?? Path.Combine(".", "logs");
            try
            {
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }
                _logFilePath = Path.Combine(logDir, "process.log");
                _errorLogFilePath = Path.Combine(logDir, "error.log");
            }
            catch
            {
                // Fallback to stderr only if file creation fails
                _logFilePath = null;
                _errorLogFilePath = null;
            }
        }
        else
        {
            _logFilePath = null;
            _errorLogFilePath = null;
        }
    }

    public static void SetActivePassword(string? password)
    {
        _activePassword = string.IsNullOrEmpty(password) ? null : password;
    }

    public static string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var result = input;
        if (!string.IsNullOrEmpty(_activePassword))
        {
            result = result.Replace(_activePassword, "***");
        }

        // Mask password=... or pwd=... patterns
        result = Regex.Replace(result, @"(?i)(password|pwd)\s*=\s*[^;]+", "$1=***");
        return result;
    }

    public static void Log(string level, string message)
    {
        var sanitized = Sanitize(message);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var logLine = $"[{timestamp}] [{level}] {sanitized}";

        lock (LockObj)
        {
            // Always write diagnostic logs to stderr (never stdout)
            Console.Error.WriteLine(logLine);

            if (_logFilePath != null)
            {
                try
                {
                    File.AppendAllText(_logFilePath, logLine + Environment.NewLine);
                }
                catch
                {
                    // Ignore file write errors to avoid crashing process
                }
            }
        }
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message, Exception? ex = null)
    {
        var fullMessage = ex != null ? $"{message} -> {ex.Message}" : message;
        Log("ERROR", fullMessage);

        if (_errorLogFilePath != null)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var errorDetails = ex != null
                ? $"[{timestamp}] [ERROR] {Sanitize(message)}{Environment.NewLine}Exception: {Sanitize(ex.ToString())}"
                : $"[{timestamp}] [ERROR] {Sanitize(message)}";

            lock (LockObj)
            {
                try
                {
                    File.AppendAllText(_errorLogFilePath, errorDetails + Environment.NewLine);
                }
                catch
                {
                    // Ignore file write errors to avoid crashing process
                }
            }
        }
    }
    public static void Process(string step, string message) => Log($"PROCESS:{step}", message);
}
