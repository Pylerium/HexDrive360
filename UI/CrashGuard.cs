using System.Text;

namespace MemoryEditor.UI;

/// <summary>
/// Last line of defence. Every exception that escapes an event handler, timer, async-void callback or background task
/// is caught here, written to <c>crash\crash-*.txt</c> next to the executable and reported in the output console and as
/// a toast. The application keeps running: a bug in one tool must never take down the session (or leave frozen values
/// and breakpoints behind on the console).
/// </summary>
public static class CrashGuard
{
    /// <summary>Set by the shell once the UI exists: (one-line summary, full report path).</summary>
    public static Action<string, string>? Report { get; set; }

    public static string CrashFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash");

    private static readonly object Gate = new();
    private static DateTime _lastToast = DateTime.MinValue;
    private static string _lastKey = "";
    private static int _repeat;

    public static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Handle(e.Exception, "UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // a background thread died: the runtime terminates after this – at least leave a report behind
            if (e.ExceptionObject is Exception ex) Handle(ex, e.IsTerminating ? "background thread (fatal)" : "background thread");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Handle(e.Exception.Flatten().InnerExceptions.Count == 1 ? e.Exception.InnerException! : e.Exception, "background task");
        };
    }

    /// <summary>Records an exception without crashing. Safe to call from any thread.</summary>
    public static void Handle(Exception ex, string where)
    {
        string path = "";
        try
        {
            var key = ex.GetType().FullName + "|" + ex.TargetSite?.Name + "|" + ex.Message;
            lock (Gate)
            {
                // the same exception firing from a timer 20 times a second gets one report, not thousands
                if (key == _lastKey && DateTime.Now - _lastToast < TimeSpan.FromSeconds(10)) { _repeat++; return; }
                _lastKey = key;
                _lastToast = DateTime.Now;
                _repeat = 0;
            }
            path = Write(ex, where);
        }
        catch { /* never throw from the crash handler */ }

        try
        {
            var site = ex.TargetSite is { } m ? $" in {m.DeclaringType?.Name}.{m.Name}" : "";
            var summary = $"{ex.GetType().Name}{site}: {ex.Message}";
            if (Report != null) Report(summary, path);
            else Console.Error.WriteLine($"Unhandled ({where}): {ex}");
        }
        catch { }
    }

    private static string Write(Exception ex, string where)
    {
        Directory.CreateDirectory(CrashFolder);
        // keep the folder small
        foreach (var old in new DirectoryInfo(CrashFolder).GetFiles("crash-*.txt").OrderByDescending(f => f.CreationTimeUtc).Skip(30))
            try { old.Delete(); } catch { }

        var path = Path.Combine(CrashFolder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
        var sb = new StringBuilder();
        sb.AppendLine($"Xbox 360 Memory Editor – unhandled exception ({where})");
        sb.AppendLine($"Time:    {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"Version: {typeof(CrashGuard).Assembly.GetName().Version}");
        sb.AppendLine($"OS:      {Environment.OSVersion}  ·  .NET {Environment.Version}");
        sb.AppendLine(new string('─', 80));
        for (var e = ex; e != null; e = e.InnerException)
        {
            sb.AppendLine($"{e.GetType().FullName}: {e.Message}");
            sb.AppendLine(e.StackTrace);
            if (e.InnerException != null) sb.AppendLine("--- inner exception ---");
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
