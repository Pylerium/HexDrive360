using MemoryEditor.Runtime;
using MemoryEditor.Xex;

namespace MemoryEditor.Library;

/// <summary>Everything produced by one runtime pull (or offline analysis) of a game build.</summary>
public sealed class PullResult
{
    public GameInfo Game { get; init; } = null!;
    public ModuleImage Module { get; init; } = null!;
    public PpcAnalyzer Analysis { get; init; } = null!;
    /// <summary>Raw analysis + signature results (fed to the Runtime Offsets page).</summary>
    public List<DiscoveredOffset> Discovered { get; init; } = new();
    /// <summary>Entries written to the BO2-format offsets file.</summary>
    public List<OffsetTextFormat.Entry> Entries { get; init; } = new();
    public List<string> CategoryOrder { get; init; } = new();
    public int SignaturesResolved { get; set; }
    public int SignaturesTotal { get; set; }
    public int ConstantsCarried { get; set; }
    public string PortedFrom { get; set; } = "";
    public TimeSpan Elapsed { get; set; }
}

/// <summary>Result of checking an offsets file against the module of the build that is actually running.</summary>
public sealed record Verification(int Checked, int Matched, int BytesChecked, int BytesMatched, bool Passed)
{
    public string Summary => BytesChecked > 0
        ? $"{BytesMatched}/{BytesChecked} patch sites match the running code, {Matched}/{Checked} code addresses plausible"
        : $"{Matched}/{Checked} code addresses plausible";
}

/// <summary>
/// Game-agnostic offset puller. Works for any title: it identifies the running executable, reads its image from
/// console memory, analyses the PowerPC code (functions, named functions, globals, imports, layout), resolves every
/// learned signature for the game (porting curated offsets to the running title update) and produces the result in
/// the same text format as <c>bo2_tu18_offsets.txt</c>.
/// </summary>
public static class OffsetPuller
{
    // category names used in generated files
    public const string CatKeyFunctions = "Key Functions";
    public const string CatFunctions = "Functions";
    public const string CatGlobals = "Globals";
    public const string CatImports = "Imports";
    public const string CatModule = "Module Layout";
    public const string CatLoadedModules = "Loaded Modules";
    public const string CatStrings = "String References";
    public const string CatConstants = "Constants (carried over)";

    /// <summary>
    /// Detects the running title and enriches it from the XEX header on the console's disk (title id, version,
    /// media id) so identification works without the JRPC2 plugin.
    /// </summary>
    public static async Task<GameInfo?> IdentifyAsync(XBDMClient c, Action<string>? log = null)
    {
        var g = await GameDetector.DetectAsync(c, log);
        if (g == null) return null;
        if (g.Path.Length > 0)
        {
            try
            {
                var head = await c.GetFileAsync(g.Path, 0, 0x10000);
                if (head != null && XexFile.IsXex(head))
                {
                    var x = XexFile.Parse(head);
                    if (g.TitleId == 0) g.TitleId = x.TitleId;
                    g.Version = x.Version;
                    g.MediaId = x.MediaId;
                }
            }
            catch (Exception ex) when (ex is not IOException)
            {
                log?.Invoke($"⚠️ Could not read the XEX header of {g.ModuleName}: {ex.Message}");
            }
        }
        if (g.TitleId != 0 && GameDetector.KnownTitles.TryGetValue(g.TitleId, out var known)) g.DisplayName = known;
        else if (g.DisplayName.Length == 0 || g.DisplayName == g.ModuleName) g.DisplayName = FriendlyName(g);
        return g;
    }

