using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MemoryEditor.Scripting;

/// <summary>Context handed to built-in functions.</summary>
public sealed class CallContext
{
    public ScriptInterpreter Interp { get; init; } = null!;
    public CallExpr Call { get; init; } = null!;
    public CancellationToken Token => Interp.Token;
    public IGameClient Client => Interp.Client;
    public void Log(string msg) => Interp.Log(msg);
    public ScriptError Error(string msg) => new(msg, Call.Line, Call.Col);
}

public sealed class BuiltinFunction
{
    public string Name { get; init; } = "";
    public string Signature { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "General";
    public bool IsCommand { get; init; }          // may be called without parentheses
    public bool BareFirstArg { get; init; }       // first argument may be a bare identifier used as text
    public int MinArgs { get; init; }
    public int MaxArgs { get; init; } = int.MaxValue;
    public bool NeedsConnection { get; init; }
    public Func<CallContext, object?[], Task<object?>> Impl { get; init; } = null!;
}

public enum ExecResult { Normal, Break, Continue, Return }

/// <summary>
/// Tree-walking interpreter for XScript. Runs on a background thread, is fully cancellable and
/// reports the executing line so the IDE can highlight it.
/// </summary>
public sealed class ScriptInterpreter
{
    public IGameClient Client { get; }
    public CancellationToken Token { get; private set; }
    private readonly Action<string> _log;

    public ConcurrentDictionary<string, object?> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<Dictionary<string, object?>> _locals = new();
    private ScriptProgram _program = new();
    private object? _returnValue;
    private int _callDepth;
    private long _statements;

    /// <summary>Resolves offset names (offset database + runtime offsets).</summary>
    public Func<string, uint?>? OffsetResolver { get; set; }
    /// <summary>Show a desktop notification.</summary>
    public Action<string>? Notifier { get; set; }
    /// <summary>Raised (throttled by the consumer) whenever a new line starts executing.</summary>
    public event Action<int>? LineExecuting;

    public DateTime StartedAt { get; private set; }
    public long StatementsExecuted => _statements;

    // background workers (hooks, freezes) survive the main body until Stop
    internal readonly ConcurrentDictionary<string, CancellationTokenSource> Background = new(StringComparer.OrdinalIgnoreCase);
    public int BackgroundCount => Background.Count;

    public ScriptInterpreter(IGameClient client, Action<string> log, CancellationToken token)
    {
        Client = client;
        _log = log;
        Token = token;
    }

    public void Log(string msg) => _log(msg);

    // ───────────────────────────── entry points ─────────────────────────────

    public static ScriptProgram Compile(string source) => ScriptParser.Parse(source, ScriptBuiltins.IsCommand);

    public async Task RunAsync(ScriptProgram program)
    {
        _program = program;
        StartedAt = DateTime.Now;
        foreach (var stmt in program.Body)
        {
            var r = await ExecAsync(stmt);
            if (r == ExecResult.Return) break;
            if (r is ExecResult.Break or ExecResult.Continue)
                throw new ScriptError("'break'/'continue' used outside of a loop", stmt.Line, stmt.Col);
        }
    }

    /// <summary>Runs another chunk of source sharing this interpreter's globals (hooks, eval).</summary>
    public async Task RunChildAsync(string source, CancellationToken token)
    {
        var prog = Compile(source);
        if (prog.Errors.Count > 0) throw prog.Errors[0];
        var child = new ScriptInterpreter(Client, _log, token)
        {
            OffsetResolver = OffsetResolver,
            Notifier = Notifier,
        };
        foreach (var kv in Globals) child.Globals[kv.Key] = kv.Value;
        foreach (var f in _program.Functions) prog.Functions.TryAdd(f.Key, f.Value);
        await child.RunAsync(prog);
        foreach (var kv in child.Globals) Globals[kv.Key] = kv.Value;
    }

    public async Task<object?> InvokeUserFunctionAsync(string name, params object?[] args)
    {
        if (!_program.Functions.TryGetValue(name, out var f))
            throw new ScriptError($"Function '{name}' is not defined", 0, 0);
        return await CallUserAsync(f, args, f);
    }

