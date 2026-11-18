using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace MemoryEditor.Runtime.Gsc;

public enum RestartMode { None, MapRestart, FastRestart }

/// <summary>
/// Live GSC injection for Black Ops II on Xbox 360.
///
/// A compiled script is written into a free block of title memory (the "arena", 0x40300000 by default – the area
/// established BO2 injectors use, inside a 3.5 MB read/write heap region) and the ScriptParseTree entry of the script it
/// replaces is re-pointed at it. The game picks it up the next time scripts load (match start or a round restart).
///
/// Safety rules:
///  • the VM executes bytecode straight from the buffer, so a script that may be running is never overwritten:
///    re-injecting writes to a fresh slot and the previous slot is only reused after a restart;
///  • every write is verified by reading it back before the pointer is switched (a 4-byte, atomic write);
///  • original pointers are saved to disk first, so "Restore" works even after the app or the console link restarts;
///  • the arena must be committed read/write memory and is checked for foreign data before first use.
/// </summary>
public sealed class GscInjector
{
    public sealed class Slot
    {
        public string Target { get; set; } = "";
        public uint Address { get; set; }
        public uint Size { get; set; }
        /// <summary>False once replaced / restored: the bytes may still be executing until the next restart.</summary>
        public bool Active { get; set; }
        public DateTime InjectedAt { get; set; }
        public string Source { get; set; } = "";
    }

    private sealed class SessionFile
    {
        public string BuildKey { get; set; } = "";
        public uint ArenaBase { get; set; }
        public Dictionary<string, uint> OriginalBuffers { get; set; } = new();
        public List<Slot> Slots { get; set; } = new();
        /// <summary>Arena ranges we have written (their bytes are ours, not the game's).</summary>
        public List<Range> Written { get; set; } = new();
    }

    public sealed class Range
    {
        public uint Start { get; set; }
        public uint End { get; set; }
    }

    /// <summary>True when the GSC Loader plugin (Tesseract.dll) is running inside the game.</summary>
    public bool LoaderActive { get; private set; }
    /// <summary>The game's folder on the console (e.g. Hdd:\games\call of duty - black ops ii\).</summary>
    public string GameFolder { get; private set; } = "";
    /// <summary>The GSC Loader reads replacement scripts from here, mirroring the game's paths.</summary>
    public string LoaderFolder => GameFolder.Length > 0 ? GameFolder + @"raw\" : "";
    /// <summary>Set to inject live even though the GSC Loader is running (not recommended: both use title memory).</summary>
    public bool AllowWithLoader { get; set; }
    /// <summary>Free (zero-filled) part of the arena region found at attach time.</summary>
    public uint FreeStart { get; private set; }

    private readonly Func<XBDMClient?> _client;
    private readonly Action<string> _log;
    private SessionFile _session = new();
    private string _sessionPath = "";

    public IReadOnlyList<ScriptEntry> Pool { get; private set; } = Array.Empty<ScriptEntry>();
    public string BuildKey { get; private set; } = "";
    public string GameName { get; private set; } = "";
    public uint ArenaBase { get; set; } = 0x40300000;
    public uint ArenaEnd { get; private set; }
    /// <summary>The last 256 bytes of the arena hold console command strings for <see cref="SendCommandAsync"/>.</summary>
    private uint CommandScratch => ArenaEnd - 0x100;
    public uint? CbufAddText { get; set; }

    public IReadOnlyList<Slot> Slots => _session.Slots;
    public IEnumerable<Slot> ActiveSlots => _session.Slots.Where(s => s.Active);
    public bool IsReady => Pool.Count > 0 && ArenaEnd > ArenaBase;

    public static string SessionFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Games", "gsc");

    public GscInjector(Func<XBDMClient?> client, Action<string> log)
    {
        _client = client;
        _log = log;
    }

    private XBDMClient Client() => _client() is { IsConnected: true } c ? c : throw new IOException("Connect to the console first");

    // ═════════════════════════════════════════════════════════════════════════
    //  attach: pool + arena + saved session
    // ═════════════════════════════════════════════════════════════════════════

