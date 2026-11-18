using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemoryEditor.Runtime;

/// <summary>Byte pattern with nibble wildcards, e.g. "7D 88 02 A6 4? ?? ?? ?1".</summary>
public sealed class BytePattern
{
    public byte[] Value { get; }
    public byte[] Mask { get; }
    public int Length => Value.Length;

    public BytePattern(byte[] value, byte[] mask) { Value = value; Mask = mask; }

    public static BytePattern Parse(string pattern)
    {
        var tokens = pattern.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        // allow compact "7D8802A6????????"
        if (tokens.Length == 1 && tokens[0].Length > 2)
            tokens = Enumerable.Range(0, tokens[0].Length / 2).Select(i => tokens[0].Substring(i * 2, 2)).ToArray();
        var v = new byte[tokens.Length];
        var m = new byte[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i].Length == 1 ? "?" + tokens[i] : tokens[i];
            if (t == "?" || t == "??") { v[i] = 0; m[i] = 0; continue; }
            if (t.Length != 2) throw new FormatException($"Bad pattern token '{tokens[i]}'");
            int hi = t[0] == '?' ? -1 : int.Parse(t[0].ToString(), NumberStyles.HexNumber);
            int lo = t[1] == '?' ? -1 : int.Parse(t[1].ToString(), NumberStyles.HexNumber);
            v[i] = (byte)(((hi < 0 ? 0 : hi) << 4) | (lo < 0 ? 0 : lo));
            m[i] = (byte)((hi < 0 ? 0 : 0xF0) | (lo < 0 ? 0 : 0x0F));
        }
        return new BytePattern(v, m);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Value.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            var hex = Value[i].ToString("X2");
            sb.Append((Mask[i] & 0xF0) != 0 ? hex[0] : '?');
            sb.Append((Mask[i] & 0x0F) != 0 ? hex[1] : '?');
        }
        return sb.ToString();
    }

    public bool MatchAt(byte[] data, int offset)
    {
        if (offset < 0 || offset + Value.Length > data.Length) return false;
        for (int i = 0; i < Value.Length; i++)
            if ((data[offset + i] & Mask[i]) != Value[i]) return false;
        return true;
    }

    /// <summary>Finds matches (4-byte aligned when <paramref name="aligned"/>), stopping after <paramref name="max"/>.</summary>
    public List<int> FindAll(byte[] data, int start, int end, int max = 16, bool aligned = true)
    {
        var res = new List<int>();
        int step = aligned ? 4 : 1;
        // anchor on the first fully specified byte for speed
        int anchor = Array.FindIndex(Mask, b => b == 0xFF);
        end = Math.Min(end, data.Length - Value.Length);
        for (int i = start; i <= end; i += step)
        {
            if (anchor >= 0 && data[i + anchor] != Value[anchor]) continue;
            if (MatchAt(data, i))
            {
                res.Add(i);
                if (res.Count >= max) break;
            }
        }
        return res;
    }
}

public sealed class SignatureDef
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Signatures";
    public string Pattern { get; set; } = "";
    /// <summary>Byte offset from the match start to the instruction/address of interest.</summary>
    public int Offset { get; set; }
    /// <summary>none | branch (follow b/bl at Offset) | lis_pair (address built by lis at Offset + the following addi/load)</summary>
    public string Resolve { get; set; } = "none";
    /// <summary>Added after resolving (e.g. field offset).</summary>
    public int Adjust { get; set; }
    public string Notes { get; set; } = "";
    /// <summary>Patch bytes carried into ported offset files ("| Bytes: 60 00 00 00"). Optional.</summary>
    public string? Bytes { get; set; }
    /// <summary>Field offset written as "+ 0x…" after the resolved base (pointer / struct entries). Optional.</summary>
    public uint? Plus { get; set; }
}

