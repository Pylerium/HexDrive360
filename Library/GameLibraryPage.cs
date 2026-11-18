using MemoryEditor.Runtime;
using MemoryEditor.UI;

namespace MemoryEditor.Library;

/// <summary>Services the Game Library needs from the offset database (implemented by the main window).</summary>
public interface IOffsetDatabaseHost
{
    /// <summary>Loads an offsets file into the offset database; returns the number of entries.</summary>
    Task<int> LoadDatabaseAsync(string path, string title);
    IReadOnlyList<OffsetManager.OffsetEntry> DatabaseEntries { get; }
    void ShowPulled(PullResult result);
    void ShowDetected(GameInfo game);
}

/// <summary>
/// Game launcher style Offset Database: a library of every game (and every build / TU of it) with its offsets,
/// one-click launch, live runtime pulls, signature learning to port offsets between title updates, and automation.
/// The classic offset database tools (pointer resolve, bulk apply, export to script) are kept as a second view.
/// </summary>
public sealed partial class GameLibraryPage : UserControl
{
    private readonly IAppHost _host;
    private readonly IOffsetDatabaseHost _db;
    private readonly GameLibrary _lib;
    private readonly CoverCache _covers = new();

    // ── sidebar ──
    private readonly TextBox _search = new() { PlaceholderText = "Search games", Dock = DockStyle.Top };
    private readonly ComboBox _sort = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly GameListBox _list = new() { Dock = DockStyle.Fill };
    private readonly Label _libCount = Ui.Muted("");

