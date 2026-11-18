using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace MemoryEditor.Runtime.Debug;

/// <summary>
/// XBDM notification channel: a second connection that the console pushes debug events to (breakpoints, data
/// watchpoints, exceptions, title changes). Lines are delivered on a background thread.
/// </summary>
public sealed class XbdmNotifyChannel : IDisposable
{
    private readonly TcpClient _tcp = new();
    private NetworkStream? _stream;
    private readonly CancellationTokenSource _cts = new();
    private Task? _reader;

    public event Action<string>? LineReceived;

    public static async Task<XbdmNotifyChannel> OpenAsync(string host, int port, CancellationToken ct = default)
    {
        var ch = new XbdmNotifyChannel();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(5000);
        await ch._tcp.ConnectAsync(host, port, timeout.Token);
        ch._stream = ch._tcp.GetStream();
        var banner = await ch.ReadLineAsync(timeout.Token);                 // 201- connected
        if (!banner.StartsWith("201")) throw new IOException($"Unexpected XBDM banner: {banner}");
        // retail-style XBDM wants "notify reconnectport=<port>" (the port is only used if the link drops)
        int localPort = ((System.Net.IPEndPoint)ch._tcp.Client.LocalEndPoint!).Port;
        string resp = "";
        foreach (var cmd in new[] { $"notify reconnectport={localPort}", "notify" })
        {
            await ch._stream.WriteAsync(Encoding.ASCII.GetBytes(cmd + "\r\n"), timeout.Token);
            resp = await ch.ReadLineAsync(timeout.Token);                   // 205- now a notification channel
            if (resp.StartsWith("2")) break;
        }
        if (!resp.StartsWith("2")) throw new IOException($"Console refused the notification channel: {resp}");
        ch._reader = Task.Run(ch.ReadLoopAsync);
        return ch;
    }

    private readonly byte[] _buf = new byte[4096];
    private int _pos, _len;

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _len)
            {
                _len = await _stream!.ReadAsync(_buf.AsMemory(), ct);
                _pos = 0;
                if (_len <= 0) throw new IOException("Notification channel closed");
            }
            while (_pos < _len)
            {
                char c = (char)_buf[_pos++];
                if (c == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append(c);
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await ReadLineAsync(_cts.Token);
                if (line.Length > 0) LineReceived?.Invoke(line);
            }
        }
        catch { /* closed */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _stream?.Close(); } catch { }
        try { _tcp.Close(); } catch { }
    }
}

