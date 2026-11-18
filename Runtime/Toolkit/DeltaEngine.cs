using System.Buffers.Binary;
using System.Collections;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>Reads memory for the delta engine (real console, or a simulation in tests).</summary>
public interface IMemorySource
{
    Task<byte[]?> ReadAsync(uint address, uint length, CancellationToken ct);
}

public sealed class XbdmMemorySource : IMemorySource
{
    private readonly XBDMClient _c;
    public XbdmMemorySource(XBDMClient c) => _c = c;
    public async Task<byte[]?> ReadAsync(uint address, uint length, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await _c.GetMemoryAsync(address, length);
    }
}

public enum DeltaFilter { Unchanged, Changed, Increased, Decreased }
public enum DeltaValueType { Auto, Float, Int32 }

/// <summary>
/// Real-time differential snapshot engine ("Delta Tracker"). A baseline snapshot of the selected memory is taken,
/// then every pass re-reads only the surviving 4-byte words and keeps the ones that behaved as requested
/// (unchanged while idle, changed / increased / decreased while acting). Candidates live in one bit per word,
/// so hundreds of megabytes can be tracked; reads shrink to the surviving runs as the set narrows.
/// </summary>
public sealed class DeltaEngine
{
    public sealed class Region
    {
        public uint Base { get; init; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public BitArray Alive { get; set; } = new(0);
        public int AliveCount { get; set; }
        public uint Size => (uint)Data.Length;
    }

    private readonly List<Region> _regions = new();
    /// <summary>How often each surviving address changed during "action" passes (consistency signal).</summary>
    private readonly Dictionary<uint, int> _actionHits = new();

    public IReadOnlyList<Region> Regions => _regions;
    public long Candidates => _regions.Sum(r => (long)r.AliveCount);
    public int Passes { get; private set; }
    public bool HasBaseline => _regions.Count > 0;
    public DeltaValueType ValueType { get; set; } = DeltaValueType.Auto;
    /// <summary>Pause after every 64 KB read (ms). Bulk reads at full speed starve the game and XBDM.</summary>
    public int PaceMs { get; set; } = 2;
    public List<string> History { get; } = new();

    public int ActionHits(uint address) => _actionHits.GetValueOrDefault(address);

    public void Reset()
    {
        _regions.Clear();
        _actionHits.Clear();
        History.Clear();
        Passes = 0;
    }

    /// <summary>Takes the baseline snapshot of the given ranges (every aligned word becomes a candidate).</summary>
    public async Task BaselineAsync(IMemorySource src, IEnumerable<(uint Start, uint Size)> ranges, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        Reset();
        var list = ranges.Select(r => (Start: r.Start & ~3u, Size: r.Size & ~3u)).Where(r => r.Size >= 4).ToList();
        long total = list.Sum(r => (long)r.Size), done = 0;
        const uint chunk = 0x10000;
        foreach (var (start, size) in list)
        {
            var data = new byte[size];
            var alive = new BitArray((int)(size / 4), true);
            for (uint off = 0; off < size; off += chunk)
            {
                ct.ThrowIfCancellationRequested();
                uint n = Math.Min(chunk, size - off);
                var part = await src.ReadAsync(start + off, n, ct);
                if (part != null && part.Length >= n) Buffer.BlockCopy(part, 0, data, (int)off, (int)n);
                if (PaceMs > 0) await Task.Delay(PaceMs, ct);
                else for (uint w = off / 4; w < (off + n) / 4; w++) alive[(int)w] = false; // unreadable → never a candidate
                done += n;
                progress?.Report(($"Baseline {done / 1048576.0:0.0} / {total / 1048576.0:0.0} MB", (double)done / Math.Max(1, total)));
            }
            var reg = new Region { Base = start, Data = data, Alive = alive };
            reg.AliveCount = Count(alive);
            if (reg.AliveCount > 0) _regions.Add(reg);
        }
        History.Add($"Baseline: {Candidates:N0} words in {_regions.Count} regions ({total / 1048576.0:0.0} MB)");
    }

    private static int Count(BitArray b)
    {
        int n = 0;
        for (int i = 0; i < b.Length; i++) if (b[i]) n++;
        return n;
    }

