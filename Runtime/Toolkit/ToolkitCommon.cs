using System.Buffers.Binary;
using System.Globalization;
using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>Services shared by every runtime tool (implemented by <see cref="RuntimeToolkitPage"/>).</summary>
public interface IToolHost
{
    IAppHost App { get; }
    IReadOnlyList<OffsetManager.OffsetEntry> Database { get; }
    SymbolMap Symbols { get; }
    void Disassemble(uint address);
    void ScanRange(uint start, uint size);
    void Graph(uint address, LiveType type, string label);
    void PointerChain(uint baseAddress, IEnumerable<uint> offsets);
    /// <summary>Offsets discovered at runtime for the running game (Runtime Offsets page / library pulls).</summary>
    IReadOnlyList<DiscoveredOffset> RuntimeOffsets { get; }
    /// <summary>Opens Active Memory at an address.</summary>
    void ActiveMemory(uint address);
}

/// <summary>Base class for a tool: a themed page with helpers for rows, inputs and connection checks.</summary>
public abstract class ToolBase : UserControl
{
    protected readonly IToolHost Host;
    protected IAppHost App => Host.App;

    protected ToolBase(IToolHost host)
    {
        Host = host;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        DoubleBuffered = true;
    }

    public abstract string Key { get; }
    public abstract string Title { get; }
    public abstract string Glyph { get; }
    public abstract string Description { get; }

    /// <summary>The tool became visible – start timers here.</summary>
    public virtual void OnShown() { }
    /// <summary>The tool was hidden – stop timers here so hidden tools never poll the console.</summary>
    public virtual void OnHidden() { }
    public virtual void OnConnectionChanged(bool connected) { }

    protected XBDMClient? Client => App.Client is { IsConnected: true } c ? c : null;

    protected XBDMClient? RequireClient()
    {
        var c = Client;
        if (c == null) App.Toast("Connect to the console first", ToastHost.Kind.Warning);
        return c;
    }

    protected static FlowLayoutPanel Row(params Control[] controls)
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 6) };
        f.Controls.AddRange(controls);
        return f;
    }

    protected static Label L(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Theme.TextMuted, Font = Theme.UISemibold, BackColor = Color.Transparent,
        Margin = new Padding(0, 8, 6, 0), Tag = "keep",
    };

    protected static TextBox T(string text, int width, string placeholder = "", bool mono = true) => new()
    {
        Text = text, Width = width, PlaceholderText = placeholder, Font = mono ? Theme.Mono : Theme.UI, Margin = new Padding(0, 4, 10, 0),
    };

    protected static IconButton B(string text, string glyph, ButtonVariant v, EventHandler click, string? tip = null)
    {
        var b = IconButton.Create(text, glyph, v, click, tip);
        b.Height = 30;
        b.Margin = new Padding(0, 2, 6, 0);
        return b;
    }

    protected static ComboBox C(int width, params object[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(0, 4, 10, 0) };
        c.Items.AddRange(items);
        if (items.Length > 0) c.SelectedIndex = 0;
        return c;
    }

    protected static DataGridView Grid(params (string Header, int Width)[] cols)
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        };
        foreach (var (h, w) in cols)
        {
            int i = g.Columns.Add(h.Replace(" ", ""), h);
            g.Columns[i].Width = w;
        }
        if (g.Columns.Count > 0) g.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        Theme.StyleGrid(g);
        return g;
    }

    protected static Panel Card(Control inner, Padding? padding = null)
    {
        var p = Ui.Card(DockStyle.Fill, 0, padding ?? new Padding(1));
        p.Controls.Add(inner);
        return p;
    }

    /// <summary>Parses "0x82000000", "82000000" or an offset name from the database / runtime offsets.</summary>
    protected bool TryAddress(string text, out uint address)
    {
        address = 0;
        var t = text.Trim();
        if (t.Length == 0) return false;
        var hex = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t;
        if (uint.TryParse(hex.Replace("_", "").Replace(" ", ""), NumberStyles.HexNumber, null, out address)) return true;
        if (Host.Symbols.Resolve(t) is { } a) { address = a; return true; }
        return false;
    }

    protected uint? AddressOrToast(TextBox box)
    {
        if (TryAddress(box.Text, out var a)) return a;
        App.Toast($"“{box.Text}” is not an address or a known offset name", ToastHost.Kind.Warning);
        box.Focus();
        return null;
    }

    protected static uint ParseNumber(string s)
    {
        s = s.Trim();
        if (s.StartsWith("-")) return unchecked((uint)-(int)ParseNumber(s[1..]));
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return uint.Parse(s[2..], NumberStyles.HexNumber);
        return uint.Parse(s, CultureInfo.InvariantCulture);
    }
}

/// <summary>Value types understood by the live tools (Xbox 360 memory is big-endian).</summary>
public enum LiveType { Int32, UInt32, Float, Int16, Byte, Int64, Double }

public static class LiveValue
{
    public static int Size(LiveType t) => t switch
    {
        LiveType.Byte => 1, LiveType.Int16 => 2, LiveType.Int64 or LiveType.Double => 8, _ => 4,
    };