/// <summary>Registers of a stopped thread (from "getcontext").</summary>
public sealed class ThreadContext
{
    public uint Thread { get; init; }
    public uint Pc { get; init; }
    public uint Lr { get; init; }
    public ulong[] Gpr { get; } = new ulong[32];
    public double[] Fpr { get; } = new double[32];
    public bool HasFpr { get; set; }
    public Dictionary<string, string> Raw { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A decoded load / store that touched the watched address.</summary>
public sealed class TraceHit
{
    public uint Pc { get; init; }
    public uint Instruction { get; init; }
    public string Disassembly { get; init; } = "";
    public bool IsStore { get; init; }
    public bool IsFloat { get; init; }
    public int DataReg { get; init; }          // rS / fS (store) or rD / fD (load)
    public int BaseReg { get; init; }          // rA
    public int IndexReg { get; init; } = -1;   // rB for indexed forms
    public uint BaseValue { get; init; }       // value of rA when it executed → object base
    public int Offset { get; init; }           // displacement → field offset inside the object
    public uint EffectiveAddress { get; init; }
    public string DataValue { get; init; } = "";
    public uint Thread { get; init; }
    public int Count { get; set; } = 1;

    public string Summary =>
        $"{(IsStore ? "writes" : "reads")} via {Disassembly}  ·  object base r{BaseReg} = 0x{BaseValue:X8}, field +0x{Offset:X}" +
        (DataValue.Length > 0 ? $"  ·  {(IsFloat ? "f" : "r")}{DataReg} = {DataValue}" : "");
}

/// <summary>
/// Debugger operations over XBDM used by the Dynamic Modder: data watchpoints, thread contexts and resuming.
/// Every operation that stops the game is paired with a guaranteed cleanup (clear breakpoint, continue, go).
/// </summary>
public static class XbdmDebugger
{
    public enum Access { Write, Read, ReadWrite }

    public static string BreakCommand(uint address, int size, Access kind, bool clear) =>
        $"break {(kind switch { Access.Read => "read", Access.ReadWrite => "readwrite", _ => "write" })}=0x{address:X8} size={size}" + (clear ? " clear" : "");

    public static async Task<ThreadContext?> GetContextAsync(XBDMClient c, uint thread)
    {
        var r = await c.SendRawAsync($"getcontext thread=0x{thread:X} control int fp");
        if (!r.Ok) r = await c.SendRawAsync($"getcontext thread=0x{thread:X} control int");
        if (!r.Ok) return null;
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in r.Lines.Prepend(r.Message))
            foreach (var (k, v) in XBDMClient.ParseKeyValues(line)) raw[k] = v;

        static ulong U(string? s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            s = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
            return ulong.TryParse(s, NumberStyles.HexNumber, null, out var v) ? v : 0;
        }

        var ctx = new ThreadContext { Thread = thread, Pc = (uint)U(raw.GetValueOrDefault("Iar")), Lr = (uint)U(raw.GetValueOrDefault("Lr")) };
        foreach (var (k, v) in raw) ctx.Raw[k] = v;
        for (int i = 0; i < 32; i++)
        {
            ctx.Gpr[i] = U(raw.GetValueOrDefault($"Gpr{i}") ?? raw.GetValueOrDefault($"r{i}"));
            var f = raw.GetValueOrDefault($"Fpr{i}") ?? raw.GetValueOrDefault($"f{i}");
            if (f != null) { ctx.Fpr[i] = BitConverter.Int64BitsToDouble((long)U(f)); ctx.HasFpr = true; }
        }
        return ctx;
    }

    /// <summary>Decodes D-form and X-form loads / stores (integer and floating point).</summary>
    public static bool DecodeAccess(uint w, out bool store, out bool isFloat, out int rS, out int rA, out int rB, out int disp, out int size)
    {
        uint op = w >> 26;
        rS = (int)((w >> 21) & 31);
        rA = (int)((w >> 16) & 31);
        rB = -1;
        disp = (short)(w & 0xFFFF);
        store = false; isFloat = false; size = 4;
        switch (op)
        {
            case 32: case 33: size = 4; return true;                         // lwz(u)
            case 34: case 35: size = 1; return true;                         // lbz(u)
            case 40: case 41: case 42: case 43: size = 2; return true;       // lhz(u) lha(u)
            case 36: case 37: store = true; size = 4; return true;           // stw(u)
            case 38: case 39: store = true; size = 1; return true;           // stb(u)
            case 44: case 45: store = true; size = 2; return true;           // sth(u)
            case 48: case 49: isFloat = true; size = 4; return true;         // lfs(u)
            case 50: case 51: isFloat = true; size = 8; return true;         // lfd(u)
            case 52: case 53: store = true; isFloat = true; size = 4; return true; // stfs(u)
            case 54: case 55: store = true; isFloat = true; size = 8; return true; // stfd(u)
            case 58: disp = (short)(w & 0xFFFC); size = 8; return true;      // ld / ldu / lwa
            case 62: disp = (short)(w & 0xFFFC); store = true; size = 8; return true; // std / stdu
            case 31:
            {
                uint xo = (w >> 1) & 0x3FF;
                rB = (int)((w >> 11) & 31);
                disp = 0;
                switch (xo)
                {
                    case 23: case 55: size = 4; return true;                      // lwzx lwzux
                    case 87: case 119: size = 1; return true;                     // lbzx lbzux
                    case 279: case 311: case 343: case 375: size = 2; return true;// lhzx lhzux lhax lhaux
                    case 151: case 183: store = true; size = 4; return true;      // stwx stwux
                    case 215: case 247: store = true; size = 1; return true;      // stbx stbux
                    case 407: case 439: store = true; size = 2; return true;      // sthx sthux
                    case 535: case 567: isFloat = true; size = 4; return true;    // lfsx lfsux
                    case 599: case 631: isFloat = true; size = 8; return true;    // lfdx lfdux
                    case 663: case 695: store = true; isFloat = true; size = 4; return true; // stfsx stfsux
                    case 727: case 759: store = true; isFloat = true; size = 8; return true; // stfdx stfdux
                    case 21: case 53: size = 8; return true;                      // ldx ldux
                    case 149: case 181: store = true; size = 8; return true;      // stdx stdux
                }
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Places a data watchpoint on <paramref name="address"/> and collects the code that accesses it for up to
    /// <paramref name="timeout"/>. Each hit stops the game for a moment; the thread is resumed straight away.
    /// The breakpoint is always cleared and execution resumed, even on cancellation or error.
    /// </summary>
    public static async Task<List<TraceHit>> TraceAsync(XBDMClient c, uint address, int size, Access kind, TimeSpan timeout,
        int maxDistinct, Action<string>? log, CancellationToken ct)
    {
        var hits = new List<TraceHit>();
        using var channel = await XbdmNotifyChannel.OpenAsync(c.IpAddress, c.Port, ct);
        var events = System.Threading.Channels.Channel.CreateUnbounded<string>();
        channel.LineReceived += l => events.Writer.TryWrite(l);

        bool armed = false;
        try
        {
            var r = await c.SendRawAsync(BreakCommand(address, size, kind, clear: false));
            if (!r.Ok) throw new IOException($"The console refused the watchpoint: {r.Code} {r.Message}");
            armed = true;
            log?.Invoke($"🎯 Watching 0x{address:X8} ({kind}, {size} bytes) – trigger it in game…");

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(timeout);
            while (hits.Count < maxDistinct)
            {
                string line;
                try { line = await events.Reader.ReadAsync(window.Token); }
                catch (OperationCanceledException) { break; }

                var kv = XBDMClient.ParseKeyValues(line);
                if (!kv.TryGetValue("thread", out var ts)) continue;            // not a stop event
                bool isData = line.StartsWith("data", StringComparison.OrdinalIgnoreCase) || kv.ContainsKey("dataaddr") ||
                              line.Contains("write", StringComparison.OrdinalIgnoreCase) || line.Contains("read", StringComparison.OrdinalIgnoreCase);
                uint thread = XBDMClient.ParseUInt(ts);
                try
                {
                    if (!isData) continue;
                    var ctx = await GetContextAsync(c, thread);
                    uint pc = ctx?.Pc ?? XBDMClient.ParseUInt(kv.GetValueOrDefault("addr"));
                    if (pc == 0) continue;
                    var known = hits.FirstOrDefault(h => h.Pc == pc);
                    if (known != null) { known.Count++; continue; }
                    uint w = await c.ReadUInt32Async(pc) ?? 0;
                    var hit = BuildHit(pc, w, thread, ctx);
                    hits.Add(hit);
                    log?.Invoke($"   ↳ 0x{pc:X8}  {hit.Summary}");
                }
                finally
                {
                    await c.SendRawAsync($"continue thread=0x{thread:X}");
                    await c.SendRawAsync("go");
                }
            }
        }
        finally
        {
            if (armed)
            {
                try { await c.SendRawAsync(BreakCommand(address, size, kind, clear: true)); } catch { }
            }
            try { await c.SendRawAsync("go"); } catch { }   // never leave the game halted
        }
        return hits;
    }

    private static TraceHit BuildHit(uint pc, uint w, uint thread, ThreadContext? ctx)
    {
        var dis = Ppc.Disassemble(w, pc);
        if (!DecodeAccess(w, out var store, out var isFloat, out var rS, out var rA, out var rB, out var disp, out _))
            return new TraceHit { Pc = pc, Instruction = w, Disassembly = dis, Thread = thread, BaseReg = -1 };
        uint baseVal = rA == 0 && rB < 0 ? 0 : (uint)(ctx?.Gpr[rA] ?? 0);
        uint ea = rB >= 0 ? unchecked(baseVal + (uint)(ctx?.Gpr[rB] ?? 0)) : unchecked(baseVal + (uint)disp);
        string data = "";
        if (ctx != null)
            data = isFloat ? (ctx.HasFpr ? ctx.Fpr[rS].ToString("0.#####", CultureInfo.InvariantCulture) : "") : $"0x{(uint)ctx.Gpr[rS]:X8}";
        return new TraceHit
        {
            Pc = pc, Instruction = w, Disassembly = dis, IsStore = store, IsFloat = isFloat, DataReg = rS, BaseReg = rA, IndexReg = rB,
            BaseValue = baseVal, Offset = rB >= 0 ? (int)(ea - baseVal) : disp, EffectiveAddress = ea, DataValue = data, Thread = thread,
        };
    }
}
