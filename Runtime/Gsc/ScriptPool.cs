using System.Buffers.Binary;
using System.Text;

namespace MemoryEditor.Runtime.Gsc;

/// <summary>
/// One ScriptParseTree asset: <c>struct { const char* name; int len; byte* buffer; }</c> (12 bytes, big-endian).
/// The game looks scripts up by name in this pool when a map / round starts and executes the bytecode straight from
/// <see cref="Buffer"/> – re-pointing <see cref="BufferField"/> at another compiled script replaces it.
/// </summary>
public sealed class ScriptEntry
{
    public int Index { get; init; }
    public uint EntryAddress { get; init; }
    public uint BufferField => EntryAddress + 8;
    public uint NamePointer { get; init; }
    public string Name { get; init; } = "";
    public uint Length { get; init; }
    public uint Buffer { get; set; }
    public bool IsClientScript => Name.EndsWith(".csc", StringComparison.OrdinalIgnoreCase);
    public string FileName => Name[(Name.LastIndexOf('/') + 1)..];
    public override string ToString() => Name;
}

/// <summary>
/// Finds the ScriptParseTree pool of the running game at runtime – no hard-coded offsets are required:
///  1. a known pool address for the build (TU18 ships with one) is validated and used directly, otherwise
///  2. the game's writable image sections are scanned for runs of 12-byte records whose buffer pointer leads to a
///     compiled script (magic 80 47 53 43 0D 0A 00 06).
/// </summary>
public static class ScriptPool
{
    /// <summary>Known first-entry addresses (address of the name field) per executable timestamp.</summary>
    private static readonly Dictionary<uint, uint[]> KnownPools = new()
    {
        [0x53643D71] = new[] { 0x831EBB7Cu },   // Black Ops II TU18 default_mp.xex (validated on a live console)
    };

    private const int RecordSize = 12;

    public static async Task<List<ScriptEntry>> DiscoverAsync(XBDMClient c, Action<string>? log, IProgress<string>? status, CancellationToken ct)
    {
        var modules = await c.GetModulesAsync();
        var running = await c.GetRunningTitleAsync();
        var file = running?.Path is { Length: > 0 } p ? p[(p.LastIndexOf('\\') + 1)..] : "";
        var title = modules.FirstOrDefault(m => file.Length > 0 && m.Name.Equals(file, StringComparison.OrdinalIgnoreCase))
                    ?? modules.FirstOrDefault(m => m.Base == 0x82000000)
                    ?? throw new InvalidOperationException("No game is running");

        // 1. known pool for this build
        if (KnownPools.TryGetValue(title.Timestamp, out var seeds))
            foreach (var seed in seeds)
            {
                status?.Report("Reading the script pool…");
                var list = await ReadPoolAtAsync(c, seed, status, ct);
                if (list.Count >= 16)
                {
                    log?.Invoke($"📜 Script pool @ 0x{seed:X8}: {list.Count} scripts ({title.Name}, known build)");
                    return list;
                }
            }

        // 2. scan the image's writable data for the pool
        status?.Report("Searching the game's data for the script pool…");
        var regions = await c.WalkMemoryAsync();
        var data = regions.Where(r => r.Base < title.Base + title.Size && r.End > title.Base && ((r.Protect & 0xFF) is 0x04 or 0x08 or 0x40 or 0x80))
                          .Select(r => (Start: Math.Max(r.Base, title.Base), End: Math.Min(r.End, title.Base + title.Size))).ToList();
        var candidates = new List<uint>();
        foreach (var (start, end) in data)
            for (uint at = start; at < end; at += 0x10000)
            {
                ct.ThrowIfCancellationRequested();
                uint n = Math.Min(0x10000u + RecordSize * 32, end - at);
                var buf = await c.GetMemoryAsync(at, n);
                if (buf == null) continue;
                for (int i = 0; i + RecordSize * 16 <= buf.Length && i < 0x10000; i += 4)
                    if (LooksLikeRun(buf, i, 16)) { candidates.Add(at + (uint)i); i += RecordSize * 16; }
                await Task.Delay(1, ct);
            }

        foreach (var cand in candidates)
        {
            // walk back to the first record of the run
            uint first = cand;
            while (true)
            {
                var prev = await c.GetMemoryAsync(first - RecordSize, RecordSize);
                if (prev == null || !LooksLikeRecord(prev, 0)) break;
                first -= RecordSize;
            }
            var list = await ReadPoolAtAsync(c, first, status, ct);
            if (list.Count >= 16)
            {
                log?.Invoke($"📜 Script pool found by scan @ 0x{first:X8}: {list.Count} scripts ({title.Name} build 0x{title.Timestamp:X8})");
                return list;
            }
        }
        throw new InvalidOperationException("The script pool was not found – load into a lobby or a match first (scripts are only in memory then)");
    }

