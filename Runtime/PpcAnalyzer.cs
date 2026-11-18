using System.Text;
using System.Text.RegularExpressions;
using MemoryEditor.Xex;

namespace MemoryEditor.Runtime;

public sealed class DiscoveredOffset
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public uint Address { get; set; }
    public string Source { get; set; } = "";       // Analysis / Signature / Static / Module
    public string Confidence { get; set; } = "";   // High / Medium / Low
    public string Notes { get; set; } = "";
    public string Type { get; set; } = "";         // Function / Global / String / Section / Import
    public override string ToString() => $"{Name} @ 0x{Address:X8}";
}

/// <summary>A loaded module image (memory layout: offset 0 == BaseAddress).</summary>
public sealed class ModuleImage
{
    public string Name { get; init; } = "";
    public uint BaseAddress { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public List<PeImage.Section> Sections { get; init; } = new();
    public uint EntryPoint { get; init; }
    public uint Timestamp { get; init; }
    public XexFile? Xex { get; init; }

    public uint End => BaseAddress + (uint)Data.Length;
    public bool Contains(uint va) => va >= BaseAddress && va < End;
    public uint Word(uint va)
    {
        long o = (long)va - BaseAddress;
        if (o < 0 || o + 4 > Data.Length) return 0;
        return (uint)(Data[o] << 24 | Data[o + 1] << 16 | Data[o + 2] << 8 | Data[o + 3]);
    }
    public IEnumerable<PeImage.Section> CodeSections => Sections.Where(s => s.IsCode);
    public IEnumerable<PeImage.Section> DataSections => Sections.Where(s => !s.IsCode);
    public bool IsCode(uint va) => Sections.Any(s => s.IsCode && va >= s.VirtualAddress && va < s.End);
    public PeImage.Section? SectionOf(uint va) => Sections.FirstOrDefault(s => va >= s.VirtualAddress && va < s.End);

    public static ModuleImage FromPe(string name, byte[] image, uint baseAddress, XexFile? xex = null)
    {
        var pe = PeImage.TryParse(image, baseAddress);
        var sections = pe?.Sections ?? new List<PeImage.Section>();
        if (sections.Count == 0)
            sections.Add(new PeImage.Section { Name = ".image", VirtualAddress = baseAddress, VirtualSize = (uint)image.Length, Characteristics = 0x60000020 });
        return new ModuleImage
        {
            Name = name, BaseAddress = baseAddress, Data = image, Sections = sections,
            EntryPoint = xex?.EntryPoint ?? pe?.EntryPoint ?? 0, Timestamp = pe?.TimeDateStamp ?? 0, Xex = xex
        };
    }
}

/// <summary>
/// Static PowerPC (Xenon) analysis that recovers offsets from a module image of any game.
/// </summary>
public sealed class PpcAnalyzer
{
    private readonly ModuleImage _m;
    private readonly Action<string, double>? _progress;
    private readonly CancellationToken _ct;

    public SortedSet<uint> FunctionStarts { get; } = new();
    public Dictionary<uint, string> Strings { get; } = new();
    /// <summary>address constant → instruction addresses that materialize it (lis/addi, lis/lwz ...)</summary>
    public Dictionary<uint, List<uint>> AddressRefs { get; } = new();
    public List<DiscoveredOffset> Results { get; } = new();

    private static readonly Regex IdentRx = new(@"^[A-Za-z_][A-Za-z0-9_]{2,63}$", RegexOptions.Compiled);
    private static readonly Regex FuncNameRx = new(@"^([A-Z][A-Za-z0-9]*_[A-Za-z0-9_]{2,}|[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+){1,})(?=$|[:(\s])", RegexOptions.Compiled);

    public PpcAnalyzer(ModuleImage module, Action<string, double>? progress = null, CancellationToken ct = default)
    {
        _m = module;
        _progress = progress;
        _ct = ct;
    }

    private void Report(string s, double p) => _progress?.Invoke(s, p);

    public List<DiscoveredOffset> Run()
    {
        Report("Mapping module layout…", 0.02);
        AddModuleLayout();
        Report("Finding functions…", 0.08);
        FindFunctions();
        _ct.ThrowIfCancellationRequested();
        Report("Extracting strings…", 0.25);
        FindStrings();
        _ct.ThrowIfCancellationRequested();
        Report("Tracing code references…", 0.40);
        TraceCode();
        _ct.ThrowIfCancellationRequested();
        Report("Scanning name tables…", 0.85);
        ScanNameTables();
        Report("Finding import thunks…", 0.93);
        FindImportThunks();
        Report($"Done – {Results.Count:N0} offsets", 1.0);
        return Results;
    }