    public async Task AttachAsync(IProgress<string>? status, CancellationToken ct)
    {
        var c = Client();
        var modules = await c.GetModulesAsync();
        var running = await c.GetRunningTitleAsync();
        var file = running?.Path is { Length: > 0 } p ? p[(p.LastIndexOf('\\') + 1)..] : "";
        var title = modules.FirstOrDefault(m => file.Length > 0 && m.Name.Equals(file, StringComparison.OrdinalIgnoreCase))
                    ?? modules.FirstOrDefault(m => m.Base == 0x82000000)
                    ?? throw new InvalidOperationException("No game is running");
        GameName = title.Name;
        BuildKey = $"{Path.GetFileNameWithoutExtension(title.Name)}_{title.Timestamp:X8}";
        LoaderActive = modules.Any(m => m.Name.Equals("Tesseract.dll", StringComparison.OrdinalIgnoreCase));
        var dev = running?.Path is { Length: > 0 } rp ? XBDMClient.ToXbdmPath(rp) : "";
        GameFolder = dev.Contains('\\') ? dev[..(dev.LastIndexOf('\\') + 1)] : "";

        Pool = await ScriptPool.DiscoverAsync(c, _log, status, ct);

        // arena: must be committed, writable, cached memory
        var regions = await c.WalkMemoryAsync();
        var region = regions.FirstOrDefault(r => r.Base <= ArenaBase && r.End > ArenaBase)
                     ?? throw new InvalidOperationException($"Injection memory 0x{ArenaBase:X8} is not allocated on the console – choose another address");
        if ((region.Protect & 0xFF) is not (0x04 or 0x40) || (region.Protect & 0x700) != 0)
            throw new InvalidOperationException($"Injection memory 0x{ArenaBase:X8} is not plain read/write memory ({region.ProtectText})");
        ArenaEnd = region.End;

        LoadSession();
        // reconcile with the console: a pool entry that points into the arena is ours; anything else was reloaded by the
        // game (map change) – its backup is stale and its slot is free
        foreach (var e in Pool)
        {
            bool inArena = e.Buffer >= ArenaBase && e.Buffer < ArenaEnd;
            foreach (var s in _session.Slots.Where(s => s.Active && s.Target == e.Name && s.Address != e.Buffer)) s.Active = false;
            if (!inArena) _session.OriginalBuffers.Remove(e.Name);
        }
        if (!Pool.Any(e => e.Buffer >= ArenaBase && e.Buffer < ArenaEnd))
        {
            _session.Slots.Clear();                       // nothing of ours is loaded – earlier slots are free…
            _session.Written.Clear();                     // …but after a restart the game may reuse that memory: re-check it
        }
        int live = Pool.Count(e => e.Buffer >= ArenaBase && e.Buffer < ArenaEnd);
        if (live > 0) _log($"💉 {live} injected script(s) from an earlier session are still active");
        await FindFreeArenaAsync(c, status, ct);
        if (LoaderActive) _log("📦 The GSC Loader (Tesseract.dll) is running in this game – scripts will go to its raw folder instead of live memory");
        SaveSession();
        status?.Report($"{Pool.Count} scripts · injection memory 0x{FreeStart:X8}–0x{ArenaEnd:X8} ({(ArenaEnd - FreeStart) / 1024:N0} KB)");
    }

