using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;
using MemoryEditor.UI;
using ScintillaNET;

namespace MemoryEditor.Scripting;

/// <summary>
/// Full XScript IDE: tabbed Scintilla editors, explorer, live diagnostics, autocomplete, call tips,
/// hover docs, run/stop with execution line tracking, variables inspector and API reference.
/// </summary>
public sealed class ScriptIde : UserControl
{
    // ── host integration ──
    private readonly Func<ScriptExecutor?> _getConsoleExecutor;
    private readonly ScriptManager _manager;
    private readonly Action<string> _log;
    public Func<IEnumerable<string>>? OffsetNames { get; set; }
    public Func<string, uint?>? ResolveOffset { get; set; }
    public Action<string, ToastHost.Kind>? Toast { get; set; }

    // ── documents ──
    private sealed class Doc
    {
        public string Name = "untitled.xs";
        public string? LibraryName;
        public string? FilePath;
        public Scintilla Editor = null!;
        public bool Dirty;
        public List<ScriptError> Problems = new();
    }

    private readonly List<Doc> _docs = new();
    private Doc? _active;
    private int _untitled = 1;

    // ── UI ──
    private readonly Panel _editorHost = new() { Dock = DockStyle.Fill, BackColor = Theme.CodeBg, Tag = "keep" };
    private readonly DocTabStrip _tabs;
    private readonly TreeView _explorer = new() { Dock = DockStyle.Fill, HideSelection = false, ShowLines = false, FullRowSelect = true, ItemHeight = 24 };
    private readonly ListView _problems = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    private readonly ListView _vars = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly ListBox _reference = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 46, IntegralHeight = false };
    private readonly TextBox _refFilter = new() { Dock = DockStyle.Top, PlaceholderText = "Search functions…" };
    private readonly ListBox _quickMods = new() { Dock = DockStyle.Fill, SelectionMode = System.Windows.Forms.SelectionMode.MultiExtended, IntegralHeight = false };
    private readonly Label _quickModsCount = new() { Dock = DockStyle.Bottom, Height = 24, Text = "0 active", TextAlign = ContentAlignment.MiddleLeft };
    private readonly TextBox _find = new() { Width = 200, PlaceholderText = "Find  (Ctrl+F)" };
    private readonly ComboBox _target = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly StatusPill _state = new() { Text = "Ready", DotColor = Theme.TextDim };
    private readonly Label _statusPos = new() { AutoSize = true, Text = "Ln 1, Col 1" };
    private readonly Label _statusProblems = new() { AutoSize = true, Text = "No problems" };
    private readonly Label _statusInfo = new() { AutoSize = true, Text = "XScript" };
    private readonly IconButton _btnRun, _btnStop;
    private readonly Panel[] _inspectorPages;
    private readonly IconButton[] _inspectorTabs;
    private readonly SplitContainer _centerSplit;
    private readonly System.Windows.Forms.Timer _diagTimer = new() { Interval = 350 };
    private readonly System.Windows.Forms.Timer _runTimer = new() { Interval = 60 };
    private readonly Dictionary<string, bool> _activeMods = new();

    private ScriptExecutor? _simExecutor;
    private ScriptExecutor? _runningExecutor;
    private volatile int _execLine = -1;
    private int _shownExecLine = -1;
    private readonly HashSet<ScriptExecutor> _wired = new();

    private const int IndError = 8, IndWarning = 9, IndFind = 10;
    private const int MarkError = 1, MarkExecBg = 2, MarkExecArrow = 3;

    public ScriptIde(Func<ScriptExecutor?> consoleExecutor, ScriptManager manager, Action<string> log)
    {
        _getConsoleExecutor = consoleExecutor;
        _manager = manager;
        _log = log;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        Font = Theme.UI;

        // ── toolbar ──
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 8, 12, 6), WrapContents = false, BackColor = Theme.Surface };
        _btnRun = IconButton.Create("Run", Theme.Glyph.Play, ButtonVariant.Success, (_, _) => _ = RunAsync(), "Run (F5)");
        _btnStop = IconButton.Create("Stop", Theme.Glyph.Stop, ButtonVariant.Danger, (_, _) => Stop(), "Stop (Shift+F5)");
        _btnStop.Enabled = false;
        var btnNew = IconButton.Create("", Theme.Glyph.Add, ButtonVariant.Ghost, (_, _) => NewDocument(), "New script (Ctrl+N)");
        var btnOpen = IconButton.Create("", Theme.Glyph.Open, ButtonVariant.Ghost, (_, _) => OpenFile(), "Open file (Ctrl+O)");
        var btnSave = IconButton.Create("", Theme.Glyph.Save, ButtonVariant.Ghost, (_, _) => Save(), "Save (Ctrl+S)");
        var btnCheck = IconButton.Create("Check", Theme.Glyph.Check, ButtonVariant.Neutral, (_, _) => { RunDiagnostics(); Notify(_active!.Problems.Count == 0 ? "No problems found" : $"{_active.Problems.Count} problem(s)", _active.Problems.Count == 0 ? ToastHost.Kind.Success : ToastHost.Kind.Warning); }, "Check syntax");
        var btnFormat = IconButton.Create("Format", Theme.Glyph.Format, ButtonVariant.Neutral, (_, _) => FormatActive(), "Format document (Ctrl+Shift+F)");
        var btnSelfTest = IconButton.Create("Self-test", Theme.Glyph.Bug, ButtonVariant.Ghost, async (_, _) => await SelfTestAsync(), "Run the engine self-test against simulated memory");
        _target.Items.AddRange(new object[] { "Target: Console", "Target: Simulator" });
        _target.SelectedIndex = 0;
        _target.Margin = new Padding(8, 4, 8, 0);
        _find.Margin = new Padding(14, 4, 0, 0);
        _find.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { FindNext(!e.Shift); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Escape) { ClearFind(); _active?.Editor.Focus(); e.SuppressKeyPress = true; }
        };
        _find.TextChanged += (_, _) => HighlightAll(_find.Text);
        _state.Margin = new Padding(14, 3, 0, 0);
        toolbar.Controls.AddRange(new Control[] { btnNew, btnOpen, btnSave, Sep(), _btnRun, _btnStop, _target, Sep(), btnCheck, btnFormat, btnSelfTest, _find, _state });

        // ── explorer ──
        var explorerPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        var explorerHeader = Header("EXPLORER");
        _explorer.NodeMouseDoubleClick += (_, e) => OpenNode(e.Node);
        _explorer.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && _explorer.SelectedNode != null) OpenNode(_explorer.SelectedNode); if (e.KeyCode == Keys.Delete) DeleteSelectedLibraryScript(); };
        var explorerMenu = new ContextMenuStrip();
        explorerMenu.Items.Add("Open", null, (_, _) => { if (_explorer.SelectedNode != null) OpenNode(_explorer.SelectedNode); });
        explorerMenu.Items.Add("Delete saved script", null, (_, _) => DeleteSelectedLibraryScript());
        explorerMenu.Items.Add("Refresh", null, async (_, _) => await RefreshLibraryAsync());
        StyleMenu(explorerMenu);
        _explorer.ContextMenuStrip = explorerMenu;
        _explorer.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) _explorer.SelectedNode = e.Node; };
        explorerPanel.Controls.Add(_explorer);
        explorerPanel.Controls.Add(explorerHeader);

        // ── center: tabs + editor + problems ──
        _tabs = new DocTabStrip();
        _tabs.Selected += i => Activate(_docs[i]);
        _tabs.Closed += i => CloseDocument(_docs[i]);
        _tabs.NewRequested += () => NewDocument();

        var editorArea = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CodeBg, Tag = "keep" };
        editorArea.Controls.Add(_editorHost);
        editorArea.Controls.Add(_tabs);

        var problemsPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        _problems.Columns.Add("", 26);
        _problems.Columns.Add("Line", 56);
        _problems.Columns.Add("Message", 800);
        _problems.DoubleClick += (_, _) => GotoProblem();
        _problems.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) GotoProblem(); };
        problemsPanel.Controls.Add(_problems);
        problemsPanel.Controls.Add(Header("PROBLEMS"));

        _centerSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 4 };
        _centerSplit.Panel1.Controls.Add(editorArea);
        _centerSplit.Panel2.Controls.Add(problemsPanel);

        // ── inspector ──
        var inspector = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        var inspTabs = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 2), BackColor = Theme.Surface };
        var pVars = new Panel { Dock = DockStyle.Fill };
        var pRef = new Panel { Dock = DockStyle.Fill };
        var pMods = new Panel { Dock = DockStyle.Fill };
        _vars.Columns.Add("Variable", 110);
        _vars.Columns.Add("Value", 120);
        _vars.Columns.Add("Type", 60);
        pVars.Controls.Add(_vars);
        _reference.DrawItem += DrawReferenceItem;
        _reference.DoubleClick += (_, _) => InsertReference();
        _refFilter.TextChanged += (_, _) => FillReference();
        pRef.Controls.Add(_reference);
        pRef.Controls.Add(_refFilter);
        BuildQuickMods(pMods);
        _inspectorPages = new[] { pVars, pRef, pMods };
        _inspectorTabs = new[]
        {
            IconButton.Create("Variables", Theme.Glyph.List, ButtonVariant.Ghost),
            IconButton.Create("Reference", Theme.Glyph.Document, ButtonVariant.Ghost),
            IconButton.Create("Quick Mods", Theme.Glyph.Bolt, ButtonVariant.Ghost),
        };
        for (int i = 0; i < _inspectorTabs.Length; i++)
        {
            int k = i;
            _inspectorTabs[i].Height = 28;
            _inspectorTabs[i].Click += (_, _) => ShowInspector(k);
            inspTabs.Controls.Add(_inspectorTabs[i]);
        }
        foreach (var p in _inspectorPages) inspector.Controls.Add(p);
        inspector.Controls.Add(inspTabs);

        var rightSplit = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 4, FixedPanel = FixedPanel.Panel2 };
        rightSplit.Panel1.Controls.Add(_centerSplit);
        rightSplit.Panel2.Controls.Add(inspector);

        var mainSplit = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 4, FixedPanel = FixedPanel.Panel1 };
        mainSplit.Panel1.Controls.Add(explorerPanel);
        mainSplit.Panel2.Controls.Add(rightSplit);

        // ── status bar ──
        var status = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(12, 5, 12, 0), BackColor = Theme.Surface, WrapContents = false };
        foreach (var l in new[] { _statusPos, _statusProblems, _statusInfo })
        {
            l.ForeColor = Theme.TextMuted;
            l.Margin = new Padding(0, 0, 22, 0);
            l.Tag = "keep";
            l.Font = Theme.UISmall;
            status.Controls.Add(l);
        }

        Controls.Add(mainSplit);
        Controls.Add(status);
        Controls.Add(toolbar);

        // layout once we know our size
        bool laidOut = false;
        Layout += (_, _) =>
        {
            if (laidOut || Width < 400 || Height < 300) return;
            laidOut = true;
            try
            {
                mainSplit.SplitterDistance = 220;
                rightSplit.SplitterDistance = Math.Max(300, rightSplit.Width - 320);
                _centerSplit.SplitterDistance = Math.Max(200, _centerSplit.Height - 150);
            }
            catch { }
        };

        _diagTimer.Tick += (_, _) => { _diagTimer.Stop(); RunDiagnostics(); };
        _runTimer.Tick += (_, _) => RunTick();

        Theme.Apply(this);
        ShowInspector(1);
        FillReference();
        BuildExplorer();
        _ = RefreshLibraryAsync();
        if (!LoadSession()) OpenDocument("welcome.xs", ScriptTemplates.Welcome, markClean: true);
    }

    // ───────────────────────────── public API ─────────────────────────────

    public bool IsRunning => _runningExecutor?.IsRunning == true;

    public void InsertText(string text)
    {
        if (_active == null) NewDocument();
        var ed = _active!.Editor;
        ed.ReplaceSelection(text);
        ed.Focus();
    }

    public void OpenDocument(string name, string content, bool markClean = false, string? libraryName = null, string? filePath = null)
    {
        var existing = _docs.FirstOrDefault(d => (libraryName != null && d.LibraryName == libraryName) || (filePath != null && d.FilePath == filePath));
        if (existing != null) { Activate(existing); return; }

        var doc = new Doc { Name = name, LibraryName = libraryName, FilePath = filePath };
        doc.Editor = CreateEditor();
        doc.Editor.Text = content;
        doc.Editor.EmptyUndoBuffer();
        if (markClean) doc.Editor.SetSavePoint();
        doc.Dirty = !markClean;
        doc.Editor.SavePointLeft += (_, _) => { doc.Dirty = true; RefreshTabs(); };
        doc.Editor.SavePointReached += (_, _) => { doc.Dirty = false; RefreshTabs(); };
        _docs.Add(doc);
        _editorHost.Controls.Add(doc.Editor);
        Activate(doc);
    }

    public void NewDocument() => OpenDocument($"untitled-{_untitled++}.xs", "// New script\n\n");

    public async Task RunAsync()
    {
        if (_active == null) return;
        if (IsRunning) { Notify("A script is already running", ToastHost.Kind.Warning); return; }

        RunDiagnostics();
        if (_active.Problems.Any(p => !p.IsWarning))
        {
            ShowProblemsAndJump();
            Notify("Fix the syntax errors before running", ToastHost.Kind.Error);
            return;
        }

        bool sim = _target.SelectedIndex == 1;
        var exec = sim ? (_simExecutor ??= new ScriptExecutor(new MockGameClient(), _log)) : _getConsoleExecutor();
        if (exec == null)
        {
            Notify("Not connected – connect to a console or switch the target to Simulator", ToastHost.Kind.Warning);
            return;
        }
        exec.OffsetResolver = ResolveOffset;
        exec.Notifier = m => Notify(m, ToastHost.Kind.Info);
        Wire(exec);

        _runningExecutor = exec;
        ClearExecMarkers();
        _active.Editor.MarkerDeleteAll(MarkError);
        var runDoc = _active;
        _btnRun.Enabled = false;
        _btnStop.Enabled = true;
        _state.Text = sim ? "Running (simulator)" : "Running";
        _state.DotColor = Theme.Success;
        _runTimer.Start();
        ShowInspector(0);
        await exec.ExecuteAsync(runDoc.Editor.Text);
    }

    public void Stop()
    {
        _runningExecutor?.RequestCancel();
        _getConsoleExecutor()?.RequestCancel();
        _simExecutor?.RequestCancel();
    }

    public void SaveSession()
    {
        try
        {
            var path = SessionPath;
            var items = _docs.Select(d => new SessionDoc
            {
                Name = d.Name, LibraryName = d.LibraryName, FilePath = d.FilePath, Content = d.Editor.Text, Dirty = d.Dirty, Active = d == _active
            }).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(items));
        }
        catch { }
    }

    // ───────────────────────────── editor setup ─────────────────────────────

    private static string Keywords => string.Join(" ", ScriptLexer.Keywords.OrderBy(k => k));
    private static string BuiltinNames => string.Join(" ", ScriptBuiltins.All.Keys.SelectMany(k => new[] { k, k.ToLowerInvariant() }).Distinct().OrderBy(k => k));

    private string OffsetKeywordList()
    {
        try
        {
            var names = OffsetNames?.Invoke() ?? Enumerable.Empty<string>();
            return string.Join(" ", names.Select(NormalizeName).Where(n => n.Length > 0).Distinct().Take(20000));
        }
        catch { return ""; }
    }

    public static string NormalizeName(string n) => new(n.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

    private Scintilla CreateEditor()
    {
        var ed = new Scintilla
        {
            Dock = DockStyle.Fill,
            BorderStyle = ScintillaNET.BorderStyle.None,
            Visible = false,
        };
        Theme.Exclude(ed);
        var mono = Theme.Mono;

        ed.StyleResetDefault();
        var def = ed.Styles[Style.Default];
        def.Font = mono.Name;
        def.SizeF = 10.5F;
        def.BackColor = Theme.CodeBg;
        def.ForeColor = Color.FromArgb(0xD4, 0xD8, 0xE0);
        ed.StyleClearAll();

        ed.LexerName = "cpp";
        ed.SetProperty("fold", "1");
        ed.SetProperty("fold.compact", "0");
        ed.SetProperty("fold.comment", "1");
        ed.SetProperty("lexer.cpp.allow.dollars", "1");
        ed.SetProperty("lexer.cpp.track.preprocessor", "0");
        ed.SetKeywords(0, Keywords);
        ed.SetKeywords(1, BuiltinNames);
        ed.SetKeywords(3, OffsetKeywordList());

        void S(int style, Color fore, bool bold = false, bool italic = false)
        {
            ed.Styles[style].ForeColor = fore;
            ed.Styles[style].BackColor = Theme.CodeBg;
            ed.Styles[style].Bold = bold;
            ed.Styles[style].Italic = italic;
        }
        S(Style.Cpp.Default, Color.FromArgb(0xD4, 0xD8, 0xE0));
        S(Style.Cpp.Identifier, Color.FromArgb(0x9C, 0xDC, 0xFE));
        S(Style.Cpp.Comment, Color.FromArgb(0x6A, 0x99, 0x55), italic: true);
        S(Style.Cpp.CommentLine, Color.FromArgb(0x6A, 0x99, 0x55), italic: true);
        S(Style.Cpp.CommentDoc, Color.FromArgb(0x6A, 0x99, 0x55), italic: true);
        S(Style.Cpp.CommentLineDoc, Color.FromArgb(0x6A, 0x99, 0x55), italic: true);
        S(Style.Cpp.Number, Color.FromArgb(0xB5, 0xCE, 0xA8));
        S(Style.Cpp.String, Color.FromArgb(0xCE, 0x91, 0x78));
        S(Style.Cpp.Character, Color.FromArgb(0xCE, 0x91, 0x78));
        S(Style.Cpp.StringEol, Color.FromArgb(0xF0, 0x6B, 0x6B));
        S(Style.Cpp.Word, Color.FromArgb(0xC5, 0x86, 0xC0), bold: true);
        S(Style.Cpp.Word2, Color.FromArgb(0xDC, 0xDC, 0xAA));
        S(Style.Cpp.GlobalClass, Color.FromArgb(0x4E, 0xC9, 0xB0));
        S(Style.Cpp.Operator, Color.FromArgb(0xB4, 0xBC, 0xC8));
        S(Style.Cpp.Preprocessor, Color.FromArgb(0x6A, 0x99, 0x55));
        S(Style.Cpp.EscapeSequence, Color.FromArgb(0xD7, 0xBA, 0x7D));

        ed.Styles[Style.LineNumber].BackColor = Theme.CodeBg;
        ed.Styles[Style.LineNumber].ForeColor = Color.FromArgb(0x4B, 0x53, 0x63);
        ed.Styles[Style.LineNumber].Font = mono.Name;
        ed.Styles[Style.BraceLight].ForeColor = Color.White;
        ed.Styles[Style.BraceLight].BackColor = Color.FromArgb(0x33, 0x4A, 0x6E);
        ed.Styles[Style.BraceLight].Bold = true;
        ed.Styles[Style.BraceBad].ForeColor = Theme.Danger;
        ed.Styles[Style.IndentGuide].ForeColor = Color.FromArgb(0x2A, 0x2F, 0x3A);
        ed.Styles[Style.IndentGuide].BackColor = Theme.CodeBg;
        ed.Styles[Style.CallTip].BackColor = Theme.Surface2;
        ed.Styles[Style.CallTip].ForeColor = Theme.Text;
        ed.Styles[Style.CallTip].Font = "Segoe UI";
        ed.Styles[Style.CallTip].SizeF = 9.5F;
        ed.CallTipSetForeHlt(Theme.Accent);

        ed.CaretForeColor = Color.White;
        ed.CaretWidth = 2;
        ed.CaretLineVisible = true;
        ed.CaretLineBackColor = Color.FromArgb(0x1A, 0x1E, 0x27);
        ed.SetSelectionBackColor(true, Color.FromArgb(0x26, 0x4F, 0x78));
        ed.SelectionBackColor = Color.FromArgb(0x26, 0x4F, 0x78);
        ed.IndentationGuides = IndentView.LookBoth;
        ed.TabWidth = 4;
        ed.IndentWidth = 4;
        ed.UseTabs = false;
        ed.TabIndents = true;
        ed.BackspaceUnindents = true;
        ed.ScrollWidthTracking = true;
        ed.ScrollWidth = 1;
        ed.MouseDwellTime = 450;
        ed.MultipleSelection = true;
        ed.AdditionalSelectionTyping = true;
        ed.EndAtLastLine = false;
        ed.Technology = Technology.DirectWrite;
        ed.FontQuality = FontQuality.LcdOptimized;
        ed.WhitespaceTextColor = Color.FromArgb(0x3A, 0x40, 0x4C);

        // margins: 0 = line numbers, 1 = markers, 2 = folding
        ed.Margins[0].Type = MarginType.Number;
        ed.Margins[0].Width = 46;
        ed.Margins[0].BackColor = Theme.CodeBg;
        ed.Margins[1].Type = MarginType.Symbol;
        ed.Margins[1].Width = 16;
        ed.Margins[1].Mask = (1u << MarkError) | (1u << MarkExecArrow);
        ed.Margins[1].BackColor = Theme.CodeBg;
        ed.Margins[2].Type = MarginType.Symbol;
        ed.Margins[2].Mask = Marker.MaskFolders;
        ed.Margins[2].Sensitive = true;
        ed.Margins[2].Width = 16;
        ed.SetFoldMarginColor(true, Theme.CodeBg);
        ed.SetFoldMarginHighlightColor(true, Theme.CodeBg);
        for (int i = 25; i <= 31; i++)
        {
            ed.Markers[i].SetForeColor(Theme.CodeBg);
            ed.Markers[i].SetBackColor(Color.FromArgb(0x5A, 0x63, 0x75));
        }
        ed.Markers[Marker.Folder].Symbol = MarkerSymbol.BoxPlus;
        ed.Markers[Marker.FolderOpen].Symbol = MarkerSymbol.BoxMinus;
        ed.Markers[Marker.FolderEnd].Symbol = MarkerSymbol.BoxPlusConnected;
        ed.Markers[Marker.FolderMidTail].Symbol = MarkerSymbol.TCorner;
        ed.Markers[Marker.FolderOpenMid].Symbol = MarkerSymbol.BoxMinusConnected;
        ed.Markers[Marker.FolderSub].Symbol = MarkerSymbol.VLine;
        ed.Markers[Marker.FolderTail].Symbol = MarkerSymbol.LCorner;
        ed.AutomaticFold = AutomaticFold.Show | AutomaticFold.Click | AutomaticFold.Change;

        ed.Markers[MarkError].Symbol = MarkerSymbol.Circle;
        ed.Markers[MarkError].SetBackColor(Theme.Danger);
        ed.Markers[MarkError].SetForeColor(Theme.Danger);
        ed.Markers[MarkExecBg].Symbol = MarkerSymbol.Background;
        ed.Markers[MarkExecBg].SetBackColor(Color.FromArgb(0x1F, 0x3A, 0x2C));
        ed.Markers[MarkExecArrow].Symbol = MarkerSymbol.ShortArrow;
        ed.Markers[MarkExecArrow].SetBackColor(Theme.Success);
        ed.Markers[MarkExecArrow].SetForeColor(Theme.Success);

        ed.Indicators[IndError].Style = IndicatorStyle.Squiggle;
        ed.Indicators[IndError].ForeColor = Theme.Danger;
        ed.Indicators[IndWarning].Style = IndicatorStyle.Squiggle;
        ed.Indicators[IndWarning].ForeColor = Theme.Warning;
        ed.Indicators[IndFind].Style = IndicatorStyle.RoundBox;
        ed.Indicators[IndFind].ForeColor = Theme.Warning;
        ed.Indicators[IndFind].Alpha = 60;
        ed.Indicators[IndFind].OutlineAlpha = 140;
        ed.Indicators[IndFind].Under = true;

        ed.AutoCIgnoreCase = true;
        ed.AutoCMaxHeight = 12;
        ed.AutoCOrder = Order.PerformSort;
        ed.AutoCAutoHide = true;
        ed.AutocompleteListBackColor = Theme.Surface2;
        ed.AutocompleteListTextColor = Theme.Text;
        ed.AutocompleteListSelectedBackColor = Theme.Selection;
        ed.AutocompleteListSelectedTextColor = Color.White;

        ed.CharAdded += Editor_CharAdded;
        ed.UpdateUI += Editor_UpdateUI;
        ed.TextChanged += (_, _) => { _diagTimer.Stop(); _diagTimer.Start(); };
        ed.DwellStart += Editor_DwellStart;
        ed.DwellEnd += (_, _) => ed.CallTipCancel();
        ed.KeyDown += Editor_KeyDown;
        Theme.DarkScrollbars(ed);
        return ed;
    }

    private void RefreshOffsetKeywords()
    {
        var list = OffsetKeywordList();
        foreach (var d in _docs) d.Editor.SetKeywords(3, list);
    }

    // ───────────────────────────── editor behaviours ─────────────────────────────

    private Scintilla? Ed => _active?.Editor;

    private void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        var ed = (Scintilla)sender!;
        if (e.KeyCode == Keys.Space && e.Control) { ShowAutoComplete(ed, true); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.OemQuestion && e.Control) { ToggleComment(ed); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.D && e.Control && !e.Shift)
        {
            var line = ed.Lines[ed.CurrentLine];
            ed.InsertText(line.EndPosition, (line.Text.EndsWith("\n") ? "" : "\n") + line.Text.TrimEnd('\r', '\n') + (line.Text.EndsWith("\n") ? "\n" : ""));
            e.SuppressKeyPress = true;
        }
    }

    private void Editor_CharAdded(object? sender, CharAddedEventArgs e)
    {
        var ed = (Scintilla)sender!;
        char ch = (char)e.Char;

        if (ch == '\n')
        {
            // auto indent, extra level after '{'
            int line = ed.CurrentLine;
            if (line > 0)
            {
                var prev = ed.Lines[line - 1];
                int indent = prev.Indentation;
                var trimmed = prev.Text.TrimEnd();
                if (trimmed.EndsWith("{")) indent += 4;
                ed.Lines[line].Indentation = indent;
                ed.GotoPosition(ed.Lines[line].IndentPosition);
                // typed between {} → push closing brace down
                int pos = ed.CurrentPosition;
                if (trimmed.EndsWith("{") && pos < ed.TextLength && ed.GetCharAt(pos) == '}')
                {
                    ed.InsertText(pos, "\n" + new string(' ', Math.Max(0, indent - 4)));
                }
            }
            return;
        }
        if (ch == '}')
        {
            var line = ed.Lines[ed.CurrentLine];
            if (line.Text.Trim() == "}" && ed.CurrentLine > 0)
            {
                int open = ed.BraceMatch(ed.CurrentPosition - 1);
                if (open >= 0) line.Indentation = ed.Lines[ed.LineFromPosition(open)].Indentation;
            }
            return;
        }
        if (ch == '(')
        {
            ShowCallTip(ed, ed.CurrentPosition - 1);
            return;
        }
        if (ch == ')') { ed.CallTipCancel(); return; }
        if (ch == ',' && ed.CallTipActive) { ShowCallTip(ed, FindOpenParen(ed, ed.CurrentPosition - 1)); return; }

        if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '$')
        {
            var style = ed.GetStyleAt(Math.Max(0, ed.CurrentPosition - 1));
            if (style is Style.Cpp.String or Style.Cpp.Comment or Style.Cpp.CommentLine or Style.Cpp.Number) return;
            ShowAutoComplete(ed, false);
        }
    }

    private void ShowAutoComplete(Scintilla ed, bool force)
    {
        int pos = ed.CurrentPosition;
        int start = ed.WordStartPosition(pos, true);
        int len = pos - start;
        if (start > 0 && ed.GetCharAt(start - 1) == '$') { }
        if (!force && len < 2) return;
        var words = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in ScriptLexer.Keywords) words.Add(k);
        foreach (var f in ScriptBuiltins.All.Values) words.Add(f.Name);
        // identifiers in the document
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(ed.Text, @"\b(?:var|let|const|func|function)\s+([A-Za-z_]\w*)|\b([A-Za-z_]\w*)\s*="))
        {
            var w = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (w.Length > 1) words.Add(w);
        }
        foreach (var w in new[] { "lastWalk", "lastAddoff", "hookValue" }) words.Add(w);
        try
        {
            var prefix = len > 0 ? ed.GetTextRange(start, len) : "";
            foreach (var o in (OffsetNames?.Invoke() ?? Enumerable.Empty<string>()).Select(NormalizeName))
                if (prefix.Length == 0 || o.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) words.Add(o);
        }
        catch { }
        var list = string.Join(" ", words.Where(w => !w.Contains(' ')).Take(4000));
        ed.AutoCShow(len, list);
    }

    private static int FindOpenParen(Scintilla ed, int pos)
    {
        int depth = 0;
        for (int p = pos; p >= 0 && p > pos - 400; p--)
        {
            int c = ed.GetCharAt(p);
            if (c == ')') depth++;
            else if (c == '(') { if (depth == 0) return p; depth--; }
            else if (c == '\n') break;
        }
        return -1;
    }

    private void ShowCallTip(Scintilla ed, int parenPos)
    {
        if (parenPos < 0) return;
        int wordEnd = parenPos;
        int wordStart = ed.WordStartPosition(wordEnd, true);
        if (wordStart == wordEnd) return;
        var name = ed.GetTextRange(wordStart, wordEnd - wordStart);
        if (!ScriptBuiltins.All.TryGetValue(name, out var f))
        {
            // user function
            var m = System.Text.RegularExpressions.Regex.Match(ed.Text, $@"\bfunc(?:tion)?\s+{System.Text.RegularExpressions.Regex.Escape(name)}\s*\(([^)]*)\)");
            if (!m.Success) return;
            ed.CallTipShow(wordStart, $"{name}({m.Groups[1].Value.Trim()})\nuser function");
            return;
        }
        var tip = f.Signature + "\n" + f.Description;
        ed.CallTipShow(wordStart, tip);
        // highlight the current argument
        int commas = 0, depth = 0;
        for (int p = parenPos + 1; p < ed.CurrentPosition; p++)
        {
            int c = ed.GetCharAt(p);
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0) commas++;
        }
        int open = f.Signature.IndexOf('(');
        if (open < 0) return;
        var args = f.Signature[(open + 1)..f.Signature.LastIndexOf(')')].Split(',');
        if (commas < args.Length)
        {
            int s = open + 1 + args.Take(commas).Sum(a => a.Length + 1);
            int len = args[commas].Length;
            while (len > 0 && f.Signature[s] == ' ') { s++; len--; }
            ed.CallTipSetHlt(s, s + len);
        }
    }

    private void Editor_DwellStart(object? sender, DwellEventArgs e)
    {
        var ed = (Scintilla)sender!;
        if (e.Position < 0) return;
        var word = ed.GetWordFromPosition(e.Position);
        if (string.IsNullOrEmpty(word)) return;
        if (ScriptBuiltins.All.TryGetValue(word, out var f))
        {
            ed.CallTipShow(e.Position, $"{f.Signature}\n{f.Description}\n[{f.Category}]");
            return;
        }
        var addr = ResolveOffset?.Invoke(word);
        if (addr.HasValue) ed.CallTipShow(e.Position, $"offset {word}\n= 0x{addr.Value:X8}");
        else if (word.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || (word.Length > 2 && word[0] == '0' && (word[1] | 0x20) == 'x'))
        {
            if (uint.TryParse(word[2..], System.Globalization.NumberStyles.HexNumber, null, out var v))
                ed.CallTipShow(e.Position, $"0x{v:X8}\n= {v:N0}  ({(int)v})");
        }
    }

    private void Editor_UpdateUI(object? sender, UpdateUIEventArgs e)
    {
        var ed = (Scintilla)sender!;
        _statusPos.Text = $"Ln {ed.CurrentLine + 1}, Col {ed.GetColumn(ed.CurrentPosition) + 1}" +
                          (ed.SelectionEnd != ed.SelectionStart ? $"  ({Math.Abs(ed.SelectionEnd - ed.SelectionStart)} selected)" : "");
        // brace matching
        int caret = ed.CurrentPosition;
        int bracePos = -1;
        if (caret > 0 && IsBrace(ed.GetCharAt(caret - 1))) bracePos = caret - 1;
        else if (IsBrace(ed.GetCharAt(caret))) bracePos = caret;
        if (bracePos >= 0)
        {
            int match = ed.BraceMatch(bracePos);
            if (match < 0) ed.BraceBadLight(bracePos);
            else ed.BraceHighlight(bracePos, match);
        }
        else ed.BraceHighlight(-1, -1);
    }

    private static bool IsBrace(int c) => c is '(' or ')' or '[' or ']' or '{' or '}';

    private static void ToggleComment(Scintilla ed)
    {
        int first = ed.LineFromPosition(ed.SelectionStart), last = ed.LineFromPosition(ed.SelectionEnd);
        if (last > first && ed.SelectionEnd == ed.Lines[last].Position) last--;
        bool allCommented = true;
        for (int i = first; i <= last; i++)
            if (ed.Lines[i].Text.Trim().Length > 0 && !ed.Lines[i].Text.TrimStart().StartsWith("//")) allCommented = false;
        ed.BeginUndoAction();
        for (int i = first; i <= last; i++)
        {
            var line = ed.Lines[i];
            if (line.Text.Trim().Length == 0) continue;
            int ip = line.IndentPosition;
            if (allCommented)
            {
                int len = ed.GetTextRange(ip, 3) == "// " ? 3 : 2;
                ed.DeleteRange(ip, len);
            }
            else ed.InsertText(ip, "// ");
        }
        ed.EndUndoAction();
    }

    // ───────────────────────────── diagnostics ─────────────────────────────

    private void RunDiagnostics()
    {
        if (_active == null) return;
        var ed = _active.Editor;
        var text = ed.Text;
        var prog = ScriptInterpreter.Compile(text);
        var problems = prog.Errors.ToList();
        problems.AddRange(StaticWarnings(prog));
        _active.Problems = problems;

        ed.IndicatorCurrent = IndError;
        ed.IndicatorClearRange(0, ed.TextLength);
        ed.IndicatorCurrent = IndWarning;
        ed.IndicatorClearRange(0, ed.TextLength);
        foreach (var p in problems)
        {
            if (p.Line < 1 || p.Line > ed.Lines.Count) continue;
            var line = ed.Lines[p.Line - 1];
            int pos = Math.Min(line.Position + Math.Max(0, p.Column - 1), Math.Max(line.Position, line.EndPosition - 1));
            int end = ed.WordEndPosition(pos, true);
            if (end <= pos) end = Math.Min(pos + 1, ed.TextLength);
            ed.IndicatorCurrent = p.IsWarning ? IndWarning : IndError;
            ed.IndicatorFillRange(pos, end - pos);
        }

        _problems.BeginUpdate();
        _problems.Items.Clear();
        foreach (var p in problems.OrderBy(p => p.Line))
        {
            var item = new ListViewItem(p.IsWarning ? "⚠" : "✕") { ForeColor = p.IsWarning ? Theme.Warning : Theme.Danger, Tag = p };
            item.SubItems.Add(p.Line.ToString());
            item.SubItems.Add(p.Message);
            item.UseItemStyleForSubItems = false;
            item.SubItems[1].ForeColor = Theme.TextMuted;
            item.SubItems[2].ForeColor = Theme.Text;
            _problems.Items.Add(item);
        }
        _problems.EndUpdate();

        int errs = problems.Count(p => !p.IsWarning), warns = problems.Count(p => p.IsWarning);
        _statusProblems.Text = errs + warns == 0 ? "No problems" : $"{errs} error(s), {warns} warning(s)";
        _statusProblems.ForeColor = errs > 0 ? Theme.Danger : warns > 0 ? Theme.Warning : Theme.TextMuted;
    }

    private IEnumerable<ScriptError> StaticWarnings(ScriptProgram prog)
    {
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lastWalk", "lastAddoff", "hookValue" };
        var used = new List<IdentExpr>();
        var calls = new List<CallExpr>();
        void E(Expr? e)
        {
            switch (e)
            {
                case null: return;
                case IdentExpr id: used.Add(id); break;
                case UnaryExpr u: E(u.Operand); break;
                case BinaryExpr b: E(b.Left); E(b.Right); break;
                case TernaryExpr t: E(t.Cond); E(t.Then); E(t.Else); break;
                case CallExpr c:
                    calls.Add(c);
                    for (int i = 0; i < c.Args.Count; i++)
                    {
                        if (i == 0 && c.Args[0] is IdentExpr && ScriptBuiltins.All.TryGetValue(c.Name, out var bf) && bf.BareFirstArg) continue;
                        E(c.Args[i]);
                    }
                    break;
                case IndexExpr ix: E(ix.Target); E(ix.Index); break;
                case ArrayExpr a: a.Items.ForEach(E); break;
                case AssignExpr a: declared.Add(a.Name); if (a.IndexTarget != null) E(a.IndexTarget); E(a.Index); E(a.Value); break;
                case IncDecExpr inc: declared.Add(inc.Name); break;
            }
        }
        void S(Stmt? s)
        {
            switch (s)
            {
                case null: return;
                case ExprStmt es: E(es.Expr); break;
                case VarStmt v: declared.Add(v.Name); E(v.Init); break;
                case BlockStmt b: b.Body.ForEach(S); break;
                case IfStmt i: E(i.Cond); S(i.Then); S(i.Else); break;
                case WhileStmt w: E(w.Cond); S(w.Body); break;
                case RepeatStmt r: E(r.Count); S(r.Body); break;
                case ForStmt f: S(f.Init); E(f.Cond); E(f.Step); S(f.Body); break;
                case ForInStmt fi: declared.Add(fi.Var); E(fi.From); E(fi.To); S(fi.Body); break;
                case ReturnStmt r: E(r.Value); break;
                case FuncStmt fn: fn.Params.ForEach(p => declared.Add(p)); S(fn.Body); break;
            }
        }
        prog.Body.ForEach(S);

        foreach (var c in calls)
        {
            if (prog.Functions.ContainsKey(c.Name)) continue;
            if (!ScriptBuiltins.All.TryGetValue(c.Name, out var f))
            {
                var hint = ScriptBuiltins.SuggestName(c.Name);
                yield return new ScriptError($"Unknown function '{c.Name}'" + (hint != null ? $" – did you mean '{hint}'?" : ""), c.Line, c.Col) { IsWarning = true };
            }
            else if (c.Args.Count < f.MinArgs || c.Args.Count > f.MaxArgs)
                yield return new ScriptError($"{f.Name} expects {f.Signature}", c.Line, c.Col) { IsWarning = true };
        }
        foreach (var id in used)
        {
            if (declared.Contains(id.Name) || prog.Functions.ContainsKey(id.Name)) continue;
            if (ResolveOffset?.Invoke(id.Name) != null) continue;
            yield return new ScriptError($"'{id.Name}' is not declared and is not a known offset", id.Line, id.Col) { IsWarning = true };
        }
    }

    private void GotoProblem()
    {
        if (_problems.SelectedItems.Count == 0 || _problems.SelectedItems[0].Tag is not ScriptError p || Ed == null) return;
        GotoLine(p.Line, p.Column);
    }

    private void GotoLine(int line, int col = 1)
    {
        var ed = Ed;
        if (ed == null || line < 1 || line > ed.Lines.Count) return;
        var l = ed.Lines[line - 1];
        l.EnsureVisible();
        ed.GotoPosition(Math.Min(l.Position + Math.Max(0, col - 1), l.EndPosition));
        ed.Focus();
    }

    private void ShowProblemsAndJump()
    {
        var first = _active?.Problems.FirstOrDefault(p => !p.IsWarning);
        if (first != null) GotoLine(first.Line, first.Column);
    }

    // ───────────────────────────── execution ─────────────────────────────

    private void Wire(ScriptExecutor exec)
    {
        if (!_wired.Add(exec)) return;
        exec.LineExecuting += l => _execLine = l;
        exec.Finished += err => BeginInvoke(() => OnFinished(exec, err));
    }

    private void OnFinished(ScriptExecutor exec, ScriptError? err)
    {
        if (exec != _runningExecutor) return;
        _runTimer.Stop();
        ClearExecMarkers();
        RefreshVariables(exec);
        _btnRun.Enabled = true;
        _btnStop.Enabled = exec.BackgroundTasks > 0;
        if (err != null)
        {
            _state.Text = $"Error on line {err.Line}";
            _state.DotColor = Theme.Danger;
            if (Ed != null && err.Line > 0 && err.Line <= Ed.Lines.Count)
            {
                Ed.Lines[err.Line - 1].MarkerAdd(MarkError);
                GotoLine(err.Line, err.Column);
            }
            Notify($"Line {err.Line}: {err.Message}", ToastHost.Kind.Error);
        }
        else
        {
            _state.Text = exec.BackgroundTasks > 0 ? $"{exec.BackgroundTasks} background task(s)" : $"Finished in {exec.LastDuration.TotalMilliseconds:0} ms";
            _state.DotColor = exec.BackgroundTasks > 0 ? Theme.Warning : Theme.Success;
            if (exec.BackgroundTasks > 0) _runTimer.Start();
        }
    }

    private void RunTick()
    {
        var exec = _runningExecutor;
        if (exec == null) return;
        if (exec.IsRunning)
        {
            int line = _execLine;
            if (line != _shownExecLine && Ed != null && line > 0 && line <= Ed.Lines.Count)
            {
                ClearExecMarkers();
                Ed.Lines[line - 1].MarkerAdd(MarkExecBg);
                Ed.Lines[line - 1].MarkerAdd(MarkExecArrow);
                _shownExecLine = line;
                _state.Text = $"Running · line {line}";
            }
        }
        else if (exec.BackgroundTasks == 0)
        {
            _btnStop.Enabled = false;
            _runTimer.Stop();
            if (_state.Text.Contains("background")) { _state.Text = "Ready"; _state.DotColor = Theme.TextDim; }
        }
        else
        {
            _state.Text = $"{exec.BackgroundTasks} background task(s)";
        }
        if ((Environment.TickCount64 / 60) % 6 == 0) RefreshVariables(exec);
    }

    private void ClearExecMarkers()
    {
        foreach (var d in _docs)
        {
            d.Editor.MarkerDeleteAll(MarkExecBg);
            d.Editor.MarkerDeleteAll(MarkExecArrow);
        }
        _shownExecLine = -1;
    }

    private void RefreshVariables(ScriptExecutor exec)
    {
        List<KeyValuePair<string, object?>> vars;
        try { vars = exec.Variables.ToList(); } catch { return; }
        _vars.BeginUpdate();
        _vars.Items.Clear();
        foreach (var (k, v) in vars)
        {
            var display = v is long l ? $"{l}  (0x{unchecked((uint)l):X})" : ScriptInterpreter.Format(v);
            var type = v switch { long => "int", double => "float", string => "string", bool => "bool", List<object?> => "array", null => "null", _ => "?" };
            var it = new ListViewItem(k);
            it.SubItems.Add(display);
            it.SubItems.Add(type);
            _vars.Items.Add(it);
        }
        _vars.EndUpdate();
    }

    private async Task SelfTestAsync()
    {
        var exec = new ScriptExecutor(new MockGameClient(), _log);
        _log("🧪 Running engine self-test on simulated memory…");
        await exec.ExecuteAsync(
            "log \"Self-test start\"\n" +
            "Peek(0x80001000, 16)\n" +
            "PokeInt32(0x80002000, 0x12345678)\n" +
            "assert(ReadUInt32(0x80002000) == 0x12345678, \"write/read mismatch\")\n" +
            "func sq(x) { return x * x }\n" +
            "var total = 0\nfor i in 0..5 { total += sq(i) }\n" +
            "assert(total == 30, \"loop/func failure\")\n" +
            "log \"Self-test completed: all checks passed\"\n");
        Notify(exec.LastError == null ? "Self-test passed" : $"Self-test failed: {exec.LastError.Message}",
            exec.LastError == null ? ToastHost.Kind.Success : ToastHost.Kind.Error);
    }

    // ───────────────────────────── documents ─────────────────────────────

    private void Activate(Doc d)
    {
        _active = d;
        foreach (var x in _docs) x.Editor.Visible = x == d;
        d.Editor.BringToFront();
        RefreshTabs();
        RunDiagnostics();
        d.Editor.Focus();
        _statusInfo.Text = d.FilePath ?? (d.LibraryName != null ? $"Library · {d.LibraryName}" : "Unsaved");
    }

    private void RefreshTabs()
    {
        _tabs.SetTabs(_docs.Select(d => (d.Name, d.Dirty)).ToList(), _active == null ? -1 : _docs.IndexOf(_active));
    }

    private void CloseDocument(Doc d)
    {
        if (d.Dirty)
        {
            var r = MessageBox.Show(FindForm(), $"Save changes to {d.Name}?", "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            if (r == DialogResult.Yes && !SaveDoc(d)) return;
        }
        int idx = _docs.IndexOf(d);
        _docs.Remove(d);
        _editorHost.Controls.Remove(d.Editor);
        d.Editor.Dispose();
        if (_docs.Count == 0) { NewDocument(); return; }
        Activate(_docs[Math.Clamp(idx - 1, 0, _docs.Count - 1)]);
    }

    private void Save() { if (_active != null) SaveDoc(_active); }

    private bool SaveDoc(Doc d)
    {
        try
        {
            if (d.FilePath != null)
            {
                File.WriteAllText(d.FilePath, d.Editor.Text);
                d.Editor.SetSavePoint();
                Notify($"Saved {Path.GetFileName(d.FilePath)}", ToastHost.Kind.Success);
                return true;
            }
            var name = d.LibraryName;
            string desc = "";
            if (name == null)
            {
                var r = Prompt.ShowTwo(FindForm(), "Save script", "Script name", Path.GetFileNameWithoutExtension(d.Name).Replace("untitled", "My Script"), "Description (optional)");
                if (r == null) return false;
                name = r.Value.First.Trim();
                desc = r.Value.Second;
                if (name.Length == 0) return false;
            }
            var script = new ScriptManager.SavedScript
            {
                Metadata = new ScriptManager.ScriptMetadata { Name = name, Description = desc },
                Content = d.Editor.Text,
                Template = "Custom"
            };
            _manager.SaveScriptAsync(script).GetAwaiter().GetResult();
            d.LibraryName = name;
            d.Name = name;
            d.Editor.SetSavePoint();
            RefreshTabs();
            _ = RefreshLibraryAsync();
            _log($"💾 Script saved: {name}");
            Notify($"Saved \"{name}\" to the script library", ToastHost.Kind.Success);
            return true;
        }
        catch (Exception ex)
        {
            Notify($"Save failed: {ex.Message}", ToastHost.Kind.Error);
            return false;
        }
    }

    private void SaveAs()
    {
        if (_active == null) return;
        using var sfd = new SaveFileDialog { Filter = "XScript (*.xs)|*.xs|Text (*.txt)|*.txt|All files|*.*", FileName = Path.GetFileNameWithoutExtension(_active.Name) + ".xs" };
        if (sfd.ShowDialog(FindForm()) != DialogResult.OK) return;
        _active.FilePath = sfd.FileName;
        _active.LibraryName = null;
        _active.Name = Path.GetFileName(sfd.FileName);
        SaveDoc(_active);
        RefreshTabs();
    }

    private void OpenFile()
    {
        using var ofd = new OpenFileDialog { Filter = "Scripts (*.xs;*.txt;*.cs;*.lua;*.ini;*.scr)|*.xs;*.txt;*.cs;*.lua;*.ini;*.scr|All files|*.*", Multiselect = true };
        if (ofd.ShowDialog(FindForm()) != DialogResult.OK) return;
        foreach (var f in ofd.FileNames)
        {
            try
            {
                OpenDocument(Path.GetFileName(f), File.ReadAllText(f), markClean: true, filePath: f);
                _log($"📥 Opened {Path.GetFileName(f)}");
            }
            catch (Exception ex) { Notify($"Could not open {Path.GetFileName(f)}: {ex.Message}", ToastHost.Kind.Error); }
        }
    }

    private void FormatActive()
    {
        if (Ed == null) return;
        var ed = Ed;
        var lines = ed.Text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        int depth = 0, blank = 0;
        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (t.Length == 0)
            {
                if (++blank <= 1) sb.Append('\n');
                continue;
            }
            blank = 0;
            var (opens, closes, leadingCloses) = CountBraces(t);
            int d = Math.Max(0, depth - leadingCloses);
            sb.Append(new string(' ', d * 4)).Append(t).Append('\n');
            depth = Math.Max(0, depth + opens - closes);
        }
        var result = sb.ToString().TrimEnd('\n') + "\n";
        if (result == ed.Text) { Notify("Already formatted", ToastHost.Kind.Info); return; }
        int line = ed.CurrentLine;
        ed.BeginUndoAction();
        ed.TargetWholeDocument();
        ed.ReplaceTarget(result);
        ed.EndUndoAction();
        GotoLine(Math.Min(line + 1, ed.Lines.Count));
        _log("✨ Script formatted");
    }

    private static (int opens, int closes, int leadingCloses) CountBraces(string line)
    {
        int o = 0, c = 0, lead = 0;
        bool inStr = false, leading = true;
        char q = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (inStr) { if (ch == '\\') i++; else if (ch == q) inStr = false; continue; }
            if (ch == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            if (ch is '"' or '\'') { inStr = true; q = ch; leading = false; continue; }
            if (ch == '{') { o++; leading = false; }
            else if (ch == '}') { c++; if (leading) lead++; }
            else if (!char.IsWhiteSpace(ch)) leading = false;
        }
        return (o, c, lead);
    }

    // ───────────────────────────── find ─────────────────────────────

    private void HighlightAll(string text)
    {
        var ed = Ed;
        if (ed == null) return;
        ed.IndicatorCurrent = IndFind;
        ed.IndicatorClearRange(0, ed.TextLength);
        if (text.Length < 2) return;
        ed.TargetStart = 0;
        ed.TargetEnd = ed.TextLength;
        ed.SearchFlags = SearchFlags.None;
        int n = 0;
        while (ed.SearchInTarget(text) != -1 && n++ < 5000)
        {
            ed.IndicatorFillRange(ed.TargetStart, ed.TargetEnd - ed.TargetStart);
            ed.TargetStart = ed.TargetEnd;
            ed.TargetEnd = ed.TextLength;
        }
        _statusInfo.Text = n > 0 ? $"{n} match(es)" : "No matches";
    }

    private void FindNext(bool forward)
    {
        var ed = Ed;
        if (ed == null || _find.Text.Length == 0) return;
        ed.SearchFlags = SearchFlags.None;
        if (forward) { ed.TargetStart = ed.SelectionEnd; ed.TargetEnd = ed.TextLength; }
        else { ed.TargetStart = ed.SelectionStart; ed.TargetEnd = 0; }
        if (ed.SearchInTarget(_find.Text) == -1)
        {
            if (forward) { ed.TargetStart = 0; ed.TargetEnd = ed.TextLength; }
            else { ed.TargetStart = ed.TextLength; ed.TargetEnd = 0; }
            if (ed.SearchInTarget(_find.Text) == -1) return;
        }
        ed.SetSelection(ed.TargetEnd, ed.TargetStart);
        ed.ScrollCaret();
    }

    private void ClearFind()
    {
        _find.Text = "";
        if (Ed == null) return;
        Ed.IndicatorCurrent = IndFind;
        Ed.IndicatorClearRange(0, Ed.TextLength);
    }

    private void ReplaceAll()
    {
        if (Ed == null) return;
        var r = Prompt.ShowTwo(FindForm(), "Replace", "Find", _find.Text.Length > 0 ? _find.Text : Ed.SelectedText, "Replace with");
        if (r == null || r.Value.First.Length == 0) return;
        var ed = Ed;
        ed.BeginUndoAction();
        int count = 0;
        ed.TargetStart = 0;
        ed.TargetEnd = ed.TextLength;
        while (ed.SearchInTarget(r.Value.First) != -1 && count < 100000)
        {
            ed.ReplaceTarget(r.Value.Second);
            ed.TargetStart = ed.TargetEnd;
            ed.TargetEnd = ed.TextLength;
            count++;
        }
        ed.EndUndoAction();
        Notify($"Replaced {count} occurrence(s)", ToastHost.Kind.Info);
    }

    // ───────────────────────────── keyboard ─────────────────────────────

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5: _ = RunAsync(); return true;
            case Keys.Shift | Keys.F5: Stop(); return true;
            case Keys.Control | Keys.S: Save(); return true;
            case Keys.Control | Keys.Shift | Keys.S: SaveAs(); return true;
            case Keys.Control | Keys.N: NewDocument(); return true;
            case Keys.Control | Keys.O: OpenFile(); return true;
            case Keys.Control | Keys.W: if (_active != null) CloseDocument(_active); return true;
            case Keys.Control | Keys.F: _find.Focus(); if (Ed?.SelectedText.Length is > 0 and < 80) _find.Text = Ed.SelectedText; _find.SelectAll(); return true;
            case Keys.Control | Keys.H: ReplaceAll(); return true;
            case Keys.F3: FindNext(true); return true;
            case Keys.Shift | Keys.F3: FindNext(false); return true;
            case Keys.Control | Keys.Shift | Keys.F: FormatActive(); return true;
            case Keys.Control | Keys.G:
            {
                var s = Prompt.Show(FindForm(), "Go to line", "Line number", (Ed?.CurrentLine + 1)?.ToString() ?? "1");
                if (int.TryParse(s, out var ln)) GotoLine(ln);
                return true;
            }
            case Keys.Control | Keys.Tab:
                if (_docs.Count > 1 && _active != null) Activate(_docs[(_docs.IndexOf(_active) + 1) % _docs.Count]);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ───────────────────────────── explorer ─────────────────────────────

    private TreeNode _savedRoot = null!, _templatesRoot = null!, _snippetsRoot = null!;

    private void BuildExplorer()
    {
        _explorer.Nodes.Clear();
        _savedRoot = _explorer.Nodes.Add("Saved scripts");
        _templatesRoot = _explorer.Nodes.Add("Templates");
        _snippetsRoot = _explorer.Nodes.Add("Snippets (insert)");
        _templatesRoot.Nodes.Add(new TreeNode("Welcome / quick start") { Tag = ("template", ScriptTemplates.Welcome) });
        foreach (var (name, code) in ScriptTemplates.All) _templatesRoot.Nodes.Add(new TreeNode(name) { Tag = ("template", code) });
        foreach (var (name, code) in ScriptTemplates.Snippets) _snippetsRoot.Nodes.Add(new TreeNode(name) { Tag = ("snippet", code) });
        foreach (TreeNode n in _explorer.Nodes) n.NodeFont = Theme.UISemibold;
        _savedRoot.Expand();
        _templatesRoot.Expand();
    }

    public async Task RefreshLibraryAsync()
    {
        try
        {
            var scripts = await _manager.LoadAllScriptsAsync();
            if (IsDisposed) return;
            void Fill()
            {
                _savedRoot.Nodes.Clear();
                foreach (var s in scripts.OrderBy(s => s.Metadata.Name))
                    _savedRoot.Nodes.Add(new TreeNode(s.Metadata.Name) { Tag = ("saved", s.Content), ToolTipText = s.Metadata.Description });
                _savedRoot.Text = $"Saved scripts ({scripts.Count})";
                _savedRoot.Expand();
            }
            if (InvokeRequired) BeginInvoke(Fill); else Fill();
            _log($"📁 Loaded {scripts.Count} saved scripts");
        }
        catch (Exception ex) { _log($"❌ Failed loading saved scripts: {ex.Message}"); }
    }

    private void OpenNode(TreeNode node)
    {
        if (node.Tag is not ValueTuple<string, string> tag) { node.Toggle(); return; }
        var (kind, code) = tag;
        switch (kind)
        {
            case "snippet": InsertText(code); break;
            case "template": OpenDocument(NormalizeName(node.Text).ToLowerInvariant() + ".xs", code); break;
            case "saved": OpenDocument(node.Text, code, markClean: true, libraryName: node.Text); break;
        }
    }

    private void DeleteSelectedLibraryScript()
    {
        var node = _explorer.SelectedNode;
        if (node?.Tag is not ValueTuple<string, string> tag || tag.Item1 != "saved") return;
        if (MessageBox.Show(FindForm(), $"Delete saved script \"{node.Text}\"?", "Delete script", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (_manager.DeleteScript(node.Text))
        {
            _log($"🗑️ Deleted script: {node.Text}");
            node.Remove();
            foreach (var d in _docs.Where(d => d.LibraryName == node.Text)) { d.LibraryName = null; d.Dirty = true; }
            RefreshTabs();
        }
    }

    // ───────────────────────────── inspector ─────────────────────────────

    private void ShowInspector(int i)
    {
        for (int k = 0; k < _inspectorPages.Length; k++)
        {
            _inspectorPages[k].Visible = k == i;
            _inspectorTabs[k].Variant = k == i ? ButtonVariant.Neutral : ButtonVariant.Ghost;
        }
    }

    private void FillReference()
    {
        var q = _refFilter.Text.Trim();
        _reference.BeginUpdate();
        _reference.Items.Clear();
        foreach (var f in ScriptBuiltins.All.Values
                     .Where(f => !f.Description.Contains("(alias of"))
                     .Where(f => q.Length == 0 || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Description.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Category.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f.Category).ThenBy(f => f.Name))
            _reference.Items.Add(f);
        _reference.EndUpdate();
    }

    private void DrawReferenceItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var f = (BuiltinFunction)_reference.Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(sel ? Theme.Blend(Theme.Input, Theme.Accent, 0.2f) : Theme.Input)) e.Graphics.FillRectangle(bg, e.Bounds);
        TextRenderer.DrawText(e.Graphics, f.Signature, Theme.MonoSmall, new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 4, e.Bounds.Width - 12, 18), Color.FromArgb(0xDC, 0xDC, 0xAA), TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, f.Description, Theme.UISmall, new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 22, e.Bounds.Width - 12, 20), Theme.TextMuted, TextFormatFlags.EndEllipsis);
        using var p = new Pen(Theme.Border);
        e.Graphics.DrawLine(p, e.Bounds.X, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    private void InsertReference()
    {
        if (_reference.SelectedItem is not BuiltinFunction f) return;
        var call = f.Name + "(";
        InsertText(call);
        ShowCallTip(Ed!, Ed!.CurrentPosition - 1);
    }

    private void BuildQuickMods(Panel host)
    {
        _quickMods.Items.AddRange(new object[]
        {
            "God Mode", "Infinite Ammo", "Speed x2.5", "Max Money (999999)", "Max XP (999999)", "Invincibility",
            "Perfect Aim", "No Clip", "Rapid Fire", "No Recoil", "All Perks", "Unlock All"
        });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(6, 6, 6, 4) };
        var act = IconButton.Create("Activate", Theme.Glyph.Check, ButtonVariant.Success, (_, _) =>
        {
            foreach (var m in _quickMods.SelectedItems.Cast<object>().Select(o => o.ToString()!)) { _activeMods[m] = true; _log($"✅ Activated: {m}"); }
            _quickModsCount.Text = $"{_activeMods.Count} active";
        });
        var deact = IconButton.Create("Deactivate", Theme.Glyph.Close, ButtonVariant.Neutral, (_, _) =>
        {
            foreach (var m in _quickMods.SelectedItems.Cast<object>().Select(o => o.ToString()!)) { _activeMods.Remove(m); _log($"❌ Deactivated: {m}"); }
            _quickModsCount.Text = $"{_activeMods.Count} active";
        });
        buttons.Controls.Add(act);
        buttons.Controls.Add(deact);
        var hint = new Label { Dock = DockStyle.Top, Height = 40, Text = "Legacy quick-mod toggles. For real mods open a template or write a script.", ForeColor = Theme.TextMuted, Padding = new Padding(6, 4, 6, 0), Tag = "keep", Font = Theme.UISmall };
        _quickModsCount.Padding = new Padding(8, 0, 0, 0);
        host.Controls.Add(_quickMods);
        host.Controls.Add(hint);
        host.Controls.Add(_quickModsCount);
        host.Controls.Add(buttons);
    }

    // ───────────────────────────── session ─────────────────────────────

    private sealed class SessionDoc
    {
        public string Name { get; set; } = "";
        public string? LibraryName { get; set; }
        public string? FilePath { get; set; }
        public string Content { get; set; } = "";
        public bool Dirty { get; set; }
        public bool Active { get; set; }
    }

    private static string SessionPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", ".session");

    private bool LoadSession()
    {
        try
        {
            if (!File.Exists(SessionPath)) return false;
            var items = JsonSerializer.Deserialize<List<SessionDoc>>(File.ReadAllText(SessionPath));
            if (items == null || items.Count == 0) return false;
            Doc? active = null;
            foreach (var s in items)
            {
                OpenDocument(s.Name, s.Content, markClean: !s.Dirty, libraryName: s.LibraryName, filePath: s.FilePath);
                if (s.Active) active = _docs[^1];
            }
            if (active != null) Activate(active);
            return true;
        }
        catch { return false; }
    }

    // ───────────────────────────── helpers ─────────────────────────────

    public void RefreshOffsets() => RefreshOffsetKeywords();

    private void Notify(string msg, ToastHost.Kind kind)
    {
        if (Toast != null) Toast(msg, kind);
        else _log(msg);
    }

    private static Control Sep() => new Panel { Width = 1, Height = 24, BackColor = Theme.Border, Margin = new Padding(8, 4, 8, 0), Tag = "keep" };

    private static Label Header(string text) => new()
    {
        Text = text, Dock = DockStyle.Top, Height = 30, Padding = new Padding(12, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI Semibold", 8.25F), ForeColor = Theme.TextDim, BackColor = Theme.Surface, Tag = "keep"
    };

    public static void StyleMenu(ToolStrip menu)
    {
        menu.Renderer = new DarkMenuRenderer();
        menu.BackColor = Theme.Surface2;
        menu.ForeColor = Theme.Text;
        menu.Font = Theme.UI;
        foreach (ToolStripItem i in menu.Items) i.ForeColor = Theme.Text;
    }

    /// <summary>Document tab strip with dirty indicators, close buttons and a "+" button.</summary>
    private sealed class DocTabStrip : Control
    {
        private List<(string Name, bool Dirty)> _tabs = new();
        private int _selected = -1, _hover = -1;
        private readonly List<Rectangle> _rects = new();
        private Rectangle _plus;
        public event Action<int>? Selected;
        public event Action<int>? Closed;
        public event Action? NewRequested;

        public DocTabStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Top;
            Height = 36;
            Theme.Exclude(this);
        }

        public void SetTabs(List<(string, bool)> tabs, int selected) { _tabs = tabs; _selected = selected; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);
            _rects.Clear();
            int x = 0;
            for (int i = 0; i < _tabs.Count; i++)
            {
                var (name, dirty) = _tabs[i];
                int w = Math.Min(220, TextRenderer.MeasureText(name, Theme.UI).Width + 52);
                var r = new Rectangle(x, 0, w, Height);
                _rects.Add(r);
                bool sel = i == _selected;
                using (var b = new SolidBrush(sel ? Theme.CodeBg : i == _hover ? Theme.Surface2 : Theme.Surface)) g.FillRectangle(b, r);
                if (sel) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X, 0, r.Width, 2);
                using (var p = new Pen(Theme.Border)) g.DrawLine(p, r.Right - 1, 6, r.Right - 1, Height - 6);
                TextRenderer.DrawText(g, name, sel ? Theme.UISemibold : Theme.UI, new Rectangle(r.X + 14, 0, r.Width - 44, Height), sel ? Theme.Text : Theme.TextMuted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                var close = CloseRect(r);
                if (dirty && i != _hover)
                    using (var b = new SolidBrush(Theme.TextMuted)) g.FillEllipse(b, close.X + 4, close.Y + 4, 8, 8);
                else if (i == _hover || sel)
                {
                    var icon = Theme.Icon(7.5F);
                    TextRenderer.DrawText(g, Theme.Glyph.Close, icon, new Point(close.X + 2, close.Y + 2), Theme.TextMuted, TextFormatFlags.NoPadding);
                }
                x += w;
            }
            _plus = new Rectangle(x + 4, 4, 28, 28);
            var plusIcon = Theme.Icon(9F);
            TextRenderer.DrawText(g, Theme.Glyph.Add, plusIcon, new Point(_plus.X + 8, _plus.Y + 8), _hover == -2 ? Theme.Text : Theme.TextDim, TextFormatFlags.NoPadding);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }

        private static Rectangle CloseRect(Rectangle r) => new(r.Right - 26, r.Y + (r.Height - 16) / 2, 16, 16);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = _rects.FindIndex(r => r.Contains(e.Location));
            if (h < 0 && _plus.Contains(e.Location)) h = -2;
            if (h != _hover) { _hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_plus.Contains(e.Location)) { NewRequested?.Invoke(); return; }
            int i = _rects.FindIndex(r => r.Contains(e.Location));
            if (i < 0) return;
            if (e.Button == MouseButtons.Middle || CloseRect(_rects[i]).Contains(e.Location)) Closed?.Invoke(i);
            else Selected?.Invoke(i);
            base.OnMouseUp(e);
        }
    }
}

/// <summary>Dark renderer for context menus and tool strips.</summary>
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.TextMuted;
        base.OnRenderArrow(e);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.Blend(Theme.Surface2, Theme.Accent, 0.25f);
        public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
        public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
        public override Color MenuItemBorder => Theme.Border;
        public override Color MenuBorder => Theme.BorderStrong;
        public override Color ToolStripDropDownBackground => Theme.Surface2;
        public override Color ImageMarginGradientBegin => Theme.Surface2;
        public override Color ImageMarginGradientMiddle => Theme.Surface2;
        public override Color ImageMarginGradientEnd => Theme.Surface2;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color MenuItemPressedGradientBegin => Theme.Surface3;
        public override Color MenuItemPressedGradientEnd => Theme.Surface3;
        public override Color ToolStripBorder => Theme.Border;
        public override Color ToolStripGradientBegin => Theme.Surface;
        public override Color ToolStripGradientMiddle => Theme.Surface;
        public override Color ToolStripGradientEnd => Theme.Surface;
        public override Color ButtonSelectedHighlight => MenuItemSelected;
        public override Color ButtonSelectedGradientBegin => MenuItemSelected;
        public override Color ButtonSelectedGradientEnd => MenuItemSelected;
    }
}
