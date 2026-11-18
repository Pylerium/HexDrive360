using MemoryEditor.Xex;

namespace MemoryEditor.Runtime;

public sealed class GameInfo
{
    public string ModuleName { get; set; } = "";
    public string Path { get; set; } = "";
    public uint Base { get; set; }
    public uint Size { get; set; }
    public uint Timestamp { get; set; }
    public uint Checksum { get; set; }
    public uint TitleId { get; set; }
    public string DisplayName { get; set; } = "";
    /// <summary>Executable version from the XEX execution info (read from the console's disk).</summary>
    public string Version { get; set; } = "";
    public uint MediaId { get; set; }
    public List<XBDMClient.ModuleInfo> AllModules { get; set; } = new();
    public string CacheKey => ProfileStore.CacheKey(ModuleName, Timestamp);
    public override string ToString() => DisplayName.Length > 0 ? DisplayName : ModuleName;
}

public static class GameDetector
{
    /// <summary>Well known title IDs (informational; detection works for any game).</summary>
    public static readonly Dictionary<uint, string> KnownTitles = new()
    {
        [0x415607E6] = "Call of Duty 4: Modern Warfare",
        [0x41560817] = "Call of Duty: Modern Warfare 2",
        [0x41560855] = "Call of Duty: Black Ops",
        [0x415608CB] = "Call of Duty: Modern Warfare 3",
        [0x415608C3] = "Call of Duty: Black Ops II",
        [0x4D5307E6] = "Halo 3",
        [0x4D53085B] = "Halo: Reach",
        [0x545407F2] = "Grand Theft Auto IV",
        [0x545408A7] = "Grand Theft Auto V",
        [0x584111F7] = "Minecraft: Xbox 360 Edition",
        [0xFFFE07D1] = "Xbox 360 Dashboard",
    };

    private static readonly HashSet<string> SystemModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "xboxkrnl.exe", "xam.xex", "xbdm.xex", "hud.xex", "xshell.xex", "dash.xex", "xbdm.dll", "vk.xex", "huduiskin.xex",
        "xamshell.xex", "createprofile.xex", "signin.xex", "updater.xex", "gamerprofile.xex", "Guide.MP.Purchase.xex",
        "launch.xex", "xbOnline.xex", "JRPC2.xex", "xbWatson.xex", "dashuiskin.xex", "nomni.xex", "xex.xex"
    };

    public static async Task<GameInfo?> DetectAsync(XBDMClient c, Action<string>? log = null)
    {
        if (!c.IsConnected) return null;
        var modules = await c.GetModulesAsync();
        if (modules.Count == 0)
        {
            log?.Invoke("⚠️ Console returned no module list");
            return null;
        }

        var running = await c.GetRunningTitleAsync();
        string runningFile = running?.Path is { Length: > 0 } p ? p[(p.LastIndexOf('\\') + 1)..] : "";

        XBDMClient.ModuleInfo? title =
            modules.FirstOrDefault(m => runningFile.Length > 0 && m.Name.Equals(runningFile, StringComparison.OrdinalIgnoreCase)) ??
            modules.FirstOrDefault(m => m.Base == 0x82000000) ??
            modules.LastOrDefault(m => !SystemModules.Contains(m.Name) && m.Name.EndsWith(".xex", StringComparison.OrdinalIgnoreCase)) ??
            modules.LastOrDefault();
        if (title == null) return null;

        var info = new GameInfo
        {
            ModuleName = title.Name,
            Path = running?.Path ?? "",
            Base = title.Base,
            Size = title.Size,
            Timestamp = title.Timestamp != 0 ? title.Timestamp : running?.Timestamp ?? 0,
            Checksum = title.Checksum != 0 ? title.Checksum : running?.Checksum ?? 0,
            AllModules = modules,
        };
        info.TitleId = await c.GetTitleIdAsync() ?? 0;
        info.DisplayName = info.TitleId != 0 && KnownTitles.TryGetValue(info.TitleId, out var n) ? n : title.Name;
        return info;
    }

    /// <summary>Reads the module image from console memory (section by section; unreadable pages are zero).</summary>
    public static async Task<ModuleImage> DumpModuleAsync(XBDMClient c, GameInfo g, Action<string, double>? progress, CancellationToken ct)
    {
        var header = await c.GetMemoryAsync(g.Base, 0x1000) ?? throw new IOException($"Cannot read module header at 0x{g.Base:X8}");
        var pe = PeImage.TryParse(header, g.Base);
        uint size = g.Size != 0 ? g.Size : pe?.SizeOfImage ?? 0;
        if (size == 0 || size > 0x10000000) throw new InvalidDataException($"Implausible module size 0x{size:X}");

        var image = new byte[size];
        Buffer.BlockCopy(header, 0, image, 0, Math.Min(header.Length, image.Length));

        var ranges = new List<(uint Start, uint Len, string Name)>();
        if (pe != null && pe.Sections.Count > 0)
        {
            foreach (var s in pe.Sections)
            {
                uint start = s.Rva, len = Math.Min(s.VirtualSize, size > start ? size - start : 0);
                if (len > 0) ranges.Add((start, len, s.Name));
            }
        }
        else ranges.Add((0x1000, size - 0x1000, "image"));

        long total = ranges.Sum(r => (long)r.Len), done = 0;
        const uint chunk = 0x10000;
        int failures = 0;
        foreach (var (start, len, name) in ranges)
        {
            for (uint off = 0; off < len; off += chunk)
            {
                ct.ThrowIfCancellationRequested();
                uint n = Math.Min(chunk, len - off);
                var data = await c.GetMemoryAsync(g.Base + start + off, n);
                if (data != null) Buffer.BlockCopy(data, 0, image, (int)(start + off), Math.Min(data.Length, (int)n));
                else failures++;
                done += n;
                progress?.Invoke($"Reading {name} from console… {done / 1048576.0:0.0} / {total / 1048576.0:0.0} MB", 0.85 * done / total);
            }
        }
        if (failures > 0) progress?.Invoke($"{failures} unreadable chunk(s) were zero-filled", 0.85);
        return ModuleImage.FromPe(g.ModuleName, image, g.Base);
    }
}