    public ScriptEntry? Find(string target) =>
        Pool.FirstOrDefault(e => e.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
        ?? Pool.FirstOrDefault(e => e.Name.Equals(target + ".gsc", StringComparison.OrdinalIgnoreCase));

    public bool IsInjected(ScriptEntry e) => _session.Slots.Any(s => s.Active && s.Target == e.Name);
    public uint? OriginalOf(ScriptEntry e) => _session.OriginalBuffers.TryGetValue(e.Name, out var v) ? v : null;

    // ═════════════════════════════════════════════════════════════════════════
    //  inject / restore
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Injects a compiled script in place of <paramref name="target"/>. Returns the slot used.</summary>
    public async Task<Slot> InjectAsync(string target, byte[] compiled, string source, IProgress<double>? progress, CancellationToken ct)
    {
        var c = Client();
        if (!IsReady) throw new InvalidOperationException("Scan the game's scripts first");
        var entry = Find(target) ?? throw new InvalidOperationException($"“{target}” is not loaded in this game – pick a script from the list");
        var gsc = GscBinary.TryParse(compiled) ?? throw new InvalidOperationException("That file is not a compiled GSC script");
        if (!gsc.IsValid) throw new InvalidOperationException(string.Join("; ", gsc.Problems));
        if (KindMismatch(entry.Name, source) is { } why) throw new InvalidOperationException(why);
        if (LoaderActive && !AllowWithLoader)
            throw new InvalidOperationException(
                "The GSC Loader (Tesseract.dll) is running in this game. It loads scripts itself and uses the same memory, so live injection on top of it " +
                "crashes the game. Use the “GSC Loader” method (copies the script into the game's raw folder), or remove Tesseract.dll / the loader default_mp.xex to inject live.");

        // first time this script is replaced: remember the stock pointer (persisted before anything is written)
        if (!_session.OriginalBuffers.ContainsKey(entry.Name))
        {
            var cur = await ReadU32Async(c, entry.BufferField) ?? throw new IOException("Could not read the script pool entry");
            if (cur >= ArenaBase && cur < ArenaEnd) throw new InvalidOperationException($"{entry.Name} already points into injection memory but no backup exists – restart the game to get a clean pool");
            _session.OriginalBuffers[entry.Name] = cur;
            SaveSession();
        }

        uint size = (uint)compiled.Length;
        uint at = Allocate(size) ?? throw new InvalidOperationException(
            $"Injection memory is full ({(ArenaEnd - ArenaBase) / 1024:N0} KB, {ActiveSlots.Count()} active + {_session.Slots.Count(s => !s.Active)} pending slots). " +
            "Restart the round (or use Restore all) to free memory used by replaced scripts.");

        await EnsureArenaCleanAsync(c, at, size);

        // write + verify, then switch the pointer
        const uint Chunk = 0x1000;
        for (uint off = 0; off < size; off += Chunk)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(Chunk, size - off);
            var part = compiled.AsSpan((int)off, n).ToArray();
            if (!await c.SetMemoryAsync(at + off, part)) throw new IOException($"Writing the script failed at 0x{at + off:X8}");
            progress?.Report(0.9 * (off + (uint)n) / size);
        }
        var back = await c.GetMemoryAsync(at, size);
        if (back == null || !back.AsSpan().SequenceEqual(compiled)) throw new IOException("Verification failed – the script in console memory differs from the compiled file. Nothing was switched.");
        progress?.Report(0.95);

        foreach (var old in _session.Slots.Where(s => s.Active && s.Target == entry.Name)) old.Active = false;
        _session.Written.Add(new Range { Start = at, End = at + size });
        var slot = new Slot { Target = entry.Name, Address = at, Size = size, Active = true, InjectedAt = DateTime.Now, Source = source };
        _session.Slots.Add(slot);
        SaveSession();

        if (!await WriteU32Async(c, entry.BufferField, at)) throw new IOException("Switching the script pointer failed");
        entry.Buffer = at;
        progress?.Report(1);
        _log($"💉 {entry.Name} ← {(source.Length > 0 ? Path.GetFileName(source) : "compiled script")} ({gsc.Summary}) @ 0x{at:X8}");
        return slot;
    }

    /// <summary>Points a script back at the game's own bytecode.</summary>
    public async Task<bool> RestoreAsync(string target)
    {
        var c = Client();
        var entry = Find(target);
        if (entry == null || !_session.OriginalBuffers.TryGetValue(entry.Name, out var orig)) return false;
        // the stock buffer must still be a compiled script (zone memory is unloaded / reused on map change)
        if (await c.GetMemoryAsync(orig, 8) is not { } m || !GscBinary.HasMagic(m))
            _log($"⚠️ The original {entry.Name} is no longer in memory (map changed?) – the game reloads it on the next map");
        else if (!await WriteU32Async(c, entry.BufferField, orig)) throw new IOException("Restoring the script pointer failed");
        else entry.Buffer = orig;
        foreach (var s in _session.Slots.Where(s => s.Target == entry.Name)) s.Active = false;
        _session.OriginalBuffers.Remove(entry.Name);
        SaveSession();
        _log($"↩️ {entry.Name} restored to the game's script");
        return true;
    }

