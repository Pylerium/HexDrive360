using System.Runtime.InteropServices;
using MemoryEditor.Library;
using MemoryEditor.Runtime;
using MemoryEditor.Runtime.Toolkit;
using MemoryEditor.Scripting;
using MemoryEditor.UI;
using MemoryEditor.Xex;

namespace MemoryEditor;

/// <summary>
/// Application shell: window chrome, navigation, page hosting, output console, connection lifecycle,
/// keyboard shortcuts, command palette, layout persistence and the <see cref="IAppHost"/> service surface
/// that feature pages (Runtime Offsets, XEX tools, Script IDE) use to talk to the rest of the app.
///
/// The feature logic itself still lives in MainForm.cs (memory viewer, scanner, recorder, offset database,
/// static analysis, registers, GUI builder, tools); the shell only re-hosts those pages.
/// </summary>
public partial class MainForm : IOffsetDatabaseHost
{
    // ───────────────────────────── page model ─────────────────────────────

    private sealed record PageDef(string Key, string Title, string Subtitle, string Glyph, string Group, Func<Control> Factory);

    private readonly List<PageDef> _pageDefs = new();
    private readonly Dictionary<string, Control> _pages = new();
    private string? _currentPage;

    // ───────────────────────────── shell controls ─────────────────────────────

    private NavRail _nav = null!;
    private Panel _contentHost = null!;
    private Label _pageTitle = null!;
    private Label _pageSubtitle = null!;
    private StatusPill _gamePill = null!;
    private Label _statusLeft = null!;
    private Label _statusRight = null!;
    private Splitter _consoleSplitter = null!;
    private TextBox _consoleFilter = null!;
    private Label _consoleCount = null!;
    private IconButton _btnFollow = null!;
    private ToastHost? _toasts;
    private ScriptIde? _scriptIde;
    private RuntimeOffsetsPage? _runtimePage;
    private XexToolsPage? _xexPage;
    private GameLibraryPage? _libraryPage;
    private RuntimeToolkitPage? _toolkitPage;

    // auto-reconnect (console dropped the debug link, e.g. while switching titles)
    private CancellationTokenSource? _reconnectCts;
    private (string Host, int Port)? _lastTarget;

    private const int HeaderHeight = 56;
    private const int ConsoleHeaderHeight = 36;

    // ───────────────────────────── state ─────────────────────────────

    private bool _connecting;
    private bool _lastConnected;
    private string _consoleName = "";
    private string _startPage = "memory";
    private bool _startNavCollapsed;

    // logging
    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MemoryEditor.log");
    private readonly LogSink _log = new(LogPath);
    private readonly List<LogEntry> _logHistory = new();
    private readonly List<LogEntry> _drainBuffer = new();
    private System.Windows.Forms.Timer? _logFlushTimer;
    private System.Windows.Forms.Timer? _filterDebounce;
    private bool _followOutput = true;
    private int _consoleLines;
    private const int MaxHistory = 6000;
    private const int MaxConsoleLines = 4000;

    // offset-name resolution cache for the script IDE (normalized name → address)
    private Dictionary<string, uint>? _offsetLookup;
    private (int Static, int Runtime, int Pointers) _offsetLookupKey;

    // ═════════════════════════════════════════════════════════════════════════
    //  construction
    // ═════════════════════════════════════════════════════════════════════════

    private void InitializeComponent()
    {
        SuspendLayout();

        Text = "HexDrive360";
        Size = new Size(1600, 1000);
        MinimumSize = new Size(1200, 720);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.UI;
        AllowDrop = true;
        Theme.ApplyWindowChrome(this);

        _toasts = new ToastHost(this) { TopOffset = HeaderHeight + 12 };
        _freezer = new FreezeService(() => _xbdmClient) { Log = LogMessage };
        _freezer.Changed += () => { if (IsHandleCreated && !IsDisposed) BeginInvoke(UpdateStatusBar); };
        CrashGuard.Report = (summary, path) =>
        {
            LogMessage($"🛡️ Recovered from an internal error – {summary}" + (path.Length > 0 ? $"  (report: {path})" : ""));
            if (IsHandleCreated && !IsDisposed)
                try { BeginInvoke(() => Toast("An internal error was caught – the app keeps running. Details are in the output console.", ToastHost.Kind.Error)); } catch { }
        };

        var header = BuildHeader();
        var statusBar = BuildStatusBar();
        _nav = BuildNav();

        // body = page header + page content + output console (console sits to the right of the nav rail)
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Tag = "keep" };
        var pageHeader = BuildPageHeader();
        _contentHost = new Panel
        {
            Name = "contentHost",
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(16, 0, 16, 12),
            Tag = "keep",
        };
        consolePanel = BuildConsole();
        _consoleSplitter = new Splitter
        {
            Dock = DockStyle.Bottom,
            Height = 5,
            BackColor = Theme.Bg,
            MinSize = 90,
            MinExtra = 220,
            Cursor = Cursors.HSplit,
        };
        _consoleSplitter.Paint += (_, e) =>
        {
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawLine(p, 0, 2, _consoleSplitter.Width, 2);
        };
        _consoleSplitter.SplitterMoved += (_, _) =>
        {
            if (_consoleExpanded && consolePanel != null) _consoleExpandedHeight = consolePanel.Height;
        };

        // docking: last added is docked first (outermost)
        body.Controls.Add(_contentHost);
        body.Controls.Add(pageHeader);
        body.Controls.Add(_consoleSplitter);
        body.Controls.Add(consolePanel);

        Controls.Add(body);
        Controls.Add(_nav);
        Controls.Add(header);
        Controls.Add(statusBar);

        RegisterPages();
        BuildPages();

        // restyle the legacy pages (light Material colors → dark theme) exactly once
        Theme.Apply(this);
        txtIpAddress.Font = txtPort.Font = Theme.Mono; // the theme pass normalises non-Consolas text boxes to UI font

        foreach (var c in new Control[] { header, _nav, pageHeader, statusBar, _contentHost })
        {
            c.AllowDrop = true;
            c.DragEnter += OnShellDragEnter;
            c.DragDrop += OnShellDragDrop;
        }
        DragEnter += OnShellDragEnter;
        DragDrop += OnShellDragDrop;

        _logFlushTimer = new System.Windows.Forms.Timer { Interval = 90 };
        _logFlushTimer.Tick += (_, _) => FlushLog();
        _logFlushTimer.Start();

        Shown += (_, _) =>
        {
            ApplyConsoleState();
            _nav.Collapsed = _startNavCollapsed;
            Navigate(_pages.ContainsKey(_startPage) ? _startPage : "memory");
            UpdateConnectionUi();
            FlushLog();
            if (_autoConnect && txtIpAddress.Text.Trim().Length > 0) _ = AutoConnectOnStartupAsync();
        };

        ResumeLayout(true);

