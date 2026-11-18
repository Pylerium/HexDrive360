using System.Security.Cryptography;
using System.Text;

namespace MemoryEditor.Xex;

/// <summary>
/// Xbox 360 XEX2 container: header parsing plus full conversion to the PE "basefile" (.exe)
/// – AES-128 session-key decryption (retail / devkit / XEX1 keys) and none / basic / LZX ("normal") decompression.
/// </summary>
public sealed class XexFile
{
    // Publicly documented XEX keys (Xenia, xextool, libxenon).
    private static readonly byte[] RetailKey = Convert.FromHexString("20B185A59D28FDC340583FBB0896BF91");
    private static readonly byte[] DevkitKey = new byte[16];
    private static readonly byte[] Xex1Key = Convert.FromHexString("A26C10F71FD935E98B99922CE9321572");

    public const uint KeyFileFormatInfo = 0x000003FF;
    public const uint KeyResourceInfo = 0x000002FF;
    public const uint KeyDeltaPatch = 0x000005FF;
    public const uint KeyBoundingPath = 0x000080FF;
    public const uint KeyOriginalBase = 0x00010001;
    public const uint KeyEntryPoint = 0x00010100;
    public const uint KeyImageBase = 0x00010201;
    public const uint KeyImportLibraries = 0x000103FF;
    public const uint KeyChecksumTimestamp = 0x00018002;
    public const uint KeyOriginalPeName = 0x000183FF;
    public const uint KeyStaticLibraries = 0x000200FF;
    public const uint KeyTlsInfo = 0x00020104;
    public const uint KeyDefaultStackSize = 0x00020200;
    public const uint KeyDefaultHeapSize = 0x00020401;
    public const uint KeySystemFlags = 0x00030000;
    public const uint KeyExecutionInfo = 0x00040006;
    public const uint KeyTitleWorkspaceSize = 0x00040201;
    public const uint KeyGameRatings = 0x00040310;
    public const uint KeyLanKey = 0x00040404;
    public const uint KeyXbox360Logo = 0x000405FF;
    public const uint KeyMultidiscMediaIds = 0x000406FF;
    public const uint KeyAlternateTitleIds = 0x000407FF;

    public sealed class OptionalHeader
    {
        public uint Key { get; init; }
        public uint Value { get; init; }
        public string Name => KeyNames.TryGetValue(Key, out var n) ? n : $"0x{Key:X8}";
    }

    private static readonly Dictionary<uint, string> KeyNames = new()
    {
        [KeyFileFormatInfo] = "File format info", [KeyResourceInfo] = "Resource info", [KeyDeltaPatch] = "Delta patch descriptor",
        [KeyBoundingPath] = "Bounding path", [KeyOriginalBase] = "Original base address", [KeyEntryPoint] = "Entry point",
        [KeyImageBase] = "Image base address", [KeyImportLibraries] = "Import libraries", [KeyChecksumTimestamp] = "Checksum / timestamp",
        [KeyOriginalPeName] = "Original PE name", [KeyStaticLibraries] = "Static libraries", [KeyTlsInfo] = "TLS info",
        [KeyDefaultStackSize] = "Default stack size", [KeyDefaultHeapSize] = "Default heap size", [KeySystemFlags] = "System flags",
        [KeyExecutionInfo] = "Execution info", [KeyTitleWorkspaceSize] = "Title workspace size", [KeyGameRatings] = "Game ratings",
        [KeyLanKey] = "LAN key", [KeyXbox360Logo] = "Xbox 360 logo", [KeyMultidiscMediaIds] = "Multi-disc media IDs",
        [KeyAlternateTitleIds] = "Alternate title IDs",
    };

    public sealed class ImportLibrary
    {
        public string Name { get; init; } = "";
        public string Version { get; init; } = "";
        public string MinVersion { get; init; } = "";
        public List<uint> Records { get; } = new();
    }

    public sealed class ImportEntry
    {
        public string Library { get; init; } = "";
        public ushort Ordinal { get; init; }
        public uint RecordAddress { get; init; }
        public bool IsThunk { get; init; }
    }

    public string Path { get; private set; } = "";
    public byte[] Raw { get; private set; } = Array.Empty<byte>();
    public string Magic { get; private set; } = "";
    public uint ModuleFlags { get; private set; }
    public uint PeDataOffset { get; private set; }
    public uint SecurityInfoOffset { get; private set; }
    public List<OptionalHeader> Headers { get; } = new();