    public async Task<int> RestoreAllAsync()
    {
        int n = 0;
        foreach (var name in _session.OriginalBuffers.Keys.ToList())
            if (await RestoreAsync(name)) n++;
        return n;
    }

    /// <summary>After the game reloaded its scripts, slots of replaced scripts are no longer executing and can be reused.</summary>
    public void ReleasePendingSlots()
    {
        int before = _session.Slots.Count;
        _session.Slots.RemoveAll(s => !s.Active);
        if (_session.Slots.Count != before) SaveSession();
    }

    /// <summary>First fit after every slot that may still be executing (active or replaced since the last restart).</summary>
    private uint? Allocate(uint size)
    {
        const uint Align = 0x80;
        uint limit = CommandScratch;
        uint cursor = Math.Max(ArenaBase, FreeStart);
        foreach (var s in _session.Slots.OrderBy(s => s.Address))
        {
            if (s.Address >= cursor && s.Address - cursor >= size) break;          // fits in the gap before this slot
            cursor = Math.Max(cursor, (s.Address + s.Size + Align - 1) & ~(Align - 1));
        }
        return cursor + size <= limit ? cursor : null;
    }

    /// <summary>
    /// Every byte of a block must be zero or written by us before (an earlier, now replaced script). Anything else is
    /// memory the game or a plugin is using – writing there corrupts it and crashes the game, so nothing is written.
    /// </summary>
    private async Task EnsureArenaCleanAsync(XBDMClient c, uint at, uint size)
    {
        var block = await c.GetMemoryAsync(at, size);
        if (block == null || block.Length < size) throw new IOException($"Injection memory 0x{at:X8} is not readable");
        for (int i = 0; i < block.Length; i++)
        {
            if (block[i] == 0) continue;
            uint a = at + (uint)i;
            var ours = _session.Written.FirstOrDefault(w => a >= w.Start && a < w.End);
            if (ours != null) { i = (int)Math.Min(block.Length - 1, ours.End - at - 1); continue; }
            throw new InvalidOperationException(
                $"Injection memory 0x{a:X8} is in use by the game or a plugin – nothing was written. " +
                (LoaderActive ? "The GSC Loader (Tesseract.dll) is running and uses this memory. " : "") +
                "Restart the game and inject from the pregame lobby, or set another arena address.");
        }
    }

    /// <summary>
    /// Restricts allocation to the largest zero-filled (or previously ours) stretch of the arena region, so scripts never
    /// land on memory something else is using. The command scratch area moves with it.
    /// </summary>
    private async Task FindFreeArenaAsync(XBDMClient c, IProgress<string>? status, CancellationToken ct)
    {
        status?.Report("Checking injection memory…");
        const uint Gran = 0x100;
        uint bestStart = 0, bestLen = 0, runStart = ArenaBase, runLen = 0;
        for (uint at = ArenaBase; at < ArenaEnd; at += 0x10000)
        {
            ct.ThrowIfCancellationRequested();
            uint n = Math.Min(0x10000u, ArenaEnd - at);
            var buf = await c.GetMemoryAsync(at, n);
            for (uint off = 0; off < n; off += Gran)
            {
                uint a = at + off;
                bool free = buf != null && off + Gran <= buf.Length && IsFreeOrOurs(buf, (int)off, (int)Gran, a);
                if (free) { if (runLen == 0) runStart = a; runLen += Gran; if (runLen > bestLen) { bestLen = runLen; bestStart = runStart; } }
                else runLen = 0;
            }
            await Task.Delay(1, ct);
        }
        if (bestLen < 0x10000) throw new InvalidOperationException($"No free injection memory in 0x{ArenaBase:X8}–0x{ArenaEnd:X8} – something else is using it (GSC Loader / another injector?). Set another arena address.");
        if (bestStart != ArenaBase || bestStart + bestLen != ArenaEnd)
            _log($"🧭 Injection memory: using the free part 0x{bestStart:X8}–0x{bestStart + bestLen:X8} ({bestLen / 1024:N0} KB) – the rest of 0x{ArenaBase:X8}–0x{ArenaEnd:X8} is in use");
        FreeStart = bestStart;
        ArenaEnd = bestStart + bestLen;
    }