    /// <summary>Best effort readable name: folder of the executable ("Hdd:\Games\Black Ops 2\default_mp.xex" → "Black Ops 2").</summary>
    public static string FriendlyName(GameInfo g)
    {
        var p = XBDMClient.ToXbdmPath(g.Path);
        var parts = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var folder = parts[^2];
            if (!folder.EndsWith(":") && !folder.Equals("Games", StringComparison.OrdinalIgnoreCase) && !System.Text.RegularExpressions.Regex.IsMatch(folder, "^[0-9A-F]{8,16}$"))
                return folder;
        }
        return g.ModuleName;
    }

    /// <summary>Dumps the running module from console memory and analyses it.</summary>
    public static async Task<PullResult> PullAsync(XBDMClient c, GameInfo g, LibrarySettings o, Action<string, double> progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var module = await GameDetector.DumpModuleAsync(c, g, (s, p) => progress(s, p / 0.85 * 0.5), ct);
        var r = await AnalyzeAsync(module, g, o, progress, 0.5, ct);
        r.Elapsed = sw.Elapsed;
        return r;
    }

    /// <summary>Analyses a module image (from the console or a file) and builds the offsets file entries.</summary>
    public static async Task<PullResult> AnalyzeAsync(ModuleImage module, GameInfo g, LibrarySettings o, Action<string, double> progress, double start, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var analyzer = new PpcAnalyzer(module, (s, p) => progress(s, start + p * (0.9 - start)), ct);
        var discovered = await Task.Run(analyzer.Run, ct);

        progress("Resolving learned signatures…", 0.92);
        var profiles = ProfileStore.LoadAll().Where(p => p.Matches(g.ModuleName, g.TitleId)).ToList();

        return await Task.Run(() =>
        {
            var result = new PullResult { Game = g, Module = module, Analysis = analyzer, Discovered = discovered };
            var entries = result.Entries;
            var order = result.CategoryOrder;

            // 1) curated offsets ported through signatures (keeps the original names, categories, bytes and +offsets)
            foreach (var p in profiles)
            {
                if (p.LearnedFrom.Length > 0) result.PortedFrom = p.LearnedFrom;
                foreach (var sig in p.Signatures)
                {
                    ct.ThrowIfCancellationRequested();
                    result.SignaturesTotal++;
                    var (addr, info) = SignatureEngine.Apply(module, sig);
                    discovered.Add(new DiscoveredOffset
                    {
                        Name = sig.Name, Category = "Signatures · " + sig.Category, Address = addr ?? 0, Source = "Signature",
                        Confidence = addr == null ? "Failed" : info == "unique match" ? "High" : "Medium", Type = "Signature",
                        Notes = addr == null ? $"✕ {info}" : info,
                    });
                    if (addr == null) continue;
                    result.SignaturesResolved++;
                    var cat = OffsetTextFormat.CleanCategory(sig.Category);
                    if (!order.Contains(cat)) order.Add(cat);
                    entries.Add(new OffsetTextFormat.Entry(cat, sig.Name, addr.Value, sig.Plus, sig.Bytes));
                }
                foreach (var k in p.Constants)
                {
                    var cat = OffsetTextFormat.CleanCategory(k.Category);
                    if (!order.Contains(cat)) order.Add(cat);
                    entries.Add(new OffsetTextFormat.Entry(cat, k.Name, 0, RawValue: k.Value));
                    result.ConstantsCarried++;
                }
            }

            // 2) everything the analysis found
            static string Bucket(string c) => c switch
            {
                "Key Functions" => CatKeyFunctions,
                "Named Globals" => CatGlobals,
                "Imports" => CatImports,
                "Module" => CatModule,
                "String References" => CatStrings,
                _ when c.StartsWith("Named Functions") => CatFunctions,
                _ => c,
            };
            foreach (var d in discovered)
            {
                if (d.Source == "Signature" || d.Address == 0) continue;
                var cat = Bucket(d.Category);
                if (cat == CatStrings && !o.IncludeStringReferences) continue;
                if (cat == CatImports && !o.IncludeImports) continue;
                if (cat == CatModule && !o.IncludeModuleLayout) continue;
                if (d.Type == "Info") continue;
                entries.Add(new OffsetTextFormat.Entry(cat, d.Name, d.Address));
            }

            // 3) every module loaded in the title process (bases change between boots for DLLs/plugins)
            if (o.IncludeLoadedModules)
                foreach (var m in g.AllModules)
                    entries.Add(new OffsetTextFormat.Entry(CatLoadedModules, m.Name, m.Base));

            order.AddRange(new[] { CatKeyFunctions, CatFunctions, CatGlobals, CatConstants, CatImports, CatModule, CatLoadedModules, CatStrings }.Where(c => !order.Contains(c)));
            result.Elapsed = sw.Elapsed;
            progress($"Done – {entries.Count:N0} offsets", 1.0);
            return result;
        }, ct);
    }

    public static Dictionary<string, string> Header(GameRecord rec, GameBuild build, int count) => new()
    {
        ["Game"] = rec.Name,
        ["Build"] = build.Label,
        ["Title ID"] = rec.TitleIdText,
        ["Module"] = build.ModuleName,
        ["Base"] = $"0x{build.ModuleBase:X8}",
        ["Timestamp"] = $"0x{build.Timestamp:X8}" + (build.Timestamp != 0 ? $" ({DateTimeOffset.FromUnixTimeSeconds(build.Timestamp):yyyy-MM-dd})" : ""),
        ["Checksum"] = $"0x{build.Checksum:X8}",
        ["Version"] = build.Version,
        ["Source"] = build.Source,
        ["Generated"] = $"{DateTime.Now:yyyy-MM-dd HH:mm} by Memory Editor Pro · {count:N0} offsets",
    };

    // ───────────────────────────── verification ─────────────────────────────

    /// <summary>
    /// Checks whether an offsets file belongs to the module that is loaded: patch sites ("| Bytes:") must currently
    /// hold either the patch or its On/Off counterpart, and plain code addresses should look like real code.
    /// </summary>
    public static Verification Verify(ModuleImage m, PpcAnalyzer? a, IReadOnlyList<OffsetManager.OffsetEntry> entries, bool versionMatches = false)
    {
        int chk = 0, ok = 0, bchk = 0, bok = 0;
        var bytesAt = entries.Where(e => !e.IsPointerBased && OffsetTextFormat.ParseBytes(e.ByteValues) != null)
                             .GroupBy(e => e.BaseAddress)
                             .ToDictionary(gr => gr.Key, gr => gr.Select(e => OffsetTextFormat.ParseBytes(e.ByteValues)!).ToList());

        foreach (var (addr, variants) in bytesAt)
        {
            if (!m.Contains(addr)) continue;
            int len = variants.Max(v => v.Length);
            if (!m.Contains(addr + (uint)len - 1)) continue;
            int off = (int)(addr - m.BaseAddress);
            bool match = variants.Any(v => m.Data.AsSpan(off, v.Length).SequenceEqual(v));
            // On/Off pairs must match one of their states. A single patch site normally still holds the ORIGINAL
            // code (the patch is not applied), so it only counts as evidence when it matches – never against.
            if (variants.Count >= 2) { bchk++; if (match) bok++; }
            else if (match) { bchk++; bok++; }
        }

        foreach (var e in entries)
        {
            if (e.IsPointerBased || e.ByteValues != null || !m.Contains(e.BaseAddress) || !m.IsCode(e.BaseAddress)) continue;
            chk++;
            uint addr = e.BaseAddress & ~3u;
            uint w = m.Word(addr);
            bool funcStart = a?.FunctionStarts.Contains(addr) == true || (addr >= m.BaseAddress + 4 && m.Word(addr - 4) == 0x4E800020) || w == 0x7D8802A6;
            bool insideFunction = w != 0 && a?.FunctionContaining(addr) != null;
            if (funcStart || insideFunction) ok++;
        }

        bool passed = bchk >= 3 ? bok >= bchk * 0.75 : chk >= 3 && ok >= chk * 0.6;
        // the file is labelled with the title update the console reports (e.g. "TU18" ↔ v0.0.18.2): accept on
        // plausible code alone, as long as the patch sites are not clearly wrong
        if (!passed && versionMatches) passed = (chk == 0 || ok >= chk * 0.8) && (bchk == 0 || bok >= bchk * 0.5);
        return new Verification(chk, ok, bchk, bok, passed);
    }

    /// <summary>"TU18" / "TU 18" label ↔ executable version x.x.18.x (the build field carries the title update number).</summary>
    public static bool LabelMatchesVersion(string label, string version)
    {
        var tu = System.Text.RegularExpressions.Regex.Match(label, @"\bTU\s*#?(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var parts = version.Split('.');
        return tu.Success && parts.Length >= 3 && int.TryParse(parts[2], out var b) && b == int.Parse(tu.Groups[1].Value);
    }

    // ───────────────────────────── learning ─────────────────────────────

    /// <summary>
    /// Learns relocation-tolerant signatures for every address in a verified offsets file, so the same offsets can be
    /// found automatically in any other build (title update) of the game. Values that are not addresses (sizes,
    /// indices) are kept as constants.
    /// </summary>
    public static (OffsetProfile Profile, int Learned, int Failed, int Constants) Learn(
        ModuleImage m, PpcAnalyzer a, GameInfo g, string buildLabel, IReadOnlyList<OffsetManager.OffsetEntry> entries,
        Action<string, double>? progress, CancellationToken ct)
    {
        var profile = ProfileStore.LoadAll().FirstOrDefault(p => p.Matches(g.ModuleName, g.TitleId))
                      ?? new OffsetProfile { Game = g.ToString(), Module = g.ModuleName, TitleId = g.TitleId != 0 ? g.TitleId.ToString("X8") : "" };
        profile.LearnedFrom = buildLabel;
        int learned = 0, failed = 0, constants = 0, i = 0;

        // one signature per address; every entry sharing it (On/Off pairs, aliases) reuses the pattern
        var cache = new Dictionary<uint, SignatureDef?>();
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke($"Learning {e.Name}…", (double)i++ / Math.Max(1, entries.Count));
            profile.Signatures.RemoveAll(x => x.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase));
            profile.Constants.RemoveAll(x => x.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase));

            if (e.BaseAddress < 0x80000000)
            {
                profile.Constants.Add(new ConstantDef { Name = e.Name, Category = e.Category, Value = $"0x{e.BaseAddress:X}" + (e.Offset is { } plus ? $" + 0x{plus:X}" : "") + (e.ByteValues != null ? $" | Bytes: {e.ByteValues}" : "") });
                constants++;
                continue;
            }

            if (!cache.TryGetValue(e.BaseAddress, out var sig))
                cache[e.BaseAddress] = sig = SignatureEngine.Generate(m, a, e.Name, e.BaseAddress, e.Category);
            if (sig == null) { failed++; continue; }

            profile.Signatures.Add(new SignatureDef
            {
                Name = e.Name, Category = e.Category, Pattern = sig.Pattern, Offset = sig.Offset, Resolve = sig.Resolve, Adjust = sig.Adjust,
                Bytes = string.IsNullOrWhiteSpace(e.ByteValues) ? null : OffsetTextFormat.NormalizeBytes(e.ByteValues!),
                Plus = e.Offset, Notes = $"learned from {buildLabel} (0x{e.BaseAddress:X8})",
            });
            learned++;
        }
        ProfileStore.Save(profile);
        return (profile, learned, failed, constants);
    }
}