    // security info
    public uint ImageSize { get; private set; }
    public uint LoadAddress { get; private set; }
    public uint ImageFlags { get; private set; }
    public uint Region { get; private set; }
    public uint AllowedMediaTypes { get; private set; }
    public byte[] EncryptedKey { get; private set; } = new byte[16];

    // optional header values
    public uint EntryPoint { get; private set; }
    public uint ImageBase { get; private set; }
    public uint TitleId { get; private set; }
    public uint MediaId { get; private set; }
    public string Version { get; private set; } = "";
    public string BaseVersion { get; private set; } = "";
    public byte DiscNumber { get; private set; }
    public byte DiscCount { get; private set; }
    public uint SaveGameId { get; private set; }
    public string OriginalPeName { get; private set; } = "";
    public uint DefaultStackSize { get; private set; }
    public uint Timestamp { get; private set; }
    public uint Checksum { get; private set; }
    public List<ImportLibrary> ImportLibraries { get; } = new();
    public List<string> StaticLibraries { get; } = new();
    public List<(string Name, uint Address, uint Size)> Resources { get; } = new();

    public ushort EncryptionType { get; private set; }   // 0 none, 1 normal
    public ushort CompressionType { get; private set; }  // 0 none, 1 basic, 2 normal (LZX), 3 delta
    public uint LzxWindowSize { get; private set; }
    public uint LzxFirstBlockSize { get; private set; }
    public List<(uint DataSize, uint ZeroSize)> BasicBlocks { get; } = new();

    public string CompressionName => CompressionType switch { 0 => "None", 1 => "Basic (zero-fill)", 2 => "Normal (LZX)", 3 => "Delta patch", _ => $"Unknown ({CompressionType})" };
    public string EncryptionName => EncryptionType switch { 0 => "None", 1 => "AES-128", _ => $"Unknown ({EncryptionType})" };
    public bool IsPatch => CompressionType == 3 || Headers.Any(h => h.Key == KeyDeltaPatch) || (ModuleFlags & 0x10) != 0 || (ModuleFlags & 0x20) != 0;
    public string ModuleType
    {
        get
        {
            var parts = new List<string>();
            if ((ModuleFlags & 0x01) != 0) parts.Add("Title");
            if ((ModuleFlags & 0x02) != 0) parts.Add("Exports to title");
            if ((ModuleFlags & 0x04) != 0) parts.Add("System debugger");
            if ((ModuleFlags & 0x08) != 0) parts.Add("DLL");
            if ((ModuleFlags & 0x10) != 0) parts.Add("Module patch");
            if ((ModuleFlags & 0x20) != 0) parts.Add("Full patch");
            if ((ModuleFlags & 0x40) != 0) parts.Add("Delta patch");
            if ((ModuleFlags & 0x80) != 0) parts.Add("User mode");
            return parts.Count == 0 ? "Unknown" : string.Join(", ", parts);
        }
    }

    /// <summary>Which key decrypted the image (set by <see cref="ExtractBasefile"/>).</summary>
    public string? KeyUsed { get; private set; }

    public static bool IsXex(byte[] data) =>
        data.Length >= 4 && data[0] == 'X' && data[1] == 'E' && data[2] == 'X' && (data[3] == '2' || data[3] == '1' || data[3] == '%' || data[3] == '-');

    public static XexFile Load(string path)
    {
        var x = Parse(File.ReadAllBytes(path));
        x.Path = path;
        return x;
    }