    // ── hero ──
    private readonly CoverTile _cover = new() { Size = new Size(112, 112), Location = new Point(20, 20) };
    private readonly Label _name = new() { AutoSize = false, Font = new Font("Segoe UI Semibold", 17F), ForeColor = Theme.Text, BackColor = Color.Transparent, Tag = "keep", UseMnemonic = false };
    private readonly Label _meta = new() { AutoSize = false, Font = Theme.UI, ForeColor = Theme.TextMuted, BackColor = Color.Transparent, Tag = "keep", UseMnemonic = false };
    private readonly StatusPill _pillRunning = new() { Text = "Running", DotColor = Theme.Success, Visible = false, Margin = new Padding(0, 0, 6, 0) };
    private readonly StatusPill _pillVerified = new() { Glyph = Theme.Glyph.Check, Text = "Verified", DotColor = Theme.Success, Visible = false, Margin = new Padding(0, 0, 6, 0) };
    private readonly StatusPill _pillSource = new() { Text = "", DotColor = Theme.Accent, Visible = false, Margin = new Padding(0, 0, 6, 0) };
    private readonly ComboBox _builds = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, Margin = new Padding(0, 1, 8, 0) };
    private readonly IconButton _btnLaunch, _btnLaunchPull, _btnPull, _btnLearn, _btnExport, _btnMore, _btnCancel;
    private readonly Ui.ThinProgress _progress = new() { Dock = DockStyle.Bottom, Visible = false };
    private readonly StackPanel _heroStack = new() { Dock = DockStyle.Top, Spacing = 10 };
    private readonly FlowLayoutPanel _facts = new() { WrapContents = true, BackColor = Theme.Surface, Margin = new Padding(0) };
    private readonly FlowLayoutPanel _buildRow = new() { WrapContents = true, BackColor = Theme.Surface, Margin = new Padding(0) };
    private readonly FlowLayoutPanel _actions = new() { WrapContents = true, BackColor = Theme.Surface, Margin = new Padding(0) };
    private readonly FactLabel _fTitle = new("Title ID"), _fExe = new("Executable"), _fOffsets = new("Offsets"), _fUpdated = new("Last pulled"),
                               _fBuilt = new("Build date"), _fVersion = new("Version"), _fLocation = new("Location on console") { MaxValueWidth = 520 };
    private Label? _lblBuild;
    private readonly Label _noBuilds = new()
    {
        AutoSize = true, Font = Theme.UI, ForeColor = Theme.TextMuted, BackColor = Color.Transparent, Tag = "keep", UseMnemonic = false,
        Margin = new Padding(0, 5, 10, 0), Text = "No builds yet – start the game on your console and press Pull offsets.",
    };
    private readonly Label _status = new() { AutoSize = false, Font = Theme.UISmall, ForeColor = Theme.TextDim, BackColor = Color.Transparent, Tag = "keep", UseMnemonic = false };

    // ── offsets view ──
    private readonly ListBox _cats = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 28, BorderStyle = BorderStyle.None, IntegralHeight = false };
    private readonly TextBox _filter = new() { Width = 300, PlaceholderText = "Filter offsets by name, address or bytes" };
    private readonly Label _gridCount = Ui.Muted("");
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
    };
    private readonly Panel _offsetsView = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg, Tag = "keep" };
    private readonly Panel _toolsView = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg, Tag = "keep", Visible = false };
    private readonly IconButton _tabOffsets, _tabTools;

    // ── state ──
    private GameRecord? _selected;
    private GameInfo? _running;
    private List<OffsetManager.OffsetEntry> _entries = new();
    private List<OffsetManager.OffsetEntry> _view = new();
    private List<(string Name, int Count)> _catItems = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _suppressBuildChange;
    private int _sortColumn = -1;
    private bool _sortDesc;
    private readonly System.Windows.Forms.Timer _filterDebounce = new() { Interval = 140 };

    public GameLibrary Library => _lib;
    public bool AutoPullOnConnect => _lib.Settings.AutoPullOnConnect;

    public GameLibraryPage(IAppHost host, IOffsetDatabaseHost db, Control classicTools)
    {
        _host = host;
        _db = db;
        _lib = GameLibrary.Load();
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        DoubleBuffered = true;

        _btnLaunch = IconButton.Create("Launch", Theme.Glyph.Play, ButtonVariant.Success, async (_, _) => await LaunchAsync(false), "Start this game on the console");
        _btnLaunchPull = IconButton.Create("Launch & pull", Theme.Glyph.Bolt, ButtonVariant.Neutral, async (_, _) => await LaunchAsync(true), "Start the game, wait for it to load, then pull every offset");
        _btnPull = IconButton.Create("Pull offsets", Theme.Glyph.Download, ButtonVariant.Primary, async (_, _) => await PullRunningAsync(userInitiated: true), "Read the running game from memory and map all of its offsets");
        _btnLearn = IconButton.Create("Learn signatures", Theme.Glyph.Fingerprint, ButtonVariant.Neutral, async (_, _) => await LearnSelectedAsync(), "Learn this build's offsets so they are found automatically in other title updates");
        _btnExport = IconButton.Create("Export", Theme.Glyph.Upload, ButtonVariant.Neutral, (_, _) => ExportSelected(), "Save this build's offsets file (BO2 text format)");
        _btnMore = IconButton.Create("", Theme.Glyph.Menu, ButtonVariant.Ghost, (s, _) => ShowMoreMenu((Control)s!), "More actions");
        _btnCancel = IconButton.Create("Cancel", Theme.Glyph.Stop, ButtonVariant.Danger, (_, _) => _cts?.Cancel(), "Cancel the running operation");
        _btnCancel.Visible = false;
        _tabOffsets = IconButton.Create("Offsets", Theme.Glyph.List, ButtonVariant.Primary, (_, _) => ShowView(false));
        _tabTools = IconButton.Create("Pointer & apply tools", Theme.Glyph.Tools, ButtonVariant.Ghost, (_, _) => ShowView(true), "Classic offset database: resolve pointers, apply selected, export to script");

        var sidebar = BuildSidebar();
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(14, 0, 0, 0), Tag = "keep" };
        var hero = BuildHero();
        var body = BuildBody(classicTools);
        right.Controls.Add(body);
        right.Controls.Add(Ui.Spacer(DockStyle.Top, 12));
        right.Controls.Add(hero);

        Controls.Add(right);
        Controls.Add(sidebar);

        _filterDebounce.Tick += (_, _) => { _filterDebounce.Stop(); ApplyFilter(); };
        _watch.Tick += async (_, _) => await WatchTickAsync();

        Theme.Apply(this);
        Theme.StyleGrid(_grid);
        _grid.Columns[2].DefaultCellStyle.Font = Theme.Mono;
        _grid.Columns[3].DefaultCellStyle.Font = Theme.Mono;
        _grid.Columns[4].DefaultCellStyle.Font = Theme.MonoSmall;

        _lib.Changed = () => { if (!IsDisposed) BeginInvoke(RefreshList); };
        UpdateHero();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  layout
    // ═════════════════════════════════════════════════════════════════════════

    private Control BuildSidebar()
    {
        var side = Ui.Card(DockStyle.Left, 0, new Padding(0));
        side.Width = 310;

        // stacked header: title row, tool row (wraps), search – nothing can overlap at any width
        var head = new StackPanel { Dock = DockStyle.Top, BackColor = Theme.Surface, Padding = new Padding(12, 12, 12, 10), Spacing = 8 };
        var title = Ui.Title("Library");
        title.Font = Theme.Heading;
        title.Margin = new Padding(0, 0, 8, 0);
        _libCount.Font = Theme.UISmall;
        _libCount.Margin = new Padding(0, 4, 0, 0);
        var titleRow = new FlowLayoutPanel { WrapContents = true, BackColor = Theme.Surface, Margin = new Padding(0) };
        titleRow.Controls.AddRange(new Control[] { title, _libCount });
        var tools = new FlowLayoutPanel { WrapContents = true, BackColor = Theme.Surface, Margin = new Padding(0) };
        var bScan = IconButton.Create("Scan console", Theme.Glyph.Search, ButtonVariant.Neutral, async (_, _) => await ScanConsoleAsync(), "Find every game installed on the console's drives");
        var bAdd = IconButton.Create("", Theme.Glyph.Add, ButtonVariant.Neutral, (s, _) => ShowAddMenu((Control)s!), "Import an offsets file, analyse an XEX, or add a game");
        bScan.Height = bAdd.Height = 28;
        _sort.Items.AddRange(new object[] { "Recent", "Name", "Offsets", "Favorites" });
        _sort.SelectedItem = _sort.Items.Contains(_lib.Settings.SortMode) ? _lib.Settings.SortMode : "Recent";
        _sort.Margin = new Padding(0, 2, 0, 4);
        bScan.Margin = bAdd.Margin = new Padding(0, 0, 6, 4);
        _sort.SelectedIndexChanged += (_, _) => { _lib.Settings.SortMode = _sort.SelectedItem?.ToString() ?? "Recent"; RefreshList(); _lib.Save(); };
        tools.Controls.AddRange(new Control[] { bScan, bAdd, _sort });
        _search.Dock = DockStyle.None;
        _search.Margin = new Padding(0);
        _search.TextChanged += (_, _) => RefreshList();
        head.Controls.AddRange(new Control[] { titleRow, tools, _search });

        _list.IsRunning = r => _running != null && r.Matches(_running);
        _list.CoverFor = r => _covers.Get(r.CoverPath);
        _list.SelectedIndexChanged += async (_, _) => { if (_list.SelectedItem is GameRecord r && r != _selected) await SelectGameAsync(r); };
        _list.DoubleClick += async (_, _) => { if (_list.SelectedItem is GameRecord) await LaunchAsync(false); };
        _list.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            int i = _list.IndexFromPoint(e.Location);
            if (i >= 0) { _list.SelectedIndex = i; ShowMoreMenu(_list, e.Location); }
        };
        _list.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await LaunchAsync(false); }
            else if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; await PullRunningAsync(true); }
            else if (e.KeyCode == Keys.Delete) { e.SuppressKeyPress = true; RemoveSelectedGame(); }
        };
        var listHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(4, 4, 4, 4), Tag = "keep" };
        listHost.Controls.Add(_list);

        var auto = BuildAutomation();
        side.Controls.Add(listHost);
        side.Controls.Add(auto);
        side.Controls.Add(head);
        return side;
    }

    private Control BuildAutomation()
    {
        var s = _lib.Settings;
        var box = new Panel { Dock = DockStyle.Bottom, Height = 268, BackColor = Theme.Surface, Padding = new Padding(14, 10, 10, 8), Tag = "keep" };
        box.Paint += (_, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, 0, box.Width, 0); };

        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface, AutoScroll = true };
        var head = new Label { Text = "AUTOMATION", AutoSize = true, Font = Theme.UISmall, ForeColor = Theme.TextDim, BackColor = Color.Transparent, Tag = "keep", Margin = new Padding(0, 0, 0, 6) };
        flow.Controls.Add(head);

        var tip = new ToolTip();
        void Opt(string text, string hint, Func<bool> get, Action<bool> set)
        {
            var cb = new CheckBox { Text = text, Checked = get(), AutoSize = true, Margin = new Padding(0, 1, 0, 3) };
            cb.CheckedChanged += (_, _) => { set(cb.Checked); _lib.Save(); UpdateWatcher(); };
            tip.SetToolTip(cb, hint);
            flow.Controls.Add(cb);
        }
        Opt("Pull offsets when I connect", "Identify the running game on connect and pull it if this build is new", () => s.AutoPullOnConnect, v => s.AutoPullOnConnect = v);
        Opt("Watch for game changes", "Poll the console and follow you when you launch another game or title update", () => s.WatchTitleChanges, v => s.WatchTitleChanges = v);
        Opt("Auto-pull new games & title updates", "Pull automatically the first time a game / build is seen", () => s.AutoPullNewBuilds, v => s.AutoPullNewBuilds = v);
        Opt("Load the running game into the database", "Switch the offset database to whatever is running", () => s.LoadIntoDatabase, v => s.LoadIntoDatabase = v);
        Opt("Learn signatures from verified files", "When an imported offsets file matches the running build, learn it so other TUs are mapped automatically", () => s.AutoLearnSignatures, v => s.AutoLearnSignatures = v);
        Opt("Include imports & module layout", "Import thunks, sections, entry point", () => s.IncludeImports, v => { s.IncludeImports = v; s.IncludeModuleLayout = v; });
        Opt("Include loaded modules", "Base address of every module in the title process", () => s.IncludeLoadedModules, v => s.IncludeLoadedModules = v);
        Opt("Include string references", "Every referenced string (large files)", () => s.IncludeStringReferences, v => s.IncludeStringReferences = v);

        box.Controls.Add(flow);
        return box;
    }

    /// <summary>
    /// Responsive header: cover on the left; name, facts, build picker, actions and status stacked on the right.
    /// Every row wraps instead of clipping and the card grows to fit its content at any window width.
    /// </summary>
    private Control BuildHero()
    {
        var hero = Ui.Card(DockStyle.Top, 168, new Padding(20, 18, 18, 14));

        var left = new Panel { Dock = DockStyle.Left, Width = 132, BackColor = Theme.Surface, Tag = "keep" };
        _cover.Location = new Point(0, 2);
        left.Controls.Add(_cover);

        _facts.Controls.AddRange(new Control[] { _fTitle, _fExe, _fOffsets, _fUpdated, _fBuilt, _fVersion, _fLocation });

        _builds.SelectedIndexChanged += async (_, _) => { if (!_suppressBuildChange) await SelectBuildAsync(_builds.SelectedIndex); };
        var lblBuild = _lblBuild = new Label { Text = "Build", AutoSize = true, Font = Theme.UISemibold, ForeColor = Theme.TextMuted, BackColor = Color.Transparent, Tag = "keep", Margin = new Padding(0, 6, 8, 0) };
        _builds.Margin = new Padding(0, 2, 10, 4);
        foreach (var pill in new[] { _pillRunning, _pillVerified, _pillSource }) pill.Margin = new Padding(0, 2, 6, 4);
        _buildRow.Controls.AddRange(new Control[] { lblBuild, _builds, _noBuilds, _pillRunning, _pillVerified, _pillSource });

        foreach (var b in new[] { _btnLaunch, _btnLaunchPull, _btnPull, _btnLearn, _btnExport, _btnMore, _btnCancel }) b.Margin = new Padding(0, 0, 8, 6);
        _actions.Controls.AddRange(new Control[] { _btnLaunch, _btnLaunchPull, _btnPull, _btnLearn, _btnExport, _btnMore, _btnCancel });

        _name.UseMnemonic = false;
        _heroStack.Controls.AddRange(new Control[] { _name, _meta, _facts, _buildRow, _actions, _status });

        var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Tag = "keep" };
        right.Controls.Add(_heroStack);

        hero.Controls.Add(right);
        hero.Controls.Add(left);
        hero.Controls.Add(_progress);

        void Fit()
        {
            int h = Math.Max(_cover.Height + 4, _heroStack.Height) + hero.Padding.Vertical + (_progress.Visible ? _progress.Height : 0);
            if (hero.Height != h) hero.Height = h;
        }
        _heroStack.HeightChanged += Fit;
        _progress.VisibleChanged += (_, _) => Fit();
        return hero;
    }

    private Control BuildBody(Control classicTools)
    {
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Tag = "keep" };

        // view switch + filter
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Theme.Bg, Padding = new Padding(0, 2, 0, 4) };
        _tabOffsets.Height = _tabTools.Height = 30;
        _filter.Margin = new Padding(14, 4, 8, 0);
        _filter.TextChanged += (_, _) => { _filterDebounce.Stop(); _filterDebounce.Start(); };
        _filter.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { _filter.Clear(); e.SuppressKeyPress = true; } };
        _gridCount.Margin = new Padding(0, 9, 0, 0);
        bar.Controls.AddRange(new Control[] { _tabOffsets, _tabTools, _filter, _gridCount });

        // categories + grid
        var catCard = Ui.Card(DockStyle.Left, 0, new Padding(1, 6, 1, 1));
        catCard.Width = 230;
        _cats.BackColor = Theme.Surface;
        _cats.ForeColor = Theme.Text;
        Theme.Exclude(_cats);
        Theme.DarkScrollbars(_cats);
        _cats.DrawItem += DrawCategory;
        _cats.SelectedIndexChanged += (_, _) => ApplyFilter();
        catCard.Controls.Add(_cats);

        _grid.Columns.Add("name", "Name");
        _grid.Columns.Add("cat", "Category");
        _grid.Columns.Add("addr", "Address");
        _grid.Columns.Add("plus", "+ Offset");
        _grid.Columns.Add("bytes", "Bytes");
        _grid.Columns.Add("type", "Type");
        int[] widths = { 300, 180, 110, 80, 160, 80 };
        for (int i = 0; i < widths.Length; i++) { _grid.Columns[i].Width = widths[i]; _grid.Columns[i].SortMode = DataGridViewColumnSortMode.Programmatic; }
        _grid.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.Columns[0].MinimumWidth = 160;
        _grid.CellValueNeeded += Grid_CellValueNeeded;
        _grid.ColumnHeaderMouseClick += (_, e) => SortBy(e.ColumnIndex);
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ViewSelectedInMemory(); };
        _grid.ContextMenuStrip = BuildGridMenu();
        _grid.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C) { CopySelected(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter) { ViewSelectedInMemory(); e.SuppressKeyPress = true; }
        };
        var gridCard = Ui.Card(DockStyle.Fill, 0, new Padding(1));
        gridCard.Controls.Add(_grid);

        var gridActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, BackColor = Theme.Bg, Padding = new Padding(0, 8, 0, 0) };
        gridActions.Controls.AddRange(new Control[]
        {
            IconButton.Create("View memory", Theme.Glyph.Memory, ButtonVariant.Neutral, (_, _) => ViewSelectedInMemory(), "Open the address in the memory viewer (Enter)"),
            IconButton.Create("Watch", Theme.Glyph.Pin, ButtonVariant.Neutral, (_, _) => WatchSelected(), "Add to the scanner watch list"),
            IconButton.Create("Monitor region", Theme.Glyph.Record, ButtonVariant.Purple, (s, _) => ShowMonitorMenu((Control)s!, null), "Record the memory around the selected offset(s) in the Memory Recorder"),
            IconButton.Create("Apply patch", Theme.Glyph.Bolt, ButtonVariant.Warning, async (_, _) => await ApplySelectedAsync(), "Write the Bytes of the selected entries to the console"),
            IconButton.Create("Insert into script", Theme.Glyph.Code, ButtonVariant.Neutral, (_, _) => InsertSelectedIntoScript(), "Insert as named constants in the Script IDE"),
            IconButton.Create("Copy", Theme.Glyph.Copy, ButtonVariant.Ghost, (_, _) => CopySelected(), "Copy in offsets-file format (Ctrl+C)"),
        });
        foreach (Control c in gridActions.Controls) { c.Height = 30; c.Margin = new Padding(0, 0, 6, 6); }

        var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(10, 0, 0, 0), Tag = "keep" };
        gridHost.Controls.Add(gridCard);
        gridHost.Controls.Add(gridActions);

        _offsetsView.Controls.Add(gridHost);
        _offsetsView.Controls.Add(catCard);

        classicTools.Dock = DockStyle.Fill;
        _toolsView.Controls.Add(classicTools);

        body.Controls.Add(_offsetsView);
        body.Controls.Add(_toolsView);
        body.Controls.Add(bar);
        return body;
    }

    private void ShowView(bool tools)
    {
        _toolsView.Visible = tools;
        _offsetsView.Visible = !tools;
        _tabOffsets.Variant = tools ? ButtonVariant.Ghost : ButtonVariant.Primary;
        _tabTools.Variant = tools ? ButtonVariant.Primary : ButtonVariant.Ghost;
        _filter.Visible = _gridCount.Visible = !tools;
        (tools ? _toolsView : _offsetsView).BringToFront();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  library list / hero
    // ═════════════════════════════════════════════════════════════════════════

    private void RefreshList()
    {
        if (IsDisposed) return;
        var q = _search.Text.Trim();
        IEnumerable<GameRecord> games = _lib.Games;
        if (q.Length > 0)
            games = games.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || g.TitleIdText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                     g.ModuleName.Contains(q, StringComparison.OrdinalIgnoreCase));
        games = _lib.Settings.SortMode switch
        {
            "Name" => games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            "Offsets" => games.OrderByDescending(g => g.TotalOffsets),
            "Favorites" => games.OrderByDescending(g => g.Favorite).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            _ => games.OrderByDescending(g => _running != null && g.Matches(_running))
                      .ThenByDescending(g => Max(g.LastSeen, g.LastLaunched, g.Builds.Max(b => b.PulledAt), g.Added)),
        };
        var items = games.ToArray();

        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Items.AddRange(items);
        if (_selected != null)
        {
            int i = Array.IndexOf(items, _selected);
            if (i >= 0) _list.SelectedIndex = i;
        }
        _list.EndUpdate();
        _libCount.Text = $"{_lib.Games.Count} game{(_lib.Games.Count == 1 ? "" : "s")} · {_lib.Games.Sum(g => g.TotalOffsets):N0} offsets";
    }

    private static DateTime Max(params DateTime?[] d) => d.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(DateTime.MinValue).Max();

    private void UpdateHero()
    {
        var g = _selected;
        _cover.GameName = g?.Name ?? "";
        _cover.Cover = g != null ? _covers.Get(g.CoverPath) : null;
        _cover.Invalidate();

        bool connected = _host.Client?.IsConnected == true;
        bool running = g != null && _running != null && g.Matches(_running);
        var b = g?.Current;

        if (g == null)
        {
            _name.Text = _lib.Games.Count == 0 ? "Your library is empty" : "Select a game";
            _meta.Text = _lib.Games.Count == 0
                ? "Connect to your console and start a game – it is detected and its offsets are pulled automatically. Or use Scan console / + to add games and import offset files."
                : "Pick a game on the left to see every offset of every build.";
            _meta.Visible = true;
            _facts.Visible = false;
        }
        else
        {
            _name.Text = g.Name;
            _meta.Visible = false;
            _facts.Visible = true;
            _fTitle.Value = g.TitleId != 0 ? g.TitleIdText : "unknown";
            _fExe.Value = g.ModuleName.Length > 0 ? g.ModuleName : "unknown";
            _fOffsets.Value = b != null ? $"{b.OffsetCount:N0} in {b.Label}" : "not pulled yet";
            _fOffsets.ValueColor = b != null ? Theme.Text : Theme.Warning;
            _fUpdated.Visible = b?.PulledAt != null;
            if (b?.PulledAt is { } t) _fUpdated.Value = Ago(t);
            _fBuilt.Visible = b != null && b.Timestamp != 0;
            if (_fBuilt.Visible) _fBuilt.Value = DateTimeOffset.FromUnixTimeSeconds(b!.Timestamp).ToString("d MMM yyyy");
            _fVersion.Visible = !string.IsNullOrEmpty(b?.Version);
            if (_fVersion.Visible) _fVersion.Value = "v" + b!.Version;
            _fLocation.Value = g.LaunchPath.Length > 0 ? g.LaunchPath : "not set – use Scan console";
            _fLocation.ValueColor = g.LaunchPath.Length > 0 ? Theme.Text : Theme.TextDim;
        }

        _suppressBuildChange = true;
        _builds.BeginUpdate();
        _builds.Items.Clear();
        if (g != null)
        {
            foreach (var x in g.Builds.OrderByDescending(x => x.Timestamp)) _builds.Items.Add(new BuildItem(x, _running != null && x.Timestamp != 0 && x.Timestamp == _running.Timestamp));
            int sel = g.Builds.Count == 0 ? -1 : _builds.Items.Cast<BuildItem>().ToList().FindIndex(x => x.Build == b);
            _builds.SelectedIndex = sel;
        }
        _builds.EndUpdate();
        // never show an empty / disabled picker – say what to do instead
        _builds.Visible = g != null && g.Builds.Count > 0;
        _noBuilds.Visible = g != null && g.Builds.Count == 0;
        if (_lblBuild != null) _lblBuild.Visible = _builds.Visible;
        _buildRow.Visible = g != null;
        _suppressBuildChange = false;

        _pillRunning.Visible = running;
        if (running) _pillRunning.Text = b != null && _running!.Timestamp == b.Timestamp ? "Running · this build" : "Running";
        _pillVerified.Visible = b?.Verified == true;
        _pillSource.Visible = b != null && b.Source.Length > 0;
        if (b != null) _pillSource.Text = b.Source;

        _btnLaunch.Enabled = g != null && connected && !_busy;
        _btnLaunchPull.Enabled = g != null && connected && !_busy;
        _btnPull.Enabled = connected && !_busy;
        _btnPull.Text = running || g == null || !connected || _running == null ? "Pull offsets" : "Pull running game";

        // explain disabled actions instead of leaving the user guessing
        string notConnected = "Connect to your console first (top bar → Connect)";
        _btnLaunch.ToolTipText = !connected ? notConnected : g?.LaunchPath.Length > 0 ? $"Start {g.Name} on the console" : "Start this game – you will be asked where it is on the console";
        _btnLaunchPull.ToolTipText = !connected ? notConnected : "Start the game, wait for it to load, then pull every offset";
        _btnPull.ToolTipText = !connected ? notConnected : "Read the running game from memory and map all of its offsets";
        _btnLearn.ToolTipText = !connected ? notConnected : b == null ? "Pull or import offsets first" : "Learn this build's offsets so they are found automatically in other title updates";
        _btnExport.ToolTipText = b == null ? "Nothing to export yet" : "Save this build's offsets file (BO2 text format)";
        _btnLearn.Enabled = b != null && connected && !_busy;
        _btnExport.Enabled = b != null && File.Exists(GameLibrary.ResolvePath(b.OffsetFile));
        _btnCancel.Visible = _busy;
        _list.Invalidate();
        _heroStack.Relayout();
    }

    private sealed class BuildItem
    {
        public GameBuild Build { get; }
        private readonly bool _running;
        public BuildItem(GameBuild b, bool running) { Build = b; _running = running; }
        public override string ToString() => $"{Build.Label}  ({Build.OffsetCount:N0})" + (_running ? "  ● running" : "") + (Build.Verified ? "  ✓" : "");
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} d ago";
        return t.ToString("yyyy-MM-dd");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  selection & database loading
    // ═════════════════════════════════════════════════════════════════════════

    public async Task SelectGameAsync(GameRecord rec, GameBuild? build = null)
    {
        _selected = rec;
        if (build != null) rec.SelectedBuild = build.Label;
        _lib.Settings.SelectedGameId = rec.Id;
        if (_list.SelectedItem != rec)
        {
            int i = _list.Items.IndexOf(rec);
            if (i >= 0) _list.SelectedIndex = i; // re-enters via SelectedIndexChanged, guarded by _selected
        }
        UpdateHero();
        await LoadCurrentBuildAsync();
        _lib.Save();
    }

    private async Task SelectBuildAsync(int index)
    {
        if (_selected == null || index < 0 || index >= _builds.Items.Count) return;
        var b = ((BuildItem)_builds.Items[index]!).Build;
        _selected.SelectedBuild = b.Label;
        UpdateHero();
        await LoadCurrentBuildAsync();
        _lib.Save();
    }

    private async Task LoadCurrentBuildAsync()
    {
        var rec = _selected;
        var b = rec?.Current;
        var path = b == null ? "" : GameLibrary.ResolvePath(b.OffsetFile);
        if (rec == null || b == null || !File.Exists(path))
        {
            _entries = new List<OffsetManager.OffsetEntry>();
            RebuildCategories();
            ApplyFilter();
            if (b != null && !File.Exists(path)) SetStatus($"Offsets file is missing: {path}");
            return;
        }
        int n = await _db.LoadDatabaseAsync(path, $"{rec.Name} · {b.Label}");
        if (rec != _selected) return; // user moved on while loading
        _entries = _db.DatabaseEntries.ToList();
        if (n != b.OffsetCount) { b.OffsetCount = n; _lib.Save(); }
        RebuildCategories();
        ApplyFilter();
        SetStatus($"{rec.Name} · {b.Label} loaded into the offset database ({n:N0} offsets)");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  offsets view
    // ═════════════════════════════════════════════════════════════════════════

    private void RebuildCategories()
    {
        var prev = _cats.SelectedIndex > 0 && _cats.SelectedIndex < _catItems.Count ? _catItems[_cats.SelectedIndex].Name : null;
        _catItems = new List<(string, int)> { ("All offsets", _entries.Count) };
        _catItems.AddRange(_entries.GroupBy(e => e.Category).Select(g => (g.Key, g.Count())));
        _cats.BeginUpdate();
        _cats.Items.Clear();
        foreach (var c in _catItems) _cats.Items.Add(c.Name);
        int idx = prev == null ? 0 : Math.Max(0, _catItems.FindIndex(c => c.Name == prev));
        _cats.SelectedIndex = _catItems.Count > 0 ? idx : -1;
        _cats.EndUpdate();
    }

    private void DrawCategory(object? sender, DrawItemEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, e.Bounds);
        if (e.Index < 0 || e.Index >= _catItems.Count) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 2, e.Bounds.Width - 8, e.Bounds.Height - 4);
        if (sel)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Theme.RoundRect(r, 5);
            using var b = new SolidBrush(Theme.Blend(Theme.Surface2, Theme.Accent, 0.22f));
            g.FillPath(b, path);
        }
        var (name, count) = _catItems[e.Index];
        var countText = count.ToString("N0");
        var cw = TextRenderer.MeasureText(countText, Theme.UISmall).Width;
        TextRenderer.DrawText(g, name, e.Index == 0 ? Theme.UISemibold : Theme.UI, new Rectangle(r.X + 8, r.Y, r.Width - cw - 18, r.Height),
            sel ? Theme.Text : Theme.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, countText, Theme.UISmall, new Rectangle(r.Right - cw - 8, r.Y, cw + 4, r.Height), Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    private void ApplyFilter()
    {
        IEnumerable<OffsetManager.OffsetEntry> q = _entries;
        if (_cats.SelectedIndex > 0 && _cats.SelectedIndex < _catItems.Count)
        {
            var cat = _catItems[_cats.SelectedIndex].Name;
            q = q.Where(e => e.Category == cat);
        }
        var f = _filter.Text.Trim();
        if (f.Length > 0)
        {
            var hex = f.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? f[2..] : f;
            q = q.Where(e => e.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                             e.Category.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                             e.BaseAddress.ToString("X8").Contains(hex, StringComparison.OrdinalIgnoreCase) ||
                             (e.ByteValues?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        _view = q.ToList();
        SortView();
        _grid.RowCount = 0;
        _grid.RowCount = _view.Count;
        _grid.Invalidate();
        _gridCount.Text = _view.Count == _entries.Count ? $"{_entries.Count:N0} offsets" : $"{_view.Count:N0} of {_entries.Count:N0}";
    }

    private void SortBy(int col)
    {
        _sortDesc = _sortColumn == col && !_sortDesc;
        _sortColumn = col;
        foreach (DataGridViewColumn c in _grid.Columns) c.HeaderCell.SortGlyphDirection = SortOrder.None;
        _grid.Columns[col].HeaderCell.SortGlyphDirection = _sortDesc ? SortOrder.Descending : SortOrder.Ascending;
        SortView();
        _grid.Invalidate();
    }

    private void SortView()
    {
        if (_sortColumn < 0) return;
        Comparison<OffsetManager.OffsetEntry> cmp = _sortColumn switch
        {
            0 => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            1 => (a, b) => string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase),
            2 => (a, b) => a.BaseAddress.CompareTo(b.BaseAddress),
            3 => (a, b) => (a.Offset ?? 0).CompareTo(b.Offset ?? 0),
            4 => (a, b) => string.Compare(a.ByteValues, b.ByteValues, StringComparison.Ordinal),
            _ => (a, b) => string.Compare(a.Type, b.Type, StringComparison.Ordinal),
        };
        _view.Sort(_sortDesc ? (a, b) => cmp(b, a) : cmp);
    }

    private void Grid_CellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _view.Count) return;
        var o = _view[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => o.Name,
            1 => o.Category,
            2 => $"0x{o.BaseAddress:X8}",
            3 => o.Offset is { } p ? $"+0x{p:X}" : "",
            4 => o.ByteValues ?? "",
            _ => o.Type,
        };
    }

    private List<OffsetManager.OffsetEntry> SelectedEntries() =>
        _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i >= 0 && i < _view.Count).OrderBy(i => i).Select(i => _view[i]).ToList();

    private static uint FinalAddress(OffsetManager.OffsetEntry e) => unchecked(e.BaseAddress + (e.Offset ?? 0));

    private ContextMenuStrip BuildGridMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("View in memory", null, (_, _) => ViewSelectedInMemory());
        m.Items.Add("Add to watch list", null, (_, _) => WatchSelected());
        m.Items.Add("Open in Active Memory", null, (_, _) => { if (SelectedEntries().FirstOrDefault(e => e.BaseAddress >= 0x80000000) is { } e) _host.OpenActiveMemory(FinalAddress(e)); });
        m.Items.Add("Disassemble", null, (_, _) => { if (SelectedEntries().FirstOrDefault(e => e.BaseAddress >= 0x80000000) is { } e) _host.Disassemble(FinalAddress(e)); });
        m.Items.Add("Graph value (Int32)", null, (_, _) => { if (SelectedEntries().FirstOrDefault(e => e.BaseAddress >= 0x80000000) is { } e) _host.GraphValue(FinalAddress(e), "Int32", e.Name); });
        m.Items.Add("Graph value (Float)", null, (_, _) => { if (SelectedEntries().FirstOrDefault(e => e.BaseAddress >= 0x80000000) is { } e) _host.GraphValue(FinalAddress(e), "Float", e.Name); });
        m.Items.Add("Open as pointer chain", null, (_, _) => { if (SelectedEntries().FirstOrDefault(e => e.BaseAddress >= 0x80000000) is { } e) _host.PointerChain(e.BaseAddress, e.Offset is { } p ? new[] { p } : Array.Empty<uint>()); });
        m.Items.Add("Monitor region in Memory Recorder…", null, (_, _) => ShowMonitorMenu(_grid, _grid.PointToClient(Cursor.Position)));
        m.Items.Add("Apply patch bytes", null, async (_, _) => await ApplySelectedAsync());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Copy (offsets format)", null, (_, _) => CopySelected());
        m.Items.Add("Copy address", null, (_, _) => { var s = SelectedEntries(); if (s.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, s.Select(e => $"0x{FinalAddress(e):X8}"))); });
        m.Items.Add("Insert into script", null, (_, _) => InsertSelectedIntoScript());
        Scripting.ScriptIde.StyleMenu(m);
        return m;
    }

    private void ViewSelectedInMemory()
    {
        var s = SelectedEntries().FirstOrDefault();
        if (s == null) return;
        if (s.BaseAddress < 0x80000000) { _host.Toast($"{s.Name} is a value (0x{s.BaseAddress:X}), not an address", ToastHost.Kind.Info); return; }
        _host.ViewMemory(FinalAddress(s));
    }

    /// <summary>Region choices for the Memory Recorder, based on the selected offset(s).</summary>
    private void ShowMonitorMenu(Control anchor, Point? at)
    {
        var sel = SelectedEntries().Where(e => e.BaseAddress >= 0x80000000).ToList();
        if (sel.Count == 0)
        {
            _host.Toast("Select an offset with an address (values like sizes can't be monitored)", ToastHost.Kind.Info);
            return;
        }
        var e0 = sel[0];
        uint addr = FinalAddress(e0);
        string name = sel.Count == 1 ? e0.Name : $"{sel.Count} offsets";
        static uint Down(uint v, uint align) => v & ~(align - 1);
        uint Around(uint before) => addr >= 0x80000000 + before ? Down(addr - before, 0x10) : Down(addr, 0x10);

        var m = new ContextMenuStrip();
        void Item(string text, uint start, uint size, string label) =>
            m.Items.Add(text, null, (_, _) => _host.MonitorRegion(start, size, label));

        int patchLen = Math.Max(4, OffsetTextFormat.ParseBytes(e0.ByteValues)?.Length ?? 4);
        Item($"Exact · {e0.Name} ({patchLen} bytes @ 0x{addr:X8})", Down(addr, 4), (uint)patchLen, e0.Name);
        Item("Around it · 256 bytes", Around(0x40), 0x100, $"{e0.Name} ±256 B");
        Item("Around it · 1 KB", Around(0x100), 0x400, $"{e0.Name} ±1 KB");
        Item("Around it · 4 KB", Around(0x400), 0x1000, $"{e0.Name} ±4 KB");
        Item("Around it · 64 KB", Around(0x4000), 0x10000, $"{e0.Name} ±64 KB");
        if (e0.Offset is { } plus && plus > 0 && plus <= 0x100000)
        {
            m.Items.Add(new ToolStripSeparator());
            Item($"Whole structure · base 0x{e0.BaseAddress:X8} + 0x{plus:X}", e0.BaseAddress, plus + 4, $"{e0.Name} structure");
            Item($"Next element · 0x{addr:X8} + 0x{plus:X}", addr, plus, $"{e0.Name} [1]");
        }
        if (sel.Count > 1)
        {
            uint lo = sel.Min(FinalAddress), hi = sel.Max(FinalAddress);
            if (hi - lo <= 0x1000000)
            {
                m.Items.Add(new ToolStripSeparator());
                Item($"Span of {sel.Count} selected · 0x{Down(lo, 0x10):X8} – 0x{hi + 4:X8} ({hi + 4 - Down(lo, 0x10):N0} bytes)", Down(lo, 0x10), hi + 4 - Down(lo, 0x10), $"{sel.Count} selected offsets");
            }
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Custom region…", null, (_, _) =>
        {
            var r = Prompt.ShowTwo(FindForm(), $"Monitor {name}", "Start address", $"0x{Down(addr, 0x10):X8}", "Size (bytes, decimal or 0x hex)", "0x100");
            if (r == null) return;
            uint start = XBDMClient.ParseUInt(r.Value.First.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? r.Value.First : "0x" + r.Value.First);
            uint size = XBDMClient.ParseUInt(r.Value.Second);
            if (start < 0x80000000 || size == 0 || size > 0x4000000) { _host.Toast("Enter an address ≥ 0x80000000 and a size up to 64 MB", ToastHost.Kind.Warning); return; }
            _host.MonitorRegion(start, size, name);
        });
        Scripting.ScriptIde.StyleMenu(m);
        m.Show(anchor, at ?? new Point(0, anchor.Height));
    }

    private void WatchSelected()
    {
        foreach (var e in SelectedEntries().Where(e => e.BaseAddress >= 0x80000000).Take(50)) _host.AddWatch(FinalAddress(e), e.Name);
    }

    private async Task ApplySelectedAsync()
    {
        var c = _host.Client;
        if (c == null || !c.IsConnected) { _host.Toast("Connect to the console first", ToastHost.Kind.Warning); return; }
        var patches = SelectedEntries().Select(e => (e, bytes: OffsetTextFormat.ParseBytes(e.ByteValues))).Where(x => x.bytes != null && x.e.BaseAddress >= 0x80000000).ToList();
        if (patches.Count == 0) { _host.Toast("Select entries that have Bytes to write", ToastHost.Kind.Info); return; }
        if (_selected != null && _running != null && !_selected.Matches(_running) &&
            MessageBox.Show(FindForm(), $"The selected offsets belong to {_selected.Name}, but {_running} is running.\n\nWrite anyway?", "Different game running",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        int ok = 0;
        foreach (var (e, bytes) in patches)
        {
            if (await c.SetMemoryAsync(FinalAddress(e), bytes!)) ok++;
            else _host.Log($"❌ Failed to write {e.Name} @ 0x{FinalAddress(e):X8}");
        }
        _host.Log($"✅ Applied {ok}/{patches.Count} patch(es)");
        _host.Toast($"Applied {ok} of {patches.Count} patch(es)", ok == patches.Count ? ToastHost.Kind.Success : ToastHost.Kind.Warning);
    }

    private void CopySelected()
    {
        var s = SelectedEntries();
        if (s.Count == 0) return;
        var text = OffsetTextFormat.Write(new Dictionary<string, string>(), s.Select(ToEntry));
        try { Clipboard.SetText(text); _host.Toast($"Copied {s.Count} offset(s)", ToastHost.Kind.Success); } catch { }
    }

    private void InsertSelectedIntoScript()
    {
        var s = SelectedEntries();
        if (s.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"// {_selected?.Name} · {_selected?.Current?.Label}");
        foreach (var e in s)
        {
            var id = Scripting.ScriptIde.NormalizeName(e.Name);
            if (id.Length == 0 || char.IsDigit(id[0])) id = "_" + id;
            sb.AppendLine($"var {id} = 0x{FinalAddress(e):X8};" + (e.ByteValues != null ? $"   // bytes: {e.ByteValues}" : ""));
        }
        _host.InsertScript(sb.ToString());
    }

    private static OffsetTextFormat.Entry ToEntry(OffsetManager.OffsetEntry e) =>
        new(e.Category, e.Name, e.BaseAddress, e.Offset, e.ByteValues);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _watch.Dispose();
            _filterDebounce.Dispose();
            _covers.Dispose();
            _lastPull = null;
        }
        base.Dispose(disposing);
    }
}