    public bool HasUserFunction(string name) => _program.Functions.ContainsKey(name);

    // ───────────────────────────── variables ─────────────────────────────

    public bool TryGetVar(string name, out object? value)
    {
        if (_locals.Count > 0 && _locals.Peek().TryGetValue(name, out value)) return true;
        return Globals.TryGetValue(name, out value);
    }

    public void SetVar(string name, object? value, bool declare = false)
    {
        if (_locals.Count > 0)
        {
            var scope = _locals.Peek();
            if (declare || scope.ContainsKey(name) || !Globals.ContainsKey(name))
            {
                scope[name] = value;
                return;
            }
        }
        Globals[name] = value;
    }

    public IEnumerable<KeyValuePair<string, object?>> SnapshotVariables()
    {
        var d = new Dictionary<string, object?>(Globals, StringComparer.OrdinalIgnoreCase);
        if (_locals.Count > 0) foreach (var kv in _locals.Peek()) d[kv.Key] = kv.Value;
        return d.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase);
    }

    // ───────────────────────────── statements ─────────────────────────────

    private int _lastLine = -1;

    private async ValueTask<ExecResult> ExecAsync(Stmt s)
    {
        Token.ThrowIfCancellationRequested();
        _statements++;
        if (s.Line != _lastLine && s is not BlockStmt)
        {
            _lastLine = s.Line;
            LineExecuting?.Invoke(s.Line);
        }
        if ((_statements & 0x3FF) == 0) await Task.Yield();

        switch (s)
        {
            case ExprStmt es:
                await EvalAsync(es.Expr);
                return ExecResult.Normal;

            case VarStmt vs:
                SetVar(vs.Name, vs.Init == null ? 0L : await EvalAsync(vs.Init), declare: _locals.Count > 0);
                return ExecResult.Normal;

            case BlockStmt b:
                foreach (var st in b.Body)
                {
                    var r = await ExecAsync(st);
                    if (r != ExecResult.Normal) return r;
                }
                return ExecResult.Normal;

            case IfStmt ifs:
                if (Truthy(await EvalAsync(ifs.Cond))) return await ExecAsync(ifs.Then);
                if (ifs.Else != null) return await ExecAsync(ifs.Else);
                return ExecResult.Normal;

            case WhileStmt ws:
                while (ws.Cond == null || Truthy(await EvalAsync(ws.Cond)))
                {
                    var r = await ExecAsync(ws.Body);
                    if (r == ExecResult.Break) break;
                    if (r == ExecResult.Return) return r;
                    if (ws.Cond == null && (_statements & 0xFF) == 0) await Task.Yield();
                }
                return ExecResult.Normal;

            case RepeatStmt rs:
            {
                var n = ToLong(await EvalAsync(rs.Count), rs.Count);
                for (long k = 0; k < n; k++)
                {
                    var r = await ExecAsync(rs.Body);
                    if (r == ExecResult.Break) break;
                    if (r == ExecResult.Return) return r;
                }
                return ExecResult.Normal;
            }

            case ForStmt fs:
                if (fs.Init != null) await ExecAsync(fs.Init);
                while (fs.Cond == null || Truthy(await EvalAsync(fs.Cond)))
                {
                    var r = await ExecAsync(fs.Body);
                    if (r == ExecResult.Break) break;
                    if (r == ExecResult.Return) return r;
                    if (fs.Step != null) await EvalAsync(fs.Step);
                }
                return ExecResult.Normal;

            case ForInStmt fi:
            {
                var from = await EvalAsync(fi.From);
                if (fi.To == null)
                {
                    // iterate array / string
                    IEnumerable<object?> items = from switch
                    {
                        List<object?> l => l.ToList(),
                        string str => str.Select(ch => (object?)ch.ToString()),
                        _ => throw new ScriptError("for-in needs a range (a..b) or an array", fi.Line, fi.Col)
                    };
                    foreach (var it in items)
                    {
                        SetVar(fi.Var, it);
                        var r = await ExecAsync(fi.Body);
                        if (r == ExecResult.Break) break;
                        if (r == ExecResult.Return) return r;
                    }
                    return ExecResult.Normal;
                }
                long a = ToLong(from, fi.From), b2 = ToLong(await EvalAsync(fi.To), fi.To);
                long step = a <= b2 ? 1 : -1;
                for (long k = a; fi.Inclusive ? (step > 0 ? k <= b2 : k >= b2) : (step > 0 ? k < b2 : k > b2); k += step)
                {
                    SetVar(fi.Var, k);
                    var r = await ExecAsync(fi.Body);
                    if (r == ExecResult.Break) break;
                    if (r == ExecResult.Return) return r;
                }
                return ExecResult.Normal;
            }

            case BreakStmt: return ExecResult.Break;
            case ContinueStmt: return ExecResult.Continue;
            case ReturnStmt ret:
                _returnValue = ret.Value == null ? null : await EvalAsync(ret.Value);
                return ExecResult.Return;
            case FuncStmt:
                return ExecResult.Normal; // hoisted at parse time
        }
        throw new ScriptError($"Unsupported statement {s.GetType().Name}", s.Line, s.Col);
    }

