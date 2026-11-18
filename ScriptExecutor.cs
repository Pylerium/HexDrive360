using MemoryEditor.Scripting;

namespace MemoryEditor;

/// <summary>
/// Facade over the XScript engine (Scripting/ScriptInterpreter). Keeps the original API
/// (ctor(client, log), ExecuteAsync, RequestCancel) and adds state for the IDE.
/// </summary>
public class ScriptExecutor
{
    private readonly IGameClient _client;
    private readonly Action<string> _logMessage;
    private CancellationTokenSource? _cts;
    private ScriptInterpreter? _current;

    public bool IsRunning { get; private set; }
    public int BackgroundTasks => _current?.BackgroundCount ?? 0;
    public bool HasActiveWork => IsRunning || BackgroundTasks > 0;

    public Func<string, uint?>? OffsetResolver { get; set; }
    public Action<string>? Notifier { get; set; }

    /// <summary>Line (1-based) currently executing.</summary>
    public event Action<int>? LineExecuting;
    /// <summary>Raised when a run ends. Error is null on success.</summary>
    public event Action<ScriptError?>? Finished;
    public event Action? Started;

    public ScriptError? LastError { get; private set; }
    public TimeSpan LastDuration { get; private set; }
    public long LastStatementCount { get; private set; }

    public ScriptExecutor(IGameClient client, Action<string> logCallback)
    {
        _client = client;
        _logMessage = logCallback;
    }

    public IGameClient Client => _client;

    public IEnumerable<KeyValuePair<string, object?>> Variables =>
        _current?.SnapshotVariables() ?? Enumerable.Empty<KeyValuePair<string, object?>>();

    /// <summary>Parses without running – used for live diagnostics.</summary>
    public static ScriptProgram Check(string script) => ScriptInterpreter.Compile(script);

    public async Task ExecuteAsync(string script)
    {
        if (IsRunning)
        {
            _logMessage("⚠️ A script is already running – stop it first");
            return;
        }

        var program = ScriptInterpreter.Compile(script);
        if (program.Errors.Count > 0)
        {
            foreach (var e in program.Errors.Take(10)) _logMessage($"❌ Syntax error – line {e.Line}: {e.Message}");
            LastError = program.Errors[0];
            Finished?.Invoke(LastError);
            return;
        }

        // stop previous background hooks/freezes before a new run
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var interp = new ScriptInterpreter(_client, _logMessage, _cts.Token)
        {
            OffsetResolver = OffsetResolver,
            Notifier = Notifier,
        };
        interp.LineExecuting += l => LineExecuting?.Invoke(l);
        _current = interp;

        IsRunning = true;
        LastError = null;
        Started?.Invoke();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logMessage("🚀 Script started");
        try
        {
            await Task.Run(() => interp.RunAsync(program));
            _logMessage($"✅ Script finished in {sw.Elapsed.TotalMilliseconds:0} ms ({interp.StatementsExecuted:N0} statements)" +
                        (interp.BackgroundCount > 0 ? $" – {interp.BackgroundCount} background task(s) still running, press Stop to end them" : ""));
        }
        catch (ScriptExit)
        {
            _logMessage("⏹️ Script exited");
        }
        catch (OperationCanceledException)
        {
            _logMessage("⏹️ Script stopped");
        }
        catch (ScriptError e)
        {
            LastError = e;
            _logMessage($"❌ Runtime error – line {e.Line}: {e.Message}");
        }
        catch (Exception ex)
        {
            LastError = new ScriptError(ex.Message, 0, 0);
            _logMessage($"❌ Script error: {ex.Message}");
        }
        finally
        {
            LastDuration = sw.Elapsed;
            LastStatementCount = interp.StatementsExecuted;
            IsRunning = false;
            Finished?.Invoke(LastError);
        }
    }

    public void RequestCancel()
    {
        try { _cts?.Cancel(); } catch { }
    }
}