    private void Add(string name, string cat, uint addr, string type, string conf, string notes, string source = "Analysis")
        => Results.Add(new DiscoveredOffset { Name = name, Category = cat, Address = addr, Type = type, Confidence = conf, Notes = notes, Source = source });

    // ───────────────────────────── layout ─────────────────────────────

    private void AddModuleLayout()
    {
        var mod = _m.Name;
        Add($"{mod} base", "Module", _m.BaseAddress, "Section", "High", $"Image size 0x{_m.Data.Length:X}", "Module");
        Add($"{mod} end", "Module", _m.End, "Section", "High", "First byte after the image", "Module");
        if (_m.EntryPoint != 0) Add($"{mod} entry point", "Module", _m.EntryPoint, "Function", "High", "Module entry point", "Module");
        foreach (var s in _m.Sections)
            Add($"section {s.Name}", "Module", s.VirtualAddress, "Section", "High", $"{s.Kind}, size 0x{s.VirtualSize:X} (ends 0x{s.End:X8})", "Module");
    }

    // ───────────────────────────── functions ─────────────────────────────

    private void FindFunctions()
    {
        foreach (var s in _m.CodeSections)
        {
            for (uint va = s.VirtualAddress; va + 4 <= s.End; va += 4)
            {
                uint w = _m.Word(va);
                if (w == 0x7D8802A6) // mflr r12 – standard Xbox 360 prologue
                {
                    // the prologue may be preceded by spills; walk back over std/stw rX,-n(r1)
                    uint start = va;
                    for (int k = 0; k < 4; k++)
                    {
                        uint prev = _m.Word(start - 4);
                        uint op = prev >> 26;
                        if ((op == 62 || op == 36) && ((prev >> 16) & 31) == 1 && (short)(prev & 0xFFFF) < 0) start -= 4;
                        else break;
                    }
                    FunctionStarts.Add(start);
                }
                else if ((w >> 26) == 18 && (w & 3) == 1) // bl
                {
                    uint target = BranchTarget(va, w);
                    if (_m.IsCode(target)) FunctionStarts.Add(target);
                }
            }
        }
        Add("function count", "Module", _m.BaseAddress, "Info", "High", $"{FunctionStarts.Count:N0} functions detected (bl targets + mflr r12 prologues)", "Module");
    }

    public static uint BranchTarget(uint pc, uint insn)
    {
        int li = (int)(insn & 0x03FFFFFC);
        if ((li & 0x02000000) != 0) li |= unchecked((int)0xFC000000);
        return (insn & 2) != 0 ? (uint)li : unchecked(pc + (uint)li);
    }

    public uint? FunctionContaining(uint va)
    {
        var view = FunctionStarts.GetViewBetween(0, va);
        return view.Count == 0 ? null : view.Max;
    }

    // ───────────────────────────── strings ─────────────────────────────

    private void FindStrings()
    {
        foreach (var s in _m.DataSections)
        {
            long start = (long)s.VirtualAddress - _m.BaseAddress;
            long end = Math.Min(start + s.VirtualSize, _m.Data.Length);
            var d = _m.Data;
            for (long i = start; i < end; i++)
            {
                if (i > start && d[i - 1] != 0) continue;
                long j = i;
                while (j < end && j - i < 512 && (d[j] is >= 0x20 and < 0x7F || d[j] is 0x09 or 0x0A)) j++;
                if (j < end && d[j] == 0 && j - i >= 4)
                {
                    Strings[(uint)(i + _m.BaseAddress)] = Encoding.ASCII.GetString(d, (int)i, (int)(j - i));
                    i = j;
                }
            }
        }
    }

    // ───────────────────────────── code tracing ─────────────────────────────

    private sealed class CallSite
    {
        public uint Target;
        public uint Pc;
        public uint? R3, R4, R5;
    }