    /// <summary>Re-reads the surviving words and keeps the ones matching <paramref name="filter"/>.</summary>
    public async Task<long> FilterAsync(IMemorySource src, DeltaFilter filter, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        if (!HasBaseline) throw new InvalidOperationException("Take a baseline first");
        long before = Candidates, processed = 0;
        foreach (var reg in _regions)
        {
            if (reg.AliveCount == 0) continue;
            foreach (var (startWord, endWord) in Runs(reg))
            {
                ct.ThrowIfCancellationRequested();
                uint off = (uint)startWord * 4, len = (uint)(endWord - startWord) * 4;
                // read in ≤64 KB pieces
                for (uint p = 0; p < len; p += 0x10000)
                {
                    uint n = Math.Min(0x10000, len - p);
                    var cur = await src.ReadAsync(reg.Base + off + p, n, ct);
                    if (PaceMs > 0 && n >= 0x4000) await Task.Delay(PaceMs, ct);
                    processed += Apply(reg, filter, cur, (int)((off + p) / 4), (int)(n / 4), trackHits: before <= 2_000_000);
                    progress?.Report(($"Filtering… {processed:N0} / {before:N0}", (double)processed / Math.Max(1, before)));
                }
            }
        }
        _regions.RemoveAll(r => r.AliveCount == 0);
        // forget hit counters of words that were eliminated
        if (_actionHits.Count > 0 && Candidates < _actionHits.Count / 2)
            foreach (var k in _actionHits.Keys.Where(k => !IsAlive(k)).ToList()) _actionHits.Remove(k);
        Passes++;
        History.Add($"{filter}: {before:N0} → {Candidates:N0}");
        return Candidates;
    }

    /// <summary>Compares one freshly read block against the snapshot and updates the candidate bits (synchronous: uses spans).</summary>
    private long Apply(Region reg, DeltaFilter filter, byte[]? cur, int w0, int wn, bool trackHits)
    {
        long processed = 0;
        for (int w = w0; w < w0 + wn; w++)
        {
            if (!reg.Alive[w]) continue;
            processed++;
            if (cur == null || cur.Length < wn * 4) { reg.Alive[w] = false; reg.AliveCount--; continue; }
            int i = (w - w0) * 4, o = w * 4;
            var oldSpan = reg.Data.AsSpan(o, 4);
            var newSpan = cur.AsSpan(i, 4);
            if (!Keep(filter, oldSpan, newSpan)) { reg.Alive[w] = false; reg.AliveCount--; continue; }
            if (filter != DeltaFilter.Unchanged && trackHits)
            {
                uint a = reg.Base + (uint)o;
                _actionHits[a] = _actionHits.GetValueOrDefault(a) + 1;
            }
            newSpan.CopyTo(oldSpan);
        }
        return processed;
    }

    private bool Keep(DeltaFilter f, ReadOnlySpan<byte> oldB, ReadOnlySpan<byte> newB)
    {
        bool same = oldB.SequenceEqual(newB);
        switch (f)
        {
            case DeltaFilter.Unchanged: return same;
            case DeltaFilter.Changed: return !same;
        }
        if (same) return false;
        int cmp;
        var t = ValueType;
        if (t == DeltaValueType.Auto) t = DeltaHeuristics.LooksLikeFloat(BinaryPrimitives.ReadUInt32BigEndian(newB)) ? DeltaValueType.Float : DeltaValueType.Int32;
        if (t == DeltaValueType.Float)
        {
            float a = BinaryPrimitives.ReadSingleBigEndian(oldB), b = BinaryPrimitives.ReadSingleBigEndian(newB);
            if (float.IsNaN(a) || float.IsNaN(b)) return false;
            cmp = b.CompareTo(a);
        }
        else cmp = BinaryPrimitives.ReadInt32BigEndian(newB).CompareTo(BinaryPrimitives.ReadInt32BigEndian(oldB));
        return f == DeltaFilter.Increased ? cmp > 0 : cmp < 0;
    }

    public bool IsAlive(uint address)
    {
        foreach (var r in _regions)
            if (address >= r.Base && address < r.Base + r.Size) return r.Alive[(int)((address - r.Base) / 4)];
        return false;
    }

    /// <summary>Runs of alive words, merging gaps up to 64 words (one request is cheaper than many tiny ones).</summary>
    private static IEnumerable<(int Start, int End)> Runs(Region r)
    {
        int n = r.Alive.Length, i = 0;
        while (i < n)
        {
            while (i < n && !r.Alive[i]) i++;
            if (i >= n) yield break;
            int start = i, last = i;
            int gap = 0;
            while (i < n && gap <= 64)
            {
                if (r.Alive[i]) { last = i; gap = 0; } else gap++;
                i++;
            }
            yield return (start, last + 1);
        }
    }

