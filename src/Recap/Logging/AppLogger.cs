using System.IO;

namespace Recap.Logging;

public static class AppLogger
{
    private static readonly object Lock = new();
    private static string? _logFilePath;

    public static void Initialize(string logsFolder)
    {
        try
        {
            logsFolder = Environment.ExpandEnvironmentVariables(logsFolder);
            Directory.CreateDirectory(logsFolder);
            lock (Lock)
            {
                _logFilePath = Path.Combine(logsFolder, "recap-debug.log");
                if (File.Exists(_logFilePath))
                    File.Copy(_logFilePath, Path.Combine(logsFolder, "recap-debug.previous.log"), overwrite: true);
                File.WriteAllText(_logFilePath, string.Empty);
            }
            Info($"Session started. PID={Environment.ProcessId}; Runtime={Environment.Version}; OS={Environment.OSVersion}; AppVersion={typeof(AppLogger).Assembly.GetName().Version}");
            Info($"Log file: {_logFilePath}");
        }
        catch (Exception ex)
        {
            _logFilePath = null;
            System.Diagnostics.Debug.WriteLine($"Unable to initialize logging: {ex}");
        }
    }

    public static void Debug(string message) => Write("DEBUG", message);
    public static void Info(string message) => Write("INFO", message);
    public static void Warning(string message) => Write("WARN", message);

    public static void Error(Exception exception, string message)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");
    }

    private static void Write(string level, string message)
    {
        if (string.IsNullOrWhiteSpace(_logFilePath))
        {
            return;
        }

        try
        {
            lock (Lock)
            {
                File.AppendAllText(
                    _logFilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [Thread {Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break recording.
        }
    }
}
