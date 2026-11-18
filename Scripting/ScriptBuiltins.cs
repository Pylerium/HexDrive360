using System.Globalization;
using System.Text;

namespace MemoryEditor.Scripting;

/// <summary>Built-in function library for XScript. Also feeds the IDE (autocomplete, call tips, docs).</summary>
public static class ScriptBuiltins
{
    public static readonly Dictionary<string, BuiltinFunction> All = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Random Rng = new();

    public static bool IsCommand(string name) => All.TryGetValue(name, out var f) && f.IsCommand;

    public static string? SuggestName(string name)
    {
        string? best = null;
        int bestD = int.MaxValue;
        foreach (var k in All.Keys)
        {
            var d = Levenshtein(name.ToLowerInvariant(), k.ToLowerInvariant());
            if (d < bestD) { bestD = d; best = k; }
        }
        return bestD <= Math.Max(2, name.Length / 3) ? All[best!].Name : null;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    // ───────────────────────────── helpers ─────────────────────────────

    public static uint Addr(object? v, CallContext c)
    {
        if (v is string s)
        {
            if (ScriptInterpreter.TryParseNumber(s, out var n)) return unchecked((uint)Convert.ToInt64(n, CultureInfo.InvariantCulture));
            var off = c.Interp.OffsetResolver?.Invoke(s);
            if (off.HasValue) return off.Value;
            throw c.Error($"'{s}' is not an address or known offset");
        }
        return unchecked((uint)ScriptInterpreter.ToLong(v, c.Call));
    }

    private static long L(object? v, CallContext c) => ScriptInterpreter.ToLong(v, c.Call);
    private static double D(object? v, CallContext c) => ScriptInterpreter.ToDouble(v, c.Call);
    private static string S(object? v) => ScriptInterpreter.Format(v);

    public static byte[] ParseHex(string hex)
    {
        var clean = new StringBuilder();
        foreach (var ch in hex)
        {
            if (Uri.IsHexDigit(ch)) clean.Append(ch);
            else if (ch is ' ' or ',' or '-' or '\t' or ':') continue;
            else if (ch == 'x' || ch == 'X') { if (clean.Length > 0 && clean[^1] == '0') clean.Length--; }
            else throw new FormatException($"'{ch}' is not a hex digit");
        }
        if (clean.Length % 2 != 0) throw new FormatException("hex string has an odd number of digits");
        return Convert.FromHexString(clean.ToString());
    }

    private static byte[] BE(byte[] le) { if (BitConverter.IsLittleEndian) Array.Reverse(le); return le; }

    private static async Task<byte[]> Read(CallContext c, uint addr, int len)
    {
        var data = await c.Client.GetMemoryAsync(addr, (uint)len);
        if (data == null || data.Length < len) throw c.Error($"read of {len} bytes at 0x{addr:X8} failed");
        return data;
    }

    private static async Task Write(CallContext c, uint addr, byte[] data)
    {
        if (!await c.Client.SetMemoryAsync(addr, data)) throw c.Error($"write of {data.Length} bytes at 0x{addr:X8} failed");
    }

    private static uint U32(byte[] d, int o = 0) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);

    private static void Add(string name, string sig, string desc, string cat, Func<CallContext, object?[], Task<object?>> impl,
        int min = 0, int max = int.MaxValue, bool command = false, bool net = false, bool bare = false, params string[] aliases)
    {
        var f = new BuiltinFunction
        {
            Name = name, Signature = sig, Description = desc, Category = cat, Impl = impl,
            MinArgs = min, MaxArgs = max, IsCommand = command, NeedsConnection = net, BareFirstArg = bare
        };
        All[name] = f;
        foreach (var a in aliases)
            All[a] = new BuiltinFunction
            {
                Name = a, Signature = sig.Replace(name + "(", a + "("), Description = $"{desc} (alias of {name})", Category = cat,
                Impl = impl, MinArgs = min, MaxArgs = max, IsCommand = command, NeedsConnection = net, BareFirstArg = bare
            };
    }

    private static Task<object?> R(object? v) => Task.FromResult(v);

    // ───────────────────────────── library ─────────────────────────────

