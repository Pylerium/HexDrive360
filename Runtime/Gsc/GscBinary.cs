using System.Buffers.Binary;
using System.Text;

namespace MemoryEditor.Runtime.Gsc;

/// <summary>
/// Compiled Black Ops II (T6) GSC/CSC script as the console loads it. Big-endian, 0x40-byte header:
///
///   00  magic 80 47 53 43 0D 0A 00 06        ("\x80GSC\r\n\0" + version 6)
///   08  u32 source CRC
///   0C  u32 include table      10 u32 animtree table   14 u32 code section
///   18  u32 string fixups      1C u32 exports          20 u32 imports
///   24  u32 fixups / size      28 u32 profile / size   2C u32 code size
///   30  u16 name (offset)      32 u16 string count     34 u16 export count
///   36  u16 import count       38 u16 fixup count      3A u16 profile count
///   3C  u8  include count      3D u8  animtree count   3E u8 flags
/// </summary>
public sealed class GscBinary
{
    public static readonly byte[] Magic = { 0x80, 0x47, 0x53, 0x43, 0x0D, 0x0A, 0x00 };
    public const int HeaderSize = 0x40;

    public byte[] Data { get; }
    public byte Version { get; }
    public uint SourceCrc { get; }
    public uint DeclaredSize { get; }
    public string Name { get; } = "";
    public IReadOnlyList<string> Includes { get; } = Array.Empty<string>();
    public int Exports { get; }
    public int Imports { get; }
    public int Strings { get; }
    public IReadOnlyList<string> Problems { get; }

    public bool IsValid => Problems.Count == 0;

    private GscBinary(byte[] data)
    {
        Data = data;
        var problems = new List<string>();
        var s = data.AsSpan();
        Version = s[7];
        SourceCrc = BinaryPrimitives.ReadUInt32BigEndian(s[0x08..]);
        DeclaredSize = BinaryPrimitives.ReadUInt32BigEndian(s[0x24..]);
        Strings = BinaryPrimitives.ReadUInt16BigEndian(s[0x32..]);
        Exports = BinaryPrimitives.ReadUInt16BigEndian(s[0x34..]);
        Imports = BinaryPrimitives.ReadUInt16BigEndian(s[0x36..]);

        if (Version != 0x06) problems.Add($"version 0x{Version:X2} is not Black Ops II (0x06)");
        ushort nameOff = BinaryPrimitives.ReadUInt16BigEndian(s[0x30..]);
        Name = ReadCString(data, nameOff);

        // a little-endian (PC) script has the offsets byte-swapped: they are absurdly large when read big-endian
        uint includes = BinaryPrimitives.ReadUInt32BigEndian(s[0x0C..]);
        uint code = BinaryPrimitives.ReadUInt32BigEndian(s[0x14..]);
        if (includes > data.Length || code > data.Length)
            problems.Add("header offsets point outside the file – this looks like a PC (little-endian) script, not Xbox 360");
        if (DeclaredSize != 0 && DeclaredSize != data.Length && DeclaredSize > data.Length)
            problems.Add($"file is truncated: header says {DeclaredSize:N0} bytes, file has {data.Length:N0}");

        int incCount = s[0x3C];
        var inc = new List<string>();
        if (includes + incCount * 4u <= data.Length)
            for (int i = 0; i < incCount; i++)
                inc.Add(ReadCString(data, BinaryPrimitives.ReadUInt32BigEndian(s[(int)(includes + i * 4)..])));
        Includes = inc;
        Problems = problems;
    }

    public static bool HasMagic(ReadOnlySpan<byte> b) => b.Length >= 8 && b[..7].SequenceEqual(Magic);

    /// <summary>Parses a compiled script; returns null when the bytes are not a compiled GSC at all (e.g. source text).</summary>
    public static GscBinary? TryParse(byte[] data) => data.Length >= HeaderSize && HasMagic(data) ? new GscBinary(data) : null;

    /// <summary>True when the bytes look like GSC source text rather than a compiled script.</summary>
    public static bool LooksLikeSource(byte[] data)
    {
        if (HasMagic(data)) return false;
        int n = Math.Min(data.Length, 4096), text = 0;
        for (int i = 0; i < n; i++) if (data[i] is 9 or 10 or 13 or (>= 32 and < 127) or >= 0xC2) text++;
        return n > 0 && text > n * 0.95;
    }

    private static string ReadCString(byte[] d, uint off)
    {
        if (off >= d.Length) return "";
        int end = Array.IndexOf(d, (byte)0, (int)off);
        if (end < 0) end = d.Length;
        return Encoding.ASCII.GetString(d, (int)off, Math.Min(end - (int)off, 256));
    }

    public string Summary =>
        $"{Data.Length:N0} bytes · {Exports} functions · {Imports} imports · {Strings} strings" + (Includes.Count > 0 ? $" · {Includes.Count} includes" : "");
}
