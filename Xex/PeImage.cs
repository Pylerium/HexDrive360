using System.Text;

namespace MemoryEditor.Xex;

/// <summary>Minimal parser for Xbox 360 PE images (basefiles) – headers are little-endian, code is big-endian PPC.</summary>
public sealed class PeImage
{
    public sealed class Section
    {
        public string Name { get; init; } = "";
        public uint VirtualAddress { get; init; }   // absolute (load address + RVA)
        public uint Rva { get; init; }
        public uint VirtualSize { get; init; }
        public uint RawOffset { get; init; }
        public uint RawSize { get; init; }
        public uint Characteristics { get; init; }
        public bool IsCode => (Characteristics & 0x20000020) != 0;        // CNT_CODE | MEM_EXECUTE
        public bool IsWritable => (Characteristics & 0x80000000) != 0;
        public bool IsInitializedData => (Characteristics & 0x40) != 0;
        public uint End => VirtualAddress + VirtualSize;
        public string Kind => IsCode ? "code" : IsWritable ? "data (rw)" : "data (ro)";
        public override string ToString() => $"{Name,-8} 0x{VirtualAddress:X8}-0x{End:X8} {Kind}";
    }

    public uint ImageBase { get; private set; }
    public uint EntryPoint { get; private set; }
    public uint SizeOfImage { get; private set; }
    public ushort Machine { get; private set; }
    public uint TimeDateStamp { get; private set; }
    public List<Section> Sections { get; } = new();
    public int SectionTableOffset { get; private set; }

    public bool IsXbox360 => Machine == 0x01F2;

    public static bool LooksLikePe(byte[] data) =>
        data.Length > 0x40 && data[0] == 'M' && data[1] == 'Z' &&
        BitConverter.ToInt32(data, 0x3C) is var lfa && lfa > 0 && lfa + 0x18 < data.Length &&
        data[lfa] == 'P' && data[lfa + 1] == 'E';

    /// <param name="loadAddressOverride">Use the XEX load address when the PE header base is unreliable.</param>
    public static PeImage? TryParse(byte[] data, uint loadAddressOverride = 0)
    {
        try
        {
            if (!LooksLikePe(data)) return null;
            var pe = new PeImage();
            int lfa = BitConverter.ToInt32(data, 0x3C);
            int fh = lfa + 4;
            pe.Machine = BitConverter.ToUInt16(data, fh);
            int nsec = BitConverter.ToUInt16(data, fh + 2);
            pe.TimeDateStamp = BitConverter.ToUInt32(data, fh + 4);
            int optSize = BitConverter.ToUInt16(data, fh + 16);
            int opt = fh + 20;
            ushort magic = BitConverter.ToUInt16(data, opt);
            uint entryRva = BitConverter.ToUInt32(data, opt + 16);
            uint imageBase = magic == 0x20B ? (uint)BitConverter.ToUInt64(data, opt + 24) : BitConverter.ToUInt32(data, opt + 28);
            pe.SizeOfImage = BitConverter.ToUInt32(data, opt + 56);
            pe.ImageBase = loadAddressOverride != 0 ? loadAddressOverride : imageBase;
            pe.EntryPoint = entryRva == 0 ? 0 : pe.ImageBase + entryRva;
            int st = opt + optSize;
            pe.SectionTableOffset = st;
            for (int i = 0; i < nsec && st + 40 <= data.Length; i++, st += 40)
            {
                var name = Encoding.ASCII.GetString(data, st, 8).TrimEnd('\0');
                uint vsize = BitConverter.ToUInt32(data, st + 8);
                uint rva = BitConverter.ToUInt32(data, st + 12);
                pe.Sections.Add(new Section
                {
                    Name = name,
                    Rva = rva,
                    VirtualAddress = pe.ImageBase + rva,
                    VirtualSize = vsize,
                    RawSize = BitConverter.ToUInt32(data, st + 16),
                    RawOffset = BitConverter.ToUInt32(data, st + 20),
                    Characteristics = BitConverter.ToUInt32(data, st + 36),
                });
            }
            return pe;
        }
        catch
        {
            return null;
        }
    }

    public Section? FindSection(string name) => Sections.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public Section? SectionAt(uint va) => Sections.FirstOrDefault(s => va >= s.VirtualAddress && va < s.End);

    /// <summary>
    /// The basefile is a memory image (section data lives at its RVA). Rewrites the section table so that
    /// PointerToRawData = VirtualAddress and SizeOfRawData = VirtualSize, which lets IDA / Ghidra / PE tools load it directly.
    /// </summary>
    public static void NormalizeRawPointers(byte[] data)
    {
        var pe = TryParse(data);
        if (pe == null) return;
        int st = pe.SectionTableOffset;
        foreach (var s in pe.Sections)
        {
            uint size = Math.Min(s.VirtualSize, (uint)Math.Max(0, data.Length - (int)Math.Min(s.Rva, (uint)data.Length)));
            BitConverter.TryWriteBytes(data.AsSpan(st + 16), size);
            BitConverter.TryWriteBytes(data.AsSpan(st + 20), s.Rva);
            st += 40;
        }
    }
}
