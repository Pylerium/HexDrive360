using System.Text.Json;
using MemoryEditor.Runtime.Gsc;
using MemoryEditor.UI;
using ScintillaNET;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>
/// Live GSC for Black Ops II: write GSC in a code editor, compile it to console bytecode, inject it in place of one of the
/// game's scripts and restart the round – one key (Ctrl+F5). Also injects compiled .gsc files (GSC Studio output, menus),
/// keeps a "mod set" of files that are re-injected automatically whenever they change on disk, dumps the game's own
/// scripts and restores everything with one click.
/// </summary>
public sealed class LiveGscTool : ToolBase
{
    private sealed class ModFile
    {
        public string Path { get; set; } = "";
        public string Target { get; set; } = "";
        public bool Enabled { get; set; } = true;
        [System.Text.Json.Serialization.JsonIgnore] public string Status { get; set; } = "";
    }

    private const int IndError = 8;
    private const int MarkError = 3;

    private readonly GscInjector _inj;
    private readonly Scintilla _ed;
    private readonly DataGridView _poolGrid = Grid(("Script", 250), ("Size", 70), ("State", 80));
    private readonly DataGridView _setGrid = Grid(("On", 34), ("File", 170), ("Replaces", 200), ("Status", 120));
    private readonly TextBox _poolFilter = T("", 200, "Filter scripts", mono: false);
    private readonly TextBox _target = T("", 270, "Game script to replace");
    private readonly ComboBox _restart = C(150, "No restart", "map_restart", "fast_restart");
    private readonly ComboBox _method = C(200, "Auto", "Live memory", "GSC Loader (raw folder)");
    private HashSet<string> _loaderScripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _arena = T("0x40300000", 100, "Arena");
    private readonly CheckBox _watch = new() { Text = "Auto re-inject on save", AutoSize = true, Margin = new Padding(6, 8, 10, 0) };
    private readonly Label _status = L("Not attached");
    private readonly ListBox _output = new() { Dock = DockStyle.Fill, BorderStyle = System.Windows.Forms.BorderStyle.None, IntegralHeight = false, HorizontalScrollbar = true };
    private readonly Label _docLabel = L("untitled.gsc");
    private readonly ProgressBar _progress = new() { Width = 120, Height = 10, Margin = new Padding(0, 12, 10, 0), Visible = false };

