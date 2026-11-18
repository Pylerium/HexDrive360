using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using MemoryEditor.Library;
using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>
/// Active Memory: a live, editable view of the running game. It follows whatever is running (re-syncing on game /
/// title-update changes), lists the game's modules, sections, regions and every known offset, shows memory in a live
/// hex editor with a data inspector, keeps a live value table of all offsets (editable, freezable) and logs every
/// change – all of it wired into the recorder, watch list, graph, disassembler, pointer chain and scanner.
/// </summary>
public sealed partial class ActiveMemoryTool : ToolBase
{
    /// <summary>One known location of the running game (from the offset database or runtime analysis).</summary>
    private sealed class Item
    {
        public string Name = "";
        public string Category = "";
        public string Source = "";
        public uint Address;
        public LiveType Type = LiveType.Int32;
        public string Kind = "Value";       // Value · Code · Patch · String
        public int Length = 4;
        public string? Patch;
        public string Display = "";
        public DateTime ChangedAt;
        public byte[]? Last;
    }

    private sealed record Change(DateTime Time, uint Address, string Symbol, string Old, string New);

    // ── header ──
    private readonly Label _game = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 13F), ForeColor = Theme.Text, BackColor = Color.Transparent, Tag = "keep", Margin = new Padding(0, 4, 12, 0), UseMnemonic = false };
    private readonly Label _gameMeta = new() { AutoSize = true, Font = Theme.UISmall, ForeColor = Theme.TextMuted, BackColor = Color.Transparent, Tag = "keep", Margin = new Padding(0, 10, 12, 0), UseMnemonic = false };
    private readonly TextBox _goto = T("", 190, "Address or offset name");
    private readonly ComboBox _rate = C(80, "100 ms", "250 ms", "500 ms", "1 s", "2 s");
    private readonly CheckBox _live = new() { Text = "Live", Checked = true, AutoSize = true, Margin = new Padding(0, 7, 10, 0) };
    private readonly IconButton _btnPull;

    // ── explorer ──
    private readonly TextBox _search = T("", 0, "Search offsets, modules, addresses", mono: false);
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HideSelection = false, ShowLines = false, FullRowSelect = true, ItemHeight = 22 };

    // ── center ──
    private readonly HexView _hex = new() { Dock = DockStyle.Fill };
    private readonly Label _hexInfo = new() { Dock = DockStyle.Top, Height = 24, Font = Theme.UISmall, ForeColor = Theme.TextMuted, BackColor = Theme.Bg, Tag = "keep", TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
    private readonly DataGridView _values = Grid(("Name", 240), ("Category", 150), ("Address", 100), ("Type", 70), ("Value", 200), ("Frozen", 60));
    private readonly TextBox _valuesFilter = T("", 240, "Filter live values", mono: false);
    private readonly ComboBox _valuesScope = C(170, "All offsets", "In hex view", "Values only", "Patches only", "Changed recently", "Frozen");
    private readonly DataGridView _changes = Grid(("Time", 90), ("Address", 100), ("Symbol", 260), ("Old", 120), ("New", 120));
    private readonly IconButton _tabValues, _tabChanges;
    private readonly Panel _valuesPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg, Tag = "keep" };
    private readonly Panel _changesPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg, Tag = "keep", Visible = false };

    // ── inspector ──
    private readonly DataGridView _inspector = Grid(("Type", 90), ("Value", 170));

    // ── state ──
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _syncTimer = new() { Interval = 3000 };
    private readonly Stack<uint> _back = new();
    private readonly List<Change> _changeLog = new();
    private readonly List<uint> _pinned = new();
    private List<Item> _items = new();
    private List<Item> _view = new();
    private Dictionary<uint, Item> _byAddress = new();
    private List<XBDMClient.ModuleInfo> _modules = new();
    private Dictionary<string, List<XBDMClient.SectionInfo>> _sections = new();
    private List<XBDMClient.MemoryRegion> _regions = new();
    private string _titleKey = "";
    private int _catalogSignature = -1;
    private bool _reading, _syncing, _valuesReading;
    private readonly Dictionary<uint, LiveType> _typeOverrides = new();

    public override string Key => "active";
    public override string Title => "Active Memory";
    public override string Glyph => Theme.Glyph.Memory;
    public override string Description => "Live, editable view of everything in the running game";

    private FreezeService? Freezer => App.Freezer;

    public ActiveMemoryTool(IToolHost host) : base(host)
    {
        _btnPull = B("Pull offsets", Theme.Glyph.Download, ButtonVariant.Primary, (_, _) => App.PullRunningGame(), "Map every offset of the running game (Game Library pull)");
        _tabValues = B("Live values", Theme.Glyph.List, ButtonVariant.Primary, (_, _) => ShowBottom(false));
        _tabChanges = B("Changes", Theme.Glyph.Record, ButtonVariant.Ghost, (_, _) => ShowBottom(true));

        BuildLayout();
        WireEvents();
        _rate.SelectedIndex = 1;
        _game.Text = "No game";
        _gameMeta.Text = "Connect to the console – the running game is picked up automatically";
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  layout
    // ═════════════════════════════════════════════════════════════════════════

    private void BuildLayout()
    {
        // header
        _btnExplorer = B("Explorer", Theme.Glyph.List, ButtonVariant.Primary, (_, _) => { _userExplorer = !(_explorer?.Visible ?? true); ApplyPanels(); }, "Show / hide the explorer (modules, offsets, regions)");
        _btnInspector = B("Inspector", Theme.Glyph.Info, ButtonVariant.Primary, (_, _) => { _userInspector = !(_inspector2?.Visible ?? true); ApplyPanels(); }, "Show / hide the value inspector");
        var header = Row(_game, _gameMeta, _btnPull,
            B("Sync", Theme.Glyph.Refresh, ButtonVariant.Neutral, async (_, _) => await SyncAsync(true), "Re-read modules, regions and offsets of the running game"),
            L("Go to"), _goto,
            B("", Theme.Glyph.Play, ButtonVariant.Neutral, (_, _) => GoFromBox(), "Go (Enter)"),
            B("", Theme.Glyph.Refresh, ButtonVariant.Ghost, (_, _) => Back(), "Back"),
            _live, _rate, _btnExplorer, _btnInspector);

        // explorer (left)
        var explorer = _explorer = Ui.Card(DockStyle.Left, 0, new Padding(6, 8, 4, 4));
        explorer.Width = 270;
        _search.Dock = DockStyle.Top;
        _tree.BackColor = Theme.Surface;
        _tree.ForeColor = Theme.Text;
        Theme.DarkScrollbars(_tree);
        explorer.Controls.Add(_tree);
        explorer.Controls.Add(Ui.Spacer(DockStyle.Top, 6));
        explorer.Controls.Add(_search);

        // inspector (right)
        var inspector = _inspector2 = Ui.Card(DockStyle.Right, 0, new Padding(1));
        inspector.Width = 290;
        _inspector.ReadOnly = true;
        _inspector.Columns[1].DefaultCellStyle.Font = Theme.Mono;
        var inspActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, BackColor = Theme.Surface, Padding = new Padding(6, 6, 0, 6) };
        inspActions.Controls.AddRange(new Control[]
        {
            B("Freeze", Theme.Glyph.Snowflake, ButtonVariant.Neutral, (_, _) => FreezeCursor(), "Freeze the selected inspector value (or 4 bytes) at the cursor"),
            B("Watch", Theme.Glyph.Pin, ButtonVariant.Neutral, (_, _) => App.AddWatch(_hex.CursorAddress, NameFor(_hex.CursorAddress))),
            B("Graph", Theme.Glyph.Analyze, ButtonVariant.Neutral, (_, _) => Host.Graph(_hex.CursorAddress, InspectorType(), NameFor(_hex.CursorAddress))),
            B("Pin", Theme.Glyph.Pin, ButtonVariant.Ghost, (_, _) => Pin(_hex.CursorAddress), "Pin the cursor address in the explorer"),
            B("Code", Theme.Glyph.Code, ButtonVariant.Ghost, (_, _) => Host.Disassemble(_hex.CursorAddress & ~3u), "Disassemble at the cursor"),
            B("Follow ptr", Theme.Glyph.Link, ButtonVariant.Ghost, (_, _) => FollowPointer(), "Jump to the 32-bit pointer at the cursor"),
            B("Chain", Theme.Glyph.Link, ButtonVariant.Ghost, (_, _) => Host.PointerChain(_hex.CursorAddress, Array.Empty<uint>()), "Open a pointer chain from the cursor"),
        });
        var inspTitle = new Label { Text = "INSPECTOR", Dock = DockStyle.Top, Height = 26, Font = Theme.UISmall, ForeColor = Theme.TextDim, BackColor = Theme.Surface, Padding = new Padding(10, 6, 0, 0), Tag = "keep" };
        inspector.Controls.Add(_inspector);
        inspector.Controls.Add(inspActions);
        inspector.Controls.Add(inspTitle);

        // hex (center top)
        var hexActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0) };
        hexActions.Controls.AddRange(new Control[]
        {
            B("Record view", Theme.Glyph.Record, ButtonVariant.Purple, (_, _) => App.MonitorRegion(_hex.BaseAddress, (uint)_hex.ViewLength, $"view @ 0x{_hex.BaseAddress:X8}"), "Monitor the visible range in the Memory Recorder"),
            B("Record selection", Theme.Glyph.Record, ButtonVariant.Neutral, (_, _) => App.MonitorRegion(_hex.SelectionStart, Math.Max(4, _hex.SelectionLength), $"selection @ 0x{_hex.SelectionStart:X8}")),
            B("Write…", Theme.Glyph.Save, ButtonVariant.Warning, async (_, _) => await WriteBytesPromptAsync(), "Write a hex string at the cursor"),
            B("Scan here", Theme.Glyph.Search, ButtonVariant.Neutral, (_, _) => Host.ScanRange(_hex.BaseAddress & 0xFFFF0000, 0x10000), "Pattern-scan the surrounding 64 KB"),
            B("Memory page", Theme.Glyph.Memory, ButtonVariant.Ghost, (_, _) => App.ViewMemory(_hex.CursorAddress), "Open in the classic Memory page"),
            B("Copy", Theme.Glyph.Copy, ButtonVariant.Ghost, (_, _) => CopySelection()),
        });
        var hexCard = Card(_hex);
        var hexHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Tag = "keep" };
        hexHost.Controls.Add(hexCard);
        hexHost.Controls.Add(hexActions);
        hexHost.Controls.Add(_hexInfo);

        // values / changes (center bottom)
        _values.ReadOnly = false;
        _values.Columns[4].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;   // the value gets the spare width
        _values.Columns[5].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        _values.Columns[5].Width = 80;
        foreach (DataGridViewColumn col in _values.Columns) col.ReadOnly = col.Index != 4;
        _values.Columns[2].DefaultCellStyle.Font = Theme.Mono;
        _values.Columns[4].DefaultCellStyle.Font = Theme.Mono;
        _changes.Columns[1].DefaultCellStyle.Font = Theme.Mono;
        _changes.Columns[3].DefaultCellStyle.Font = Theme.Mono;
        _changes.Columns[4].DefaultCellStyle.Font = Theme.Mono;

        var bottomBar = Row(_tabValues, _tabChanges, _valuesFilter, _valuesScope,
            B("Record changes", Theme.Glyph.Record, ButtonVariant.Ghost, (_, _) => RecordChangedRange(), "Record the address range covered by the change log"),
            B("Clear log", Theme.Glyph.Clear, ButtonVariant.Ghost, (_, _) => { _changeLog.Clear(); _changes.RowCount = 0; }));
        _valuesPage.Controls.Add(Card(_values));
        _changesPage.Controls.Add(Card(_changes));
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 300, BackColor = Theme.Bg, Padding = new Padding(0, 8, 0, 0), Tag = "keep" };
        bottom.Controls.Add(_valuesPage);
        bottom.Controls.Add(_changesPage);
        bottom.Controls.Add(bottomBar);
        var split = new Splitter { Dock = DockStyle.Bottom, Height = 6, BackColor = Theme.Bg, MinSize = 120, MinExtra = 200 };

        var center = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(10, 0, 10, 0), Tag = "keep" };
        center.Controls.Add(hexHost);
        center.Controls.Add(split);
        center.Controls.Add(bottom);

        _splitLeft = new Splitter { Dock = DockStyle.Left, Width = 5, BackColor = Theme.Bg, MinSize = 180, MinExtra = 480 };
        _splitRight = new Splitter { Dock = DockStyle.Right, Width = 5, BackColor = Theme.Bg, MinSize = 220, MinExtra = 480 };
        Controls.Add(center);
        Controls.Add(_splitRight);
        Controls.Add(inspector);
        Controls.Add(_splitLeft);
        Controls.Add(explorer);
        Controls.Add(header);
        Resize += (_, _) => ApplyPanels();
    }

    private Panel? _explorer, _inspector2;
    private Splitter? _splitLeft, _splitRight;
    private IconButton? _btnExplorer, _btnInspector;
    private bool? _userExplorer, _userInspector;   // null = automatic (by width)

    /// <summary>
    /// The hex editor needs ~640 px. Side panels hide automatically when the window is too narrow for all three
    /// (inspector first, then explorer); the header buttons override the automatic choice.
    /// </summary>
    private void ApplyPanels()
    {
        if (_explorer == null || _inspector2 == null) return;
        int w = Width;
        bool showInspector = _userInspector ?? w >= 1180;
        bool showExplorer = _userExplorer ?? w >= 900;
        SuspendLayout();
        _inspector2.Visible = _splitRight!.Visible = showInspector;
        _explorer.Visible = _splitLeft!.Visible = showExplorer;
        ResumeLayout(true);
        if (_btnExplorer != null) _btnExplorer.Variant = showExplorer ? ButtonVariant.Primary : ButtonVariant.Ghost;
        if (_btnInspector != null) _btnInspector.Variant = showInspector ? ButtonVariant.Primary : ButtonVariant.Ghost;
    }

    private void ShowBottom(bool changes)
    {
        _changesPage.Visible = changes;
        _valuesPage.Visible = !changes;
        _tabValues.Variant = changes ? ButtonVariant.Ghost : ButtonVariant.Primary;
        _tabChanges.Variant = changes ? ButtonVariant.Primary : ButtonVariant.Ghost;
        _valuesFilter.Visible = _valuesScope.Visible = !changes;
        if (changes) { _changes.RowCount = 0; _changes.RowCount = _changeLog.Count; }
    }

    private void WireEvents()
    {
        _timer.Tick += async (_, _) => { await RefreshHexAsync(); await RefreshValuesAsync(); };
        _syncTimer.Tick += async (_, _) => await CheckTitleAsync();
        _live.CheckedChanged += (_, _) => UpdateTimers();
        _rate.SelectedIndexChanged += (_, _) => _timer.Interval = _rate.SelectedIndex switch { 0 => 100, 1 => 250, 2 => 500, 3 => 1000, _ => 2000 };
        _goto.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; GoFromBox(); } };

        _hex.SymbolAt = a => _byAddress.TryGetValue(a, out var it) ? it.Name : null;
        _hex.IsFrozen = a => Freezer?.Covers(a) == true;
        _hex.Describe = a => Host.Symbols.Describe(a);
        _hex.ViewChanged += async () => await RefreshHexAsync();
        _hex.CaretMoved += UpdateInspector;
        _hex.ByteEdited += async (a, v) => await WriteAsync(a, new[] { v }, quiet: true);

        _tree.BeforeExpand += (_, e) => { if (e.Node != null) ExpandNode(e.Node); };
        _tree.NodeMouseDoubleClick += (_, e) => OpenNode(e.Node);
        _tree.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && _tree.SelectedNode != null) { e.SuppressKeyPress = true; OpenNode(_tree.SelectedNode); } };
        _tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) { _tree.SelectedNode = e.Node; ShowNodeMenu(e.Node, e.Location); } };
        _search.TextChanged += (_, _) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); BuildTree(); };

        _values.VirtualMode = true;
        _values.CellValueNeeded += ValuesCellNeeded;
        _values.CellValuePushed += async (_, e) => await ValuePushedAsync(e);
        _values.CellFormatting += ValuesFormatting;
        _values.CellClick += (_, e) => { if (e.ColumnIndex == 5 && e.RowIndex >= 0 && e.RowIndex < _view.Count) ToggleFreeze(_view[e.RowIndex]); };
        _values.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < _view.Count && e.ColumnIndex != 4) Open(_view[e.RowIndex].Address); };
        _values.ContextMenuStrip = BuildValuesMenu();
        _values.DataError += (_, e) => e.ThrowException = false;
        _valuesFilter.TextChanged += (_, _) => ApplyValuesFilter();
        _valuesScope.SelectedIndexChanged += (_, _) => ApplyValuesFilter();

        _changes.VirtualMode = true;
        _changes.CellValueNeeded += (_, e) =>
        {
            int i = _changeLog.Count - 1 - e.RowIndex; // newest first
            if (i < 0 || i >= _changeLog.Count) return;
            var c = _changeLog[i];
            e.Value = e.ColumnIndex switch { 0 => c.Time.ToString("HH:mm:ss.f"), 1 => $"0x{c.Address:X8}", 2 => c.Symbol, 3 => c.Old, _ => c.New };
        };
        _changes.CellDoubleClick += (_, e) => { int i = _changeLog.Count - 1 - e.RowIndex; if (i >= 0 && i < _changeLog.Count) Open(_changeLog[i].Address); };

        _inspector.CellDoubleClick += async (_, e) => await EditInspectorAsync(e.RowIndex);
        if (Freezer != null) Freezer.Changed += () => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { _hex.Invalidate(); _values.Invalidate(); }); };
    }

    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 200 };

    // ═════════════════════════════════════════════════════════════════════════
    //  lifecycle
    // ═════════════════════════════════════════════════════════════════════════

    public override void OnShown()
    {
        UpdateTimers();
        _ = SyncAsync(false);
    }

    public override void OnHidden()
    {
        _timer.Stop();
        _syncTimer.Stop();
    }

    public override void OnConnectionChanged(bool connected)
    {
        if (!connected) { _titleKey = ""; _game.Text = "Not connected"; _gameMeta.Text = "The view resumes automatically when the console is back"; }
        else if (Visible) _ = SyncAsync(false);
        UpdateTimers();
    }

    private void UpdateTimers()
    {
        bool on = Visible && Client != null;
        if (on && _live.Checked) _timer.Start(); else _timer.Stop();
        if (on) _syncTimer.Start(); else _syncTimer.Stop();
    }

    /// <summary>Opens the view at an address (called from other tools and pages).</summary>
    public void Open(uint address)
    {
        if (_hex.CursorAddress != address) _back.Push(_hex.CursorAddress);
        _goto.Text = $"0x{address:X8}";
        _hex.GoTo(address);
        _hex.Focus();
    }

    private void GoFromBox()
    {
        var a = AddressOrToast(_goto);
        if (a != null) Open(a.Value);
    }

    private void Back()
    {
        if (_back.Count > 0) { var a = _back.Pop(); _goto.Text = $"0x{a:X8}"; _hex.GoTo(a); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _syncTimer.Dispose(); _searchDebounce.Dispose(); }
        base.Dispose(disposing);
    }
}
