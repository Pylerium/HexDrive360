namespace MemoryEditor;

/// <summary>
/// Xbox 360 virtual address map knowledge shared by every tool that polls or writes memory.
///
///   0x00000000–0x3FFFFFFF   unmapped for titles (reads fail)
///   0x40000000–0x7FFFFFFF   title heap (game allocations)                         – safe
///   0x80000000–0x81FFFFFF   kernel / XAM / system images                          – reads are fine, writes crash
///   0x82000000–0x8FFFFFFF   title executable image (code, .rdata, .data, .bss)    – safe
///   0x90000000–0xFFFFFFFF   system DLLs, physical / GPU / uncached mappings       – polling can hang XBDM
/// </summary>
public static class MemorySafety
{
    public static bool IsTitleHeap(uint a) => a >= 0x40000000 && a < 0x80000000;
    public static bool IsTitleImage(uint a) => a >= 0x82000000 && a < 0x90000000;
    public static bool IsGameMemory(uint a) => IsTitleHeap(a) || IsTitleImage(a);

    /// <summary>True when every byte of the range is in game memory.</summary>
    public static bool IsGameRange(uint start, uint size)
    {
        ulong end = (ulong)start + Math.Max(size, 1) - 1;
        if (end > uint.MaxValue) return false;
        // both ends inside the same safe area (the areas are contiguous, the gap 0x80000000–0x81FFFFFF is not)
        return (IsTitleHeap(start) && IsTitleHeap((uint)end)) || (IsTitleImage(start) && IsTitleImage((uint)end));
    }

    public static string Describe(uint a) => a switch
    {
        < 0x40000000 => "unmapped / low memory",
        < 0x80000000 => "title heap",
        < 0x82000000 => "kernel / system image",
        < 0x90000000 => "title image",
        < 0xA0000000 => "system module / physical mapping",
        _ => "physical / GPU / uncached memory",
    };

    /// <summary>
    /// Asks before a tool starts polling (or writing) memory outside the game's own memory. Returns true when the
    /// range is safe or the user explicitly accepted the risk.
    /// </summary>
    public static bool Confirm(IWin32Window? owner, uint start, uint size, string action)
    {
        if (IsGameRange(start, size)) return true;
        uint bad = IsGameMemory(start) ? (uint)Math.Min(uint.MaxValue, (ulong)start + size - 1) : start;
        return MessageBox.Show(owner,
                   $"0x{start:X8}–0x{(ulong)start + size:X8} reaches {Describe(bad)} (0x{bad:X8}), outside the running game's memory.\n\n" +
                   $"{action} there can hang the debug link or crash the console. Continue anyway?",
                   "Outside game memory", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    /// <summary>
    /// Parses addresses and numbers the way people type them: 0x82000000, 82000000, 82000000h, $82000000,
    /// "0x8200_0000", "0x82000000 (name)" and surrounding whitespace. Bare digits are hex when <paramref name="hexDefault"/>.
    /// </summary>
    public static bool TryParseAddress(string? text, out uint value, bool hexDefault = true)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        int cut = s.IndexOfAny(new[] { ' ', '\t', '(', ',', ';', '|' });
        if (cut > 0) s = s[..cut];
        s = s.Replace("_", "").Replace("`", "");
        bool hex = hexDefault;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { s = s[2..]; hex = true; }
        else if (s.StartsWith('$') || s.StartsWith('#')) { s = s[1..]; hex = true; }
        else if (s.EndsWith('h') || s.EndsWith('H')) { s = s[..^1]; hex = true; }
        if (s.Length == 0) return false;
        return hex
            ? uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value)
            : uint.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary><see cref="TryParseAddress"/> that throws a readable error.</summary>
    public static uint ParseAddress(string? text, string field = "Address", bool hexDefault = true)
        => TryParseAddress(text, out var v, hexDefault) ? v
           : throw new FormatException($"{field}: “{text?.Trim()}” is not a valid address (use hex like 0x82000000)");
}
