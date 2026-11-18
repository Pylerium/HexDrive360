using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>
/// Hosts the runtime tools. Tools are created on first use; only the visible tool polls the console (timers are
/// stopped when a tool or the whole page is hidden), so the toolkit never competes with other pages for the link.
/// </summary>
public sealed class RuntimeToolkitPage : UserControl, IToolHost
{
    private sealed record ToolDef(string Key, string Title, string Glyph, string Description, Func<ToolBase> Create);

    private readonly List<ToolDef> _defs = new();
    private readonly Dictionary<string, ToolBase> _tools = new();
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 54, BorderStyle = BorderStyle.None, IntegralHeight = false };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(14, 0, 0, 0), Tag = "keep" };
    private readonly Func<IReadOnlyList<OffsetManager.OffsetEntry>> _database;
    private ToolBase? _current;
    private bool _pageVisible;

    public IAppHost App { get; }
    public SymbolMap Symbols { get; }
    public IReadOnlyList<OffsetManager.OffsetEntry> Database => _database();

    private readonly Func<IReadOnlyList<DiscoveredOffset>> _runtime;
    public IReadOnlyList<DiscoveredOffset> RuntimeOffsets => _runtime();

    public RuntimeToolkitPage(IAppHost app, Func<IReadOnlyList<OffsetManager.OffsetEntry>> database, Func<IEnumerable<(string Name, uint Address)>> symbols,
                              Func<IReadOnlyList<DiscoveredOffset>>? runtime = null)
    {
        App = app;
        _database = database;
        _runtime = runtime ?? (() => Array.Empty<DiscoveredOffset>());
        Symbols = new SymbolMap(symbols);
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;

        void Def(string key, string title, string glyph, string desc, Func<ToolBase> create) => _defs.Add(new ToolDef(key, title, glyph, desc, create));
        Def("active", "Active Memory", Theme.Glyph.Memory, "Live view & edit of the running game", () => new ActiveMemoryTool(this));
        Def("dynamic", "Dynamic Modder", Theme.Glyph.Wand, "Find variables by what you do in game", () => new DynamicModderTool(this));
        Def("gsc", "Live GSC", Theme.Glyph.Document, "Write, compile & inject GSC scripts", () => new LiveGscTool(this));
        Def("mods", "Mod Toggles", Theme.Glyph.Bolt, "On/Off patches as live switches", () => new ModTogglesTool(this));
        Def("chain", "Pointer Chain", Theme.Glyph.Link, "Follow multi-level pointers live", () => new PointerChainTool(this));
        Def("graph", "Value Graph", Theme.Glyph.Analyze, "Plot live values over time", () => new ValueGraphTool(this));
        Def("disasm", "Disassembler", Theme.Glyph.Code, "Live PPC code, follow & patch", () => new DisassemblerTool(this));
        Def("scan", "Pattern Scan", Theme.Glyph.Search, "Find bytes, text or values", () => new PatternScanTool(this));
        Def("map", "Memory Map", Theme.Glyph.List, "Regions, modules, sections, dumps", () => new MemoryMapTool(this));
        Def("threads", "Threads", Theme.Glyph.Cpu, "Suspend, resume, pause the game", () => new ThreadsTool(this));
        Def("files", "File Manager", Theme.Glyph.Folder, "Browse, upload, download console files", () => new FileManagerTool(this));
        Def("console", "Console", Theme.Glyph.Terminal, "Info, restart, XBDM terminal", () => new ConsoleTool(this));

        var side = _side = Ui.Card(DockStyle.Left, 0, new Padding(4, 8, 4, 4));
        side.Width = 236;
        _list.MouseMove += (_, e) =>
        {
            int i = _list.IndexFromPoint(e.Location);
            string? tip = _compact && i >= 0 && i < _defs.Count ? $"{_defs[i].Title} – {_defs[i].Description}" : null;
            if (tip != _lastTip) { _lastTip = tip; _tip.SetToolTip(_list, tip); }
        };
        Resize += (_, _) => UpdateCompact();
        _list.BackColor = Theme.Surface;
        Theme.Exclude(_list);
        Theme.DarkScrollbars(_list);
        _list.Items.AddRange(_defs.Select(d => (object)d.Title).ToArray());
        _list.DrawItem += DrawTool;
        _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedIndex >= 0) Show(_defs[_list.SelectedIndex].Key); };
        side.Controls.Add(_list);

        Controls.Add(_host);
        Controls.Add(side);
        Theme.Apply(this);
        _list.SelectedIndex = 0;
    }

    private Panel? _side;
    private bool _compact;
    private string? _lastTip;
    private readonly ToolTip _tip = new() { InitialDelay = 200 };

    /// <summary>Below ~1250 px the tool list collapses to an icon rail so the tool itself keeps its space.</summary>
    private void UpdateCompact()
    {
        bool compact = Width > 0 && Width < 1250;
        if (compact == _compact || _side == null) return;
        _compact = compact;
        _side.Width = compact ? 62 : 236;
        _list.Invalidate();
    }

    private void DrawTool(object? sender, DrawItemEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, e.Bounds);
        if (e.Index < 0 || e.Index >= _defs.Count) return;
        var d = _defs[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 3, e.Bounds.Width - 8, e.Bounds.Height - 6);
        if (sel)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Theme.RoundRect(r, 7);
            using var b = new SolidBrush(Theme.Blend(Theme.Surface2, Theme.Accent, 0.2f));
            g.FillPath(b, path);
        }
        if (_compact)
        {
            TextRenderer.DrawText(g, d.Glyph, Theme.Icon(14F), r, sel ? Theme.Accent : Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }
        TextRenderer.DrawText(g, d.Glyph, Theme.Icon(13F), new Point(r.X + 12, r.Y + 14), sel ? Theme.Accent : Theme.TextMuted, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, d.Title, sel ? Theme.UISemibold : Theme.UI, new Rectangle(r.X + 42, r.Y + 7, r.Width - 46, 20), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, d.Description, Theme.UISmall, new Rectangle(r.X + 42, r.Y + 26, r.Width - 46, 18), Theme.TextDim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    public ToolBase Show(string key)
    {
        if (!_tools.TryGetValue(key, out var tool))
        {
            var def = _defs.First(d => d.Key == key);
            tool = def.Create();
            tool.Visible = false;
            Theme.Apply(tool);
            _tools[key] = tool;
            _host.Controls.Add(tool);
        }
        if (_current != tool)
        {
            if (_current != null) { _current.OnHidden(); _current.Visible = false; }
            _current = tool;
            tool.Visible = true;
            tool.BringToFront();
            if (_pageVisible) tool.OnShown();
        }
        int idx = _defs.FindIndex(d => d.Key == key);
        if (_list.SelectedIndex != idx) _list.SelectedIndex = idx;
        return tool;
    }

    private T Open<T>(string key) where T : ToolBase
    {
        App.Navigate("toolkit");
        return (T)Show(key);
    }

    /// <summary>The shell calls these when the Runtime Tools page is shown / left.</summary>
    public void OnPageShown() { _pageVisible = true; _current?.OnShown(); }
    public void OnPageHidden() { _pageVisible = false; _current?.OnHidden(); }

    public void OnConnectionChanged(bool connected)
    {
        if (InvokeRequired) { BeginInvoke(() => OnConnectionChanged(connected)); return; }
        foreach (var t in _tools.Values) t.OnConnectionChanged(connected);
    }

    // ── IToolHost ──
    public void Disassemble(uint address) => _ = Open<DisassemblerTool>("disasm").ShowAsync(address);
    public void ScanRange(uint start, uint size) => Open<PatternScanTool>("scan").SetRange(start, size);
    public void Graph(uint address, LiveType type, string label) => Open<ValueGraphTool>("graph").Add(address, type, label);
    public void PointerChain(uint baseAddress, IEnumerable<uint> offsets) => Open<PointerChainTool>("chain").LoadChain(baseAddress, offsets);
    public void ActiveMemory(uint address) => Open<ActiveMemoryTool>("active").Open(address);
}