    private static uint BE32(byte[] d, long o) => o + 4 > d.Length ? 0 : (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
    private static ushort BE16(byte[] d, long o) => o + 2 > d.Length ? (ushort)0 : (ushort)(d[o] << 8 | d[o + 1]);

    private static string FormatVersion(uint v) => $"{v >> 28}.{(v >> 24) & 0xF}.{(v >> 8) & 0xFFFF}.{v & 0xFF}";

    public static XexFile Parse(byte[] data)
    {
        if (!IsXex(data)) throw new InvalidDataException("Not an XEX file (missing XEX2 magic)");
        var x = new XexFile { Raw = data };
        x.Magic = Encoding.ASCII.GetString(data, 0, 4);
        if (x.Magic != "XEX2") throw new NotSupportedException($"{x.Magic} images are not supported – only retail/devkit XEX2");

        x.ModuleFlags = BE32(data, 4);
        x.PeDataOffset = BE32(data, 8);
        x.SecurityInfoOffset = BE32(data, 16);
        uint count = BE32(data, 20);
        if (count > 256) throw new InvalidDataException("Corrupt XEX header (too many optional headers)");
        for (int i = 0; i < count; i++)
            x.Headers.Add(new OptionalHeader { Key = BE32(data, 24 + i * 8), Value = BE32(data, 28 + i * 8) });

        // security info
        uint s = x.SecurityInfoOffset;
        if (s + 0x184 > data.Length) throw new InvalidDataException("Corrupt XEX header (security info out of range)");
        x.ImageSize = BE32(data, s + 0x004);
        x.ImageFlags = BE32(data, s + 0x10C);
        x.LoadAddress = BE32(data, s + 0x110);
        Array.Copy(data, s + 0x150, x.EncryptedKey, 0, 16);
        x.Region = BE32(data, s + 0x178);
        x.AllowedMediaTypes = BE32(data, s + 0x17C);

        foreach (var h in x.Headers)
        {
            uint o = h.Value;
            switch (h.Key)
            {
                case KeyEntryPoint: x.EntryPoint = h.Value; break;
                case KeyImageBase: x.ImageBase = h.Value; break;
                case KeyDefaultStackSize: x.DefaultStackSize = h.Value; break;
                case KeyChecksumTimestamp: x.Checksum = BE32(data, o); x.Timestamp = BE32(data, o + 4); break;
                case KeyExecutionInfo:
                    x.MediaId = BE32(data, o);
                    x.Version = FormatVersion(BE32(data, o + 4));
                    x.BaseVersion = FormatVersion(BE32(data, o + 8));
                    x.TitleId = BE32(data, o + 12);
                    x.DiscNumber = data[o + 18];
                    x.DiscCount = data[o + 19];
                    x.SaveGameId = BE32(data, o + 20);
                    break;
                case KeyOriginalPeName:
                {
                    uint len = BE32(data, o);
                    if (len > 4 && o + len <= data.Length)
                        x.OriginalPeName = Encoding.ASCII.GetString(data, (int)o + 4, (int)len - 4).TrimEnd('\0');
                    break;
                }
                case KeyFileFormatInfo:
                {
                    uint infoSize = BE32(data, o);
                    x.EncryptionType = BE16(data, o + 4);
                    x.CompressionType = BE16(data, o + 6);
                    if (x.CompressionType == 1)
                    {
                        uint n = (infoSize - 8) / 8;
                        for (uint i = 0; i < n; i++)
                            x.BasicBlocks.Add((BE32(data, o + 8 + i * 8), BE32(data, o + 12 + i * 8)));
                    }
                    else if (x.CompressionType == 2)
                    {
                        x.LzxWindowSize = BE32(data, o + 8);
                        x.LzxFirstBlockSize = BE32(data, o + 12);
                    }
                    break;
                }
                case KeyImportLibraries:
                    ParseImports(x, data, o);
                    break;
                case KeyStaticLibraries:
                {
                    uint size = BE32(data, o);
                    for (uint p = o + 4; p + 16 <= o + size && p + 16 <= data.Length; p += 16)
                    {
                        var name = Encoding.ASCII.GetString(data, (int)p, 8).TrimEnd('\0');
                        x.StaticLibraries.Add($"{name} {BE16(data, p + 8)}.{BE16(data, p + 10)}.{BE16(data, p + 12)}.{BE16(data, p + 14) & 0x7FFF}");
                    }
                    break;
                }
                case KeyResourceInfo:
                {
                    uint size = BE32(data, o);
                    for (uint p = o + 4; p + 16 <= o + size && p + 16 <= data.Length; p += 16)
                        x.Resources.Add((Encoding.ASCII.GetString(data, (int)p, 8).TrimEnd('\0'), BE32(data, p + 8), BE32(data, p + 12)));
                    break;
                }
            }
        }
        if (x.ImageBase == 0) x.ImageBase = x.LoadAddress;
        if (x.LoadAddress == 0) x.LoadAddress = x.ImageBase;
        return x;
    }

    private static void ParseImports(XexFile x, byte[] data, uint o)
    {
        try
        {
            uint stringTableSize = BE32(data, o + 4);
            uint libCount = BE32(data, o + 8);
            var names = new List<string>();
            uint sp = o + 12, end = o + 12 + stringTableSize;
            while (sp < end && sp < data.Length)
            {
                int z = Array.IndexOf(data, (byte)0, (int)sp);
                if (z < 0 || z > end) z = (int)end;
                var str = Encoding.ASCII.GetString(data, (int)sp, z - (int)sp);
                if (str.Length > 0) names.Add(str);
                sp = (uint)z + 1;
                while (sp < end && data[sp] == 0) sp++;
            }
            uint lp = end;
            for (uint i = 0; i < libCount && lp + 0x28 <= data.Length; i++)
            {
                uint size = BE32(data, lp);
                uint version = BE32(data, lp + 0x1C);
                uint minVersion = BE32(data, lp + 0x20);
                ushort nameIndex = BE16(data, lp + 0x24);
                ushort recCount = BE16(data, lp + 0x26);
                var lib = new ImportLibrary
                {
                    Name = nameIndex < names.Count ? names[nameIndex] : $"lib{nameIndex}",
                    Version = FormatVersion(version),
                    MinVersion = FormatVersion(minVersion),
                };
                for (uint r = 0; r < recCount; r++) lib.Records.Add(BE32(data, lp + 0x28 + r * 4));
                x.ImportLibraries.Add(lib);
                if (size == 0) break;
                lp += size;
            }
        }
        catch { /* imports are informational only */ }
    }

    // ───────────────────────────── conversion ─────────────────────────────

    private static byte[] AesEcbDecrypt(byte[] key, byte[] block)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(block, PaddingMode.None);
    }