    // ───────────────────────────── expressions ─────────────────────────────

    public async ValueTask<object?> EvalAsync(Expr e)
    {
        switch (e)
        {
            case LiteralExpr lit:
                if (lit.Interpolate && lit.Value is string s) return await InterpolateAsync(s, lit);
                return lit.Value;

            case IdentExpr id:
                return ResolveIdentifier(id);

            case ArrayExpr arr:
            {
                var list = new List<object?>(arr.Items.Count);
                foreach (var it in arr.Items) list.Add(await EvalAsync(it));
                return list;
            }

            case IndexExpr ix:
            {
                var target = await EvalAsync(ix.Target);
                var idx = ToLong(await EvalAsync(ix.Index), ix.Index);
                return target switch
                {
                    List<object?> l when idx >= 0 && idx < l.Count => l[(int)idx],
                    string str when idx >= 0 && idx < str.Length => str[(int)idx].ToString(),
                    List<object?> or string => throw new ScriptError($"Index {idx} out of range", ix.Line, ix.Col),
                    _ => throw new ScriptError("Only arrays and strings can be indexed", ix.Line, ix.Col)
                };
            }

            case UnaryExpr u:
            {
                var v = await EvalAsync(u.Operand);
                return u.Op switch
                {
                    "-" => v is double d ? -d : (object)(-ToLong(v, u)),
                    "!" => !Truthy(v),
                    "~" => ~ToLong(v, u),
                    _ => throw new ScriptError($"Unknown operator {u.Op}", u.Line, u.Col)
                };
            }

            case BinaryExpr b:
                if (b.Op == "&&") return Truthy(await EvalAsync(b.Left)) && Truthy(await EvalAsync(b.Right));
                if (b.Op == "||") return Truthy(await EvalAsync(b.Left)) || Truthy(await EvalAsync(b.Right));
                return Binary(b.Op, await EvalAsync(b.Left), await EvalAsync(b.Right), b);

            case TernaryExpr t:
                return Truthy(await EvalAsync(t.Cond)) ? await EvalAsync(t.Then) : await EvalAsync(t.Else);

            case AssignExpr a:
            {
                var value = await EvalAsync(a.Value);
                if (a.IndexTarget != null)
                {
                    var target = await EvalAsync(a.IndexTarget);
                    var idx = ToLong(await EvalAsync(a.Index!), a);
                    if (target is not List<object?> list) throw new ScriptError("Only arrays support index assignment", a.Line, a.Col);
                    while (list.Count <= idx) list.Add(null);
                    if (a.Op != "=") value = Binary(a.Op[..^1], list[(int)idx], value, a);
                    list[(int)idx] = value;
                    return value;
                }
                if (a.Op != "=")
                {
                    if (!TryGetVar(a.Name, out var cur)) throw new ScriptError($"Variable '{a.Name}' is not defined", a.Line, a.Col);
                    value = Binary(a.Op[..^1], cur, value, a);
                }
                SetVar(a.Name, value);
                return value;
            }

            case IncDecExpr inc:
            {
                if (!TryGetVar(inc.Name, out var cur)) cur = 0L;
                object? nv = cur is double d ? d + (inc.Increment ? 1 : -1) : ToLong(cur, inc) + (inc.Increment ? 1 : -1);
                SetVar(inc.Name, nv);
                return inc.Prefix ? nv : cur;
            }

            case CallExpr c:
                return await CallAsync(c);
        }
        throw new ScriptError($"Unsupported expression {e.GetType().Name}", e.Line, e.Col);
    }