    private void TraceCode()
    {
        var calls = new List<CallSite>();
        var namedGlobals = new List<(string Name, uint Global, uint Pc, uint Callee)>();
        var stringRefFuncs = new Dictionary<uint, HashSet<uint>>(); // string -> functions

        var known = new bool[32];
        var val = new uint[32];
        var tag = new int[32];          // index+1 of call whose return value the register holds
        int lastCallIdx = -1, sinceCall = 999;

        void ResetAll() { Array.Clear(known); Array.Clear(tag); }
        void Kill(int r) { known[r] = false; tag[r] = 0; }
        void Set(int r, uint v) { known[r] = true; val[r] = v; tag[r] = 0; }

        void Ref(uint addr, uint pc)
        {
            // module image, plus title heap / BSS globals (0x82000000-0x8FFFFFFF) so signatures can be learned for them
            if (!_m.Contains(addr) && !(addr >= 0x82000000 && addr < 0x90000000)) return;
            if (!AddressRefs.TryGetValue(addr, out var l)) AddressRefs[addr] = l = new List<uint>(2);
            if (l.Count < 64) l.Add(pc);
        }

        long total = _m.CodeSections.Sum(s => (long)s.VirtualSize), done = 0;
        foreach (var sec in _m.CodeSections)
        {
            ResetAll();
            for (uint pc = sec.VirtualAddress; pc + 4 <= sec.End; pc += 4)
            {
                if (((pc - sec.VirtualAddress) & 0x3FFFF) == 0)
                {
                    _ct.ThrowIfCancellationRequested();
                    Report("Tracing code references…", 0.40 + 0.45 * (done + (pc - sec.VirtualAddress)) / Math.Max(1, total));
                }
                if (FunctionStarts.Contains(pc)) ResetAll();
                sinceCall++;

                uint w = _m.Word(pc);
                uint op = w >> 26;
                int rD = (int)((w >> 21) & 31), rA = (int)((w >> 16) & 31);
                uint imm = w & 0xFFFF;
                int simm = (short)imm;

                switch (op)
                {
                    case 15: // addis / lis
                        if (rA == 0) Set(rD, imm << 16);
                        else if (known[rA]) Set(rD, val[rA] + (imm << 16));
                        else Kill(rD);
                        break;
                    case 14: // addi / li
                        if (rA != 0 && known[rA])
                        {
                            uint v = unchecked(val[rA] + (uint)simm);
                            Set(rD, v);
                            Ref(v, pc);
                        }
                        else if (rA == 0) { Set(rD, unchecked((uint)simm)); known[rD] = false; } // li: small constant, not an address
                        else Kill(rD);
                        break;
                    case 24: // ori rA, rS, imm
                        if (known[rD]) { uint v = val[rD] | imm; Set(rA, v); if (imm != 0) Ref(v, pc); }
                        else Kill(rA);
                        break;
                    case 25: case 26: case 27: case 28: case 29: case 20: case 21: case 23: case 30:
                        Kill(rA);
                        break;
                    case 7: case 8: case 12: case 13:
                        Kill(rD);
                        break;
                    case 32: case 33: case 34: case 35: case 40: case 41: case 42: case 43: case 58: // GPR loads
                    {
                        int d = op == 58 ? (short)(imm & ~3u) : simm;
                        if (known[rA] && rA != 1) Ref(unchecked(val[rA] + (uint)d), pc);
                        if (op is 33 or 35 or 41 or 43 || (op == 58 && (w & 3) == 1)) Kill(rA);
                        Kill(rD);
                        break;
                    }
                    case 48: case 49: case 50: case 51: // FP loads
                        if (known[rA] && rA != 1) Ref(unchecked(val[rA] + (uint)simm), pc);
                        if (op is 49 or 51) Kill(rA);
                        break;
                    case 36: case 37: case 38: case 39: case 44: case 45: case 52: case 53: case 54: case 55: case 62: // stores
                    {
                        int d = op == 62 ? (short)(imm & ~3u) : simm;
                        if (known[rA] && rA != 1)
                        {
                            uint g = unchecked(val[rA] + (uint)d);
                            Ref(g, pc);
                            // stw rS(holding a registrar's return value), global
                            if (op == 36 && tag[rD] > 0 && sinceCall < 16)
                            {
                                var c = calls[tag[rD] - 1];
                                if (c.R3 is uint s && Strings.TryGetValue(s, out var str) && IdentRx.IsMatch(str))
                                    namedGlobals.Add((str, g, pc, c.Target));
                            }
                        }
                        if (op is 37 or 39 or 45 or 53 or 55 || (op == 62 && (w & 3) == 1)) Kill(rA);
                        break;
                    }
                    case 31:
                    {
                        uint xo = (w >> 1) & 0x3FF;
                        int rB = (int)((w >> 11) & 31);
                        if (xo == 444 && rD == rB) // mr rA, rS
                        {
                            known[rA] = known[rD]; val[rA] = val[rD]; tag[rA] = tag[rD];
                        }
                        else if (XoWritesRa.Contains(xo)) Kill(rA);
                        else if (!XoNoGprWrite.Contains(xo)) Kill(rD);
                        break;
                    }
                    case 18:
                        if ((w & 1) != 0) // bl
                        {
                            uint target = BranchTarget(pc, w);
                            var cs = new CallSite
                            {
                                Target = target, Pc = pc,
                                R3 = known[3] ? val[3] : null, R4 = known[4] ? val[4] : null, R5 = known[5] ? val[5] : null
                            };
                            calls.Add(cs);
                            lastCallIdx = calls.Count - 1;
                            for (int r = 0; r <= 12; r++) if (r != 1 && r != 2) Kill(r);
                            tag[3] = lastCallIdx + 1;
                            sinceCall = 0;
                        }
                        else ResetAll();
                        break;
                    case 19:
                    {
                        uint xo = (w >> 1) & 0x3FF;
                        bool always = ((w >> 21) & 0x14) == 0x14;
                        if (xo == 16 || xo == 528)
                        {
                            if ((w & 1) != 0) { for (int r = 0; r <= 12; r++) if (r != 1 && r != 2) Kill(r); }
                            else if (always) ResetAll();
                        }
                        break;
                    }
                }
            }
            done += sec.VirtualSize;
        }

        // ── strings referenced by code ──
        foreach (var (addr, text) in Strings)
        {
            if (!AddressRefs.TryGetValue(addr, out var refs)) continue;
            var fns = new HashSet<uint>();
            foreach (var r in refs) if (FunctionContaining(r) is uint f) fns.Add(f);
            stringRefFuncs[addr] = fns;
            var shown = text.Replace("\n", "\\n").Replace("\t", "\\t");
            if (shown.Length > 60) shown = shown[..60] + "…";
            Add($"\"{shown}\"", "String References", addr, "String", "High",
                $"{refs.Count} code ref(s){(fns.Count > 0 ? $", used by {string.Join(", ", fns.Take(4).Select(f => $"sub_{f:X8}"))}" : "")}");
        }

        // ── functions named by the identifier their strings carry (assert / log messages) ──
        var nameToFuncs = new Dictionary<string, HashSet<uint>>();
        foreach (var (addr, fns) in stringRefFuncs)
        {
            var m = FuncNameRx.Match(Strings[addr]);
            if (!m.Success || fns.Count != 1) continue;
            var name = m.Groups[1].Value;
            if (!nameToFuncs.TryGetValue(name, out var set)) nameToFuncs[name] = set = new HashSet<uint>();
            set.Add(fns.First());
        }
        foreach (var (name, fns) in nameToFuncs)
            if (fns.Count == 1)
                Add(name, "Named Functions", fns.First(), "Function", "Medium", "Named from a debug/assert string referenced only inside this function");

        // ── registration calls: f("name", handler) → handler is named ──
        var registeredFns = new HashSet<uint>();
        foreach (var c in calls)
        {
            if (c.R3 is not uint s || !Strings.TryGetValue(s, out var str) || !IdentRx.IsMatch(str)) continue;
            foreach (var cand in new[] { c.R4, c.R5 })
            {
                if (cand is uint h && FunctionStarts.Contains(h) && registeredFns.Add(h))
                {
                    Add(str, "Named Functions (registered)", h, "Function", "High",
                        $"Registered by sub_{c.Target:X8}(\"{str}\", handler) at 0x{c.Pc:X8}");
                    break;
                }
            }
        }

        // ── registrar functions: called with many distinct string literals ──
        var registrars = calls.Where(c => c.R3 is uint s && Strings.TryGetValue(s, out var t) && IdentRx.IsMatch(t))
            .GroupBy(c => c.Target)
            .Select(g => (Target: g.Key, Count: g.Select(c => c.R3).Distinct().Count(), Sample: Strings[g.First().R3!.Value]))
            .Where(g => g.Count >= 20)
            .OrderByDescending(g => g.Count)
            .Take(40)
            .ToList();
        var registrarNames = new Dictionary<uint, string>();
        int idx = 1;
        foreach (var r in registrars)
        {
            var nm = $"registrar_{idx++}_{r.Target:X8}";
            registrarNames[r.Target] = nm;
            Add(nm, "Key Functions", r.Target, "Function", "High",
                $"Called with {r.Count:N0} distinct name strings (e.g. \"{r.Sample}\") – typically Dvar_Register*/Cmd_AddCommand style APIs");
        }

        // ── globals that receive a registrar's return value (dvar_t* etc.) ──
        var seenGlobals = new HashSet<uint>();
        foreach (var g in namedGlobals)
        {
            if (!seenGlobals.Add(g.Global)) continue;
            var via = registrarNames.TryGetValue(g.Callee, out var rn) ? rn : $"sub_{g.Callee:X8}";
            Add(g.Name, "Named Globals", g.Global, "Global", registrarNames.ContainsKey(g.Callee) ? "High" : "Medium",
                $"Pointer stored from {via}(\"{g.Name}\") at 0x{g.Pc:X8} – read the pointer to reach the object (e.g. dvar value)");
        }
    }

