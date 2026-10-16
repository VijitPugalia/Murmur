using System.Text;

namespace Murmur.Core.Services;

/// <summary>
/// Thread-safe, non-blocking debug logger with FileShare.ReadWrite support to prevent file-locking conflicts.
/// </summary>
public static class DebugLogger
{
    private static readonly object _lock = new();
    private static readonly string _logPath = Path.Combine(Path.GetTempPath(), "murmur_debug.log");

    /// <summary>
    /// Writes a log message safely without throwing exceptions if the file is temporarily locked.
    /// </summary>
    public static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                using var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fs, Encoding.UTF8);
                writer.Write(message);
            }
        }
        catch
        {
            // Debug logging must never throw or disrupt application flow
        }
    }

    /// <summary>
    /// Writes a formatted log line with timestamp safely.
    /// </summary>
    public static void LogLine(string message)
    {
        Log($"[{DateTime.Now:HH:mm:ss}] {message}\n");
    }
}