    private bool IsFreeOrOurs(byte[] b, int off, int len, uint addr)
    {
        if (_session.Written.Any(w => addr >= w.Start && addr + (uint)len <= w.End)) return true;
        for (int i = off; i < off + len; i++) if (b[i] != 0) return false;
        return true;
    }

    /// <summary>Server scripts (.gsc) and client scripts (.csc) are different programs – never swap one for the other.</summary>
    public static string? KindMismatch(string target, string source)
    {
        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (ext is not (".gsc" or ".csc")) return null;
        bool targetCsc = target.EndsWith(".csc", StringComparison.OrdinalIgnoreCase);
        if (targetCsc == (ext == ".csc")) return null;
        return $"{Path.GetFileName(source)} is a {(ext == ".csc" ? "client (.csc)" : "server (.gsc)")} script but {target} is a {(targetCsc ? "client (.csc)" : "server (.gsc)")} script – " +
               "replacing one with the other crashes the game. Pick a matching script.";
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  GSC Loader (Tesseract.dll): deploy into the game's raw folder
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Console path the loader reads for <paramref name="target"/> (raw\maps\mp\gametypes\_clientids.gsc …).</summary>
    public string LoaderPathFor(string target) =>
        LoaderFolder.Length == 0 ? throw new InvalidOperationException("The game's folder is unknown – is a game running?")
                                 : LoaderFolder + target.Replace('/', '\\');

    /// <summary>Copies a compiled script into the loader's raw folder (verified). The loader uses it when the next match / round loads.</summary>
    public async Task<string> DeployToLoaderAsync(string target, byte[] compiled, string source, IProgress<double>? progress, CancellationToken ct)
    {
        var c = Client();
        var gsc = GscBinary.TryParse(compiled) ?? throw new InvalidOperationException("That file is not a compiled GSC script");
        if (!gsc.IsValid) throw new InvalidOperationException(string.Join("; ", gsc.Problems));
        if (KindMismatch(target, source) is { } why) throw new InvalidOperationException(why);
        if (!target.Contains('/')) throw new InvalidOperationException("Use the full script path, e.g. maps/mp/gametypes/_clientids.gsc");
        var path = LoaderPathFor(target);

        // create raw\maps\mp\… one level at a time (mkdir fails harmlessly when a folder exists)
        var dir = path[..path.LastIndexOf('\\')];
        var parts = dir[GameFolder.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var cur = GameFolder.TrimEnd('\\');
        foreach (var p in parts) { cur += "\\" + p; await c.MakeDirectoryAsync(cur); }

        using (var ms = new MemoryStream(compiled))
            await c.UploadFileAsync(path, ms, compiled.Length, new Progress<(long Done, long Total)>(d => progress?.Report(0.9 * d.Done / Math.Max(1, d.Total))), ct);

        // verify: read it back
        using var back = new MemoryStream();
        await c.DownloadFileAsync(path, back, null, ct);
        if (!back.ToArray().AsSpan().SequenceEqual(compiled)) throw new IOException($"Verification failed – {path} on the console differs from the compiled script");
        progress?.Report(1);
        _log($"📦 GSC Loader: {target} ← {(source.Length > 0 ? Path.GetFileName(source) : "compiled script")} ({gsc.Summary}) → {path}");
        return path;
    }

    /// <summary>Removes a script from the loader's raw folder (the game's own script is used again).</summary>
    public async Task<bool> RemoveFromLoaderAsync(string target)
    {
        var c = Client();
        var r = await c.DeleteAsync(LoaderPathFor(target), false);
        if (r.Ok) _log($"🗑 GSC Loader: removed {target}");
        return r.Ok;
    }

    /// <summary>Scripts currently in the loader's raw folder, as game script names.</summary>
    public async Task<List<string>> ListLoaderScriptsAsync()
    {
        var c = Client();
        var found = new List<string>();
        if (LoaderFolder.Length == 0) return found;
        async Task Walk(string dir, int depth)
        {
            if (depth > 8) return;
            List<XBDMClient.FileEntry> entries;
            try { entries = await c.ListDirectoryAsync(dir); } catch { return; }
            foreach (var e in entries)
            {
                var full = dir.TrimEnd('\\') + "\\" + e.Name;
                if (e.IsDirectory) await Walk(full, depth + 1);
                else if (e.Name.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".csc", StringComparison.OrdinalIgnoreCase))
                    found.Add(full[LoaderFolder.Length..].Replace('\\', '/').ToLowerInvariant());
            }
        }
        await Walk(LoaderFolder.TrimEnd('\\'), 0);
        return found;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  console commands (restart)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Runs a console command in the game (Cbuf_AddText(0, text) through JRPC2).</summary>
    public async Task SendCommandAsync(string command)
    {
        var c = Client();
        if (CbufAddText is not { } fn) throw new InvalidOperationException("Cbuf_AddText is not known for this build – load the game's offsets (Game Library) or enter its address");
        var text = Encoding.ASCII.GetBytes(command.Trim() + "\n\0");
        if (text.Length > 0x100) throw new ArgumentException("Command too long (255 characters max)");
        if (!await c.SetMemoryAsync(CommandScratch, text)) throw new IOException("Could not write the command text");
        await c.JrpcCallAsync(fn, true, 0u, CommandScratch);
        _log($"⌨️ Console command: {command.Trim()}");
    }

    public async Task RestartAsync(RestartMode mode)
    {
        if (mode == RestartMode.None) return;
        await SendCommandAsync(mode == RestartMode.FastRestart ? "fast_restart" : "map_restart");
        // the old buffers stop executing once the round has restarted
        await Task.Delay(4000);
        ReleasePendingSlots();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  dump
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Reads a script's compiled bytecode from the console (the stock one when it is currently replaced).</summary>
    public async Task<byte[]> DumpAsync(ScriptEntry e, bool original)
    {
        var c = Client();
        uint buf = original && OriginalOf(e) is { } o ? o : e.Buffer;
        var head = await c.GetMemoryAsync(buf, GscBinary.HeaderSize) ?? throw new IOException("The script is not readable");
        if (!GscBinary.HasMagic(head)) throw new IOException("The buffer does not hold a compiled script any more");
        uint size = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0x24));
        if (size < GscBinary.HeaderSize || size > 0x800000) size = Math.Max(e.Length, GscBinary.HeaderSize);
        var all = await c.GetMemoryAsync(buf, size) ?? throw new IOException("Reading the script failed");
        return all;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  persistence
    // ═════════════════════════════════════════════════════════════════════════

    private void LoadSession()
    {
        Directory.CreateDirectory(SessionFolder);
        var c = _client();
        _sessionPath = Path.Combine(SessionFolder, $"session_{(c?.IpAddress ?? "console").Replace(':', '_')}_{BuildKey}.json");
        _session = new SessionFile { BuildKey = BuildKey, ArenaBase = ArenaBase };
        try
        {
            if (File.Exists(_sessionPath) && JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(_sessionPath)) is { } s && s.ArenaBase == ArenaBase)
                _session = s;
        }
        catch { /* corrupt session file – start clean */ }
    }

    private void SaveSession()
    {
        if (_sessionPath.Length == 0) return;
        try { File.WriteAllText(_sessionPath, JsonSerializer.Serialize(_session, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { _log($"⚠️ Could not save the injection backup: {ex.Message}"); }
    }

    /// <summary>The game restarted / changed map: forget slots (the pool was rebuilt by the game), keep originals only if still valid.</summary>
    public void Reset()
    {
        Pool = Array.Empty<ScriptEntry>();
        ArenaEnd = 0;
    }

    private static async Task<uint?> ReadU32Async(XBDMClient c, uint a) =>
        await c.GetMemoryAsync(a, 4) is { Length: 4 } b ? BinaryPrimitives.ReadUInt32BigEndian(b) : null;

    private static Task<bool> WriteU32Async(XBDMClient c, uint a, uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return c.SetMemoryAsync(a, b);
    }
}