    private static readonly HashSet<uint> XoWritesRa = new()
    {
        28, 60, 124, 284, 316, 412, 444, 476, 24, 536, 792, 824, 26, 954, 922, 986, 27, 539, 794, 826, 827, 58
    };

    private static readonly HashSet<uint> XoNoGprWrite = new()
    {
        0, 32, 4, 68, 151, 183, 215, 247, 407, 439, 149, 181, 663, 695, 727, 759, 983, 150, 214, 918, 662, 278, 246, 1014, 54, 86, 982,
        598, 854, 467, 144, 146, 178, 210, 242, 306, 370, 434, 498, 274, 566, 103, 231, 487, 359, 135, 167, 199, 455, 711, 742, 966
    };

    // ───────────────────────────── data tables ─────────────────────────────

    private void ScanNameTables()
    {
        var named = new HashSet<uint>(Results.Where(r => r.Type == "Function" && r.Category.StartsWith("Named")).Select(r => r.Address));
        int found = 0;
        foreach (var s in _m.DataSections)
        {
            for (uint va = s.VirtualAddress; va + 8 <= s.End; va += 4)
            {
                uint p = _m.Word(va);
                if (!Strings.TryGetValue(p, out var str) || !IdentRx.IsMatch(str)) continue;
                uint f = _m.Word(va + 4);
                if (!FunctionStarts.Contains(f) || named.Contains(f)) continue;
                named.Add(f);
                Add(str, "Named Functions (tables)", f, "Function", "High", $"From a {{name, function}} table entry at 0x{va:X8}");
                if (++found > 20000) return;
            }
        }
    }