/// <summary>A non-address value from an offsets file (struct sizes, ids, …) carried over to other builds as-is.</summary>
public sealed class ConstantDef
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class OffsetProfile
{
    public string Game { get; set; } = "";
    public string Module { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public List<SignatureDef> Signatures { get; set; } = new();
    public List<ConstantDef> Constants { get; set; } = new();
    /// <summary>Build the signatures were learned from (e.g. "TU18").</summary>
    public string LearnedFrom { get; set; } = "";

    [JsonIgnore] public string FilePath { get; set; } = "";

    public bool Matches(string module, uint titleId) =>
        (TitleId.Length > 0 && titleId != 0 && uint.TryParse(TitleId, NumberStyles.HexNumber, null, out var t) && t == titleId) ||
        (Module.Length > 0 && module.Equals(Module, StringComparison.OrdinalIgnoreCase));
}

public static class SignatureEngine
{
    /// <summary>Resolves a signature in a module. Returns the address or null with a reason.</summary>
    public static (uint? Address, string Info) Apply(ModuleImage m, SignatureDef sig)
    {
        BytePattern pat;
        try { pat = BytePattern.Parse(sig.Pattern); }
        catch (Exception ex) { return (null, $"bad pattern: {ex.Message}"); }

        var hits = new List<int>();
        foreach (var s in m.CodeSections)
        {
            int st = (int)(s.VirtualAddress - m.BaseAddress);
            hits.AddRange(pat.FindAll(m.Data, st, st + (int)s.VirtualSize, 4));
            if (hits.Count > 3) break;
        }
        if (hits.Count == 0) return (null, "pattern not found");
        string info = hits.Count == 1 ? "unique match" : $"{hits.Count} matches – using first";
        uint at = (uint)(m.BaseAddress + hits[0] + sig.Offset);

        uint result;
        switch (sig.Resolve.ToLowerInvariant())
        {
            case "branch":
            {
                uint w = m.Word(at);
                if ((w >> 26) != 18) return (null, "resolve=branch but no b/bl at offset");
                result = PpcAnalyzer.BranchTarget(at, w);
                break;
            }
            case "lis_pair":
            {
                uint w = m.Word(at);
                if ((w >> 26) != 15) return (null, "resolve=lis_pair but no lis at offset");
                int reg = (int)((w >> 21) & 31);
                uint hi = (w & 0xFFFF) << 16;
                uint? found = null;
                for (int k = 1; k <= 8 && found == null; k++)
                {
                    uint n = m.Word(at + (uint)k * 4);
                    uint op = n >> 26;
                    int ra = (int)((n >> 16) & 31);
                    if (ra != reg) continue;
                    if (op is 14 or 32 or 34 or 36 or 38 or 40 or 42 or 44 or 48 or 50 or 52 or 54)
                        found = unchecked(hi + (uint)(short)(n & 0xFFFF));
                    else if (op == 24) found = hi | (n & 0xFFFF);
                }
                if (found == null) return (null, "lis without matching low half");
                result = found.Value;
                break;
            }
            default:
                result = at;
                break;
        }
        return (unchecked(result + (uint)sig.Adjust), info);
    }

    /// <summary>
    /// Creates a relocation-tolerant signature for an address. Code addresses are signed directly; data addresses
    /// are signed through the first instruction pair that references them (resolve = lis_pair).
    /// </summary>
    public static SignatureDef? Generate(ModuleImage m, PpcAnalyzer? analysis, string name, uint address, string category = "Signatures")
    {
        uint anchor;
        int offset = 0;
        string resolve = "none";
        int adjust = 0;

        if (m.IsCode(address))
        {
            anchor = address;
        }
        else
        {
            if (analysis == null || !analysis.AddressRefs.TryGetValue(address, out var refs) || refs.Count == 0)
            {
                // the exact address may be a field: look for a reference to a nearby base
                if (analysis == null) return null;
                var near = analysis.AddressRefs.Keys.Where(k => k <= address && address - k < 0x400).DefaultIfEmpty(0u).Max();
                if (near == 0) return null;
                adjust = (int)(address - near);
                refs = analysis.AddressRefs[near];
            }
            // find the lis that starts the pair (walk back from the low-half instruction)
            uint lowPc = refs[0];
            uint lisPc = 0;
            int reg = (int)((m.Word(lowPc) >> 16) & 31);
            for (int k = 1; k <= 8; k++)
            {
                uint w = m.Word(lowPc - (uint)k * 4);
                if ((w >> 26) == 15 && ((w >> 21) & 31) == reg && ((w >> 16) & 31) == 0) { lisPc = lowPc - (uint)k * 4; break; }
            }
            if (lisPc == 0) return null;
            anchor = lisPc >= m.BaseAddress + 16 ? lisPc - 16 : lisPc;
            offset = (int)(lisPc - anchor);
            resolve = "lis_pair";
        }

        // grow until unique
        for (int words = 6; words <= 40; words += 2)
        {
            var pat = BuildMasked(m, anchor, words);
            if (pat == null) return null;
            int hits = 0;
            foreach (var s in m.CodeSections)
            {
                int st = (int)(s.VirtualAddress - m.BaseAddress);
                hits += pat.FindAll(m.Data, st, st + (int)s.VirtualSize, 2).Count;
                if (hits > 1) break;
            }
            if (hits == 1)
                return new SignatureDef { Name = name, Category = category, Pattern = pat.ToString(), Offset = offset, Resolve = resolve, Adjust = adjust, Notes = $"generated from 0x{address:X8}" };
        }
        return null;
    }

    private static BytePattern? BuildMasked(ModuleImage m, uint start, int words)
    {
        if (!m.Contains(start) || !m.Contains(start + (uint)words * 4 - 1)) return null;
        var v = new byte[words * 4];
        var k = new byte[words * 4];
        var lisRegs = new HashSet<int>();
        for (int i = 0; i < words; i++)
        {
            uint w = m.Word(start + (uint)i * 4);
            uint mask = 0xFFFFFFFF;
            uint op = w >> 26;
            int rD = (int)((w >> 21) & 31), rA = (int)((w >> 16) & 31);
            switch (op)
            {
                case 18: mask = 0xFC000003; break;                    // b / bl: target changes between builds
                case 16: mask = 0xFFFF0003; break;                    // bc: keep condition, drop displacement
                case 15: mask = 0xFFFF0000; if (rA == 0) lisRegs.Add(rD); break;
                case 14: case 24:
                case 32: case 33: case 34: case 35: case 36: case 37: case 38: case 39:
                case 40: case 41: case 42: case 43: case 44: case 45:
                case 48: case 49: case 50: case 51: case 52: case 53: case 54: case 55:
                case 58: case 62:
                {
                    int baseReg = op == 24 ? rD : rA;
                    if (lisRegs.Contains(baseReg) || baseReg == 2 || baseReg == 13) mask = 0xFFFF0000;
                    break;
                }
            }
            for (int b = 0; b < 4; b++)
            {
                k[i * 4 + b] = (byte)(mask >> (24 - b * 8));
                v[i * 4 + b] = (byte)((w & mask) >> (24 - b * 8));
            }
            // nibble granularity in the text format: round partial nibbles down to wildcards
            for (int b = 0; b < 4; b++)
            {
                byte mk = k[i * 4 + b];
                byte nm = (byte)(((mk & 0xF0) == 0xF0 ? 0xF0 : 0) | ((mk & 0x0F) == 0x0F ? 0x0F : 0));
                k[i * 4 + b] = nm;
                v[i * 4 + b] &= nm;
            }
        }
        return new BytePattern(v, k);
    }
}

public static class ProfileStore
{
    public static string Directory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Games");
    public static string CacheDirectory => Path.Combine(Directory, "cache");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static List<OffsetProfile> LoadAll()
    {
        var list = new List<OffsetProfile>();
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var f in System.IO.Directory.GetFiles(Directory, "*.json"))
        {
            try
            {
                var p = JsonSerializer.Deserialize<OffsetProfile>(File.ReadAllText(f), Json);
                if (p == null) continue;
                p.FilePath = f;
                list.Add(p);
            }
            catch { }
        }
        return list;
    }

    public static void Save(OffsetProfile p)
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (string.IsNullOrEmpty(p.FilePath))
        {
            var safe = string.Concat((p.Game.Length > 0 ? p.Game : p.Module).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            p.FilePath = Path.Combine(Directory, safe + ".json");
        }
        File.WriteAllText(p.FilePath, JsonSerializer.Serialize(p, Json));
    }

    public static string CacheKey(string module, uint timestamp) =>
        $"{string.Concat(module.Select(c => char.IsLetterOrDigit(c) ? c : '_'))}_{timestamp:X8}";

    public static void SaveCache(string key, List<DiscoveredOffset> offsets)
    {
        System.IO.Directory.CreateDirectory(CacheDirectory);
        File.WriteAllText(Path.Combine(CacheDirectory, key + ".json"), JsonSerializer.Serialize(offsets, Json));
    }

    public static List<DiscoveredOffset>? LoadCache(string key)
    {
        var f = Path.Combine(CacheDirectory, key + ".json");
        if (!File.Exists(f)) return null;
        try { return JsonSerializer.Deserialize<List<DiscoveredOffset>>(File.ReadAllText(f), Json); }
        catch { return null; }
    }
}