    private object? ResolveIdentifier(IdentExpr id)
    {
        if (TryGetVar(id.Name, out var v)) return v;
        var off = OffsetResolver?.Invoke(id.Name);
        if (off.HasValue) return (long)off.Value;
        if (_program.Functions.ContainsKey(id.Name)) return id.Name; // function reference
        throw new ScriptError($"'{id.Name}' is not defined (not a variable or a known offset)", id.Line, id.Col);
    }

    private static readonly Regex InterpRx = new(@"\$\{([^}]+)\}|\$([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    private async ValueTask<string> InterpolateAsync(string s, Node at)
    {
        var sb = new StringBuilder();
        int last = 0;
        foreach (Match m in InterpRx.Matches(s))
        {
            sb.Append(s, last, m.Index - last);
            last = m.Index + m.Length;
            if (m.Groups[1].Success)
            {
                var prog = ScriptParser.Parse(m.Groups[1].Value, _ => false);
                if (prog.Errors.Count > 0 || prog.Body.Count != 1 || prog.Body[0] is not ExprStmt es)
                {
                    sb.Append(m.Value);
                    continue;
                }
                sb.Append(Format(await EvalAsync(es.Expr)));
            }
            else
            {
                var name = m.Groups[2].Value;
                if (TryGetVar(name, out var v)) sb.Append(Format(v));
                else sb.Append(m.Value);
            }
        }
        sb.Append(s, last, s.Length - last);
        return sb.ToString();
    }

    private async ValueTask<object?> CallAsync(CallExpr c)
    {
        Token.ThrowIfCancellationRequested();

        if (_program.Functions.TryGetValue(c.Name, out var f))
        {
            var args = new object?[c.Args.Count];
            for (int i = 0; i < args.Length; i++) args[i] = await EvalAsync(c.Args[i]);
            return await CallUserAsync(f, args, c);
        }

        if (!ScriptBuiltins.All.TryGetValue(c.Name, out var bf))
        {
            var hint = ScriptBuiltins.SuggestName(c.Name);
            throw new ScriptError($"Unknown function '{c.Name}'" + (hint != null ? $" – did you mean '{hint}'?" : ""), c.Line, c.Col);
        }

        if (c.Args.Count < bf.MinArgs || c.Args.Count > bf.MaxArgs)
            throw new ScriptError($"{bf.Name} expects {bf.Signature}", c.Line, c.Col);
        if (bf.NeedsConnection && !Client.IsConnected)
            throw new ScriptError($"{bf.Name}: not connected to a console", c.Line, c.Col);

        var vals = new object?[c.Args.Count];
        for (int i = 0; i < vals.Length; i++)
        {
            if (i == 0 && bf.BareFirstArg && c.Args[0] is IdentExpr bare && !TryGetVar(bare.Name, out _))
                vals[0] = bare.Name;
            else
                vals[i] = await EvalAsync(c.Args[i]);
        }
        try
        {
            return await bf.Impl(new CallContext { Interp = this, Call = c }, vals);
        }
        catch (ScriptError) { throw; }
        catch (ScriptExit) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ScriptError($"{bf.Name}: {ex.Message}", c.Line, c.Col);
        }
    }

