using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemoryEditor.Runtime;

namespace MemoryEditor.Library;

/// <summary>One executable build of a game (a title update / TU, or the disc version).</summary>
public sealed class GameBuild
{
    /// <summary>User facing label – "TU18", or "Build 2013-11-04" until the user names it.</summary>
    public string Label { get; set; } = "";
    public uint Timestamp { get; set; }
    public uint Checksum { get; set; }
    public string Version { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public uint ModuleBase { get; set; }
    public uint ModuleSize { get; set; }
    /// <summary>Offsets file in the BO2 text format, relative to <see cref="GameLibrary.Root"/> (or absolute).</summary>
    public string OffsetFile { get; set; } = "";
    /// <summary>Hand-made / imported offsets file for this build (kept verbatim; runtime pulls are merged after it).</summary>
    public string CuratedFile { get; set; } = "";
    public int OffsetCount { get; set; }
    public DateTime? PulledAt { get; set; }
    /// <summary>Runtime pull · Imported · Ported (signatures) · Offline (file)</summary>
    public string Source { get; set; } = "";
    /// <summary>The file was checked against the live module of this exact build.</summary>
    public bool Verified { get; set; }
    public string Notes { get; set; } = "";

    [JsonIgnore] public bool HasTimestamp => Timestamp != 0;

    public static string DefaultLabel(uint timestamp, string version) =>
        timestamp != 0 ? $"Build {DateTimeOffset.FromUnixTimeSeconds(timestamp):yyyy-MM-dd}" + (version.Length > 0 ? $" (v{version})" : "")
                       : version.Length > 0 ? $"v{version}" : "Unknown build";
}

public sealed class GameRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public uint TitleId { get; set; }
    public string ModuleName { get; set; } = "";
    /// <summary>Executable on the console used by Launch (Hdd:\Games\…\default.xex).</summary>
    public string LaunchPath { get; set; } = "";
    public string CoverPath { get; set; } = "";
    public bool Favorite { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
    public DateTime? LastSeen { get; set; }
    public DateTime? LastLaunched { get; set; }
    public string SelectedBuild { get; set; } = "";
    public List<GameBuild> Builds { get; set; } = new();

    [JsonIgnore] public string TitleIdText => TitleId != 0 ? TitleId.ToString("X8") : "";
    [JsonIgnore] public int TotalOffsets => Builds.Sum(b => b.OffsetCount);
    [JsonIgnore] public GameBuild? Current => Builds.FirstOrDefault(b => b.Label == SelectedBuild) ?? Builds.OrderByDescending(b => b.PulledAt ?? DateTime.MinValue).FirstOrDefault();

    public GameBuild? FindBuild(uint timestamp, uint checksum, string module = "", uint size = 0) =>
        timestamp != 0
            ? Builds.FirstOrDefault(b => b.Timestamp == timestamp && (checksum == 0 || b.Checksum == 0 || b.Checksum == checksum))
            // homebrew / unsigned images often report no timestamp – match on module name + image size instead
            : module.Length == 0 || size == 0 ? null
            : Builds.FirstOrDefault(b => b.Timestamp == 0 && b.CuratedFile.Length == 0 && b.ModuleSize == size && b.ModuleName.Equals(module, StringComparison.OrdinalIgnoreCase));

    public bool Matches(GameInfo g) =>
        (TitleId != 0 && g.TitleId != 0 && TitleId == g.TitleId) ||
        (g.TitleId == 0 || TitleId == 0) && ModuleName.Length > 0 && ModuleName.Equals(g.ModuleName, StringComparison.OrdinalIgnoreCase) ||
        Builds.Any(b => b.Timestamp != 0 && b.Timestamp == g.Timestamp);
}

/// <summary>Automation and pull preferences (persisted with the library).</summary>
public sealed class LibrarySettings
{
    public bool AutoPullOnConnect { get; set; } = true;
    public bool WatchTitleChanges { get; set; } = true;
    public bool AutoPullNewBuilds { get; set; } = true;
    public bool LoadIntoDatabase { get; set; } = true;
    public bool AutoLearnSignatures { get; set; } = true;
    public bool IncludeStringReferences { get; set; }
    public bool IncludeImports { get; set; } = true;
    public bool IncludeModuleLayout { get; set; } = true;
    public bool IncludeLoadedModules { get; set; } = true;
    public int WatchIntervalSeconds { get; set; } = 4;
    public int SettleSeconds { get; set; } = 8;
    public string ExportFolder { get; set; } = "";
    public string SelectedGameId { get; set; } = "";
    public string SortMode { get; set; } = "Recent";
    /// <summary>The bundled bo2_tu18_offsets.txt was imported once (not re-added if the user removes it).</summary>
    public bool BundledImported { get; set; }
}

/// <summary>
/// The game library: every title seen on the console (or imported), each with its builds and the offsets file of
/// every build. Stored under <c>Games\library.json</c>; offset files are plain BO2-format text in <c>Games\&lt;game&gt;\</c>.
/// All members are used from the UI thread; saving serialises there and writes on the thread pool.
/// </summary>
public sealed class GameLibrary
{
    public static string Root => ProfileStore.Directory;
    private static string FilePath => Path.Combine(Root, "library.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public List<GameRecord> Games { get; set; } = new();
    public LibrarySettings Settings { get; set; } = new();

    [JsonIgnore] public Action? Changed { get; set; }

    public static GameLibrary Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var lib = JsonSerializer.Deserialize<GameLibrary>(File.ReadAllText(FilePath), Json);
                if (lib != null) { lib.Games ??= new(); lib.Settings ??= new(); return lib; }
            }
        }
        catch
        {
            // a corrupt library must not prevent start-up – keep a copy and start fresh
            try { File.Copy(FilePath, FilePath + ".corrupt", true); } catch { }
        }
        return new GameLibrary();
    }

    public void Save()
    {
        string json;
        try { json = JsonSerializer.Serialize(this, Json); }
        catch { return; }
        _ = Task.Run(async () =>
        {
            await _saveGate.WaitAsync();
            try
            {
                Directory.CreateDirectory(Root);
                var tmp = FilePath + ".tmp";
                await File.WriteAllTextAsync(tmp, json);
                File.Move(tmp, FilePath, true); // atomic replace – never leaves a half-written library
            }
            catch { }
            finally { _saveGate.Release(); }
        });
        Changed?.Invoke();
    }

    public GameRecord? Find(string id) => Games.FirstOrDefault(g => g.Id == id);

    public GameRecord? Find(GameInfo g) =>
        Games.FirstOrDefault(r => r.TitleId != 0 && r.TitleId == g.TitleId) ??
        Games.FirstOrDefault(r => r.Matches(g));

    public GameRecord FindOrCreate(GameInfo g)
    {
        var rec = Find(g);
        if (rec == null)
        {
            rec = new GameRecord { Name = g.ToString(), TitleId = g.TitleId, ModuleName = g.ModuleName };
            Games.Add(rec);
        }
        if (rec.TitleId == 0 && g.TitleId != 0) rec.TitleId = g.TitleId;
        if (rec.ModuleName.Length == 0) rec.ModuleName = g.ModuleName;
        if ((rec.Name.Length == 0 || rec.Name.Equals(rec.ModuleName, StringComparison.OrdinalIgnoreCase)) && g.DisplayName.Length > 0)
            rec.Name = g.DisplayName;
        if (rec.LaunchPath.Length == 0 && g.Path.Length > 0) rec.LaunchPath = XBDMClient.ToXbdmPath(g.Path);
        rec.LastSeen = DateTime.Now;
        return rec;
    }

    public static string GameDirectory(GameRecord g)
    {
        var safe = string.Concat((g.Name.Length > 0 ? g.Name : g.ModuleName).Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' ? '_' : c)).Trim();
        if (safe.Length == 0) safe = g.Id;
        var dir = Path.Combine(Root, safe + (g.TitleId != 0 ? $" [{g.TitleId:X8}]" : ""));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string ResolvePath(string offsetFile) =>
        string.IsNullOrEmpty(offsetFile) ? "" : Path.IsPathRooted(offsetFile) ? offsetFile : Path.Combine(Root, offsetFile);

    public static string MakeRelative(string path)
    {
        var rel = Path.GetRelativePath(Root, path);
        return rel.StartsWith("..") ? path : rel;
    }

    public static string SafeFileName(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
}

/// <summary>
/// Reads and writes offsets in the exact text format of <c>bo2_tu18_offsets.txt</c>:
/// <code>
/// ~~Category~~
/// Name: 0x82497EB0
/// Name: 0x83551A10 + 0x57F8
/// Name: 0x82497EB0 | Bytes: 60 00 00 00
/// </code>
/// Header comments (<c>// key: value</c>) carry game / build metadata so files can be re-imported.
/// </summary>
public static class OffsetTextFormat
{
    public sealed record Entry(string Category, string Name, uint Address, uint? Plus = null, string? Bytes = null, string? RawValue = null);

    public static string Write(IReadOnlyDictionary<string, string> header, IEnumerable<Entry> entries, IEnumerable<string>? categoryOrder = null)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in header) sb.Append("// ").Append(k).Append(": ").AppendLine(v);
        if (header.Count > 0) sb.AppendLine();

        var groups = entries.GroupBy(e => CleanCategory(e.Category)).ToList();
        var order = (categoryOrder ?? Array.Empty<string>()).Select(CleanCategory).ToList();
        groups = groups.OrderBy(g => { int i = order.IndexOf(g.Key); return i < 0 ? int.MaxValue : i; }).ToList();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool first = true;
        foreach (var g in groups)
        {
            if (!first) sb.AppendLine();
            first = false;
            sb.Append("~~").Append(g.Key).AppendLine("~~");
            foreach (var e in g)
            {
                var name = UniqueName(CleanName(e.Name), used);
                sb.Append(name).Append(": ");
                if (e.RawValue != null) sb.Append(e.RawValue);
                else
                {
                    sb.Append("0x").Append(e.Address.ToString("X8"));
                    if (e.Plus is { } plus) sb.Append(" + 0x").Append(plus.ToString("X"));
                }
                if (!string.IsNullOrWhiteSpace(e.Bytes)) sb.Append(" | Bytes: ").Append(NormalizeBytes(e.Bytes!));
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    /// <summary>Reads the <c>// key: value</c> header block of an offsets file.</summary>
    public static Dictionary<string, string> ReadHeader(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadLines(path).Take(40))
            {
                var t = line.Trim();
                if (t.Length == 0) continue;
                if (!t.StartsWith("//")) break;
                var body = t[2..].Trim();
                int c = body.IndexOf(':');
                if (c > 0) d[body[..c].Trim()] = body[(c + 1)..].Trim();
            }
        }
        catch { }
        return d;
    }

    /// <summary>Parses an offsets file with the same parser the Offset Database uses.</summary>
    public static async Task<List<OffsetManager.OffsetEntry>> ReadAsync(string path)
    {
        var m = new OffsetManager();
        return await m.LoadFromFileAsync(path) ? m.GetAllOffsets() : new List<OffsetManager.OffsetEntry>();
    }

    public static string CleanName(string name)
    {
        var s = name.Replace(':', ' ').Replace('=', '-').Replace('\r', ' ').Replace('\n', ' ').Replace("|", "/").Trim();
        if (s.StartsWith("//")) s = s.TrimStart('/').Trim();
        if (s.StartsWith("~~")) s = s.Trim('~').Trim();
        if (s.Length > 120) s = s[..117] + "...";
        return s.Length == 0 ? "unnamed" : s;
    }

    public static string CleanCategory(string c)
    {
        var s = c.Replace("~", "").Trim();
        return s.Length == 0 ? "General" : s;
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        for (int i = 2; ; i++)
        {
            var n = $"{name} ({i})";
            if (used.Add(n)) return n;
        }
    }

    public static string NormalizeBytes(string bytes)
    {
        var hex = new string(bytes.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length % 2 != 0) return bytes.Trim();
        return string.Join(" ", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    }

    public static byte[]? ParseBytes(string? bytes)
    {
        if (string.IsNullOrWhiteSpace(bytes)) return null;
        var hex = new string(bytes.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length == 0 || hex.Length % 2 != 0) return null;
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber);
        return b;
    }
}