    private static byte[] AesCbcDecrypt(byte[] key, byte[] data)
    {
        int len = data.Length & ~15;
        var output = (byte[])data.Clone();
        if (len == 0) return output;
        using var aes = Aes.Create();
        aes.Key = key;
        var dec = aes.DecryptCbc(data.AsSpan(0, len), new byte[16], PaddingMode.None);
        Buffer.BlockCopy(dec, 0, output, 0, len);
        return output;
    }

    /// <summary>Decrypts and decompresses the image. Returns the PE basefile (memory image).</summary>
    public byte[] ExtractBasefile(Action<string>? log = null)
    {
        if (IsPatch && CompressionType == 3) throw new NotSupportedException("Delta patches (.xexp) cannot be converted on their own");
        if (CompressionType > 2) throw new NotSupportedException($"Unsupported compression type {CompressionType}");
        if (PeDataOffset >= Raw.Length) throw new InvalidDataException("PE data offset is beyond end of file");

        var payload = Raw.AsSpan((int)PeDataOffset).ToArray();
        var candidates = EncryptionType == 0
            ? new[] { ("none", (byte[]?)null) }
            : new[] { ("retail", (byte[]?)RetailKey), ("devkit", DevkitKey), ("xex1", Xex1Key) };

        Exception? lastError = null;
        foreach (var (name, key) in candidates)
        {
            try
            {
                var data = key == null ? payload : AesCbcDecrypt(AesEcbDecrypt(key, EncryptedKey), payload);
                var image = Decompress(data);
                if (PeImage.LooksLikePe(image))
                {
                    KeyUsed = name;
                    log?.Invoke($"✅ Decrypted with {name} key, {CompressionName} → {image.Length:N0} bytes");
                    return image;
                }
                lastError = new InvalidDataException($"{name} key produced no valid PE image");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw new InvalidDataException($"Could not decrypt/decompress XEX: {lastError?.Message}", lastError);
    }

    private byte[] Decompress(byte[] data)
    {
        uint size = ImageSize != 0 ? ImageSize : (uint)data.Length;
        switch (CompressionType)
        {
            case 0:
            {
                var img = new byte[size];
                Buffer.BlockCopy(data, 0, img, 0, (int)Math.Min(size, (uint)data.Length));
                return img;
            }
            case 1:
            {
                var img = new byte[size];
                long src = 0, dst = 0;
                foreach (var (dataSize, zeroSize) in BasicBlocks)
                {
                    var n = (int)Math.Min(dataSize, Math.Min(data.Length - src, img.Length - dst));
                    if (n > 0) Buffer.BlockCopy(data, (int)src, img, (int)dst, n);
                    src += dataSize;
                    dst += dataSize + zeroSize;
                    if (dst >= img.Length) break;
                }
                return img;
            }
            case 2:
            {
                using var ms = new MemoryStream(data.Length);
                long p = 0;
                uint blockSize = LzxFirstBlockSize;
                int guard = 0;
                while (blockSize != 0 && p + 24 <= data.Length && guard++ < 1_000_000)
                {
                    long next = p + blockSize;
                    uint nextSize = BE32(data, p);
                    p += 4 + 20; // next block size + SHA1 of next block
                    while (p + 2 <= data.Length)
                    {
                        int chunk = BE16(data, p);
                        p += 2;
                        if (chunk == 0) break;
                        if (p + chunk > data.Length) throw new InvalidDataException("LZX chunk runs past end of file");
                        ms.Write(data, (int)p, chunk);
                        p += chunk;
                    }
                    p = next;
                    blockSize = nextSize;
                }
                return LzxDecoder.Decompress(ms.ToArray(), (int)size, (int)LzxWindowSize, allowPartial: true);
            }
        }
        throw new NotSupportedException();
    }

    /// <summary>Full conversion: writes a loadable .exe (basefile with normalized section table).</summary>
    public byte[] ConvertToExe(bool normalizeSections = true, Action<string>? log = null)
    {
        var image = ExtractBasefile(log);
        if (normalizeSections) PeImage.NormalizeRawPointers(image);
        return image;
    }

    /// <summary>Resolves import records against a decompressed image (ordinal + thunk/variable).</summary>
    public List<ImportEntry> ResolveImports(byte[] image)
    {
        var list = new List<ImportEntry>();
        uint baseAddr = LoadAddress != 0 ? LoadAddress : ImageBase;
        foreach (var lib in ImportLibraries)
        {
            foreach (var rec in lib.Records)
            {
                long off = (long)rec - baseAddr;
                if (off < 0 || off + 4 > image.Length) continue;
                uint v = BE32(image, off);
                uint type = v >> 24;
                list.Add(new ImportEntry { Library = lib.Name, Ordinal = (ushort)(v & 0xFFFF), RecordAddress = rec, IsThunk = type == 1 });
            }
        }
        return list;
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"File            {System.IO.Path.GetFileName(Path)}  ({Raw.Length:N0} bytes)");
        sb.AppendLine($"Format          {Magic}  •  {ModuleType}");
        if (TitleId != 0) sb.AppendLine($"Title ID        {TitleId:X8}");
        if (MediaId != 0) sb.AppendLine($"Media ID        {MediaId:X8}");
        if (Version.Length > 0) sb.AppendLine($"Version         {Version}  (base {BaseVersion})");
        if (DiscCount > 0) sb.AppendLine($"Disc            {DiscNumber} of {DiscCount}");
        if (OriginalPeName.Length > 0) sb.AppendLine($"Original name   {OriginalPeName}");
        sb.AppendLine($"Load address    0x{LoadAddress:X8}");
        sb.AppendLine($"Entry point     0x{EntryPoint:X8}");
        sb.AppendLine($"Image size      0x{ImageSize:X8}  ({ImageSize / 1024.0 / 1024.0:0.00} MB)");
        sb.AppendLine($"Encryption      {EncryptionName}");
        sb.AppendLine($"Compression     {CompressionName}" + (CompressionType == 2 ? $"  (window 0x{LzxWindowSize:X})" : CompressionType == 1 ? $"  ({BasicBlocks.Count} blocks)" : ""));
        if (Timestamp != 0) sb.AppendLine($"Timestamp       {DateTimeOffset.FromUnixTimeSeconds(Timestamp):yyyy-MM-dd HH:mm:ss} UTC");
        if (DefaultStackSize != 0) sb.AppendLine($"Stack size      0x{DefaultStackSize:X}");
        if (ImportLibraries.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Import libraries");
            foreach (var l in ImportLibraries) sb.AppendLine($"  {l.Name,-16} v{l.Version}  (min {l.MinVersion})  {l.Records.Count} records");
        }
        if (StaticLibraries.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Static libraries");
            foreach (var l in StaticLibraries) sb.AppendLine($"  {l}");
        }
        if (Resources.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Resources");
            foreach (var r in Resources) sb.AppendLine($"  {r.Name,-10} 0x{r.Address:X8}  0x{r.Size:X}");
        }
        sb.AppendLine();
        sb.AppendLine("Optional headers");
        foreach (var h in Headers) sb.AppendLine($"  {h.Key:X8}  {h.Name}");
        return sb.ToString();
    }
}
