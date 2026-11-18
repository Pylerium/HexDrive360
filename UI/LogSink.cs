using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace MemoryEditor.UI;

/// <summary>A single log line with its capture time and derived severity.</summary>
public readonly record struct LogEntry(DateTime Time, string Text, LogLevel Level);

public enum LogLevel { Trace, Info, Success, Warning, Error }

/// <summary>
/// Thread-safe, non-blocking logging pipeline.
///
/// Replaces the previous implementation which, for *every* message, synchronously
/// <c>Invoke</c>d onto the UI thread (deadlock-prone from background threads) and re-opened
/// the log file with <c>File.AppendAllText</c> (very slow when scans/scripts log heavily).
///
/// Producers call <see cref="Write"/> from any thread. Lines are:
///  • queued for the UI, which drains them in batches (<see cref="Drain"/>) on a timer, and
///  • streamed to disk by a single background consumer with a buffered writer.
/// </summary>
public sealed class LogSink : IDisposable
{
    private readonly ConcurrentQueue<LogEntry> _ui = new();
    private readonly Channel<string> _file = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private int _disposed;

    public string FilePath { get; }

    public LogSink(string filePath)
    {
        FilePath = filePath;
        _writer = Task.Run(WriteLoopAsync);
    }

    public static LogLevel Classify(string text)
    {
        if (text.Contains('❌') || text.Contains("error", StringComparison.OrdinalIgnoreCase) || text.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Error;
        if (text.Contains("⚠") || text.Contains("warning", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Warning;
        if (text.Contains('✅') || text.Contains('✓') || text.StartsWith("🎮") || text.StartsWith("🔓"))
            return LogLevel.Success;
        if (text.StartsWith("   ") || text.StartsWith("💡"))
            return LogLevel.Trace;
        return LogLevel.Info;
    }

    public void Write(string message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var now = DateTime.Now;
        _ui.Enqueue(new LogEntry(now, message, Classify(message)));
        _file.Writer.TryWrite($"[{now:yyyy-MM-dd HH:mm:ss}] {message}");
    }

    /// <summary>Removes up to <paramref name="max"/> pending UI entries.</summary>
    public int Drain(List<LogEntry> into, int max = 2000)
    {
        int n = 0;
        while (n < max && _ui.TryDequeue(out var e)) { into.Add(e); n++; }
        return n;
    }

    public bool HasPending => !_ui.IsEmpty;

    private async Task WriteLoopAsync()
    {
        try
        {
            await using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 8192, useAsync: true);
            await using var w = new StreamWriter(fs, new UTF8Encoding(false));
            var reader = _file.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var line)) await w.WriteLineAsync(line).ConfigureAwait(false);
                await w.FlushAsync().ConfigureAwait(false);   // flush once per burst, not once per line
            }
        }
        catch
        {
            // Logging must never take the application down (read-only folder, locked file, …).
            while (_file.Reader.TryRead(out _)) { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _file.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(1.5)); } catch { }
    }
}
