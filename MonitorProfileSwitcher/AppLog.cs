using System.Diagnostics;
using System.Text;

namespace MonitorProfileSwitcher;

internal static class AppLog
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const int MaxArchives = 5;
    private static readonly object Sync = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorProfileSwitcher",
        "logs");

    public static string CurrentLogPath => Path.Combine(LogDirectory, "app.log");

    public static void Initialize(string mode) =>
        Info($"Application started. Version={typeof(AppLog).Assembly.GetName().Version} Mode={mode} PID={Environment.ProcessId} Executable={Environment.ProcessPath}");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error("Could not open the log folder.", ex);
            throw;
        }
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                var body = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
                var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
                var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
                var formatted = new StringBuilder();
                foreach (var line in lines)
                {
                    formatted.Append(timestamp).Append(" [").Append(level).Append("] [T")
                        .Append(Environment.CurrentManagedThreadId).Append("] ").AppendLine(line);
                }

                RotateIfNeeded(formatted.Length * 2L);
                File.AppendAllText(CurrentLogPath, formatted.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never prevent profile switching or app startup.
        }
    }

    private static void RotateIfNeeded(long incomingBytes)
    {
        var current = new FileInfo(CurrentLogPath);
        if (!current.Exists || current.Length + incomingBytes < MaxLogBytes)
            return;

        var oldest = Path.Combine(LogDirectory, $"app.{MaxArchives}.log");
        if (File.Exists(oldest)) File.Delete(oldest);

        for (var index = MaxArchives - 1; index >= 1; index--)
        {
            var source = Path.Combine(LogDirectory, $"app.{index}.log");
            var destination = Path.Combine(LogDirectory, $"app.{index + 1}.log");
            if (File.Exists(source)) File.Move(source, destination, true);
        }

        File.Move(CurrentLogPath, Path.Combine(LogDirectory, "app.1.log"), true);
    }
}