        LogMessage("🚀 HexDrive360 initialized");
        LogMessage("💡 Ctrl+K command palette · Ctrl+1…9 switch pages · Ctrl+` toggle output · Ctrl+B collapse sidebar");
        LogMessage("💡 Drop a .xex onto the window to convert it to .exe and analyse it");
    }

    // ───────────────────────────── header ─────────────────────────────

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = HeaderHeight, BackColor = Theme.Surface, Padding = new Padding(8, 0, 12, 0), Tag = "keep" };
        header.Paint += (_, e) =>
        {
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        // left: sidebar toggle + brand
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(0), Tag = "keep" };
        var btnMenu = IconButton.Create("", Theme.Glyph.Menu, ButtonVariant.Ghost, (_, _) => ToggleNav(), "Collapse / expand sidebar (Ctrl+B)");
        btnMenu.Margin = new Padding(0, 12, 8, 0);
        var logo = Ui.Glyph(Theme.Glyph.Game, 17F, Theme.Accent);
        logo.Margin = new Padding(2, 16, 6, 0);
        var brand = new Label
        {
            Text = "HexDrive360", AutoSize = true, Font = Theme.Heading, ForeColor = Theme.Text,
            BackColor = Color.Transparent, Margin = new Padding(0, 11, 0, 0), Tag = "keep",
        };
        var brandSub = new Label
        {
            Text = "Xbox 360 · XBDM / JRPC2", AutoSize = true, Font = Theme.UISmall, ForeColor = Theme.TextDim,
            BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 0), Tag = "keep",
        };
        var brandStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0), Tag = "keep" };
        brandStack.Controls.Add(brand);
        brandStack.Controls.Add(brandSub);
        brandSub.Margin = new Padding(1, 0, 0, 0);
        brand.Margin = new Padding(0, 10, 0, 0);
        left.Controls.AddRange(new Control[] { btnMenu, logo, brandStack });

        // right: game / connection / target / actions
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Tag = "keep" };

        _gamePill = new StatusPill { Glyph = Theme.Glyph.Game, DotColor = Theme.TextDim, Text = "No title", Margin = new Padding(0, 15, 8, 0) };
        _gamePill.Cursor = Cursors.Hand;
        _gamePill.Click += (_, _) => Navigate("runtime");

        lblStatus = new StatusPill { Text = "Disconnected", DotColor = Theme.TextDim, Margin = new Padding(0, 15, 14, 0) };

        var sep = new Panel { Width = 1, Height = 26, BackColor = Theme.Border, Margin = new Padding(0, 15, 14, 0), Tag = "keep" };

        var lblTarget = new Label
        {
            Text = "Console", AutoSize = true, Font = Theme.UISemibold, ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent, Margin = new Padding(0, 20, 6, 0), Tag = "keep",
        };
        txtIpAddress = new TextBox { Width = 138, Text = "192.168.1.14", Font = Theme.Mono, PlaceholderText = "IP address", Margin = new Padding(0, 16, 4, 0) };
        var colon = new Label { Text = ":", AutoSize = true, ForeColor = Theme.TextDim, BackColor = Color.Transparent, Margin = new Padding(0, 19, 2, 0), Tag = "keep" };
        txtPort = new TextBox { Width = 52, Text = "730", Font = Theme.Mono, PlaceholderText = "port", Margin = new Padding(0, 16, 10, 0) };
        foreach (var tb in new[] { txtIpAddress, txtPort })
            tb.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (!_lastConnected) BtnConnect_Click(this, EventArgs.Empty); } };

        btnConnect = IconButton.Create("Connect", Theme.Glyph.Plug, ButtonVariant.Primary, BtnConnect_Click, "Connect to the console (Enter)");
        btnConnect.Margin = new Padding(0, 12, 6, 0);
        btnDisconnect = IconButton.Create("Disconnect", Theme.Glyph.Unplug, ButtonVariant.Neutral, BtnDisconnect_Click, "Close the XBDM session");
        btnDisconnect.Margin = new Padding(0, 12, 12, 0);

        var btnPalette = IconButton.Create("", Theme.Glyph.Command, ButtonVariant.Ghost, (_, _) => ShowCommandPalette(), "Command palette (Ctrl+K)");
        btnPalette.Margin = new Padding(0, 12, 0, 0);

        right.Controls.AddRange(new Control[] { _gamePill, lblStatus, sep, lblTarget, txtIpAddress, colon, txtPort, btnConnect, btnDisconnect, btnPalette });

        header.Controls.Add(right);
        header.Controls.Add(left);

        // responsive: drop lower-priority items as the window narrows (portrait / small monitors)
        void Fit()
        {
            int avail = header.ClientSize.Width - header.Padding.Horizontal - 16;
            brandStack.Visible = lblTarget.Visible = _gamePill.Visible = sep.Visible = true;
            int Need() => left.PreferredSize.Width + right.PreferredSize.Width;
            if (Need() > avail) brandStack.Visible = false;
            if (Need() > avail) lblTarget.Visible = false;
            if (Need() > avail) { _gamePill.Visible = false; sep.Visible = false; }
        }
        header.Resize += (_, _) => Fit();
        header.HandleCreated += (_, _) => Fit();
        return header;
    }

    private Control BuildPageHeader()
    {
        var p = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Bg, Padding = new Padding(20, 0, 20, 0), Tag = "keep" };
        _pageTitle = new Label { AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, BackColor = Color.Transparent, Location = new Point(18, 12), Tag = "keep" };
        _pageSubtitle = new Label { AutoSize = true, Font = Theme.UI, ForeColor = Theme.TextMuted, BackColor = Color.Transparent, Location = new Point(20, 38), Tag = "keep" };
        p.Controls.Add(_pageTitle);
        p.Controls.Add(_pageSubtitle);
        return p;
    }

    private Control BuildStatusBar()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 26, BackColor = Theme.Surface, Padding = new Padding(12, 0, 12, 0), Tag = "keep" };
        bar.Paint += (_, e) =>
        {
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawLine(p, 0, 0, bar.Width, 0);
        };
        _statusLeft = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.UISmall, ForeColor = Theme.TextMuted, BackColor = Color.Transparent, Tag = "keep", AutoEllipsis = true };
        _statusRight = new Label { Dock = DockStyle.Right, Width = 620, TextAlign = ContentAlignment.MiddleRight, Font = Theme.UISmall, ForeColor = Theme.TextDim, BackColor = Color.Transparent, Tag = "keep", AutoEllipsis = true };
        bar.Controls.Add(_statusLeft);
        bar.Controls.Add(_statusRight);
        return bar;
    }

    // ───────────────────────────── navigation ─────────────────────────────

    private NavRail BuildNav()
    {
        var nav = new NavRail { Dock = DockStyle.Left };
        nav.ItemSelected += it => Navigate(it.Key);
        return nav;
    }

    private void RegisterPages()
    {
        void Page(string key, string title, string subtitle, string glyph, string group, Func<Control> factory) =>
            _pageDefs.Add(new PageDef(key, title, subtitle, glyph, group, factory));

        Page("memory", "Memory", "Read, write, freeze and diff live console memory", Theme.Glyph.Memory, "Live", CreateMemoryTab);
        Page("scan", "Value Scanner", "First / next scans, pointer scans, watch list and structure dissection", Theme.Glyph.Search, "Live", CreateAdvancedScanTab);
        Page("record", "Memory Recorder", "Record a region over time and surface the addresses that change", Theme.Glyph.Record, "Live", CreateRecordingTab);
        Page("registers", "Registers", "Inspect and edit PowerPC thread registers", Theme.Glyph.Registers, "Live", CreateRegisterViewerTab);
        Page("toolkit", "Runtime Tools", "Mod toggles, pointer chains, live graphs, disassembler, pattern scan, memory map, threads, console", Theme.Glyph.Wand, "Live",
            () => _toolkitPage = new RuntimeToolkitPage(this, () => _offsetManager.GetAllOffsets(), runtime: () => _runtimePage?.Offsets ?? Array.Empty<DiscoveredOffset>(), symbols:
                () => StaticOffsets.Select(o => (o.Name, o.Address)).Concat(RuntimeOffsetTuples().Select(o => (o.Name, o.Address)))) { Dock = DockStyle.Fill });

        Page("runtime", "Runtime Offsets", "Detect the running title and map its offsets automatically – works for any game", Theme.Glyph.Target, "Offsets",
            () => _runtimePage = new RuntimeOffsetsPage(this) { Dock = DockStyle.Fill });
        Page("offsets", "Game Library", "Every game and title update with all of its offsets – launch, pull live, port between TUs", Theme.Glyph.Game, "Offsets",
            () =>
            {
                _libraryPage = new GameLibraryPage(this, this, Adopt(CreateOffsetCalculatorTab())) { Dock = DockStyle.Fill };
                // frozen values belong to one game build: park them when the title changes, restore when it returns
                _libraryPage.TitleChanged += (key, name) =>
                {
                    _freezer?.SwitchTitle(key, name);
                    XBDMClient.ClearQuarantine();   // a new title has a new memory layout
                };
                return _libraryPage;
            });

        Page("script", "Script IDE", "Write, check, debug and run memory scripts", Theme.Glyph.Code, "Develop", CreateScriptingTab);
        Page("static", "Static Analysis", "Sections, strings, patterns and cross references of a binary", Theme.Glyph.Analyze, "Develop", CreateStaticAnalysisTab);
        Page("xex", "XEX → EXE", "Decrypt, decompress and rebuild XEX files as PE images – automatically", Theme.Glyph.Convert, "Develop",
            () => _xexPage = new XexToolsPage(this) { Dock = DockStyle.Fill });
        Page("build", "GUI Builder", "Generate a stand-alone trainer from selected offsets", Theme.Glyph.Build, "Develop", CreateBuildTab);
        Page("tools", "Tools", "Converters, calculators and utilities", Theme.Glyph.Tools, "Develop", CreateToolsTab);
    }

    private void BuildPages()
    {
        _contentHost.SuspendLayout();
        int i = 0;
        foreach (var def in _pageDefs)
        {
            i++;
            _nav.Add(def.Key, def.Title, def.Glyph, def.Group, i <= 9 ? $"Ctrl+{i}" : "");
            Control page;
            try
            {
                page = Adopt(def.Factory());
            }
            catch (Exception ex)
            {
                // one broken page must not prevent the application from starting
                LogMessage($"❌ Failed to build page '{def.Title}': {ex.Message}");
                page = Ui.Muted($"This page failed to load: {ex.Message}");
            }
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _pages[def.Key] = page;
            _contentHost.Controls.Add(page);
        }
        _contentHost.ResumeLayout(false);
    }

    /// <summary>
    /// Legacy pages are built as <see cref="TabPage"/>s. Re-parent their children into a scrollable panel so
    /// they can be hosted by the shell without a tab strip (all control references stay valid).
    /// </summary>
    private static Control Adopt(Control page)
    {
        if (page is not TabPage tp) return page;
        var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg, Name = tp.Name };
        Theme.DarkScrollbars(host);
        var children = tp.Controls.Cast<Control>().ToArray();
        tp.Controls.Clear();
        host.Controls.AddRange(children);
        tp.Dispose();
        return host;
    }

    public void Navigate(string pageKey)
    {
        if (InvokeRequired) { BeginInvoke(() => Navigate(pageKey)); return; }
        if (!_pages.TryGetValue(pageKey, out var page)) return;
        var def = _pageDefs.First(d => d.Key == pageKey);

        if (_currentPage != pageKey)
        {
            _contentHost.SuspendLayout();
            if (_currentPage == "toolkit") _toolkitPage?.OnPageHidden(); // stop live polling of hidden tools
            if (_currentPage != null && _pages.TryGetValue(_currentPage, out var old)) old.Visible = false;
            page.Visible = true;
            page.BringToFront();
            _contentHost.ResumeLayout(true);
            _currentPage = pageKey;
            if (pageKey == "toolkit") _toolkitPage?.OnPageShown();
        }

        _pageTitle.Text = def.Title;
        _pageSubtitle.Text = def.Subtitle;
        _nav.Select(pageKey, raise: false);

        // page specific behaviour preserved from the tabbed layout
        switch (pageKey)
        {
            case "offsets":
                if (_offsetManager.Count > 0 && dgvOffsets?.Rows.Count == 0) RefreshOffsetGrid();
                break;
            case "script":
                _scriptIde?.Focus();
                break;
        }
        UpdateStatusBar();
    }

    private void ToggleNav() => _nav.Collapsed = !_nav.Collapsed;

    private void NavigateRelative(int delta)
    {
        int idx = _pageDefs.FindIndex(d => d.Key == _currentPage);
        if (idx < 0) return;
        idx = (idx + delta + _pageDefs.Count) % _pageDefs.Count;
        Navigate(_pageDefs[idx].Key);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  output console
    // ═════════════════════════════════════════════════════════════════════════

    private Panel BuildConsole()
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = _consoleExpandedHeight, BackColor = Theme.Surface, Tag = "keep" };
        panel.Paint += (_, e) =>
        {
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(p, 0, 0, panel.Width - 1, panel.Height - 1);
        };

        var head = new Panel { Dock = DockStyle.Top, Height = ConsoleHeaderHeight, BackColor = Theme.Surface, Padding = new Padding(10, 0, 6, 0), Tag = "keep" };
        head.Paint += (_, e) =>
        {
            if (!_consoleExpanded) return;
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawLine(p, 1, head.Height - 1, head.Width - 2, head.Height - 1);
        };
        head.DoubleClick += (_, _) => BtnToggleConsole_Click(this, EventArgs.Empty);

        var titleFlow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Tag = "keep" };
        var glyph = Ui.Glyph(Theme.Glyph.Terminal, 10.5F, Theme.TextMuted);
        glyph.Margin = new Padding(0, 11, 6, 0);
        var title = new Label { Text = "Output", AutoSize = true, Font = Theme.UISemibold, ForeColor = Theme.Text, BackColor = Color.Transparent, Margin = new Padding(0, 10, 8, 0), Tag = "keep" };
        _consoleCount = new Label { Text = "", AutoSize = true, Font = Theme.UISmall, ForeColor = Theme.TextDim, BackColor = Color.Transparent, Margin = new Padding(0, 11, 0, 0), Tag = "keep" };
        titleFlow.Controls.AddRange(new Control[] { glyph, title, _consoleCount });
        titleFlow.DoubleClick += (_, _) => BtnToggleConsole_Click(this, EventArgs.Empty);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Tag = "keep" };
        _consoleFilter = new TextBox { Width = 200, PlaceholderText = "Filter output", Margin = new Padding(0, 6, 8, 0), Font = Theme.UI };
        _filterDebounce = new System.Windows.Forms.Timer { Interval = 160 };
        _filterDebounce.Tick += (_, _) => { _filterDebounce.Stop(); RebuildConsole(); };
        _consoleFilter.TextChanged += (_, _) => { _filterDebounce.Stop(); _filterDebounce.Start(); };
        _consoleFilter.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { _consoleFilter.Clear(); e.SuppressKeyPress = true; } };

        _btnFollow = IconButton.Create("", Theme.Glyph.Pin, ButtonVariant.Ghost, (_, _) => SetFollow(!_followOutput), "Auto-scroll to newest output");
        var btnCopy = IconButton.Create("", Theme.Glyph.Copy, ButtonVariant.Ghost, (_, _) => CopyConsole(), "Copy output");
        btnClearConsole = IconButton.Create("", Theme.Glyph.Clear, ButtonVariant.Ghost, (_, _) => ClearConsole(), "Clear output");
        btnToggleConsole = IconButton.Create("", Theme.Glyph.ChevronDown, ButtonVariant.Ghost, BtnToggleConsole_Click, "Collapse / expand output (Ctrl+`)");
        foreach (var b in new[] { _btnFollow, btnCopy, btnClearConsole, btnToggleConsole })
        {
            b.Height = 28;
            b.Margin = new Padding(0, 4, 2, 0);
        }
        actions.Controls.AddRange(new Control[] { _consoleFilter, _btnFollow, btnCopy, btnClearConsole, btnToggleConsole });

        head.Controls.Add(actions);
        head.Controls.Add(titleFlow);

        txtConsole = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.CodeBg,
            ForeColor = Theme.Text,
            Font = Theme.MonoSmall,
            DetectUrls = false,
            HideSelection = false,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
        };
        Theme.Exclude(txtConsole);
        Theme.DarkScrollbars(txtConsole);
        // a user scrolling up pauses auto-follow; scrolling back to the end resumes it
        txtConsole.VScroll += (_, _) => { if (!_suppressScrollTracking) SetFollow(IsConsoleAtBottom()); };

        var inset = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 4, 4), BackColor = Theme.CodeBg, Tag = "keep" };
        inset.Controls.Add(txtConsole);

        panel.Controls.Add(inset);
        panel.Controls.Add(head);
        SetFollow(true);
        return panel;
    }

    private bool _suppressScrollTracking;

    private void SetFollow(bool follow)
    {
        if (_followOutput == follow && _btnFollow.Variant == (follow ? ButtonVariant.Primary : ButtonVariant.Ghost)) return;
        _followOutput = follow;
        _btnFollow.Variant = follow ? ButtonVariant.Primary : ButtonVariant.Ghost;
        if (follow && txtConsole.IsHandleCreated) ScrollConsoleToEnd();
    }

    private void BtnToggleConsole_Click(object? sender, EventArgs e)
    {
        _consoleExpanded = !_consoleExpanded;
        ApplyConsoleState();
    }

    private void ApplyConsoleState()
    {
        if (consolePanel == null || btnToggleConsole == null) return;
        SuspendLayout();
        consolePanel.Height = _consoleExpanded ? Math.Max(120, _consoleExpandedHeight) : ConsoleHeaderHeight;
        _consoleSplitter.Visible = _consoleExpanded;
        txtConsole.Parent!.Visible = _consoleExpanded;
        _consoleFilter.Visible = _btnFollow.Visible = btnClearConsole.Visible = _consoleExpanded;
        btnToggleConsole.Glyph = _consoleExpanded ? Theme.Glyph.ChevronDown : Theme.Glyph.ChevronUp;
        consolePanel.Invalidate(true);
        ResumeLayout(true);
        if (_consoleExpanded) ScrollConsoleToEnd();
    }

    private void ClearConsole()
    {
        _logHistory.Clear();
        txtConsole.Clear();
        _consoleLines = 0;
        UpdateConsoleCount();
    }

    private void CopyConsole()
    {
        var text = txtConsole.SelectionLength > 0 ? txtConsole.SelectedText : txtConsole.Text;
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); Toast("Output copied to clipboard", ToastHost.Kind.Success); } catch { }
    }

    /// <summary>
    /// Thread-safe logging entry point used by every subsystem. Never blocks the caller: the line is queued and
    /// rendered by the UI flush timer in batches, and written to <c>MemoryEditor.log</c> on a background task.
    /// </summary>
    private void LogMessage(string message) => _log.Write(message);

    private void FlushLog()
    {
        if (txtConsole == null || txtConsole.IsDisposed || !_log.HasPending) return;
        _drainBuffer.Clear();
        _log.Drain(_drainBuffer);
        if (_drainBuffer.Count == 0) return;

        _logHistory.AddRange(_drainBuffer);
        if (_logHistory.Count > MaxHistory) _logHistory.RemoveRange(0, _logHistory.Count - MaxHistory);

        var filter = _consoleFilter.Text.Trim();
        var visible = filter.Length == 0 ? _drainBuffer : _drainBuffer.Where(e => Matches(e, filter)).ToList();

        if (_consoleLines + visible.Count > MaxConsoleLines + 500)
        {
            RebuildConsole(); // amortised trim: rebuild from history only every ~500 lines past the cap
        }
        else if (visible.Count > 0)
        {
            using (new RedrawSuspender(txtConsole))
            {
                foreach (var e in visible) AppendEntry(e);
            }
            _consoleLines += visible.Count;
            if (_followOutput) ScrollConsoleToEnd();
        }

        // surface hard errors from background work while the console is collapsed
        if (!_consoleExpanded)
        {
            var err = _drainBuffer.LastOrDefault(e => e.Level == LogLevel.Error);
            if (err.Text != null) _toasts?.Show(Theme.StripEmoji(err.Text), ToastHost.Kind.Error);
        }
        UpdateConsoleCount();
    }

    private static bool Matches(LogEntry e, string filter) => e.Text.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void RebuildConsole()
    {
        var filter = _consoleFilter.Text.Trim();
        var src = filter.Length == 0 ? _logHistory : _logHistory.Where(e => Matches(e, filter)).ToList();
        int skip = Math.Max(0, src.Count - MaxConsoleLines);
        using (new RedrawSuspender(txtConsole))
        {
            txtConsole.Clear();
            for (int i = skip; i < src.Count; i++) AppendEntry(src[i]);
        }
        _consoleLines = src.Count - skip;
        ScrollConsoleToEnd();
        UpdateConsoleCount();
    }

    private void AppendEntry(LogEntry e)
    {
        txtConsole.SelectionStart = txtConsole.TextLength;
        txtConsole.SelectionLength = 0;
        txtConsole.SelectionColor = Theme.TextDim;
        txtConsole.AppendText(e.Time.ToString("HH:mm:ss  "));
        txtConsole.SelectionColor = e.Level switch
        {
            LogLevel.Error => Theme.Danger,
            LogLevel.Warning => Theme.Warning,
            LogLevel.Success => Theme.Success,
            LogLevel.Trace => Theme.TextMuted,
            _ => Theme.Text,
        };
        txtConsole.AppendText(e.Text + "\n");
    }

    private void UpdateConsoleCount()
    {
        int errors = 0, warnings = 0;
        foreach (var e in _logHistory)
        {
            if (e.Level == LogLevel.Error) errors++;
            else if (e.Level == LogLevel.Warning) warnings++;
        }
        var parts = new List<string> { $"{_logHistory.Count:N0} lines" };
        if (errors > 0) parts.Add($"{errors} error{(errors == 1 ? "" : "s")}");
        if (warnings > 0) parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
        _consoleCount.Text = string.Join(" · ", parts);
    }

    private void ScrollConsoleToEnd()
    {
        if (!txtConsole.IsHandleCreated) return;
        _suppressScrollTracking = true;
        try
        {
            txtConsole.SelectionStart = txtConsole.TextLength;
            txtConsole.SelectionLength = 0;
            txtConsole.ScrollToCaret();
        }
        finally { _suppressScrollTracking = false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SCROLLINFO { public int cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos; }
    [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO si);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

    private bool IsConsoleAtBottom()
    {
        if (!txtConsole.IsHandleCreated) return true;
        var si = new SCROLLINFO { cbSize = Marshal.SizeOf<SCROLLINFO>(), fMask = 0x17 /* SIF_ALL */ };
        if (!GetScrollInfo(txtConsole.Handle, 1 /* SB_VERT */, ref si)) return true;
        return si.nPos + si.nPage >= si.nMax - 2;
    }

    /// <summary>Suspends painting of a control while it is updated in bulk (WM_SETREDRAW).</summary>
    private readonly struct RedrawSuspender : IDisposable
    {
        private readonly Control _c;
        public RedrawSuspender(Control c)
        {
            _c = c;
            if (c.IsHandleCreated) SendMessage(c.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
        }
        public void Dispose()
        {
            if (_c == null || !_c.IsHandleCreated) return;
            SendMessage(_c.Handle, 0x000B, new IntPtr(1), IntPtr.Zero);
            _c.Invalidate();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  connection lifecycle
    // ═════════════════════════════════════════════════════════════════════════

    private async void BtnConnect_Click(object? sender, EventArgs e)
    {
        if (_connecting || (_xbdmClient?.IsConnected ?? false)) return;

        var host = txtIpAddress.Text.Trim();
        if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            Toast("Enter the console's IP address or host name", ToastHost.Kind.Warning);
            txtIpAddress.Focus();
            return;
        }
        if (!int.TryParse(txtPort.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            Toast("Port must be a number between 1 and 65535 (XBDM uses 730)", ToastHost.Kind.Warning);
            txtPort.Focus();
            return;
        }
        _reconnectCts?.Cancel();
        await ConnectCoreAsync(host, port, silent: false);
    }

    /// <summary>Opens a session; shared by the Connect button and auto-reconnect.</summary>
    private async Task<bool> ConnectCoreAsync(string host, int port, bool silent)
    {
        if (_connecting) return false;
        _connecting = true;
        UpdateConnectionUi();
        try
        {
            DetachClient(); // never leak a previous session (socket + event handlers)
            if (!silent) LogMessage($"Connecting to Xbox 360 at {host}:{port}...");
            var client = new XBDMClient(host, port);
            client.MessageReceived += OnClientMessage;
            client.ConnectionStatusChanged += OnClientStatusChanged;
            _xbdmClient = client;

            if (await client.ConnectAsync())
            {
                _consoleName = await client.GetConsoleName();
                LogMessage($"✅ Connected to: {_consoleName}");

                _memoryEngine.SetClient(client);
                _scriptExecutor = new ScriptExecutor(client, LogMessage);
                _lastConnected = true;
                _lastTarget = (host, port);

                Toast($"{(silent ? "Reconnected to" : "Connected to")} {(_consoleName.Length > 0 ? _consoleName : host)}{(client.IsJRPCConnected ? " · JRPC2 available" : "")}", ToastHost.Kind.Success);
                SaveLayout(); // remember the target
                _connecting = false;
                UpdateConnectionUi();

                // The Game Library identifies the title and pulls offsets; the Runtime page only auto-discovers on its
                // own when library pulling is switched off (avoids dumping the same module twice).
                if (_libraryPage != null) _ = RunGuardedAsync(_libraryPage.OnConnectedAsync, "Game identification");
                if (_runtimePage != null && (_libraryPage == null || !_libraryPage.AutoPullOnConnect))
                    _ = RunGuardedAsync(_runtimePage.OnConnectedAsync, "Title detection");
                return true;
            }

            DetachClient();
            if (!silent) Toast($"Could not reach {host}:{port}. Is the console on and XBDM loaded?", ToastHost.Kind.Error);
            return false;
        }
        catch (Exception ex)
        {
            DetachClient();
            if (!silent)
            {
                LogMessage($"❌ Connection failed: {ex.Message}");
                Toast($"Connection failed: {ex.Message}", ToastHost.Kind.Error);
            }
            return false;
        }
        finally
        {
            _connecting = false;
            UpdateConnectionUi();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  automation: auto-connect + emergency stop
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Connect to the last console on startup (Ctrl+K → "Auto-connect on startup" toggles it).</summary>
    private bool _autoConnect = true;

    private async Task AutoConnectOnStartupAsync()
    {
        var host = txtIpAddress.Text.Trim();
        if (!int.TryParse(txtPort.Text.Trim(), out var port) || port is <= 0 or > 65535) port = 730;
        if (_xbdmClient?.IsConnected == true || _connecting) return;
        LogMessage($"🔌 Auto-connecting to {host}:{port}…");
        if (await ConnectCoreAsync(host, port, silent: true)) return;
        // console still booting / XBDM not loaded yet: keep trying quietly in the background
        LogMessage("⏳ Console not reachable yet – retrying in the background (Ctrl+K → Disconnect stops it)");
        _lastTarget = (host, port);
        _ = AutoReconnectAsync();
    }

    /// <summary>
    /// Panic button (Ctrl+Shift+Q): stops everything that writes to or polls the console – frozen values, the memory
    /// recorder, live refresh, running scripts – and resumes the console if a debugger stop left it halted.
    /// </summary>
    private async Task EmergencyStopAsync()
    {
        int frozen = _freezer?.Count ?? 0;
        _freezer?.Clear();
        try { if (_memoryEngine.GetActiveRecording() != null) BtnStopRecording_Click(this, EventArgs.Empty); } catch { }
        _autoRefreshTimer?.Stop();
        try { _scriptIde?.Stop(); } catch { }
        if (_xbdmClient is { IsConnected: true } c)
            try { await c.SendCommandAsync("go"); } catch { }
        LogMessage($"🛑 Emergency stop – {frozen} frozen value(s) released, recorder and live refresh stopped, console resumed");
        Toast("Emergency stop: all live writes and polling stopped", ToastHost.Kind.Warning);
        UpdateStatusBar();
    }

    /// <summary>Retries the last target for ~90 s after an unexpected drop (title switch, console restart, Wi-Fi blip).</summary>
    private async Task AutoReconnectAsync()
    {
        if (_lastTarget is not { } target) return;
        _reconnectCts?.Cancel();
        var cts = _reconnectCts = new CancellationTokenSource();
        LogMessage($"🔁 Reconnecting to {target.Host}:{target.Port} automatically…");
        for (int attempt = 1; attempt <= 30 && !cts.IsCancellationRequested; attempt++)
        {
            try { await Task.Delay(3000, cts.Token); } catch (OperationCanceledException) { return; }
            if (_xbdmClient?.IsConnected == true || IsDisposed) return;
            _statusLeft.Text = $"Connection lost – reconnecting (attempt {attempt}/30)…";
            if (await ConnectCoreAsync(target.Host, target.Port, silent: true)) return;
        }
        if (!cts.IsCancellationRequested) Toast("Could not reconnect to the console – press Connect when it is back", ToastHost.Kind.Warning);
    }

    private void BtnDisconnect_Click(object? sender, EventArgs e)
    {
        _reconnectCts?.Cancel();
        _lastTarget = null; // user intent: do not auto-reconnect
        if (_xbdmClient == null) return;
        _scriptIde?.Stop();
        DetachClient();
        LogMessage("Disconnected from Xbox 360");
        UpdateConnectionUi();
    }

    private void DetachClient()
    {
        var c = _xbdmClient;
        if (c == null) return;
        c.ConnectionStatusChanged -= OnClientStatusChanged;
        c.MessageReceived -= OnClientMessage;
        try { c.Disconnect(); } catch { }
        try { c.Dispose(); } catch { }
        _xbdmClient = null;
        _lastConnected = false;
        _runtimePage?.OnDisconnected();
        _libraryPage?.OnDisconnected();
    }

    private void OnClientMessage(object? sender, string msg) => LogMessage(msg);

    private void OnClientStatusChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            bool now = _xbdmClient?.IsConnected ?? false;
            if (_lastConnected && !now && !_connecting)
            {
                Toast("Connection to the console was lost – reconnecting automatically", ToastHost.Kind.Warning);
                _runtimePage?.OnDisconnected();
                _libraryPage?.OnDisconnected();
                _ = AutoReconnectAsync();
            }
            _lastConnected = now;
            UpdateConnectionUi();
        });
    }

    private void UpdateConnectionUi()
    {
        if (lblStatus == null) return;
        bool connected = _xbdmClient?.IsConnected ?? false;

        if (_connecting)
        {
            lblStatus.Text = "Connecting…";
            lblStatus.DotColor = Theme.Warning;
        }
        else if (connected)
        {
            lblStatus.Text = _xbdmClient!.IsJRPCConnected ? "Connected · JRPC2" : "Connected";
            lblStatus.DotColor = Theme.Success;
        }
        else
        {
            lblStatus.Text = "Disconnected";
            lblStatus.DotColor = Theme.TextDim;
        }

        btnConnect.Enabled = !connected && !_connecting;
        btnDisconnect.Enabled = connected && !_connecting;
        btnDisconnect.Variant = connected ? ButtonVariant.Danger : ButtonVariant.Neutral;
        txtIpAddress.Enabled = txtPort.Enabled = !connected && !_connecting;
        _nav.SetBadge("memory", connected ? "live" : "");

        _runtimePage?.OnConnectionChanged();
        _libraryPage?.OnConnectionChanged();
        _toolkitPage?.OnConnectionChanged(connected);
        UpdateGamePill();
        UpdateStatusBar();
    }

    private void UpdateGamePill()
    {
        var g = _runtimePage?.Game;
        bool connected = _xbdmClient?.IsConnected ?? false;
        string text = g != null ? g.ToString() : connected ? "Detecting title…" : "No title";
        if (_gamePill.Text != text) _gamePill.Text = text;
        _gamePill.DotColor = g != null ? Theme.Accent : Theme.TextDim;
        int runtime = _runtimePage?.Offsets.Count ?? 0;
        _nav.SetBadge("runtime", runtime > 0 ? (runtime > 9999 ? $"{runtime / 1000}k" : runtime.ToString("N0")) : "");
    }

    private void UpdateStatusBar()
    {
        if (_statusLeft == null) return;
        var c = _xbdmClient;
        _statusLeft.Text = c is { IsConnected: true }
            ? $"Connected to {(_consoleName.Length > 0 ? _consoleName : c.IpAddress)} ({c.IpAddress}:{c.Port})" + (_runtimePage?.Game is { } g ? $"  ·  {g}" : "")
            : _connecting ? "Connecting…" : "Not connected – enter the console IP and press Connect";

        var parts = new List<string>();
        int staticCount = _offsetManager.Count;
        int runtimeCount = _runtimePage?.Offsets.Count ?? 0;
        parts.Add($"{staticCount:N0} database offsets");
        if (runtimeCount > 0) parts.Add($"{runtimeCount:N0} runtime offsets");
        if (_freezer?.Count > 0) parts.Add($"{_freezer.Count} frozen");
        if (c is { IsConnected: true })
        {
            parts.Add($"↓ {FormatFileSize(c.BytesRead)}  ↑ {FormatFileSize(c.BytesWritten)}");
            if (c.LastLatencyMs > 0) parts.Add($"{c.LastLatencyMs:0} ms");
        }
        _statusRight.Text = string.Join("   ·   ", parts);
    }

    private void SetupStatusTimer()
    {
        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (s, e) =>
        {
            bool connected = _xbdmClient?.IsConnected ?? false;
            if (connected != _lastConnected && !_connecting)
            {
                if (_lastConnected) { _runtimePage?.OnDisconnected(); _libraryPage?.OnDisconnected(); }
                _lastConnected = connected;
                UpdateConnectionUi();
            }
            else
            {
                UpdateGamePill();
                UpdateStatusBar();
            }
        };
        _statusTimer.Start();

        _autoRefreshTimer = new System.Windows.Forms.Timer();
        bool refreshing = false;
        _autoRefreshTimer.Tick += async (s, e) =>
        {
            // never let refreshes pile up when the console answers slower than the interval
            if (refreshing) return;
            if (_xbdmClient != null && _xbdmClient.IsConnected && !string.IsNullOrEmpty(txtAddress.Text))
            {
                refreshing = true;
                try { await RefreshMemoryView(); }
                finally { refreshing = false; }
            }
        };
    }

    private async Task RunGuardedAsync(Func<Task> work, string what)
    {
        try { await work(); }
        catch (Exception ex) { LogMessage($"❌ {what} failed: {ex.Message}"); }
        finally { if (!IsDisposed) { UpdateGamePill(); UpdateStatusBar(); } }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  layout persistence
    // ═════════════════════════════════════════════════════════════════════════

    private static string LayoutPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout.json");

    private void SaveLayout()
    {
        try
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var settings = new Dictionary<string, string>
            {
                ["WindowWidth"] = bounds.Width.ToString(),
                ["WindowHeight"] = bounds.Height.ToString(),
                ["WindowState"] = (WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState).ToString(),
                ["ConsoleExpanded"] = _consoleExpanded.ToString(),
                ["ConsoleHeight"] = _consoleExpandedHeight.ToString(),
                ["ConsoleIp"] = txtIpAddress.Text.Trim(),
                ["ConsolePort"] = txtPort.Text.Trim(),
                ["NavCollapsed"] = _nav.Collapsed.ToString(),
                ["Page"] = _currentPage ?? "memory",
                ["AutoConnect"] = _autoConnect.ToString(),
            };
            var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(LayoutPath, json);
        }
        catch
        {
            // layout is a convenience – never fail because of it
        }
    }

    /// <summary>Keeps the window inside the current screen's working area (high-DPI screens scale 1600×1000 past the edge).</summary>
    private void FitToScreen()
    {
        var area = Screen.FromControl(this).WorkingArea;
        if (Width > area.Width || Height > area.Height)
        {
            var target = new Size(Math.Min(Width, (int)(area.Width * 0.94)), Math.Min(Height, (int)(area.Height * 0.94)));
            // relax the minimum first, otherwise it blocks the shrink on small / portrait monitors
            MinimumSize = new Size(Math.Min(MinimumSize.Width, target.Width), Math.Min(MinimumSize.Height, target.Height));
            Size = target;
        }
        if (WindowState == FormWindowState.Normal)
            Location = new Point(area.X + Math.Max(0, (area.Width - Width) / 2), area.Y + Math.Max(0, (area.Height - Height) / 2));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToScreen();
    }

    private void LoadSavedLayout()
    {
        try
        {
            if (!File.Exists(LayoutPath)) return;
            var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LayoutPath));
            if (settings == null) return;

            var screen = Screen.FromControl(this).WorkingArea;
            if (settings.TryGetValue("WindowWidth", out var width) && int.TryParse(width, out var w))
                Width = Math.Clamp(w, MinimumSize.Width, Math.Max(MinimumSize.Width, screen.Width));
            if (settings.TryGetValue("WindowHeight", out var height) && int.TryParse(height, out var h))
                Height = Math.Clamp(h, MinimumSize.Height, Math.Max(MinimumSize.Height, screen.Height));
            if (settings.TryGetValue("WindowState", out var state) && Enum.TryParse<FormWindowState>(state, out var ws) && ws != FormWindowState.Minimized)
                WindowState = ws;
            if (settings.TryGetValue("ConsoleExpanded", out var expanded) && bool.TryParse(expanded, out var exp))
                _consoleExpanded = exp;
            if (settings.TryGetValue("ConsoleHeight", out var consoleH) && int.TryParse(consoleH, out var ch))
                _consoleExpandedHeight = Math.Clamp(ch, 120, 900);
            if (settings.TryGetValue("ConsoleIp", out var ip) && ip.Length > 0)
                txtIpAddress.Text = ip;
            if (settings.TryGetValue("ConsolePort", out var port) && port.Length > 0)
                txtPort.Text = port;
            if (settings.TryGetValue("NavCollapsed", out var nc) && bool.TryParse(nc, out var ncb))
                _startNavCollapsed = ncb;
            if (settings.TryGetValue("Page", out var page) && page.Length > 0)
                _startPage = page;
            if (settings.TryGetValue("AutoConnect", out var ac) && bool.TryParse(ac, out var acb))
                _autoConnect = acb;
        }
        catch
        {
            // use defaults
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  keyboard, command palette, drag & drop
    // ═════════════════════════════════════════════════════════════════════════

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.K:
            case Keys.Control | Keys.Shift | Keys.P:
                ShowCommandPalette();
                return true;
            case Keys.Control | Keys.B:
                ToggleNav();
                return true;
            case Keys.Control | Keys.Shift | Keys.Q:
                _ = EmergencyStopAsync();
                return true;
            case Keys.Control | Keys.Oemtilde:
                BtnToggleConsole_Click(this, EventArgs.Empty);
                return true;
            case Keys.Control | Keys.Tab:
                NavigateRelative(+1);
                return true;
            case Keys.Control | Keys.Shift | Keys.Tab:
                NavigateRelative(-1);
                return true;
        }

        var key = keyData & Keys.KeyCode;
        if ((keyData & Keys.Modifiers) == Keys.Control && key >= Keys.D1 && key <= Keys.D9)
        {
            int idx = key - Keys.D1;
            if (idx < _pageDefs.Count) { Navigate(_pageDefs[idx].Key); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ShowCommandPalette() => CommandPalette.ShowFor(this, BuildCommands);

    private IEnumerable<CommandPalette.Command> BuildCommands()
    {
        int i = 0;
        foreach (var def in _pageDefs)
        {
            i++;
            var key = def.Key;
            yield return new CommandPalette.Command
            {
                Title = $"Go to {def.Title}", Category = "Page", Glyph = def.Glyph,
                Hint = i <= 9 ? $"Ctrl+{i}" : def.Subtitle, Run = () => Navigate(key),
            };
        }

        bool connected = _xbdmClient?.IsConnected ?? false;
        if (!connected)
            yield return new CommandPalette.Command { Title = "Connect to console", Category = "Console", Glyph = Theme.Glyph.Plug, Hint = $"{txtIpAddress.Text}:{txtPort.Text}", Run = () => BtnConnect_Click(this, EventArgs.Empty) };
        else
            yield return new CommandPalette.Command { Title = "Disconnect", Category = "Console", Glyph = Theme.Glyph.Unplug, Run = () => BtnDisconnect_Click(this, EventArgs.Empty) };
        yield return new CommandPalette.Command
        {
            Title = "Emergency stop (release freezes, stop recorder & polling)", Category = "Console", Glyph = Theme.Glyph.Bolt, Hint = "Ctrl+Shift+Q",
            Run = () => _ = EmergencyStopAsync(),
        };
        yield return new CommandPalette.Command
        {
            Title = _autoConnect ? "Auto-connect on startup: ON (turn off)" : "Auto-connect on startup: OFF (turn on)", Category = "Console", Glyph = Theme.Glyph.Plug,
            Run = () => { _autoConnect = !_autoConnect; SaveLayout(); Toast($"Auto-connect on startup {(_autoConnect ? "enabled" : "disabled")}", ToastHost.Kind.Info); },
        };
        yield return new CommandPalette.Command
        {
            Title = "Open crash reports folder", Category = "Diagnostics", Glyph = Theme.Glyph.Folder,
            Run = () => { Directory.CreateDirectory(CrashGuard.CrashFolder); System.Diagnostics.Process.Start("explorer.exe", CrashGuard.CrashFolder); },
        };

        foreach (var (tk, tt, tg) in new[] { ("active", "Active memory (live view & edit)", Theme.Glyph.Memory), ("dynamic", "Dynamic modder (find variables by behaviour)", Theme.Glyph.Wand), ("gsc", "Live GSC (write, compile, inject, restart)", Theme.Glyph.Document), ("mods", "Mod toggles", Theme.Glyph.Bolt), ("chain", "Pointer chain", Theme.Glyph.Link), ("graph", "Value graph", Theme.Glyph.Analyze),
                                             ("disasm", "Disassembler", Theme.Glyph.Code), ("scan", "Pattern scan", Theme.Glyph.Search), ("map", "Memory map", Theme.Glyph.List),
                                             ("threads", "Threads", Theme.Glyph.Cpu), ("files", "Console file manager", Theme.Glyph.Folder), ("console", "XBDM console & terminal", Theme.Glyph.Terminal) })
        {
            var key = tk;
            yield return new CommandPalette.Command { Title = tt, Category = "Runtime tool", Glyph = tg, Run = () => { Navigate("toolkit"); _toolkitPage?.Show(key); } };
        }
        if (connected)
        {
            yield return new CommandPalette.Command { Title = "Pause game", Category = "Console", Glyph = Theme.Glyph.Stop, Run = () => _ = _xbdmClient?.StopExecutionAsync() };
            yield return new CommandPalette.Command { Title = "Resume game", Category = "Console", Glyph = Theme.Glyph.Play, Run = () => _ = _xbdmClient?.GoAsync() };
        }
        yield return new CommandPalette.Command { Title = "Toggle output panel", Category = "View", Glyph = Theme.Glyph.Terminal, Hint = "Ctrl+`", Run = () => BtnToggleConsole_Click(this, EventArgs.Empty) };
        yield return new CommandPalette.Command { Title = "Clear output", Category = "View", Glyph = Theme.Glyph.Clear, Run = ClearConsole };
        yield return new CommandPalette.Command { Title = "Toggle sidebar", Category = "View", Glyph = Theme.Glyph.Menu, Hint = "Ctrl+B", Run = ToggleNav };
        yield return new CommandPalette.Command { Title = "Open log file", Category = "View", Glyph = Theme.Glyph.Document, Hint = Path.GetFileName(LogPath), Run = OpenLogFile };

        if (_scriptIde != null)
        {
            yield return new CommandPalette.Command { Title = "New script", Category = "Script", Glyph = Theme.Glyph.Add, Run = () => { Navigate("script"); _scriptIde.NewDocument(); } };
            yield return new CommandPalette.Command { Title = "Run current script", Category = "Script", Glyph = Theme.Glyph.Play, Hint = "F5", Run = () => { Navigate("script"); _ = _scriptIde.RunAsync(); } };
            yield return new CommandPalette.Command { Title = "Stop script", Category = "Script", Glyph = Theme.Glyph.Stop, Hint = "Shift+F5", Run = () => _scriptIde.Stop() };
        }
        yield return new CommandPalette.Command { Title = "Convert XEX to EXE…", Category = "Tools", Glyph = Theme.Glyph.Convert, Run = PickXexFiles };
        yield return new CommandPalette.Command { Title = "Discover offsets from XEX / EXE file…", Category = "Offsets", Glyph = Theme.Glyph.Target, Run = PickModuleForDiscovery };
        if (_libraryPage != null)
        {
            var lib = _libraryPage;
            yield return new CommandPalette.Command { Title = "Pull offsets from the running game", Category = "Library", Glyph = Theme.Glyph.Download, Hint = "any game · any TU", Run = () => { Navigate("offsets"); _ = lib.PullRunningAsync(true); } };
            foreach (var game in lib.Library.Games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList())
            {
                var g = game;
                yield return new CommandPalette.Command
                {
                    Title = g.Name, Category = "Game", Glyph = Theme.Glyph.Game,
                    Hint = g.Current is { } b ? $"{b.Label} · {b.OffsetCount:N0} offsets" : "not pulled yet",
                    Run = () => { Navigate("offsets"); _ = lib.SelectGameAsync(g); },
                };
            }
        }

        // offsets (database + runtime) – jump straight to memory
        int n = 0;
        foreach (var (name, addr, cat) in StaticOffsets.Concat(RuntimeOffsetTuples()))
        {
            if (++n > 4000) yield break;
            uint a = addr;
            yield return new CommandPalette.Command { Title = name, Category = cat, Glyph = Theme.Glyph.Link, Hint = $"0x{a:X8}", Run = () => ViewMemory(a) };
        }
    }

    private IEnumerable<(string Name, uint Address, string Category)> RuntimeOffsetTuples() =>
        _runtimePage?.Offsets.Select(o => (o.Name, o.Address, "Runtime · " + o.Category)) ?? Enumerable.Empty<(string, uint, string)>();

    private void OpenLogFile()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogPath) { UseShellExecute = true }); }
        catch (Exception ex) { Toast($"Could not open the log: {ex.Message}", ToastHost.Kind.Error); }
    }

    private void PickXexFiles()
    {
        using var dlg = new OpenFileDialog { Title = "Convert XEX to EXE", Filter = "Xbox 360 executables (*.xex)|*.xex|All files (*.*)|*.*", Multiselect = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Navigate("xex");
        if (_xexPage != null) _ = RunGuardedAsync(() => _xexPage.AddAsync(dlg.FileNames), "XEX conversion");
    }

    private void PickModuleForDiscovery()
    {
        using var dlg = new OpenFileDialog { Title = "Discover offsets from a module", Filter = "Executables (*.xex;*.exe;*.bin)|*.xex;*.exe;*.bin|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Navigate("runtime");
        if (_runtimePage != null) _ = RunGuardedAsync(() => _runtimePage.DiscoverFromFileAsync(dlg.FileName), "Offset discovery");
    }

    private static readonly string[] ScriptExtensions = { ".xs", ".txt", ".js", ".lua" };

    private void OnShellDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    private void OnShellDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        var xex = files.Where(f => f.EndsWith(".xex", StringComparison.OrdinalIgnoreCase)).ToArray();
        var scripts = files.Where(f => ScriptExtensions.Any(x => f.EndsWith(x, StringComparison.OrdinalIgnoreCase))).ToArray();

        if (xex.Length > 0 && _xexPage != null)
        {
            Navigate("xex");
            _ = RunGuardedAsync(() => _xexPage.AddAsync(xex), "XEX conversion");
        }
        foreach (var s in scripts)
        {
            try { OpenScript(Path.GetFileName(s), File.ReadAllText(s)); }
            catch (Exception ex) { LogMessage($"❌ Could not open {Path.GetFileName(s)}: {ex.Message}"); }
        }
        if (xex.Length == 0 && scripts.Length == 0)
        {
            var exe = files.FirstOrDefault(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
            if (exe != null && _runtimePage != null)
            {
                Navigate("runtime");
                _ = RunGuardedAsync(() => _runtimePage.DiscoverFromFileAsync(exe), "Offset discovery");
            }
            else Toast("Drop .xex (convert), .exe (discover offsets) or script files", ToastHost.Kind.Info);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  IAppHost
    // ═════════════════════════════════════════════════════════════════════════

    XBDMClient? IAppHost.Client => _xbdmClient;

    void IAppHost.Log(string message) => LogMessage(message);

    public void Toast(string message, ToastHost.Kind kind = ToastHost.Kind.Info) => _toasts?.Show(message, kind);

    public void ViewMemory(uint address)
    {
        if (InvokeRequired) { BeginInvoke(() => ViewMemory(address)); return; }
        txtAddress.Text = $"0x{address:X8}";
        Navigate("memory");
        if (_xbdmClient?.IsConnected == true) _ = RunGuardedAsync(RefreshMemoryView, "Memory read");
    }

    public void AddWatch(uint address, string description)
    {
        if (InvokeRequired) { BeginInvoke(() => AddWatch(address, description)); return; }
        if (dgvWatchList == null || _watchListTimer == null) return;
        var name = string.IsNullOrWhiteSpace(description) ? $"Watch_{address:X8}" : description;
        _memoryEngine.AddToWatchList(address, AdvancedMemoryEngine.ValueType.Int32, name);
        dgvWatchList.Rows.Add(false, $"0x{address:X8}", name, nameof(AdvancedMemoryEngine.ValueType.Int32), "");
        if (!_watchListTimer.Enabled) _watchListTimer.Start();
        Toast($"Watching {name} @ 0x{address:X8}", ToastHost.Kind.Success);
        Navigate("scan");
    }

    public void InsertScript(string text)
    {
        if (_scriptIde == null) return;
        Navigate("script");
        _scriptIde.InsertText(text);
    }

    public void OpenScript(string name, string content)
    {
        if (_scriptIde == null) return;
        Navigate("script");
        _scriptIde.OpenDocument(name, content);
    }

    /// <summary>Promotes runtime-discovered offsets into the offset database (category "Runtime · …").</summary>
    public void PushOffsets(IReadOnlyCollection<DiscoveredOffset> offsets)
    {
        if (InvokeRequired) { BeginInvoke(() => PushOffsets(offsets)); return; }
        foreach (var o in offsets)
        {
            _offsetManager.Upsert(new OffsetManager.OffsetEntry
            {
                Name = o.Name,
                Category = "Runtime · " + (string.IsNullOrEmpty(o.Category) ? "General" : o.Category),
                BaseAddress = o.Address,
                Description = string.Join(" · ", new[] { o.Type, o.Source, o.Confidence, o.Notes }.Where(s => !string.IsNullOrEmpty(s))),
            });
        }
        _offsetLookup = null;
        RefreshOffsetGrid();
        _scriptIde?.RefreshOffsets();
        LogMessage($"✅ Added {offsets.Count:N0} runtime offset(s) to the offset database");
        Toast($"{offsets.Count:N0} offset(s) added to the database", ToastHost.Kind.Success);
        UpdateStatusBar();
    }

    public IEnumerable<(string Name, uint Address, string Category)> StaticOffsets =>
        _offsetManager.GetAllOffsets().Select(o => (o.Name, EffectiveAddress(o), o.Category));

    public void RuntimeOffsetsChanged()
    {
        if (InvokeRequired) { BeginInvoke(RuntimeOffsetsChanged); return; }
        _offsetLookup = null;
        _scriptIde?.RefreshOffsets();
        UpdateGamePill();
        UpdateStatusBar();
    }

    public void SendToStaticAnalysis(string name, byte[] image, uint baseAddress)
    {
        if (InvokeRequired) { BeginInvoke(() => SendToStaticAnalysis(name, image, baseAddress)); return; }
        _loadedBinary = image;
        _loadedBase = baseAddress;
        try
        {
            AnalyzeBinary(name);
            if (Controls.Find("btnExportAnalysis", true).FirstOrDefault() is Button export) export.Enabled = true;
            LogMessage($"✅ {name} loaded into Static Analysis ({image.Length:N0} bytes @ 0x{baseAddress:X8})");
            Navigate("static");
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Static analysis failed: {ex.Message}");
        }
    }

    public void DiscoverFromImage(ModuleImage module)
    {
        if (_runtimePage == null) return;
        Navigate("runtime");
        _ = RunGuardedAsync(() => _runtimePage.DiscoverFromModuleAsync(module), "Offset discovery");
    }

    private FreezeService? _freezer;
    FreezeService? IAppHost.Freezer => _freezer;

    public void PullRunningGame()
    {
        if (InvokeRequired) { BeginInvoke(PullRunningGame); return; }
        if (_libraryPage == null) return;
        _ = RunGuardedAsync(() => _libraryPage.PullRunningAsync(true), "Offset pull");
    }

    public void OpenActiveMemory(uint address)
    {
        if (InvokeRequired) { BeginInvoke(() => OpenActiveMemory(address)); return; }
        _toolkitPage?.ActiveMemory(address);
    }

    public void Disassemble(uint address)
    {
        if (InvokeRequired) { BeginInvoke(() => Disassemble(address)); return; }
        _toolkitPage?.Disassemble(address);
    }

    public void PointerChain(uint baseAddress, IEnumerable<uint> offsets)
    {
        if (InvokeRequired) { BeginInvoke(() => PointerChain(baseAddress, offsets)); return; }
        _toolkitPage?.PointerChain(baseAddress, offsets);
    }

    public void GraphValue(uint address, string type, string label)
    {
        if (InvokeRequired) { BeginInvoke(() => GraphValue(address, type, label)); return; }
        _toolkitPage?.Graph(address, LiveValue.Parse(type), label);
    }

    public void MonitorRegion(uint start, uint size, string label)
    {
        if (InvokeRequired) { BeginInvoke(() => MonitorRegion(start, size, label)); return; }
        if (txtRecordStartAddr == null || txtRecordEndAddr == null) return;
        size = Math.Max(4u, size);
        uint end = start + size < start ? uint.MaxValue : start + size; // never wrap past 0xFFFFFFFF

        // a running session is stopped first so the new region gets a clean recording
        if (btnStopRecording?.Enabled == true) BtnStopRecording_Click(this, EventArgs.Empty);

        txtRecordStartAddr.Text = $"0x{start:X8}";
        txtRecordEndAddr.Text = $"0x{end:X8}";
        if (txtRecordSize != null) txtRecordSize.Text = (end - start).ToString();
        Navigate("record");

        if (_xbdmClient?.IsConnected != true)
        {
            LogMessage($"📍 Recorder set to {label}: 0x{start:X8} – 0x{end:X8} ({end - start:N0} bytes) – connect and press Start");
            Toast($"Recorder region set to {label} – connect to start recording", ToastHost.Kind.Info);
            return;
        }
        LogMessage($"📹 Monitoring {label}: 0x{start:X8} – 0x{end:X8} ({end - start:N0} bytes)");
        BtnStartRecording_Click(this, EventArgs.Empty);
        Toast($"Recording {label} ({end - start:N0} bytes)", ToastHost.Kind.Success);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  offset resolution for scripts
    // ═════════════════════════════════════════════════════════════════════════

    private uint EffectiveAddress(OffsetManager.OffsetEntry o) =>
        o.IsPointerBased && _resolvedPointers.TryGetValue(o.BaseAddress, out var ptr) ? o.CalculateFinalAddress(ptr) : o.BaseAddress;

    private IEnumerable<string> AllOffsetNames()
    {
        foreach (var o in _offsetManager.GetAllOffsets()) yield return o.Name;
        if (_runtimePage != null)
            foreach (var o in _runtimePage.Offsets) yield return o.Name;
    }

    private uint? ResolveOffsetName(string name)
    {
        var key = (_offsetManager.Count, _runtimePage?.Offsets.Count ?? 0, _resolvedPointers.Count);
        var map = _offsetLookup;
        if (map == null || key != _offsetLookupKey)
        {
            _offsetLookupKey = key;
            map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            // runtime offsets first, database entries override (curated names win)
            if (_runtimePage != null)
                foreach (var o in _runtimePage.Offsets) map[ScriptIde.NormalizeName(o.Name)] = o.Address;
            foreach (var o in _offsetManager.GetAllOffsets()) map[ScriptIde.NormalizeName(o.Name)] = EffectiveAddress(o);
            _offsetLookup = map;
        }
        return map.TryGetValue(ScriptIde.NormalizeName(name), out var a) ? a : null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  IOffsetDatabaseHost (Game Library ↔ offset database)
    // ═════════════════════════════════════════════════════════════════════════

    async Task<int> IOffsetDatabaseHost.LoadDatabaseAsync(string path, string title)
    {
        if (!File.Exists(path)) { Toast($"Offsets file not found: {path}", ToastHost.Kind.Error); return 0; }
        if (!await _offsetManager.LoadFromFileAsync(path))
        {
            LogMessage($"⚠️ Could not parse {Path.GetFileName(path)}");
            return 0;
        }
        _resolvedPointers.Clear(); // pointer values belong to the previous game
        _offsetLookup = null;
        RefreshOffsetGrid();
        _scriptIde?.RefreshOffsets();
        LogMessage($"📂 Offset database → {title} ({_offsetManager.Count:N0} offsets)");
        UpdateStatusBar();
        return _offsetManager.Count;
    }

    IReadOnlyList<OffsetManager.OffsetEntry> IOffsetDatabaseHost.DatabaseEntries => _offsetManager.GetAllOffsets();

    void IOffsetDatabaseHost.ShowPulled(PullResult result)
    {
        _runtimePage?.ShowPulled(result.Game, result.Module, result.Analysis, result.Discovered);
        RuntimeOffsetsChanged();
    }

    void IOffsetDatabaseHost.ShowDetected(GameInfo game)
    {
        _runtimePage?.SetDetected(game);
        UpdateGamePill();
        UpdateStatusBar();
    }


    // ═════════════════════════════════════════════════════════════════════════
    //  teardown
    // ═════════════════════════════════════════════════════════════════════════

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _reconnectCts?.Cancel();
        _logFlushTimer?.Stop();
        _logFlushTimer?.Dispose();
        _filterDebounce?.Dispose();
        _statusTimer?.Dispose();
        _autoRefreshTimer?.Dispose();
        DetachClient();
        _freezer?.Dispose();
        _log.Dispose();
        base.OnFormClosed(e);
    }
}