    private static bool LooksLikeRun(byte[] b, int off, int count)
    {
        for (int k = 0; k < count; k++)
            if (!LooksLikeRecord(b, off + k * RecordSize)) return false;
        return true;
    }

    private static bool LooksLikeRecord(byte[] b, int off)
    {
        if (off + RecordSize > b.Length) return false;
        uint name = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off));
        uint len = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off + 4));
        uint buf = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off + 8));
        return name >= 0x40000000 && name < 0xC0000000 && buf >= 0x40000000 && buf < 0xC0000000
               && len > 0 && len < 0x800000 && (buf & 3) == 0;
    }

    /// <summary>Reads consecutive records from <paramref name="first"/> while they validate (names + script magic).</summary>
    private static async Task<List<ScriptEntry>> ReadPoolAtAsync(XBDMClient c, uint first, IProgress<string>? status, CancellationToken ct)
    {
        var list = new List<ScriptEntry>();
        const int Batch = 256;
        int misses = 0;
        for (uint at = first; list.Count < 8192; at += Batch * RecordSize)
        {
            var raw = await c.GetMemoryAsync(at, Batch * RecordSize);
            if (raw == null) break;
            bool stop = false;
            for (int i = 0; i < Batch; i++)
            {
                ct.ThrowIfCancellationRequested();
                int off = i * RecordSize;
                if (!LooksLikeRecord(raw, off))
                {
                    // free / empty slots can sit between used ones near the end – tolerate a few
                    if (++misses > 8) { stop = true; break; }
                    continue;
                }
                uint namePtr = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(off));
                uint len = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(off + 4));
                uint buffer = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(off + 8));
                var name = await ReadNameAsync(c, namePtr);
                if (name == null || !name.Contains('/') && !name.Contains('\\')) { if (++misses > 8) { stop = true; break; } continue; }
                misses = 0;
                list.Add(new ScriptEntry { Index = list.Count, EntryAddress = at + (uint)off, NamePointer = namePtr, Name = name, Length = len, Buffer = buffer });
                if (list.Count % 64 == 0) status?.Report($"Reading the script pool… {list.Count} scripts");
            }
            if (stop) break;
        }
        // sanity: the first entries must point at compiled scripts
        int ok = 0, checkedCount = 0;
        foreach (var e in list.Take(6))
        {
            checkedCount++;
            if (await c.GetMemoryAsync(e.Buffer, 8) is { } m && GscBinary.HasMagic(m)) ok++;
        }
        return checkedCount > 0 && ok * 2 >= checkedCount ? list : new List<ScriptEntry>();
    }

    private static async Task<string?> ReadNameAsync(XBDMClient c, uint ptr)
    {
        var b = await c.GetMemoryAsync(ptr, 96);
        if (b == null) return null;
        int end = Array.IndexOf(b, (byte)0);
        if (end <= 2) return null;
        for (int i = 0; i < end; i++) if (b[i] < 0x20 || b[i] > 0x7E) return null;
        return Encoding.ASCII.GetString(b, 0, end);
    }

    /// <summary>
    /// Maps a file path to the script it replaces: "…\maps\mp\gametypes\_clientids.gsc" → "maps/mp/gametypes/_clientids.gsc".
    /// A bare file name is matched against the pool when it is unique.
    /// </summary>
    public static string? TargetFor(string path, IReadOnlyList<ScriptEntry> pool)
    {
        var p = path.Replace('\\', '/');
        var ext = Path.GetExtension(p).ToLowerInvariant();
        if (ext is ".txt" or "") p = Path.ChangeExtension(p, ".gsc");
        foreach (var root in new[] { "maps/", "clientscripts/", "common_scripts/", "codescripts/" })
        {
            int i = p.LastIndexOf("/" + root, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return p[(i + 1)..].ToLowerInvariant();
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return p.ToLowerInvariant();
        }
        var fileName = Path.GetFileName(p);
        var hits = pool.Where(e => e.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToList();
        return hits.Count == 1 ? hits[0].Name : null;
    }
}
