using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ReplayCapture.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Minimal always-on file log. Writes happen on a background thread so nothing on the capture or
/// encode path ever blocks on disk.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 4096);
    private static readonly Lock Gate = new();
    private static Thread? _writer;
    private static string? _path;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReplayCapture", "logs");

    public static event Action<LogLevel, string>? Emitted;

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message, Exception? ex = null) =>
        Write(LogLevel.Error, ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel) return;

        var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-5} [{Environment.CurrentManagedThreadId,3}] {message}";
        Trace.WriteLine(line);
        Emitted?.Invoke(level, message);

        EnsureWriter();
        // Never block the caller: if the queue is saturated we drop the line rather than stall
        // a capture thread waiting on the log.
        Queue.TryAdd(line);
    }

    private static void EnsureWriter()
    {
        if (_writer is not null) return;
        lock (Gate)
        {
            if (_writer is not null) return;

            Directory.CreateDirectory(DirectoryPath);
            _path = Path.Combine(DirectoryPath, $"replaycapture-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            PruneOldLogs();

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "log-writer",
                Priority = ThreadPriority.BelowNormal,
            };
            _writer.Start();
        }
    }

    private static void WriterLoop()
    {
        using var stream = new FileStream(
            _path!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096);
        using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };

        foreach (var line in Queue.GetConsumingEnumerable())
        {
            writer.WriteLine(line);
            // Flush when we've drained the backlog, so a crash loses at most the in-flight batch.
            if (Queue.Count == 0) writer.Flush();
        }
    }

    private static void PruneOldLogs()
    {
        try
        {
            var stale = new DirectoryInfo(DirectoryPath)
                .GetFiles("replaycapture-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(10);
            foreach (var file in stale) file.Delete();
        }
        catch
        {
            // Log pruning is housekeeping; failing to prune is never worth surfacing.
        }
    }
}