    public static double ToDouble(LiveType t, ReadOnlySpan<byte> b) => t switch
    {
        LiveType.Byte => b[0],
        LiveType.Int16 => BinaryPrimitives.ReadInt16BigEndian(b),
        LiveType.Int32 => BinaryPrimitives.ReadInt32BigEndian(b),
        LiveType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(b),
        LiveType.Float => BinaryPrimitives.ReadSingleBigEndian(b),
        LiveType.Int64 => BinaryPrimitives.ReadInt64BigEndian(b),
        _ => BinaryPrimitives.ReadDoubleBigEndian(b),
    };

    public static string Format(LiveType t, ReadOnlySpan<byte> b) => t switch
    {
        LiveType.Float => BinaryPrimitives.ReadSingleBigEndian(b).ToString("0.#####", CultureInfo.InvariantCulture),
        LiveType.Double => BinaryPrimitives.ReadDoubleBigEndian(b).ToString("0.#########", CultureInfo.InvariantCulture),
        LiveType.UInt32 => $"{BinaryPrimitives.ReadUInt32BigEndian(b)}  (0x{BinaryPrimitives.ReadUInt32BigEndian(b):X8})",
        LiveType.Int32 => $"{BinaryPrimitives.ReadInt32BigEndian(b)}  (0x{BinaryPrimitives.ReadUInt32BigEndian(b):X8})",
        _ => ToDouble(t, b).ToString(CultureInfo.InvariantCulture),
    };

    public static byte[] Encode(LiveType t, string text)
    {
        text = text.Trim();
        var b = new byte[Size(t)];
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        long ParseL() => hex ? long.Parse(text[2..], NumberStyles.HexNumber) : long.Parse(text, CultureInfo.InvariantCulture);
        switch (t)
        {
            case LiveType.Byte: b[0] = unchecked((byte)ParseL()); break;
            case LiveType.Int16: BinaryPrimitives.WriteInt16BigEndian(b, unchecked((short)ParseL())); break;
            case LiveType.Int32: case LiveType.UInt32: BinaryPrimitives.WriteUInt32BigEndian(b, unchecked((uint)ParseL())); break;
            case LiveType.Int64: BinaryPrimitives.WriteInt64BigEndian(b, ParseL()); break;
            case LiveType.Float: BinaryPrimitives.WriteSingleBigEndian(b, float.Parse(text, CultureInfo.InvariantCulture)); break;
            case LiveType.Double: BinaryPrimitives.WriteDoubleBigEndian(b, double.Parse(text, CultureInfo.InvariantCulture)); break;
        }
        return b;
    }

    public static LiveType Parse(string s) => Enum.TryParse<LiveType>(s, true, out var t) ? t : LiveType.Int32;
}

/// <summary>
/// Address ↔ name lookup over the offset database and runtime offsets. Rebuilt lazily when the sources change,
/// with a sorted array for "nearest symbol below" queries (used to annotate disassembly).
/// </summary>
public sealed class SymbolMap
{
    private readonly Func<IEnumerable<(string Name, uint Address)>> _source;
    private Dictionary<uint, string> _byAddress = new();
    private Dictionary<string, uint> _byName = new(StringComparer.OrdinalIgnoreCase);
    private uint[] _sorted = Array.Empty<uint>();
    private int _signature = -1;

    public SymbolMap(Func<IEnumerable<(string Name, uint Address)>> source) => _source = source;

    private long _checkedAt;

    private void Ensure()
    {
        // look for source changes at most every 2 s – lookups happen per disassembly row / per frame
        long now = Environment.TickCount64;
        if (_signature != -1 && now - _checkedAt < 2000) return;
        _checkedAt = now;
        var items = _source().Where(x => x.Address >= 0x80000000).ToList();
        int sig = items.Count ^ (items.Count > 0 ? (int)items[0].Address ^ (int)items[^1].Address : 0);
        if (sig == _signature) return;
        _signature = sig;
        var byAddr = new Dictionary<uint, string>();
        var byName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, a) in items)
        {
            byAddr.TryAdd(a, n);
            byName.TryAdd(n, a);
            byName.TryAdd(Scripting.ScriptIde.NormalizeName(n), a);
        }
        _byAddress = byAddr;
        _byName = byName;
        _sorted = byAddr.Keys.OrderBy(x => x).ToArray();
    }

    public string? NameAt(uint address) { Ensure(); return _byAddress.GetValueOrDefault(address); }

    public uint? Resolve(string name) { Ensure(); return _byName.TryGetValue(name.Trim(), out var a) ? a : null; }

    /// <summary>"Name+0x1C" for the closest symbol at or below <paramref name="address"/> (within 64 KB).</summary>
    public string? Describe(uint address)
    {
        Ensure();
        if (_sorted.Length == 0) return null;
        int i = Array.BinarySearch(_sorted, address);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        uint a = _sorted[i];
        uint d = address - a;
        if (d > 0x10000) return null;
        return d == 0 ? _byAddress[a] : $"{_byAddress[a]}+0x{d:X}";
    }
}