    private async ValueTask<object?> CallUserAsync(FuncStmt f, object?[] args, Node at)
    {
        if (_callDepth > 256) throw new ScriptError("Stack overflow – too much recursion", at.Line, at.Col);
        var scope = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < f.Params.Count; i++) scope[f.Params[i]] = i < args.Length ? args[i] : null;
        _locals.Push(scope);
        _callDepth++;
        try
        {
            _returnValue = null;
            var r = await ExecAsync(f.Body);
            var rv = r == ExecResult.Return ? _returnValue : null;
            _returnValue = null;
            return rv;
        }
        finally
        {
            _callDepth--;
            _locals.Pop();
        }
    }

    // ───────────────────────────── value helpers ─────────────────────────────

    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        double d => d != 0 && !double.IsNaN(d),
        string s => s.Length > 0,
        List<object?> l => l.Count > 0,
        _ => true
    };

    public static long ToLong(object? v, Node? at = null) => v switch
    {
        long l => l,
        double d => (long)d,
        bool b => b ? 1 : 0,
        null => 0,
        string s when TryParseNumber(s, out var n) => n is double dd ? (long)dd : (long)n,
        _ => throw new ScriptError($"Expected a number but got {Describe(v)}", at?.Line ?? 0, at?.Col ?? 0)
    };

    public static double ToDouble(object? v, Node? at = null) => v switch
    {
        long l => l,
        double d => d,
        bool b => b ? 1 : 0,
        null => 0,
        string s when TryParseNumber(s, out var n) => Convert.ToDouble(n, CultureInfo.InvariantCulture),
        _ => throw new ScriptError($"Expected a number but got {Describe(v)}", at?.Line ?? 0, at?.Col ?? 0)
    };

    public static bool TryParseNumber(string s, out object value)
    {
        s = s.Trim();
        value = 0L;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(s[2..], NumberStyles.HexNumber, null, out var h)) { value = unchecked((long)h); return true; }
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) { value = l; return true; }
        if (double.TryParse(s.TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { value = d; return true; }
        return false;
    }

    public static string Describe(object? v) => v switch
    {
        null => "null",
        string s => $"text \"{(s.Length > 24 ? s[..24] + "…" : s)}\"",
        List<object?> => "array",
        _ => v.GetType().Name
    };

    public static string Format(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        List<object?> list => "[" + string.Join(", ", list.Select(Format)) + "]",
        _ => v.ToString() ?? ""
    };

    private static object? Binary(string op, object? l, object? r, Node at)
    {
        // string concatenation / comparison
        if (op == "+" && (l is string || r is string)) return Format(l) + Format(r);
        if (op is "==" or "!=")
        {
            bool eq;
            if (l is string || r is string) eq = Format(l) == Format(r);
            else if (l == null || r == null) eq = l == null && r == null;
            else if (l is double || r is double) eq = Math.Abs(ToDouble(l, at) - ToDouble(r, at)) < 1e-9;
            else eq = ToLong(l, at) == ToLong(r, at);
            return op == "==" ? eq : !eq;
        }
        if (op is "<" or ">" or "<=" or ">=" && l is string ls && r is string rs)
        {
            int cmp = string.CompareOrdinal(ls, rs);
            return op switch { "<" => cmp < 0, ">" => cmp > 0, "<=" => cmp <= 0, _ => cmp >= 0 };
        }

        bool isFloat = l is double || r is double;
        if (isFloat && op is "+" or "-" or "*" or "/" or "%" or "<" or ">" or "<=" or ">=")
        {
            double a = ToDouble(l, at), b = ToDouble(r, at);
            return op switch
            {
                "+" => a + b, "-" => a - b, "*" => a * b,
                "/" => b == 0 ? throw new ScriptError("Division by zero", at.Line, at.Col) : a / b,
                "%" => a % b,
                "<" => a < b, ">" => a > b, "<=" => a <= b, _ => a >= b
            };
        }

        long x = ToLong(l, at), y = ToLong(r, at);
        return op switch
        {
            "+" => unchecked(x + y),
            "-" => unchecked(x - y),
            "*" => unchecked(x * y),
            "/" => y == 0 ? throw new ScriptError("Division by zero", at.Line, at.Col) : (x % y == 0 ? x / y : (object)((double)x / y)),
            "%" => y == 0 ? throw new ScriptError("Division by zero", at.Line, at.Col) : x % y,
            "<" => x < y, ">" => x > y, "<=" => x <= y, ">=" => x >= y,
            "&" => x & y, "|" => x | y, "^" => x ^ y,
            "<<" => x << (int)y, ">>" => (long)((ulong)x >> (int)y),
            _ => throw new ScriptError($"Unknown operator {op}", at.Line, at.Col)
        };
    }
}