    private List<ScriptEntry> _poolView = new();
    private List<ModFile> _set = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, DateTime> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 400 };
    private string? _docPath;
    /// <summary>Set when the open document is an already compiled script (injected as is).</summary>
    private byte[]? _compiledDoc;
    private bool _dirty, _busy, _acknowledged, _attaching;
    private CancellationTokenSource? _cts;

    private static string SetPath => Path.Combine(GscInjector.SessionFolder, "modset.json");
    private static string ScriptsFolder => Path.Combine(GscInjector.SessionFolder, "scripts");

    public override string Key => "gsc";
    public override string Title => "Live GSC";
    public override string Glyph => Theme.Glyph.Document;
    public override string Description => "Write, compile and inject GSC into the running game";

    public LiveGscTool(IToolHost host) : base(host)
    {
        GscCompiler.Warmup();
        _inj = new GscInjector(() => App.Client, App.Log);
        _ed = CreateEditor();
        _ed.Text = Template;
        _ed.EmptyUndoBuffer();
        _ed.SetSavePoint();
        _ed.SavePointLeft += (_, _) => { _dirty = true; UpdateDocLabel(); };
        _ed.SavePointReached += (_, _) => { _dirty = false; UpdateDocLabel(); };
        _target.Text = "maps/mp/gametypes/_clientids.gsc";
        _restart.SelectedIndex = 1;
        _output.BackColor = Theme.CodeBg;
        _output.ForeColor = Theme.Text;
        _output.Font = Theme.MonoSmall;
        _output.DoubleClick += (_, _) => GoToDiagnostic();

        // ── script pool ──
        _poolGrid.VirtualMode = true;
        _poolGrid.MultiSelect = false;
        _poolGrid.CellValueNeeded += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)_poolView.Count) return;
            var s = _poolView[e.RowIndex];
            e.Value = e.ColumnIndex switch { 0 => s.Name, 1 => FormatSize(s.Length), _ => _inj.IsInjected(s) ? "injected" : _loaderScripts.Contains(s.Name) ? "in raw" : "" };
        };
        _poolGrid.CellFormatting += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)_poolView.Count || e.CellStyle == null) return;
            if (_inj.IsInjected(_poolView[e.RowIndex]) || _loaderScripts.Contains(_poolView[e.RowIndex].Name)) { e.CellStyle.ForeColor = Theme.Success; if (e.ColumnIndex == 2) e.CellStyle.Font = Theme.UISemibold; }
            else if (e.ColumnIndex == 1) e.CellStyle.ForeColor = Theme.TextDim;
        };
        _poolGrid.CellDoubleClick += (_, e) => { if ((uint)e.RowIndex < (uint)_poolView.Count) _target.Text = _poolView[e.RowIndex].Name; };
        _poolGrid.ContextMenuStrip = BuildPoolMenu();
        _poolFilter.TextChanged += (_, _) => ApplyPoolFilter();

        // ── mod set ──
        _setGrid.VirtualMode = true;
        _setGrid.ReadOnly = false;
        foreach (DataGridViewColumn col in _setGrid.Columns) col.ReadOnly = col.Index != 2;
        _setGrid.CellValueNeeded += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)_set.Count) return;
            var m = _set[e.RowIndex];
            e.Value = e.ColumnIndex switch { 0 => m.Enabled ? "✔" : "", 1 => Path.GetFileName(m.Path), 2 => m.Target, _ => m.Status };
        };
        _setGrid.CellValuePushed += (_, e) =>
        {
            if (e.ColumnIndex != 2 || (uint)e.RowIndex >= (uint)_set.Count) return;
            _set[e.RowIndex].Target = (e.Value?.ToString() ?? "").Trim().Replace('\\', '/');
            SaveSet();
        };
        _setGrid.CellClick += (_, e) =>
        {
            if (e.ColumnIndex != 0 || (uint)e.RowIndex >= (uint)_set.Count) return;
            _set[e.RowIndex].Enabled = !_set[e.RowIndex].Enabled;
            SaveSet();
            _setGrid.InvalidateRow(e.RowIndex);
        };
        _setGrid.CellFormatting += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)_set.Count || e.CellStyle == null) return;
            var m = _set[e.RowIndex];
            if (e.ColumnIndex == 0) e.CellStyle.ForeColor = Theme.Success;
            if (e.ColumnIndex == 3) e.CellStyle.ForeColor = m.Status.StartsWith("✓") ? Theme.Success : m.Status.StartsWith("✗") ? Theme.Danger : Theme.TextMuted;
            if (!m.Enabled) e.CellStyle.ForeColor = Theme.TextDim;
        };
        _setGrid.CellDoubleClick += (_, e) => { if ((uint)e.RowIndex < (uint)_set.Count && e.ColumnIndex == 1) OpenDocument(_set[e.RowIndex].Path); };
        _setGrid.ContextMenuStrip = BuildSetMenu();

        _watch.CheckedChanged += (_, _) => { RebuildWatchers(); SaveSet(); };
        _debounce.Tick += async (_, _) => await FlushChangesAsync();
        _arena.Leave += (_, _) => ApplyArena();

        // ── layout ──
        var top = Row(
            B("Attach", Theme.Glyph.Refresh, ButtonVariant.Primary, async (_, _) => await AttachAsync(true), "Find the running game's scripts (automatic when you open this tool)"),
            B("Restore all", Theme.Glyph.Refresh, ButtonVariant.Danger, async (_, _) => await RestoreAllAsync(), "Point every replaced script back at the game's own"),
            L("Method"), _method, L("Then"), _restart,
            B("Restart now", Theme.Glyph.Play, ButtonVariant.Neutral, async (_, _) => await RestartNowAsync(), "Restart the round so the game loads the injected scripts"),
            B("Command…", Theme.Glyph.Terminal, ButtonVariant.Ghost, async (_, _) => await CommandPromptAsync(), "Run a console command in the game (Cbuf_AddText)"),
            L("Arena"), _arena, _progress, _status);

        var edBar = Row(
            B("New", Theme.Glyph.Add, ButtonVariant.Ghost, (_, _) => NewDocument(), "New script (Ctrl+N)"),
            B("Open", Theme.Glyph.Open, ButtonVariant.Ghost, (_, _) => OpenDocument(null), "Open a .gsc / .txt source or a compiled .gsc (Ctrl+O)"),
            B("Save", Theme.Glyph.Save, ButtonVariant.Ghost, (_, _) => SaveDocument(false), "Save (Ctrl+S)"),
            _docLabel,
            L("Replaces"), _target,
            B("Check", Theme.Glyph.Check, ButtonVariant.Ghost, (_, _) => CheckSyntax(), "Syntax check (F7)"),
            B("Compile", Theme.Glyph.Build, ButtonVariant.Neutral, async (_, _) => await CompileToFileAsync(), "Compile to a .gsc file for GSC Studio / USB loaders (F6)"),
            B("Inject", Theme.Glyph.Upload, ButtonVariant.Success, async (_, _) => await InjectEditorAsync(restart: false), "Compile and inject (F5)"),
            B("Inject + restart", Theme.Glyph.Play, ButtonVariant.Primary, async (_, _) => await InjectEditorAsync(restart: true), "Compile, inject and restart the round (Ctrl+F5)"));

        var outputCard = Card(_output);
        outputCard.Dock = DockStyle.Bottom;
        outputCard.Height = 150;
        var editorCard = Card(_ed);
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(8, 0, 0, 0) };
        right.Controls.Add(editorCard);
        right.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 6, BackColor = Theme.Bg });
        right.Controls.Add(outputCard);
        right.Controls.Add(edBar);

        var poolCard = Card(_poolGrid);
        var poolHead = Row(L("GAME SCRIPTS"), _poolFilter);
        var setCard = Card(_setGrid);
        setCard.Dock = DockStyle.Bottom;
        setCard.Height = 210;
        var setHead = Row(L("MOD SET"),
            B("Add files", Theme.Glyph.Add, ButtonVariant.Ghost, (_, _) => AddFiles(), "Add compiled .gsc files or GSC sources"),
            B("Add folder", Theme.Glyph.Folder, ButtonVariant.Ghost, (_, _) => AddFolder(), "Add every script under a folder (maps\\mp\\… layout like GSC Studio / menus)"),
            B("Inject set", Theme.Glyph.Upload, ButtonVariant.Success, async (_, _) => await InjectSetAsync(_set.Where(m => m.Enabled).ToList(), restart: true), "Inject every enabled file, then restart"),
            _watch);
        setHead.Dock = DockStyle.Bottom;
        var left = new Panel { Dock = DockStyle.Left, Width = 470, BackColor = Theme.Bg };
        left.Controls.Add(poolCard);
        left.Controls.Add(poolHead);
        left.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 6, BackColor = Theme.Bg });
        left.Controls.Add(setCard);
        left.Controls.Add(setHead);

        Controls.Add(right);
        Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 6, BackColor = Theme.Bg });
        Controls.Add(left);
        Controls.Add(top);

        LoadSet();
        _method.SelectedIndexChanged += (_, _) => { SaveSet(); RefreshSetTargets(); };
        _restart.SelectedIndexChanged += (_, _) => SaveSet();
        Out("Live GSC – F5 inject · Ctrl+F5 inject + restart · F6 compile to file · F7 check · Ctrl+S save");
        Out("Use it in private matches / custom games you host. Inject in the pregame lobby, or mid-match with a restart.");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  lifecycle
    // ═════════════════════════════════════════════════════════════════════════

    public override void OnShown()
    {
        if (!_inj.IsReady && Client != null) _ = AttachAsync(false);
        _ed.Focus();
    }

    public override void OnConnectionChanged(bool connected)
    {
        if (!connected) { _inj.Reset(); _poolView = new(); _poolGrid.RowCount = 0; SetStatus("Not attached – connect to the console"); }
        else if (Visible) _ = AttachAsync(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { foreach (var w in _watchers) w.Dispose(); _debounce.Dispose(); }
        base.Dispose(disposing);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5: _ = InjectEditorAsync(restart: false); return true;
            case Keys.Control | Keys.F5: _ = InjectEditorAsync(restart: true); return true;
            case Keys.F6: _ = CompileToFileAsync(); return true;
            case Keys.F7: CheckSyntax(); return true;
            case Keys.Control | Keys.S: SaveDocument(false); return true;
            case Keys.Control | Keys.Shift | Keys.S: SaveDocument(true); return true;
            case Keys.Control | Keys.O: OpenDocument(null); return true;
            case Keys.Control | Keys.N: NewDocument(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  attach
    // ═════════════════════════════════════════════════════════════════════════

    private async Task AttachAsync(bool user)
    {
        if (_attaching) return;
        var c = user ? RequireClient() : Client;
        if (c == null) return;
        _attaching = true;
        try
        {
            ApplyArena();
            var status = new Progress<string>(SetStatus);
            await _inj.AttachAsync(status, CancellationToken.None);
            _inj.CbufAddText = ResolveCbuf();
            var names = new AutoCompleteStringCollection();
            names.AddRange(_inj.Pool.Select(p => p.Name).ToArray());
            _target.AutoCompleteCustomSource = names;
            _target.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _target.AutoCompleteSource = AutoCompleteSource.CustomSource;
            await RefreshLoaderListAsync();
            ApplyPoolFilter();
            RefreshSetTargets();
            int injected = _inj.Pool.Count(_inj.IsInjected);
            if (_inj.LoaderActive && _method.SelectedIndex == 0) Out("📦 GSC Loader (Tesseract.dll) detected – Auto method copies scripts into its raw folder (no live memory writes)");
            SetStatus($"{_inj.GameName} · {_inj.Pool.Count} scripts" + (injected > 0 ? $" · {injected} injected" : "") +
                      (c.IsJRPCConnected ? (_inj.CbufAddText != null ? "" : " · restart unavailable (Cbuf_AddText unknown)") : " · no JRPC2: restart manually"));
            if (user) App.Toast($"Attached to {_inj.GameName}: {_inj.Pool.Count} scripts", ToastHost.Kind.Success);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            if (_inj.LoaderActive) { await RefreshLoaderListAsync(); SetStatus("GSC Loader ready (raw folder) · " + ex.Message); }
            else if (user) App.Toast(ex.Message, ToastHost.Kind.Warning);
        }
        finally { _attaching = false; }
    }

    /// <summary>Cbuf_AddText from the loaded offsets (Game Library / runtime pull), else the TU18 address.</summary>
    private uint? ResolveCbuf()
    {
        foreach (var n in new[] { "Cbuf_AddText", "Cbuf_addText", "cbuf_addtext" })
            if (Host.Symbols.Resolve(n) is { } a && a >= 0x82000000 && a < 0x90000000) return a;
        return _inj.BuildKey.EndsWith("_53643D71", StringComparison.OrdinalIgnoreCase) ? 0x82110108 : null;
    }

    private void ApplyArena()
    {
        if (MemorySafety.TryParseAddress(_arena.Text, out var a) && a is >= 0x40000000 and < 0x80000000) _inj.ArenaBase = a & ~0xFFu;
        else { _arena.Text = $"0x{_inj.ArenaBase:X8}"; App.Toast("The arena must be in the title heap (0x40000000–0x7FFFFFFF)", ToastHost.Kind.Warning); }
    }

    /// <summary>Re-attaches when the game changed since the pool was read (the stale pool would point at freed memory).</summary>
    private async Task<bool> EnsureAttachedAsync()
    {
        var c = RequireClient();
        if (c == null) return false;
        if (!_inj.IsReady) await AttachAsync(!UseLoader);
        else
        {
            var probe = _inj.Pool[0];
            var raw = await c.GetMemoryAsync(probe.EntryAddress, 4);
            if (raw == null || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(raw) != probe.NamePointer)
            {
                Out("ℹ The game changed – re-reading its scripts");
                await AttachAsync(true);
            }
        }
        return _inj.IsReady;
    }

    private bool Acknowledge()
    {
        if (_acknowledged) return true;
        var r = MessageBox.Show(FindForm(),
            "Live GSC changes the scripts of the game this console is hosting.\n\n" +
            "Use it in private matches, custom games or split-screen that you host – not in public online lobbies.\n\n" +
            "Inject in the pregame lobby, or mid-match followed by a round restart. Continue?",
            "Live GSC", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        _acknowledged = r == DialogResult.Yes;
        return _acknowledged;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  editor → compile → inject
    // ═════════════════════════════════════════════════════════════════════════

    private async Task InjectEditorAsync(bool restart)
    {
        if (_busy) return;
        var target = _target.Text.Trim().Replace('\\', '/');
        if (target.Length == 0) { App.Toast("Choose the game script to replace", ToastHost.Kind.Warning); _target.Focus(); return; }
        if (!Acknowledge()) return;
        if (_docPath != null && _dirty) SaveDocument(false);

        var bytes = _compiledDoc ?? await CompileEditorAsync();
        if (bytes == null) return;

        if (!await EnsureAttachedAsync() && !UseLoader) return;
        await RunBusyAsync(async (p, ct) =>
        {
            await ApplyScriptAsync(target, bytes, _docPath ?? (target.EndsWith(".csc", StringComparison.OrdinalIgnoreCase) ? "editor.csc" : "editor.gsc"), p, ct);
            if (restart) await RestartAsync();
        }, $"{(UseLoader ? "Deploying" : "Injecting")} {target}…");
        ApplyPoolFilter();
    }

    /// <summary>Auto = the GSC Loader when Tesseract.dll is running (live writes would clash with it), else live memory.</summary>
    private bool UseLoader => _method.SelectedIndex == 2 || (_method.SelectedIndex == 0 && _inj.LoaderActive);

    private async Task ApplyScriptAsync(string target, byte[] bytes, string source, IProgress<double> p, CancellationToken ct)
    {
        if (UseLoader)
        {
            await _inj.DeployToLoaderAsync(target, bytes, source, p, ct);
            _loaderScripts.Add(target);
            if (!_inj.LoaderActive) Out("ℹ The GSC Loader is not running in this game – the raw folder is only read by the loader default_mp.xex + Tesseract.dll");
        }
        else
        {
            _inj.AllowWithLoader = _method.SelectedIndex == 1;
            await _inj.InjectAsync(target, bytes, source, p, ct);
        }
    }

    private async Task RefreshLoaderListAsync()
    {
        try { _loaderScripts = new HashSet<string>(await _inj.ListLoaderScriptsAsync(), StringComparer.OrdinalIgnoreCase); }
        catch { _loaderScripts.Clear(); }
        if (_loaderScripts.Count > 0) Out($"📦 GSC Loader raw folder: {string.Join(", ", _loaderScripts)}");
    }

    private async Task<byte[]?> CompileEditorAsync()
    {
        ClearMarkers();
        var src = _ed.Text;
        var r = await GscCompiler.CompileAsync(src);
        ShowDiagnostics(r.Diagnostics);
        if (!r.Success) { App.Toast($"Compile failed – {r.Diagnostics.Count(d => d.IsError)} error(s)", ToastHost.Kind.Error); return null; }
        var g = GscBinary.TryParse(r.Output!)!;
        Out($"✓ Compiled in {r.Elapsed.TotalMilliseconds:0} ms – {g.Summary}");
        return r.Output;
    }

    private void CheckSyntax()
    {
        ClearMarkers();
        var d = GscCompiler.Check(_ed.Text);
        ShowDiagnostics(d);
        if (d.Count == 0) { Out("✓ No syntax errors"); App.Toast("No syntax errors", ToastHost.Kind.Success); }
    }

    private async Task CompileToFileAsync()
    {
        if (_compiledDoc != null) { App.Toast("This document is already compiled", ToastHost.Kind.Info); return; }
        var bytes = await CompileEditorAsync();
        if (bytes == null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "Compiled GSC (*.gsc)|*.gsc|All files|*.*",
            FileName = Path.GetFileName(_target.Text.Trim().Length > 0 ? _target.Text.Trim() : "script.gsc"),
            InitialDirectory = _docPath != null ? Path.GetDirectoryName(_docPath) : ScriptsFolder,
        };
        Directory.CreateDirectory(ScriptsFolder);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (_docPath != null && Path.GetFullPath(dlg.FileName).Equals(Path.GetFullPath(_docPath), StringComparison.OrdinalIgnoreCase))
        { App.Toast("Choose a different file – that is your source", ToastHost.Kind.Warning); return; }
        File.WriteAllBytes(dlg.FileName, bytes);
        Out($"💾 Compiled script saved → {dlg.FileName}");
    }

    private async Task RestartAsync()
    {
        var mode = (RestartMode)_restart.SelectedIndex;
        if (mode == RestartMode.None) { Out("ℹ Not restarting – start / restart the match to load the script"); return; }
        var c = Client;
        if (c == null || !c.IsJRPCConnected || _inj.CbufAddText == null)
        {
            Out("ℹ Restart the round in game to load the script (automatic restart needs JRPC2 and Cbuf_AddText)");
            App.Toast("Injected – restart the round in game to load it", ToastHost.Kind.Info);
            return;
        }
        SetStatus($"Restarting ({(mode == RestartMode.FastRestart ? "fast_restart" : "map_restart")})…");
        await _inj.RestartAsync(mode);
        App.Toast("Injected and restarted", ToastHost.Kind.Success);
    }

    private async Task RestartNowAsync()
    {
        if (!await EnsureAttachedAsync()) return;
        await RunBusyAsync(async (_, _) => await RestartAsync(), "Restarting…");
    }

    private async Task CommandPromptAsync()
    {
        if (!await EnsureAttachedAsync()) return;
        var cmd = Prompt.Show(FindForm(), "Console command", "Runs in the game through Cbuf_AddText (e.g. map_restart, fast_restart, set dvar value)", "");
        if (string.IsNullOrWhiteSpace(cmd)) return;
        await RunBusyAsync(async (_, _) => await _inj.SendCommandAsync(cmd), "Sending command…");
    }

    private async Task RestoreAllAsync()
    {
        if (!await EnsureAttachedAsync()) return;
        await RunBusyAsync(async (_, _) =>
        {
            int n = await _inj.RestoreAllAsync();
            Out(n > 0 ? $"↩️ Restored {n} script(s)" : "Nothing is injected in memory");
            if (_loaderScripts.Count > 0 && MessageBox.Show(FindForm(), $"Also remove {_loaderScripts.Count} script(s) from the GSC Loader's raw folder?\n\n{string.Join("\n", _loaderScripts)}",
                    "Restore all", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                foreach (var t in _loaderScripts.ToList()) if (await _inj.RemoveFromLoaderAsync(t)) { _loaderScripts.Remove(t); n++; }
            }
            if (n > 0) await RestartAsync();
        }, "Restoring…");
        ApplyPoolFilter();
    }

    private async Task RunBusyAsync(Func<IProgress<double>, CancellationToken, Task> work, string what)
    {
        if (_busy) { App.Toast("Busy – wait for the current operation", ToastHost.Kind.Info); return; }
        _busy = true;
        _cts = new CancellationTokenSource();
        _progress.Visible = true;
        _progress.Value = 0;
        SetStatus(what);
        var p = new Progress<double>(v => _progress.Value = (int)Math.Clamp(v * 100, 0, 100));
        try { await work(p, _cts.Token); SetStatus($"{_inj.GameName} · {_inj.Pool.Count} scripts · {_inj.ActiveSlots.Count()} injected"); }
        catch (OperationCanceledException) { SetStatus("Cancelled"); }
        catch (Exception ex)
        {
            Out("✗ " + ex.Message);
            SetStatus(ex.Message);
            App.Toast(ex.Message, ToastHost.Kind.Error);
        }
        finally
        {
            _busy = false;
            _progress.Visible = false;
            _poolGrid.Invalidate();
            _setGrid.Invalidate();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  mod set (files → scripts, auto re-inject)
    // ═════════════════════════════════════════════════════════════════════════

    private void AddFiles()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Filter = "GSC scripts (*.gsc;*.csc;*.txt)|*.gsc;*.csc;*.txt|All files|*.*", Title = "Add scripts to the mod set" };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        foreach (var f in dlg.FileNames) AddToSet(f);
        SaveSet();
        RebuildWatchers();
    }

    private void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Folder with scripts in the game's layout (maps\\mp\\…)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        int before = _set.Count;
        foreach (var f in Directory.EnumerateFiles(dlg.SelectedPath, "*.*", SearchOption.AllDirectories)
                                   .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".gsc" or ".csc"))
            AddToSet(f);
        Out($"📁 Added {_set.Count - before} script(s) from {dlg.SelectedPath}");
        SaveSet();
        RebuildWatchers();
    }

    private void AddToSet(string file)
    {
        if (_set.Any(m => m.Path.Equals(file, StringComparison.OrdinalIgnoreCase))) return;
        var target = ScriptPool.TargetFor(file, _inj.Pool) ?? (Path.GetFileName(file).Equals("_clientids.gsc", StringComparison.OrdinalIgnoreCase) ? "maps/mp/gametypes/_clientids.gsc" : "");
        _set.Add(new ModFile { Path = file, Target = target, Status = target.Length == 0 ? "set “Replaces”" : "" });
        _setGrid.RowCount = _set.Count;
    }

    private void RefreshSetTargets()
    {
        foreach (var m in _set.Where(m => m.Target.Length == 0))
            m.Target = ScriptPool.TargetFor(m.Path, _inj.Pool) ?? "";
        foreach (var m in _set)
            m.Status = !File.Exists(m.Path) ? "✗ file missing" : m.Target.Length == 0 ? "set “Replaces”" : _inj.Find(m.Target) == null && _inj.IsReady ? (UseLoader ? "loader only (not loaded now)" : "✗ not in this game") : m.Status;
        _setGrid.Invalidate();
    }

    private async Task InjectSetAsync(List<ModFile> files, bool restart)
    {
        if (files.Count == 0) { App.Toast("Add scripts to the mod set first", ToastHost.Kind.Info); return; }
        if (!Acknowledge()) return;
        if (!await EnsureAttachedAsync() && !UseLoader) return;
        await RunBusyAsync(async (p, ct) =>
        {
            int ok = 0, i = 0;
            foreach (var m in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (m.Target.Length == 0) throw new InvalidOperationException("choose which script it replaces");
                    var bytes = await LoadCompiledAsync(m.Path);
                    var sub = new Progress<double>(v => ((IProgress<double>)p).Report((i + v) / files.Count));
                    await ApplyScriptAsync(m.Target, bytes, m.Path, sub, ct);
                    m.Status = $"✓ {DateTime.Now:HH:mm:ss}";
                    ok++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    m.Status = "✗ " + ex.Message;
                    Out($"✗ {Path.GetFileName(m.Path)}: {ex.Message}");
                }
                i++;
                _setGrid.Invalidate();
            }
            Out($"💉 Mod set: {ok}/{files.Count} injected");
            if (ok > 0 && restart) await RestartAsync();
        }, $"Injecting {files.Count} script(s)…");
        ApplyPoolFilter();
    }

    /// <summary>Compiled files are used as they are; GSC source is compiled first (errors go to the output panel).</summary>
    private async Task<byte[]> LoadCompiledAsync(string path)
    {
        var data = await File.ReadAllBytesAsync(path);
        if (GscBinary.TryParse(data) is { } g)
        {
            if (!g.IsValid) throw new InvalidOperationException(string.Join("; ", g.Problems));
            return data;
        }
        if (!GscBinary.LooksLikeSource(data)) throw new InvalidOperationException("not a GSC script (neither compiled nor source text)");
        var r = await GscCompiler.CompileAsync(System.Text.Encoding.UTF8.GetString(data));
        if (!r.Success)
        {
            foreach (var d in r.Diagnostics.Where(d => d.IsError)) Out($"✗ {Path.GetFileName(path)}({d.Line},{d.Column}): {d.Message}");
            throw new InvalidOperationException("compile failed");
        }
        return r.Output!;
    }

    private void RebuildWatchers()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        if (!_watch.Checked) return;
        foreach (var dir in _set.Select(m => Path.GetDirectoryName(m.Path)).Where(d => d != null && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var w = new FileSystemWatcher(dir!) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            void Changed(object? s, FileSystemEventArgs e)
            {
                if (!_set.Any(m => m.Enabled && m.Path.Equals(e.FullPath, StringComparison.OrdinalIgnoreCase))) return;
                if (IsDisposed) return;
                BeginInvoke(() => { _pendingChanges[e.FullPath] = DateTime.Now; _debounce.Start(); });
            }
            w.Changed += Changed;
            w.Created += Changed;
            w.Renamed += (s, e) => Changed(s, e);
            _watchers.Add(w);
        }
        Out($"👁 Watching {_watchers.Count} folder(s) – saved scripts are re-injected automatically");
    }

    private async Task FlushChangesAsync()
    {
        // wait until writers (GSC Studio, editors) have finished: no change for 700 ms
        if (_pendingChanges.Count == 0 || _busy) return;
        if (_pendingChanges.Values.Max() > DateTime.Now.AddMilliseconds(-700)) return;
        _debounce.Stop();
        var files = _set.Where(m => _pendingChanges.ContainsKey(m.Path)).ToList();
        _pendingChanges.Clear();
        if (files.Count == 0 || Client == null) return;
        Out($"🔁 Changed on disk: {string.Join(", ", files.Select(f => Path.GetFileName(f.Path)))}");
        await InjectSetAsync(files, restart: true);
    }

    private void LoadSet()
    {
        try
        {
            if (File.Exists(SetPath) && JsonSerializer.Deserialize<SetFile>(File.ReadAllText(SetPath)) is { } s)
            {
                _set = s.Files;
                _restart.SelectedIndex = Math.Clamp(s.Restart, 0, 2);
                _method.SelectedIndex = Math.Clamp(s.Method, 0, 2);
                _arena.Text = s.Arena ?? _arena.Text;
                _watch.Checked = s.Watch;
            }
        }
        catch { _set = new(); }
        _setGrid.RowCount = _set.Count;
        RefreshSetTargets();
    }

    private void SaveSet()
    {
        try
        {
            Directory.CreateDirectory(GscInjector.SessionFolder);
            File.WriteAllText(SetPath, JsonSerializer.Serialize(new SetFile { Files = _set, Restart = _restart.SelectedIndex, Method = _method.SelectedIndex, Arena = _arena.Text, Watch = _watch.Checked },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private sealed class SetFile
    {
        public List<ModFile> Files { get; set; } = new();
        public int Restart { get; set; } = 1;
        public int Method { get; set; }
        public string? Arena { get; set; }
        public bool Watch { get; set; }
    }

    private ContextMenuStrip BuildSetMenu()
    {
        var m = new ContextMenuStrip();
        List<ModFile> Sel() => _setGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => (uint)i < (uint)_set.Count).Select(i => _set[i]).ToList();
        m.Items.Add("Inject", null, async (_, _) => await InjectSetAsync(Sel(), restart: false));
        m.Items.Add("Inject + restart", null, async (_, _) => await InjectSetAsync(Sel(), restart: true));
        m.Items.Add("Open in editor", null, (_, _) => { if (Sel().FirstOrDefault() is { } f) OpenDocument(f.Path); });
        m.Items.Add("Replace the selected game script", null, (_, _) =>
        {
            if (_poolGrid.CurrentCell?.RowIndex is int i && (uint)i < (uint)_poolView.Count)
                foreach (var f in Sel()) f.Target = _poolView[i].Name;
            SaveSet(); _setGrid.Invalidate();
        });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Remove from set", null, (_, _) =>
        {
            foreach (var f in Sel()) _set.Remove(f);
            _setGrid.RowCount = _set.Count; SaveSet(); RebuildWatchers();
        });
        return m;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  game script pool
    // ═════════════════════════════════════════════════════════════════════════

    private void ApplyPoolFilter()
    {
        var f = _poolFilter.Text.Trim();
        _poolView = (f.Length == 0 ? _inj.Pool : _inj.Pool.Where(p => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(_inj.IsInjected).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _poolGrid.RowCount = 0;
        _poolGrid.RowCount = _poolView.Count;
    }

    private ContextMenuStrip BuildPoolMenu()
    {
        var m = new ContextMenuStrip();
        ScriptEntry? Sel() => _poolGrid.CurrentCell?.RowIndex is int i && (uint)i < (uint)_poolView.Count ? _poolView[i] : null;
        m.Items.Add("Replace with the editor script", null, (_, _) => { if (Sel() is { } s) _target.Text = s.Name; });
        m.Items.Add("Inject a file into this script…", null, async (_, _) =>
        {
            if (Sel() is not { } s) return;
            using var dlg = new OpenFileDialog { Filter = "GSC scripts (*.gsc;*.csc;*.txt)|*.gsc;*.csc;*.txt|All files|*.*" };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            AddToSet(dlg.FileName);
            var mf = _set.First(x => x.Path.Equals(dlg.FileName, StringComparison.OrdinalIgnoreCase));
            mf.Target = s.Name;
            SaveSet();
            await InjectSetAsync(new List<ModFile> { mf }, restart: false);
        });
        m.Items.Add("Restore the game's script", null, async (_, _) =>
        {
            if (Sel() is not { } s) return;
            await RunBusyAsync(async (_, _) =>
            {
                bool any = await _inj.RestoreAsync(s.Name);
                if (_loaderScripts.Contains(s.Name) && await _inj.RemoveFromLoaderAsync(s.Name)) { _loaderScripts.Remove(s.Name); any = true; }
                if (!any) Out($"{s.Name} is not replaced");
            }, "Restoring…");
            ApplyPoolFilter();
        });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Dump compiled script…", null, async (_, _) =>
        {
            if (Sel() is not { } s || RequireClient() == null) return;
            using var dlg = new SaveFileDialog { Filter = "Compiled GSC (*.gsc)|*.gsc|All files|*.*", FileName = s.FileName };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            await RunBusyAsync(async (_, _) =>
            {
                var bytes = await _inj.DumpAsync(s, original: true);
                await File.WriteAllBytesAsync(dlg.FileName, bytes);
                Out($"💾 {s.Name}: {bytes.Length:N0} bytes → {dlg.FileName}");
            }, "Dumping…");
        });
        m.Items.Add("Dump every script (game layout)…", null, async (_, _) =>
        {
            if (RequireClient() == null || !_inj.IsReady) return;
            using var dlg = new FolderBrowserDialog { Description = "Folder for the dumped scripts", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            await RunBusyAsync(async (p, ct) =>
            {
                int n = 0;
                foreach (var s in _inj.Pool)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var bytes = await _inj.DumpAsync(s, original: true);
                        var file = Path.Combine(dlg.SelectedPath, s.Name.Replace('/', Path.DirectorySeparatorChar));
                        if (!Path.HasExtension(file)) file += ".gsc";
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        await File.WriteAllBytesAsync(file, bytes, ct);
                        n++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Out($"✗ {s.Name}: {ex.Message}"); }
                    p.Report((double)n / _inj.Pool.Count);
                }
                Out($"💾 Dumped {n} scripts → {dlg.SelectedPath}");
            }, "Dumping every script…");
        });
        m.Items.Add("Copy name", null, (_, _) => { if (Sel() is { } s) Clipboard.SetText(s.Name); });
        m.Items.Add("Show pool entry in Active Memory", null, (_, _) => { if (Sel() is { } s) Host.ActiveMemory(s.EntryAddress); });
        return m;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  documents
    // ═════════════════════════════════════════════════════════════════════════

    private void NewDocument()
    {
        if (!ConfirmDiscard()) return;
        _docPath = null;
        _compiledDoc = null;
        _ed.Text = Template;
        _ed.EmptyUndoBuffer();
        _ed.SetSavePoint();
        UpdateDocLabel();
    }

    private void OpenDocument(string? path)
    {
        if (!ConfirmDiscard()) return;
        if (path == null)
        {
            using var dlg = new OpenFileDialog { Filter = "GSC scripts (*.gsc;*.csc;*.txt)|*.gsc;*.csc;*.txt|All files|*.*", InitialDirectory = Directory.Exists(ScriptsFolder) ? ScriptsFolder : "" };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            path = dlg.FileName;
        }
        var data = File.ReadAllBytes(path);
        if (GscBinary.TryParse(data) is { } g)
        {
            // a compiled script cannot be edited – keep it as the document so F5 injects it as is
            _docPath = path;
            _compiledDoc = data;
            _ed.Text = $"// {Path.GetFileName(path)} is a COMPILED script ({g.Summary}).\n// Press F5 to inject it as it is, or open its source to edit.\n//\n// Includes:\n" +
                       string.Join("\n", g.Includes.Select(i => "//   " + i)) + "\n";
            _ed.EmptyUndoBuffer();
            _ed.SetSavePoint();
        }
        else
        {
            _docPath = path;
            _compiledDoc = null;
            _ed.Text = System.Text.Encoding.UTF8.GetString(data).TrimStart('﻿');
            _ed.EmptyUndoBuffer();
            _ed.SetSavePoint();
        }
        if (ScriptPool.TargetFor(path, _inj.Pool) is { } t) _target.Text = t;
        UpdateDocLabel();
    }

    private void SaveDocument(bool saveAs)
    {
        if (_compiledDoc != null)
        { App.Toast("This document is a compiled script – it cannot be saved as text", ToastHost.Kind.Info); return; }
        if (_docPath == null || saveAs)
        {
            Directory.CreateDirectory(ScriptsFolder);
            using var dlg = new SaveFileDialog { Filter = "GSC source (*.gsc)|*.gsc|Text (*.txt)|*.txt", InitialDirectory = ScriptsFolder, FileName = _docPath != null ? Path.GetFileName(_docPath) : "my_script.gsc" };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            _docPath = dlg.FileName;
        }
        File.WriteAllText(_docPath, _ed.Text);
        _ed.SetSavePoint();
        UpdateDocLabel();
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(FindForm(), "Save changes to the current script?", "Live GSC", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) SaveDocument(false);
        return true;
    }

    private void UpdateDocLabel() => _docLabel.Text = (_docPath != null ? Path.GetFileName(_docPath) : "untitled.gsc") + (_dirty ? " •" : "");

    // ═════════════════════════════════════════════════════════════════════════
    //  output + diagnostics
    // ═════════════════════════════════════════════════════════════════════════

    private void Out(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Out(line)); return; }
        _output.Items.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        if (_output.Items.Count > 500) _output.Items.RemoveAt(0);
        _output.TopIndex = Math.Max(0, _output.Items.Count - 1);
    }

    private void SetStatus(string s) { if (!IsDisposed) _status.Text = s; }

    private void ShowDiagnostics(IReadOnlyList<GscDiagnostic> diags)
    {
        foreach (var d in diags)
        {
            Out($"{(d.IsError ? "✗" : "⚠")} line {d.Line}, col {d.Column}: {d.Message}");
            int line = Math.Clamp(d.Line - 1, 0, Math.Max(0, _ed.Lines.Count - 1));
            _ed.Lines[line].MarkerAdd(MarkError);
            int pos = Math.Min(_ed.Lines[line].Position + Math.Max(0, d.Column - 1), _ed.TextLength);
            int end = Math.Min(_ed.Lines[line].EndPosition, _ed.TextLength);
            _ed.IndicatorCurrent = IndError;
            if (end > pos) _ed.IndicatorFillRange(pos, end - pos);
        }
        if (diags.FirstOrDefault(d => d.IsError) is { } first)
        {
            int line = Math.Clamp(first.Line - 1, 0, Math.Max(0, _ed.Lines.Count - 1));
            _ed.Lines[line].Goto();
            _ed.ScrollCaret();
        }
    }

    private void ClearMarkers()
    {
        _ed.MarkerDeleteAll(MarkError);
        _ed.IndicatorCurrent = IndError;
        _ed.IndicatorClearRange(0, _ed.TextLength);
    }

    private void GoToDiagnostic()
    {
        if (_output.SelectedItem is not string s) return;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"line (\d+)|\((\d+),\d+\)");
        if (!m.Success) return;
        int line = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) - 1;
        if (line >= 0 && line < _ed.Lines.Count) { _ed.Lines[line].Goto(); _ed.ScrollCaret(); _ed.Focus(); }
    }

    private static string FormatSize(uint b) => b >= 1024 ? $"{b / 1024.0:0.#} KB" : $"{b} B";

    // ═════════════════════════════════════════════════════════════════════════
    //  editor
    // ═════════════════════════════════════════════════════════════════════════

    private const string Keywords =
        "if else while for foreach in return wait waittill waittillmatch waittillframeend endon notify thread childthread self level game anim " +
        "undefined true false break continue switch case default isdefined vectorscale";

    private const string Builtins =
        "iprintln iprintlnbold setdvar getdvar getdvarint getdvarfloat spawn spawnstruct precacheshader precachemodel precacheitem " +
        "giveweapon takeweapon takeallweapons switchtoweapon getcurrentweapon setweaponammoclip setweaponammostock freezecontrols " +
        "setorigin getorigin setplayerangles getplayerangles playerlinkto unlink moveto rotateto delete hide show setmodel " +
        "newclienthudelem newhudelem createfontstring createicon createrectangle setpoint settext setshader destroy fadeovertime " +
        "moveovertime scaleovertime isalive isplayer get_players getentarray getent randomint randomfloat int float distance " +
        "vectornormalize anglestoforward anglestoright anglestoup bullettrace magicbullet radiusdamage playfx playsound earthquake " +
        "setclientdvar enableinvulnerability disableinvulnerability setmovespeedscale setperk unsetperk hasperk " +
        "attackbuttonpressed adsbuttonpressed usebuttonpressed meleebuttonpressed fragbuttonpressed secondaryoffhandbuttonpressed " +
        "actionslotonebuttonpressed actionslottwobuttonpressed actionslotthreebuttonpressed actionslotfourbuttonpressed " +
        "jumpbuttonpressed stancebuttonpressed changeseatbuttonpressed issubstr tolower getsubstr strtok array_randomize " +
        "maps common_scripts setmatchflag registerscoreinfo";

    private const string Template = @"#include maps/mp/_utility;
#include common_scripts/utility;
#include maps/mp/gametypes/_hud_util;

// Replaces maps/mp/gametypes/_clientids.gsc – the game calls init() when a match starts.
init()
{
    level thread onPlayerConnect();
}

onPlayerConnect()
{
    for (;;)
    {
        level waittill(""connected"", player);
        player thread onPlayerSpawned();
    }
}

onPlayerSpawned()
{
    self endon(""disconnect"");
    level endon(""game_ended"");
    for (;;)
    {
        self waittill(""spawned_player"");
        self iprintln(""^2Live GSC ^7loaded – edit, then Ctrl+F5"");
    }
}
";

    private static Scintilla CreateEditor()
    {
        var ed = new Scintilla { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
        Theme.Exclude(ed);
        ed.StyleResetDefault();
        var def = ed.Styles[Style.Default];
        def.Font = Theme.Mono.Name;
        def.SizeF = 10.5F;
        def.BackColor = Theme.CodeBg;
        def.ForeColor = Color.FromArgb(0xD4, 0xD8, 0xE0);
        ed.StyleClearAll();
        ed.LexerName = "cpp";
        ed.SetProperty("fold", "1");
        ed.SetProperty("fold.compact", "0");
        ed.SetKeywords(0, Keywords);
        ed.SetKeywords(1, Builtins);

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
        S(Style.Cpp.Number, Color.FromArgb(0xB5, 0xCE, 0xA8));
        S(Style.Cpp.String, Color.FromArgb(0xCE, 0x91, 0x78));
        S(Style.Cpp.StringEol, Color.FromArgb(0xF0, 0x6B, 0x6B));
        S(Style.Cpp.Word, Color.FromArgb(0xC5, 0x86, 0xC0), bold: true);
        S(Style.Cpp.Word2, Color.FromArgb(0xDC, 0xDC, 0xAA));
        S(Style.Cpp.Operator, Color.FromArgb(0xB4, 0xBC, 0xC8));
        S(Style.Cpp.Preprocessor, Color.FromArgb(0x4E, 0xC9, 0xB0));

        ed.Styles[Style.LineNumber].BackColor = Theme.CodeBg;
        ed.Styles[Style.LineNumber].ForeColor = Color.FromArgb(0x4B, 0x53, 0x63);
        ed.Styles[Style.BraceLight].ForeColor = Color.White;
        ed.Styles[Style.BraceLight].BackColor = Color.FromArgb(0x33, 0x4A, 0x6E);
        ed.CaretForeColor = Color.White;
        ed.CaretLineVisible = true;
        ed.CaretLineBackColor = Color.FromArgb(0x1A, 0x1E, 0x27);
        ed.SetSelectionBackColor(true, Color.FromArgb(0x26, 0x4F, 0x78));
        ed.IndentationGuides = IndentView.LookBoth;
        ed.TabWidth = 4;
        ed.IndentWidth = 4;
        ed.UseTabs = false;
        ed.Margins[0].Type = MarginType.Number;
        ed.Margins[0].Width = 46;
        ed.Margins[0].BackColor = Theme.CodeBg;
        ed.Margins[1].Type = MarginType.Symbol;
        ed.Margins[1].Width = 14;
        ed.Margins[1].Mask = 1u << MarkError;
        ed.Margins[1].BackColor = Theme.CodeBg;
        ed.Margins[2].Type = MarginType.Symbol;
        ed.Margins[2].Mask = Marker.MaskFolders;
        ed.Margins[2].Sensitive = true;
        ed.Margins[2].Width = 14;
        ed.SetFoldMarginColor(true, Theme.CodeBg);
        ed.SetFoldMarginHighlightColor(true, Theme.CodeBg);
        for (int i = 25; i <= 31; i++) { ed.Markers[i].SetForeColor(Theme.CodeBg); ed.Markers[i].SetBackColor(Color.FromArgb(0x5A, 0x63, 0x75)); }
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
        ed.Indicators[IndError].Style = IndicatorStyle.Squiggle;
        ed.Indicators[IndError].ForeColor = Theme.Danger;

        // brace matching + auto-indent after "{"
        ed.UpdateUI += (_, _) =>
        {
            int p = ed.CurrentPosition;
            int bp = p > 0 && "{}()[]".Contains((char)ed.GetCharAt(p - 1)) ? p - 1 : "{}()[]".Contains((char)ed.GetCharAt(p)) ? p : -1;
            if (bp < 0) { ed.BraceHighlight(Scintilla.InvalidPosition, Scintilla.InvalidPosition); return; }
            int match = ed.BraceMatch(bp);
            if (match == Scintilla.InvalidPosition) ed.BraceBadLight(bp); else ed.BraceHighlight(bp, match);
        };
        ed.CharAdded += (_, e) =>
        {
            if (e.Char != '\n') return;
            int line = ed.CurrentLine;
            if (line == 0) return;
            var prev = ed.Lines[line - 1];
            int indent = prev.Indentation + (prev.Text.TrimEnd().EndsWith("{") ? 4 : 0);
            ed.Lines[line].Indentation = indent;
            ed.GotoPosition(ed.Lines[line].Position + indent);
        };
        return ed;
    }
}