    /// <summary>Surviving addresses with their current value bytes (up to <paramref name="max"/>).</summary>
    public IEnumerable<(uint Address, uint Raw)> Enumerate(int max = 100_000)
    {
        int n = 0;
        foreach (var r in _regions)
            for (int w = 0; w < r.Alive.Length; w++)
            {
                if (!r.Alive[w]) continue;
                yield return (r.Base + (uint)w * 4, BinaryPrimitives.ReadUInt32BigEndian(r.Data.AsSpan(w * 4, 4)));
                if (++n >= max) yield break;
            }
    }

    /// <summary>Bytes around an address from the latest snapshot (for cluster analysis); null outside tracked memory.</summary>
    public uint? WordAt(uint address)
    {
        foreach (var r in _regions)
            if (address >= r.Base && address + 4 <= r.Base + r.Size)
                return BinaryPrimitives.ReadUInt32BigEndian(r.Data.AsSpan((int)(address - r.Base), 4));
        return null;
    }
}

/// <summary>Heuristic classification of a candidate word: plausible float, vector cluster, integer, flag.</summary>
public static class DeltaHeuristics
{
    public sealed record Guess(string Kind, int Confidence, string Cluster, LiveType Type);

    /// <summary>A "physical" IEEE-754 single: finite, not denormal, magnitude in a game-plausible range (or exactly 0).</summary>
    public static bool LooksLikeFloat(uint raw)
    {
        if (raw == 0 || raw == 0x80000000) return true;
        uint exp = (raw >> 23) & 0xFF;
        if (exp == 0 || exp == 0xFF) return false;              // denormal / inf / nan
        float f = BitConverter.Int32BitsToSingle(unchecked((int)raw));
        float a = MathF.Abs(f);
        return a >= 1e-4f && a <= 1e7f;
    }

    public static bool LooksLikePointer(uint raw) => raw >= 0x40000000 && raw < 0xA0000000 && (raw & 3) == 0;

    /// <summary>Scores a candidate using its own value, its neighbours and how consistently it followed the action.</summary>
    public static Guess Classify(uint address, uint raw, Func<uint, uint?> wordAt, Func<uint, bool> isCandidate, int actionHits)
    {
        int score = 0;
        string kind, cluster = "";
        LiveType type;

        if (LooksLikeFloat(raw) && raw != 0)
        {
            kind = "Float";
            type = LiveType.Float;
            score += 40;

            // vector clustering: 3–4 plausible floats in a row that contain this word, with neighbours that also changed
            int bestRun = 1, runStart = 0;
            for (int s = -3; s <= 0; s++)
            {
                int len = 0, changed = 0;
                for (int k = 0; k < 4; k++)
                {
                    uint a = (uint)(address + (s + k) * 4);
                    if (wordAt(a) is not { } w || !LooksLikeFloat(w)) break;
                    len++;
                    if (isCandidate(a)) changed++;
                }
                if (len >= 3 && len > bestRun && -s < len && changed >= 2) { bestRun = len; runStart = s; }
            }
            if (bestRun >= 3)
            {
                score += 30;
                int index = -runStart;
                string[] axes = { "X", "Y", "Z", "W" };
                cluster = bestRun == 3 ? $"Vec3 · {axes[index]}" : $"Vec4 · {axes[index]}";
                // [x, y, z, |v|] – a 4th component equal to the length of the first three is a strong velocity signal
                if (bestRun == 4)
                {
                    var v = Enumerable.Range(0, 4).Select(k => BitConverter.Int32BitsToSingle(unchecked((int)(wordAt((uint)(address + (runStart + k) * 4)) ?? 0)))).ToArray();
                    float mag = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
                    if (mag > 0 && MathF.Abs(mag - MathF.Abs(v[3])) / mag < 0.02f) { score += 20; cluster = $"Vec3+|v| · {axes[index]}"; }
                }
            }
        }
        else if (LooksLikePointer(raw)) { kind = "Pointer"; type = LiveType.UInt32; score += 5; }
        else if (raw <= 1) { kind = "Flag"; type = LiveType.Int32; score += 15; }
        else if ((int)raw > -100000 && (int)raw < 1_000_000) { kind = "Integer"; type = LiveType.Int32; score += 30; }
        else { kind = "Raw"; type = LiveType.UInt32; }

        score += Math.Min(20, actionHits * 7);                  // followed the action in several rounds
        return new Guess(kind, Math.Min(100, score), cluster, type);
    }
}