    static ScriptBuiltins()
    {
        // ── reading ──
        Add("ReadInt32", "ReadInt32(address)", "Reads a signed 32-bit big-endian integer.", "Memory Read",
            async (c, a) => (long)(int)U32(await Read(c, Addr(a[0], c), 4)), 1, 1, net: true);
        Add("ReadUInt32", "ReadUInt32(address)", "Reads an unsigned 32-bit big-endian integer.", "Memory Read",
            async (c, a) => (long)U32(await Read(c, Addr(a[0], c), 4)), 1, 1, net: true, aliases: new[] { "ReadPointer" });
        Add("ReadInt16", "ReadInt16(address)", "Reads a signed 16-bit integer.", "Memory Read",
            async (c, a) => { var d = await Read(c, Addr(a[0], c), 2); return (long)(short)(d[0] << 8 | d[1]); }, 1, 1, net: true);
        Add("ReadUInt16", "ReadUInt16(address)", "Reads an unsigned 16-bit integer.", "Memory Read",
            async (c, a) => { var d = await Read(c, Addr(a[0], c), 2); return (long)(ushort)(d[0] << 8 | d[1]); }, 1, 1, net: true);
        Add("ReadByte", "ReadByte(address)", "Reads one byte.", "Memory Read",
            async (c, a) => (long)(await Read(c, Addr(a[0], c), 1))[0], 1, 1, net: true);
        Add("ReadInt64", "ReadInt64(address)", "Reads a signed 64-bit integer.", "Memory Read",
            async (c, a) => { var d = await Read(c, Addr(a[0], c), 8); return (long)((ulong)U32(d) << 32 | U32(d, 4)); }, 1, 1, net: true);
        Add("ReadFloat", "ReadFloat(address)", "Reads a 32-bit float.", "Memory Read",
            async (c, a) => { var d = await Read(c, Addr(a[0], c), 4); return (double)BitConverter.Int32BitsToSingle((int)U32(d)); }, 1, 1, net: true);
        Add("ReadDouble", "ReadDouble(address)", "Reads a 64-bit double.", "Memory Read",
            async (c, a) => { var d = await Read(c, Addr(a[0], c), 8); return BitConverter.Int64BitsToDouble((long)((ulong)U32(d) << 32 | U32(d, 4))); }, 1, 1, net: true);
        Add("ReadString", "ReadString(address, maxLength = 64)", "Reads a null-terminated ASCII string.", "Memory Read",
            async (c, a) =>
            {
                var len = a.Length > 1 ? (int)L(a[1], c) : 64;
                var d = await Read(c, Addr(a[0], c), Math.Clamp(len, 1, 4096));
                var end = Array.IndexOf(d, (byte)0);
                return Encoding.ASCII.GetString(d, 0, end < 0 ? d.Length : end);
            }, 1, 2, net: true);
        Add("ReadBytes", "ReadBytes(address, length)", "Reads raw bytes and returns them as a hex string.", "Memory Read",
            async (c, a) => Convert.ToHexString(await Read(c, Addr(a[0], c), (int)Math.Clamp(L(a[1], c), 1, 0x100000))), 2, 2, net: true);
        Add("Peek", "Peek(address, length = 4)", "Reads bytes, logs them and returns the hex string.", "Memory Read",
            async (c, a) =>
            {
                var addr = Addr(a[0], c);
                var len = a.Length > 1 ? (int)L(a[1], c) : 4;
                var d = await Read(c, addr, Math.Clamp(len, 1, 0x10000));
                var hex = BitConverter.ToString(d).Replace('-', ' ');
                c.Log($"  👁️ Peek(0x{addr:X8}, {len}) = {hex}");
                return Convert.ToHexString(d);
            }, 1, 2, net: true);
        Add("PeekInt32", "PeekInt32(address)", "Reads and logs a 32-bit integer.", "Memory Read",
            async (c, a) =>
            {
                var addr = Addr(a[0], c);
                var v = (int)U32(await Read(c, addr, 4));
                c.Log($"  👁️ PeekInt32(0x{addr:X8}) = {v} (0x{v:X8})");
                return (long)v;
            }, 1, 1, net: true);
        Add("PeekFloat", "PeekFloat(address)", "Reads and logs a float.", "Memory Read",
            async (c, a) =>
            {
                var addr = Addr(a[0], c);
                var v = BitConverter.Int32BitsToSingle((int)U32(await Read(c, addr, 4)));
                c.Log($"  👁️ PeekFloat(0x{addr:X8}) = {v.ToString(CultureInfo.InvariantCulture)}");
                return (double)v;
            }, 1, 1, net: true);
        Add("PeekByte", "PeekByte(address)", "Reads and logs a byte.", "Memory Read",
            async (c, a) =>
            {
                var addr = Addr(a[0], c);
                var v = (await Read(c, addr, 1))[0];
                c.Log($"  👁️ PeekByte(0x{addr:X8}) = {v} (0x{v:X2})");
                return (long)v;
            }, 1, 1, net: true);

        // ── writing ──
        Add("PokeInt32", "PokeInt32(address, value)", "Writes a 32-bit big-endian integer.", "Memory Write",
            async (c, a) => { var addr = Addr(a[0], c); var v = unchecked((int)L(a[1], c)); await Write(c, addr, BE(BitConverter.GetBytes(v))); c.Log($"  ✓ PokeInt32(0x{addr:X8}, {v})"); return null; },
            2, 2, net: true, aliases: new[] { "WriteInt32", "PokeUInt32", "WriteUInt32" });
        Add("PokeInt16", "PokeInt16(address, value)", "Writes a 16-bit integer.", "Memory Write",
            async (c, a) => { var addr = Addr(a[0], c); var v = unchecked((short)L(a[1], c)); await Write(c, addr, BE(BitConverter.GetBytes(v))); c.Log($"  ✓ PokeInt16(0x{addr:X8}, {v})"); return null; },
            2, 2, net: true, aliases: new[] { "WriteInt16" });
        Add("PokeByte", "PokeByte(address, value)", "Writes one byte.", "Memory Write",
            async (c, a) => { var addr = Addr(a[0], c); var v = unchecked((byte)L(a[1], c)); await Write(c, addr, new[] { v }); c.Log($"  ✓ PokeByte(0x{addr:X8}, {v})"); return null; },
            2, 2, net: true, aliases: new[] { "WriteByte" });
        Add("PokeInt64", "PokeInt64(address, value)", "Writes a 64-bit integer.", "Memory Write",
            async (c, a) => { var addr = Addr(a[0], c); var v = L(a[1], c); await Write(c, addr, BE(BitConverter.GetBytes(v))); c.Log($"  ✓ PokeInt64(0x{addr:X8}, {v})"); return null; },
            2, 2, net: true, aliases: new[] { "WriteInt64" });
        Add("PokeFloat", "PokeFloat(address, value)", "Writes a 32-bit float.", "Memory Write",
            async (c, a) => { var addr = Addr(a[0], c); var v = (float)D(a[1], c); await Write(c, addr, BE(BitConverter.GetBytes(v))); c.Log($"  ✓ PokeFloat(0x{addr:X8}, {v.ToString(CultureInfo.InvariantCulture)})"); return null; },
            2, 2, net: true, aliases: new[] { "WriteFloat" });
        Add("PokeDouble", "PokeDouble(address, value)", "Writes a 64-bit double.", "Memory Write",
            async (c, a) => { var addr = Addr(a[0], c); var v = D(a[1], c); await Write(c, addr, BE(BitConverter.GetBytes(v))); c.Log($"  ✓ PokeDouble(0x{addr:X8}, {v.ToString(CultureInfo.InvariantCulture)})"); return null; },
            2, 2, net: true, aliases: new[] { "WriteDouble" });
        Add("PokeString", "PokeString(address, text, nullTerminate = false)", "Writes an ASCII string.", "Memory Write",
            async (c, a) =>
            {
                var addr = Addr(a[0], c);
                var text = S(a[1]);
                var bytes = Encoding.ASCII.GetBytes(text);
                if (a.Length > 2 && ScriptInterpreter.Truthy(a[2])) bytes = bytes.Append((byte)0).ToArray();
                await Write(c, addr, bytes);
                c.Log($"  ✓ PokeString(0x{addr:X8}, \"{text}\")");
                return null;
            }, 2, 3, net: true, aliases: new[] { "WriteString" });
        Add("PokeBytes", "PokeBytes(address, \"AA BB CC\")", "Writes raw hex bytes.", "Memory Write",
            async (c, a) => { var addr = Addr(a[0], c); var b = ParseHex(S(a[1])); await Write(c, addr, b); c.Log($"  ✓ PokeBytes(0x{addr:X8}, {b.Length} bytes)"); return null; },
            2, 2, net: true, aliases: new[] { "WriteBytes" });
        Add("Poke", "Poke(address, value)", "Writes hex text (\"60 00 00 00\"), an Int32 or a float depending on the value.", "Memory Write",
            async (c, a) =>
            {
                var addr = Addr(a[0], c);
                byte[] b = a[1] switch
                {
                    string s => ParseHex(s),
                    double d => BE(BitConverter.GetBytes((float)d)),
                    _ => BE(BitConverter.GetBytes(unchecked((int)L(a[1], c))))
                };
                await Write(c, addr, b);
                c.Log($"  ✓ Poke(0x{addr:X8}, {b.Length} bytes)");
                return null;
            }, 2, 2, net: true);
        Add("Nop", "Nop(address, count = 1)", "Writes PowerPC nop (60000000) instructions.", "Memory Write",
            async (c, a) =>
            {
                var addr = Addr(a[0], c);
                var n = a.Length > 1 ? (int)Math.Clamp(L(a[1], c), 1, 1024) : 1;
                var buf = new byte[n * 4];
                for (int i = 0; i < n; i++) { buf[i * 4] = 0x60; }
                await Write(c, addr, buf);
                c.Log($"  ✓ Nop(0x{addr:X8}, {n})");
                return null;
            }, 1, 2, net: true);
        Add("Blr", "Blr(address)", "Writes 'blr' (4E800020) – makes a function return immediately.", "Memory Write",
            async (c, a) => { var addr = Addr(a[0], c); await Write(c, addr, new byte[] { 0x4E, 0x80, 0x00, 0x20 }); c.Log($"  ✓ Blr(0x{addr:X8})"); return null; }, 1, 1, net: true);
        Add("Fill", "Fill(address, length, byteValue)", "Fills a memory range with one byte value.", "Memory Write",
            async (c, a) =>
            {
                var addr = Addr(a[0], c);
                var n = (int)Math.Clamp(L(a[1], c), 1, 0x100000);
                var buf = Enumerable.Repeat(unchecked((byte)L(a[2], c)), n).ToArray();
                await Write(c, addr, buf);
                c.Log($"  ✓ Fill(0x{addr:X8}, {n})");
                return null;
            }, 3, 3, net: true);

        // ── pointers ──
        Add("walk", "walk(base, offset1, offset2, ...)", "Follows a pointer chain: reads [base+off1], then [result+off2] ... Sets $lastWalk.", "Pointers",
            async (c, a) =>
            {
                uint cur = Addr(a[0], c);
                c.Log($"  🧭 walk start 0x{cur:X8}");
                for (int i = 1; i < a.Length; i++)
                {
                    var at = unchecked(cur + (uint)L(a[i], c));
                    if (at < 0x10000) throw c.Error($"null pointer while walking (read at 0x{at:X8})");
                    cur = U32(await Read(c, at, 4));
                    c.Log($"    ➜ [0x{at:X8}] = 0x{cur:X8}");
                }
                c.Interp.Globals["lastWalk"] = (long)cur;
                c.Log($"  ✅ walk result 0x{cur:X8}");
                return (long)cur;
            }, 1, command: true, net: true, aliases: new[] { "ptr" });
        Add("addoff", "addoff(base, offset1, ...)", "Adds offsets to a base address without dereferencing. Sets $lastAddoff.", "Pointers",
            (c, a) =>
            {
                uint r = Addr(a[0], c);
                for (int i = 1; i < a.Length; i++) r = unchecked(r + (uint)L(a[i], c));
                c.Interp.Globals["lastAddoff"] = (long)r;
                c.Log($"  ➕ addoff result -> 0x{r:X8}");
                return R((long)r);
            }, 1, command: true);
        Add("IsValidPtr", "IsValidPtr(value)", "True when value looks like a valid Xbox 360 virtual address.", "Pointers",
            (c, a) => { var v = unchecked((uint)L(a[0], c)); return R(v >= 0x30000000 && v < 0xA0000000 && (v & 3) == 0); }, 1, 1);

        // ── offsets ──
        Add("offset", "offset(\"Name\")", "Returns the address of a named offset (offset database + runtime discovered offsets).", "Offsets",
            (c, a) =>
            {
                var name = S(a[0]);
                var v = c.Interp.OffsetResolver?.Invoke(name);
                if (v == null) throw c.Error($"offset '{name}' is not known – load an offset file or run Runtime Offset discovery");
                return R((long)v.Value);
            }, 1, 1, bare: true);
        Add("offset_exists", "offset_exists(\"Name\")", "True when the named offset is known.", "Offsets",
            (c, a) => R(c.Interp.OffsetResolver?.Invoke(S(a[0])) != null), 1, 1, bare: true);

        // ── background ──
        Add("freeze", "freeze(address, value, type = \"int32\", intervalMs = 50)", "Keeps writing a value in the background until Stop / unfreeze. Types: byte, int16, int32, float, double, bytes.", "Background",
            (c, a) =>
            {
                var addr = Addr(a[0], c);
                var type = a.Length > 2 ? S(a[2]).ToLowerInvariant() : (a[1] is double ? "float" : a[1] is string ? "bytes" : "int32");
                var interval = a.Length > 3 ? (int)Math.Clamp(L(a[3], c), 5, 60000) : 50;
                byte[] data = type switch
                {
                    "byte" => new[] { unchecked((byte)L(a[1], c)) },
                    "int16" or "short" => BE(BitConverter.GetBytes(unchecked((short)L(a[1], c)))),
                    "float" => BE(BitConverter.GetBytes((float)D(a[1], c))),
                    "double" => BE(BitConverter.GetBytes(D(a[1], c))),
                    "bytes" or "hex" => ParseHex(S(a[1])),
                    _ => BE(BitConverter.GetBytes(unchecked((int)L(a[1], c))))
                };
                var key = $"freeze:{addr:X8}";
                if (c.Interp.Background.TryRemove(key, out var old)) old.Cancel();
                var cts = CancellationTokenSource.CreateLinkedTokenSource(c.Token);
                c.Interp.Background[key] = cts;
                _ = Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        try { await c.Client.SetMemoryAsync(addr, data); await Task.Delay(interval, cts.Token); }
                        catch { break; }
                    }
                    c.Interp.Background.TryRemove(key, out _);
                });
                c.Log($"  ❄️ freeze 0x{addr:X8} ({type}) every {interval}ms");
                return R(null);
            }, 2, 4, net: true);
        Add("unfreeze", "unfreeze(address)", "Stops a freeze started with freeze().", "Background",
            (c, a) =>
            {
                var key = $"freeze:{Addr(a[0], c):X8}";
                if (c.Interp.Background.TryRemove(key, out var cts)) { cts.Cancel(); c.Log($"  🔓 unfreeze {key[7..]}"); }
                return R(null);
            }, 1, 1);
        Add("hook", "hook(name, address, length, intervalMs, \"script\" | functionName)", "Polls memory and runs a script or function every time the bytes change.", "Background",
            (c, a) =>
            {
                var name = S(a[0]);
                var addr = Addr(a[1], c);
                var len = (uint)Math.Clamp(L(a[2], c), 1, 0x10000);
                var interval = (int)Math.Clamp(L(a[3], c), 10, 600000);
                var action = a.Length > 4 ? S(a[4]) : "";
                var key = $"hook:{name}";
                if (c.Interp.Background.ContainsKey(key)) { c.Log($"  ⚠️ Hook '{name}' already exists"); return R(null); }
                var cts = CancellationTokenSource.CreateLinkedTokenSource(c.Token);
                c.Interp.Background[key] = cts;
                c.Log($"  🔗 Hook '{name}' @0x{addr:X8} len={len} every {interval}ms");
                _ = Task.Run(async () =>
                {
                    byte[]? last = null;
                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            var d = await c.Client.GetMemoryAsync(addr, len);
                            if (d != null && (last == null || !d.AsSpan().SequenceEqual(last)))
                            {
                                bool first = last == null;
                                last = d;
                                if (!first)
                                {
                                    c.Log($"  🔔 Hook '{name}' triggered");
                                    c.Interp.Globals["hookValue"] = Convert.ToHexString(d);
                                    if (c.Interp.HasUserFunction(action)) await c.Interp.InvokeUserFunctionAsync(action, Convert.ToHexString(d));
                                    else if (!string.IsNullOrWhiteSpace(action)) await c.Interp.RunChildAsync(action, cts.Token);
                                }
                            }
                            await Task.Delay(interval, cts.Token);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex) { c.Log($"  ⚠️ Hook '{name}': {ex.Message}"); try { await Task.Delay(interval, cts.Token); } catch { break; } }
                    }
                    c.Interp.Background.TryRemove(key, out _);
                    c.Log($"  ⏹️ Hook '{name}' stopped");
                });
                return R(null);
            }, 4, 5, net: true, bare: true);
        Add("unhook", "unhook(name)", "Stops a hook.", "Background",
            (c, a) => { if (c.Interp.Background.TryRemove($"hook:{S(a[0])}", out var cts)) cts.Cancel(); return R(null); }, 1, 1, bare: true);

        // ── output & flow ──
        Add("log", "log(value, ...)", "Prints to the console. Strings support $var and ${expression}.", "Output",
            (c, a) => { c.Log($"  💬 {string.Join(" ", a.Select(S))}"); return R(null); }, 0, command: true, aliases: new[] { "print" });
        Add("notify", "notify(message)", "Shows a desktop notification.", "Output",
            (c, a) => { var m = string.Join(" ", a.Select(S)); c.Interp.Notifier?.Invoke(m); c.Log($"  🔔 {m}"); return R(null); }, 1, command: true);
        Add("wait", "wait(milliseconds)", "Pauses the script. Stop interrupts the wait immediately.", "Flow",
            async (c, a) => { await Task.Delay((int)Math.Clamp(L(a[0], c), 0, int.MaxValue), c.Token); return null; }, 1, 1, command: true, aliases: new[] { "sleep" });
        Add("assert", "assert(condition, message = \"assertion failed\")", "Stops the script with an error when condition is false.", "Flow",
            (c, a) => { if (!ScriptInterpreter.Truthy(a[0])) throw c.Error(a.Length > 1 ? S(a[1]) : "assertion failed"); return R(null); }, 1, 2);
        Add("error", "error(message)", "Stops the script with an error.", "Flow",
            (c, a) => throw c.Error(S(a[0])), 1, 1, command: true);
        Add("exit", "exit()", "Ends the script (background hooks keep running).", "Flow",
            (c, a) => throw new ScriptExit(), 0, 0);
        Add("connected", "connected()", "True when a console is connected.", "Flow", (c, a) => R(c.Client.IsConnected), 0, 0);
        Add("xbdm", "xbdm(\"command\")", "Sends a raw XBDM command and returns the response text.", "Console",
            async (c, a) =>
            {
                if (c.Client is not XBDMClient x) throw c.Error("xbdm() requires a real console connection");
                var r = await x.SendCommandAsync(S(a[0]));
                return r;
            }, 1, 1, net: true);

        // ── values ──
        Add("hex", "hex(value, digits = 8)", "Formats a number as 0x hex text.", "Values",
            (c, a) =>
            {
                var digits = a.Length > 1 ? (int)L(a[1], c) : 8;
                var v = L(a[0], c);
                return R("0x" + (v < 0 && digits <= 8 ? unchecked((uint)v).ToString("X" + digits) : v.ToString("X" + digits)));
            }, 1, 2);
        Add("int", "int(value)", "Converts to an integer (parses \"0x..\" text too).", "Values", (c, a) => R(L(a[0], c)), 1, 1);
        Add("float", "float(value)", "Converts to a floating point number.", "Values", (c, a) => R(D(a[0], c)), 1, 1);
        Add("str", "str(value)", "Converts to text.", "Values", (c, a) => R(S(a[0])), 1, 1);
        Add("len", "len(textOrArray)", "Length of a string or array.", "Values",
            (c, a) => R(a[0] switch { string s => (long)s.Length, List<object?> l => (long)l.Count, _ => 0L }), 1, 1);
        Add("typeof", "typeof(value)", "Returns int, float, string, bool, array or null.", "Values",
            (c, a) => R(a[0] switch { long => "int", double => "float", string => "string", bool => "bool", List<object?> => "array", null => "null", _ => "object" }), 1, 1);
        Add("push", "push(array, value)", "Appends to an array and returns it.", "Values",
            (c, a) => { if (a[0] is not List<object?> l) throw c.Error("push needs an array"); l.Add(a[1]); return R(l); }, 2, 2);
        Add("contains", "contains(textOrArray, value)", "True when the value is found.", "Values",
            (c, a) => R(a[0] switch { string s => s.Contains(S(a[1]), StringComparison.OrdinalIgnoreCase), List<object?> l => l.Any(x => S(x) == S(a[1])), _ => false }), 2, 2);
        Add("float_to_hex", "float_to_hex(value)", "IEEE-754 bits of a float as hex text (3F800000 for 1.0).", "Values",
            (c, a) => R(BitConverter.SingleToInt32Bits((float)D(a[0], c)).ToString("X8")), 1, 1);
        Add("hex_to_float", "hex_to_float(\"3F800000\")", "Converts IEEE-754 hex bits to a float.", "Values",
            (c, a) => R((double)BitConverter.Int32BitsToSingle(unchecked((int)uint.Parse(S(a[0]).Replace("0x", "").Replace(" ", ""), NumberStyles.HexNumber)))), 1, 1);

        // ── math ──
        Add("abs", "abs(x)", "Absolute value.", "Math", (c, a) => R(a[0] is double d ? Math.Abs(d) : (object)Math.Abs(L(a[0], c))), 1, 1);
        Add("min", "min(a, b, ...)", "Smallest value.", "Math", (c, a) => R(a.Any(x => x is double) ? a.Min(x => D(x, c)) : (object)a.Min(x => L(x, c))), 1);
        Add("max", "max(a, b, ...)", "Largest value.", "Math", (c, a) => R(a.Any(x => x is double) ? a.Max(x => D(x, c)) : (object)a.Max(x => L(x, c))), 1);
        Add("floor", "floor(x)", "Rounds down.", "Math", (c, a) => R((long)Math.Floor(D(a[0], c))), 1, 1);
        Add("ceil", "ceil(x)", "Rounds up.", "Math", (c, a) => R((long)Math.Ceiling(D(a[0], c))), 1, 1);
        Add("round", "round(x, digits = 0)", "Rounds to the nearest value.", "Math",
            (c, a) => R(a.Length > 1 ? Math.Round(D(a[0], c), (int)L(a[1], c)) : (object)(long)Math.Round(D(a[0], c))), 1, 2);
        Add("sqrt", "sqrt(x)", "Square root.", "Math", (c, a) => R(Math.Sqrt(D(a[0], c))), 1, 1);
        Add("pow", "pow(x, y)", "x to the power of y.", "Math", (c, a) => R(Math.Pow(D(a[0], c), D(a[1], c))), 2, 2);
        Add("rand", "rand(min, max)", "Random integer in [min, max].", "Math",
            (c, a) => R((long)Rng.NextInt64(L(a[0], c), L(a[1], c) + 1)), 2, 2);
        Add("time", "time()", "Milliseconds since the script started.", "Math",
            (c, a) => R((long)(DateTime.Now - c.Interp.StartedAt).TotalMilliseconds), 0, 0);
        Add("now", "now()", "Current time as text (HH:mm:ss).", "Math", (c, a) => R(DateTime.Now.ToString("HH:mm:ss")), 0, 0);

        // ── introspection (legacy) ──
        Add("get_function", "get_function(name?)", "Lists runtime functions and variables (legacy helper).", "Introspection",
            (c, a) =>
            {
                if (a.Length > 0 && S(a[0]).Equals("var", StringComparison.OrdinalIgnoreCase))
                    c.Log($"  🔍 variables: {string.Join(", ", c.Interp.SnapshotVariables().Select(k => k.Key))}");
                else if (a.Length > 0 && All.TryGetValue(S(a[0]), out var f))
                    c.Log($"  🔍 {f.Signature} – {f.Description}");
                else
                    c.Log($"  🔍 functions: {string.Join(", ", All.Values.Where(f => !f.Description.Contains("alias of")).Select(f => f.Name))}");
                return R(null);
            }, 0, 1, bare: true);
        Add("var", "var(name?)", "Logs a variable's value, or all variable names (legacy helper).", "Introspection",
            (c, a) =>
            {
                if (a.Length == 0 || S(a[0]).Length == 0)
                    c.Log($"  📦 variables: {string.Join(", ", c.Interp.SnapshotVariables().Select(k => k.Key))}");
                else if (c.Interp.TryGetVar(S(a[0]), out var v))
                    c.Log($"  📦 var({S(a[0])}) = {S(v)}");
                else
                    c.Log($"  ⚠️ var({S(a[0])}) not found");
                return R(null);
            }, 0, 1, bare: true);
    }
}

/// <summary>Thrown by exit() to end the main body cleanly.</summary>
public sealed class ScriptExit : Exception { }