    // ───────────────────────────── imports ─────────────────────────────

    private void FindImportThunks()
    {
        int n = 0;
        // offline image: XEX import records give library + ordinal for every thunk
        if (_m.Xex != null)
        {
            foreach (var imp in _m.Xex.ResolveImports(_m.Data))
            {
                if (!imp.IsThunk) continue;
                Add($"{imp.Library}!ord_{imp.Ordinal}", "Imports", imp.RecordAddress, "Import", "High", $"Import thunk for {imp.Library} ordinal {imp.Ordinal} (0x{imp.Ordinal:X})");
                n++;
            }
            if (n > 0) return;
        }
        // live image: loader patched thunks into lis r11 / lwz r11 / mtctr r11 / bctr
        foreach (var s in _m.CodeSections)
        {
            for (uint va = s.VirtualAddress; va + 16 <= s.End; va += 4)
            {
                uint a = _m.Word(va);
                if ((a & 0xFFFF0000) != 0x3D600000) continue;
                uint b = _m.Word(va + 4);
                if ((b & 0xFFFF0000) != 0x816B0000 || _m.Word(va + 8) != 0x7D6903A6 || _m.Word(va + 12) != 0x4E800420) continue;
                uint slot = unchecked(((a & 0xFFFF) << 16) + (uint)(short)(b & 0xFFFF));
                uint target = _m.Word(slot);
                Add($"import_{va:X8}", "Imports", va, "Import", "Medium", $"Import thunk → IAT slot 0x{slot:X8}" + (target != 0 ? $" (resolved to 0x{target:X8})" : ""));
                n++;
            }
        }
    }
}
