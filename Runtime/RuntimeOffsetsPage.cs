using System.Globalization;
using System.Text;
using System.Text.Json;
using MemoryEditor.UI;
using MemoryEditor.Xex;

namespace MemoryEditor.Runtime;

/// <summary>Game detection + automatic offset discovery for whatever title is running.</summary>
public sealed class RuntimeOffsetsPage : UserControl
{
    private readonly IAppHost _host;
    private readonly List<DiscoveredOffset> _all = new();
    private List<DiscoveredOffset> _view = new();
    private ModuleImage? _module;
    private PpcAnalyzer? _analysis;
    private GameInfo? _game;
    private OffsetProfile? _profile;
    private CancellationTokenSource? _cts;
    private int _sortColumn = -1;
    private bool _sortDesc;

    // header
    private readonly Label _title = Ui.Title("No game detected");
    private readonly Label _subtitle = Ui.Muted("Connect to a console and press Detect, or analyse an XEX / EXE file offline.");
    private readonly Label _gameGlyph = Ui.Glyph(Theme.Glyph.Game, 26F, Theme.Accent);
    private readonly FlowLayoutPanel _chips = new() { Dock = DockStyle.Fill, WrapContents = true, BackColor = Theme.Surface, Padding = new Padding(0, 4, 0, 0) };
    private readonly Ui.ThinProgress _progress = new() { Dock = DockStyle.Bottom };
    private readonly Label _status = Ui.Muted("Idle");
    private readonly IconButton _btnDetect, _btnDiscover, _btnFile, _btnSigApply, _btnSigGen, _btnExport, _btnPush, _btnCancel;
    private readonly CheckBox _chkAuto = new() { Text = "Auto-discover on connect", Checked = true, AutoSize = true };

    // grid
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        AllowUserToResizeRows = false
    };
    private readonly TextBox _filter = new() { Width = 320, PlaceholderText = "Filter by name, address, notes…" };
    private readonly ComboBox _category = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly CheckBox _hideStrings = new() { Text = "Hide string references", AutoSize = true, Checked = true };
    private readonly Label _count = Ui.Muted("");

    // details
    private readonly Label _dName = new() { AutoSize = false, Height = 44, Dock = DockStyle.Top, Font = Theme.Heading, ForeColor = Theme.Text, Tag = "keep" };
    private readonly Label _dMeta = new() { AutoSize = false, Height = 64, Dock = DockStyle.Top, Font = Theme.UI, ForeColor = Theme.TextMuted, Tag = "keep" };
    private readonly RichTextBox _dPeek = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None };
    private readonly System.Windows.Forms.Timer _peekTimer = new() { Interval = 1000 };

    public IReadOnlyList<DiscoveredOffset> Offsets => _all;
    public GameInfo? Game => _game;
    public bool AutoDiscover { get => _chkAuto.Checked; set => _chkAuto.Checked = value; }

    public RuntimeOffsetsPage(IAppHost host)
    {
        _host = host;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        Padding = new Padding(18, 16, 18, 16);

        _btnDetect = IconButton.Create("Detect game", Theme.Glyph.Fingerprint, ButtonVariant.Neutral, async (_, _) => await DetectAsync(true));
        _btnDiscover = IconButton.Create("Discover offsets", Theme.Glyph.Wand, ButtonVariant.Primary, async (_, _) => await DiscoverLiveAsync());
        _btnFile = IconButton.Create("From XEX / EXE…", Theme.Glyph.Open, ButtonVariant.Neutral, async (_, _) => await DiscoverFromFileDialogAsync());
        _btnSigApply = IconButton.Create("Apply signatures", Theme.Glyph.Signature, ButtonVariant.Neutral, async (_, _) => await ApplySignaturesAsync(true));
        _btnSigGen = IconButton.Create("Generate signatures", Theme.Glyph.Add, ButtonVariant.Neutral, async (_, _) => await GenerateSignaturesAsync());
        _btnExport = IconButton.Create("Export", Theme.Glyph.Download, ButtonVariant.Neutral, (s, _) => ShowExportMenu((Control)s!));
        _btnPush = IconButton.Create("Use in Offset DB", Theme.Glyph.Upload, ButtonVariant.Success, (_, _) => PushToDatabase());
        _btnCancel = IconButton.Create("Cancel", Theme.Glyph.Stop, ButtonVariant.Danger, (_, _) => _cts?.Cancel());
        _btnCancel.Visible = false;

        // ── header card ──
        var header = Ui.Card(DockStyle.Top, 168, new Padding(18, 14, 18, 0));
        var titleRow = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Theme.Surface, Tag = "keep" };
        _gameGlyph.Location = new Point(0, 8);
        _title.Location = new Point(48, 2);
        _subtitle.Location = new Point(50, 30);
        titleRow.Controls.AddRange(new Control[] { _gameGlyph, _title, _subtitle });
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(0, 2, 0, 0) };
        actions.Controls.AddRange(new Control[] { _btnDetect, _btnDiscover, _btnFile, _btnSigApply, _btnSigGen, _btnExport, _btnPush, _btnCancel });
        var statusRow = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Theme.Surface, Tag = "keep" };
        _status.Location = new Point(0, 6);
        _chkAuto.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        statusRow.Controls.Add(_status);
        statusRow.Controls.Add(_chkAuto);
        statusRow.Resize += (_, _) => _chkAuto.Location = new Point(statusRow.Width - _chkAuto.Width - 4, 4);
        header.Controls.Add(_chips);
        header.Controls.Add(statusRow);
        header.Controls.Add(actions);
        header.Controls.Add(titleRow);
        header.Controls.Add(_progress);

        // ── filter row ──
        var filterRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 10, 0, 0), WrapContents = false, BackColor = Theme.Bg };
        _category.Margin = new Padding(10, 0, 0, 0);
        _hideStrings.Margin = new Padding(14, 4, 0, 0);
        _count.Margin = new Padding(16, 5, 0, 0);
        filterRow.Controls.AddRange(new Control[] { _filter, _category, _hideStrings, _count });
        _filter.TextChanged += (_, _) => ApplyFilter();
        _category.SelectedIndexChanged += (_, _) => ApplyFilter();
        _hideStrings.CheckedChanged += (_, _) => ApplyFilter();

        // ── grid ──
        foreach (var (name, w) in new[] { ("Name", 300), ("Category", 190), ("Address", 110), ("Source", 90), ("Confidence", 90), ("Notes", 600) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Width = w, SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.Columns[2].DefaultCellStyle.Font = Theme.Mono;
        _grid.Columns[2].DefaultCellStyle.ForeColor = Color.FromArgb(0xB5, 0xCE, 0xA8);
        _grid.Columns[5].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.CellValueNeeded += Grid_CellValueNeeded;
        _grid.CellFormatting += Grid_CellFormatting;
        _grid.ColumnHeaderMouseClick += (_, e) => SortBy(e.ColumnIndex);
        _grid.SelectionChanged += (_, _) => ShowDetails();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) _host.ViewMemory(_view[e.RowIndex].Address); };
        _grid.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C) { CopySelected(); e.Handled = true; }
        };
        _grid.ContextMenuStrip = BuildGridMenu();

        // ── details ──
        var details = Ui.Card(DockStyle.Right, 0, new Padding(14));
        details.Width = 330;
        var detailButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 84, BackColor = Theme.Surface, Padding = new Padding(0, 6, 0, 0) };
        detailButtons.Controls.AddRange(new Control[]
        {
            IconButton.Create("Memory", Theme.Glyph.Memory, ButtonVariant.Neutral, (_, _) => { if (Current is { } c) _host.ViewMemory(c.Address); }),
            IconButton.Create("Watch", Theme.Glyph.Pin, ButtonVariant.Neutral, (_, _) => { if (Current is { } c) _host.AddWatch(c.Address, c.Name); }),
            IconButton.Create("Script", Theme.Glyph.Code, ButtonVariant.Neutral, (_, _) => InsertSelectedIntoScript()),
            IconButton.Create("Copy", Theme.Glyph.Copy, ButtonVariant.Neutral, (_, _) => CopySelected()),
            IconButton.Create("Signature", Theme.Glyph.Signature, ButtonVariant.Neutral, async (_, _) => await GenerateSignaturesAsync(selectedOnly: true)),
        });
        _dPeek.Font = Theme.MonoSmall;
        details.Controls.Add(_dPeek);
        details.Controls.Add(new Label { Dock = DockStyle.Top, Height = 24, Text = "LIVE VALUE", Font = new Font("Segoe UI Semibold", 8.25F), ForeColor = Theme.TextDim, Tag = "keep" });
        details.Controls.Add(_dMeta);
        details.Controls.Add(_dName);
        details.Controls.Add(detailButtons);
        _peekTimer.Tick += async (_, _) => await PeekAsync();

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Tag = "keep" };
        var gridHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 0, 12, 0), Tag = "keep" };
        gridHost.Controls.Add(_grid);
        body.Controls.Add(gridHost);
        body.Controls.Add(details);

        Controls.Add(body);
        Controls.Add(filterRow);
        Controls.Add(Ui.Spacer(DockStyle.Top, 4));
        Controls.Add(header);

        Theme.Apply(this);
        Theme.StyleGrid(_grid);
        _grid.Columns[2].DefaultCellStyle.Font = Theme.Mono;
        UpdateCategories();
        UpdateButtons();
        ShowDetails();
    }

    private DiscoveredOffset? Current => _grid.CurrentRow is { Index: >= 0 } r && r.Index < _view.Count ? _view[r.Index] : null;

    private IEnumerable<DiscoveredOffset> SelectedOffsets =>
        _grid.SelectedRows.Cast<DataGridViewRow>().Where(r => r.Index < _view.Count).OrderBy(r => r.Index).Select(r => _view[r.Index]);

    // ───────────────────────────── workflow ─────────────────────────────

    /// <summary>Called by the main window after a successful connection.</summary>
    public async Task OnConnectedAsync()
    {
        await DetectAsync(false);
        if (_game != null && _all.Count == 0 && AutoDiscover) await DiscoverLiveAsync();
    }

    /// <summary>Shows a title identified elsewhere (Game Library) and loads its cached offsets, without dumping.</summary>
    public void SetDetected(GameInfo g)
    {
        if (InvokeRequired) { BeginInvoke(() => SetDetected(g)); return; }
        bool changed = _game == null || _game.CacheKey != g.CacheKey;
        _game = g;
        ShowGameHeader(g);
        _profile = ProfileStore.LoadAll().FirstOrDefault(p => p.Matches(g.ModuleName, g.TitleId));
        if (changed)
        {
            _module = null;
            _analysis = null;
            SetResults(ProfileStore.LoadCache(g.CacheKey) ?? new List<DiscoveredOffset>());
        }
    }

    /// <summary>Shows the result of a pull performed by the Game Library (same module / analysis objects).</summary>
    public void ShowPulled(GameInfo g, ModuleImage module, PpcAnalyzer analysis, List<DiscoveredOffset> results)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowPulled(g, module, analysis, results)); return; }
        _game = g;
        _module = module;
        _analysis = analysis;
        _profile = ProfileStore.LoadAll().FirstOrDefault(p => p.Matches(g.ModuleName, g.TitleId));
        ShowGameHeader(g);
        SetResults(results);
        try { ProfileStore.SaveCache(g.CacheKey, results); } catch { }
    }

    private void ShowGameHeader(GameInfo g)
    {
        _title.Text = g.DisplayName.Length > 0 ? g.DisplayName : g.ModuleName;
        _subtitle.Text = $"{g.ModuleName}  ·  base 0x{g.Base:X8}  ·  size 0x{g.Size:X}" +
                         (g.TitleId != 0 ? $"  ·  title {g.TitleId:X8}" : "") +
                         (g.Version.Length > 0 ? $"  ·  v{g.Version}" : "") +
                         (g.Timestamp != 0 ? $"  ·  built {DateTimeOffset.FromUnixTimeSeconds(g.Timestamp):yyyy-MM-dd}" : "");
    }

    public void OnDisconnected()
    {
        _peekTimer.Stop();
        UpdateButtons();
    }

    private async Task DetectAsync(bool userInitiated)
    {
        var c = _host.Client;
        if (c == null || !c.IsConnected)
        {
            if (userInitiated) _host.Toast("Connect to a console first (or use “From XEX / EXE…”)", ToastHost.Kind.Warning);
            return;
        }
        SetBusy(true, "Detecting running title…", indeterminate: true);
        try
        {
            var g = await GameDetector.DetectAsync(c, _host.Log);
            if (g == null) { _host.Toast("Could not detect the running title", ToastHost.Kind.Warning); return; }
            _game = g;
            _title.Text = g.DisplayName;
            _subtitle.Text = $"{g.ModuleName}  ·  base 0x{g.Base:X8}  ·  size 0x{g.Size:X}" +
                             (g.TitleId != 0 ? $"  ·  title {g.TitleId:X8}" : "") +
                             (g.Timestamp != 0 ? $"  ·  built {DateTimeOffset.FromUnixTimeSeconds(g.Timestamp):yyyy-MM-dd}" : "");
            _host.Log($"🎮 Detected {g.DisplayName} ({g.ModuleName} @ 0x{g.Base:X8}, {g.AllModules.Count} modules loaded)");

            _profile = ProfileStore.LoadAll().FirstOrDefault(p => p.Matches(g.ModuleName, g.TitleId));
            var cached = ProfileStore.LoadCache(g.CacheKey);
            if (cached != null && cached.Count > 0)
            {
                SetResults(cached);
                _host.Toast($"{g.DisplayName}: loaded {cached.Count:N0} cached offsets", ToastHost.Kind.Success);
                if (_profile != null) _host.Log($"📑 Signature profile available: {Path.GetFileName(_profile.FilePath)} ({_profile.Signatures.Count} signatures) – dump & apply with Discover");
            }
            else if (userInitiated || !AutoDiscover)
            {
                _host.Toast($"Detected {g.DisplayName}. Press “Discover offsets” to map it.", ToastHost.Kind.Info);
            }
        }
        catch (Exception ex)
        {
            _host.Toast($"Detection failed: {ex.Message}", ToastHost.Kind.Error);
        }
        finally { SetBusy(false, _all.Count > 0 ? $"{_all.Count:N0} offsets" : "Idle"); }
    }

    private async Task DiscoverLiveAsync()
    {
        var c = _host.Client;
        if (c == null || !c.IsConnected) { _host.Toast("Connect to a console first, or analyse a file offline", ToastHost.Kind.Warning); return; }
        if (_game == null) await DetectAsync(true);
        if (_game == null) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, "Reading module from console…");
        try
        {
            var g = _game;
            var module = await GameDetector.DumpModuleAsync(c, g, (s, p) => Report(s, p * 0.6), ct);
            await AnalyzeModuleAsync(module, ct, 0.6);
            ProfileStore.SaveCache(g.CacheKey, _all);
            _host.Log($"💾 Cached runtime offsets → Games\\cache\\{g.CacheKey}.json");
        }
        catch (OperationCanceledException) { _host.Toast("Discovery cancelled", ToastHost.Kind.Warning); }
        catch (Exception ex) { _host.Toast($"Discovery failed: {ex.Message}", ToastHost.Kind.Error); _host.Log($"❌ Discovery failed: {ex}"); }
        finally { SetBusy(false, $"{_all.Count:N0} offsets"); }
    }

    private async Task DiscoverFromFileDialogAsync()
    {
        using var ofd = new OpenFileDialog { Filter = "Xbox 360 executables (*.xex;*.exe)|*.xex;*.exe|All files|*.*" };
        if (ofd.ShowDialog(FindForm()) != DialogResult.OK) return;
        await DiscoverFromFileAsync(ofd.FileName);
    }

    public async Task DiscoverFromFileAsync(string path)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true, $"Loading {Path.GetFileName(path)}…", indeterminate: true);
        try
        {
            var module = await Task.Run(() => LoadModuleFromFile(path));
            await DiscoverFromModuleAsync(module);
        }
        catch (Exception ex) { _host.Toast($"Could not analyse {Path.GetFileName(path)}: {ex.Message}", ToastHost.Kind.Error); SetBusy(false, "Idle"); }
    }

    public static ModuleImage LoadModuleFromFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (XexFile.IsXex(bytes))
        {
            var xex = XexFile.Parse(bytes);
            var image = xex.ExtractBasefile();
            return ModuleImage.FromPe(Path.GetFileName(path), image, xex.LoadAddress, xex);
        }
        var pe = PeImage.TryParse(bytes) ?? throw new InvalidDataException("Not an XEX or PE file");
        // raw-pointer layout (normal PE) → rebuild memory image
        byte[] img = bytes;
        if (pe.Sections.Any(s => s.RawOffset != s.Rva && s.RawSize > 0))
        {
            img = new byte[Math.Max(pe.SizeOfImage, (uint)bytes.Length)];
            Buffer.BlockCopy(bytes, 0, img, 0, Math.Min(0x1000, bytes.Length));
            foreach (var s in pe.Sections)
                if (s.RawSize > 0 && s.RawOffset + s.RawSize <= bytes.Length && s.Rva + s.RawSize <= img.Length)
                    Buffer.BlockCopy(bytes, (int)s.RawOffset, img, (int)s.Rva, (int)Math.Min(s.RawSize, s.VirtualSize == 0 ? s.RawSize : s.VirtualSize));
        }
        return ModuleImage.FromPe(Path.GetFileName(path), img, pe.ImageBase);
    }

    public async Task DiscoverFromModuleAsync(ModuleImage module)
    {
        _cts ??= new CancellationTokenSource();
        _game = new GameInfo
        {
            ModuleName = module.Name, Base = module.BaseAddress, Size = (uint)module.Data.Length,
            TitleId = module.Xex?.TitleId ?? 0, Timestamp = module.Xex?.Timestamp ?? module.Timestamp
        };
        _game.DisplayName = _game.TitleId != 0 && GameDetector.KnownTitles.TryGetValue(_game.TitleId, out var n) ? n : module.Name;
        _title.Text = _game.DisplayName + "  (offline)";
        _subtitle.Text = $"{module.Name}  ·  base 0x{module.BaseAddress:X8}  ·  size 0x{module.Data.Length:X}" +
                         (_game.TitleId != 0 ? $"  ·  title {_game.TitleId:X8}" : "") + (module.Xex != null ? $"  ·  v{module.Xex.Version}" : "");
        _profile = ProfileStore.LoadAll().FirstOrDefault(p => p.Matches(module.Name, _game.TitleId));
        SetBusy(true, "Analysing…");
        try
        {
            await AnalyzeModuleAsync(module, _cts.Token, 0);
            ProfileStore.SaveCache(_game.CacheKey, _all);
        }
        catch (OperationCanceledException) { _host.Toast("Analysis cancelled", ToastHost.Kind.Warning); }
        finally { SetBusy(false, $"{_all.Count:N0} offsets"); }
    }

    private async Task AnalyzeModuleAsync(ModuleImage module, CancellationToken ct, double progressBase)
    {
        _module = module;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var analyzer = new PpcAnalyzer(module, (s, p) => Report(s, progressBase + p * (1 - progressBase)), ct);
        var results = await Task.Run(() => analyzer.Run(), ct);
        _analysis = analyzer;

        // static database entries that fall inside this module become verified entries
        foreach (var (name, addr, cat) in _host.StaticOffsets)
        {
            if (!module.Contains(addr)) continue;
            var kind = module.IsCode(addr) ? (analyzer.FunctionStarts.Contains(addr) ? "function start" : "code") : "data";
            results.Add(new DiscoveredOffset { Name = name, Category = $"Offset DB · {cat}", Address = addr, Source = "Static", Confidence = "High", Type = kind, Notes = $"From offset database ({kind}, {module.SectionOf(addr)?.Name ?? "?"})" });
        }
        SetResults(results);
        await ApplySignaturesAsync(false);
        _host.Log($"🧠 Analysed {module.Name}: {analyzer.FunctionStarts.Count:N0} functions, {analyzer.Strings.Count:N0} strings, {_all.Count:N0} offsets in {sw.Elapsed.TotalSeconds:0.0}s");
        _host.Toast($"{_all.Count(o => o.Category != "String References"):N0} offsets discovered for {_game?.DisplayName ?? module.Name}", ToastHost.Kind.Success);
    }

    private async Task ApplySignaturesAsync(bool userInitiated)
    {
        if (_module == null)
        {
            if (userInitiated) _host.Toast("Discover first – signatures are matched against the module image", ToastHost.Kind.Warning);
            return;
        }
        var profiles = ProfileStore.LoadAll().Where(p => _game == null || p.Matches(_game.ModuleName, _game.TitleId)).ToList();
        if (profiles.Count == 0)
        {
            if (userInitiated) _host.Toast("No signature profile for this game yet – use “Generate signatures”", ToastHost.Kind.Info);
            return;
        }
        var m = _module;
        var found = await Task.Run(() =>
        {
            var list = new List<DiscoveredOffset>();
            foreach (var p in profiles)
                foreach (var s in p.Signatures)
                {
                    var (addr, info) = SignatureEngine.Apply(m, s);
                    list.Add(new DiscoveredOffset
                    {
                        Name = s.Name, Category = "Signatures · " + s.Category, Address = addr ?? 0, Source = "Signature",
                        Confidence = addr == null ? "Failed" : info == "unique match" ? "High" : "Medium",
                        Type = "Signature", Notes = addr == null ? $"✕ {info} ({Path.GetFileName(p.FilePath)})" : $"{info} · {Path.GetFileName(p.FilePath)}"
                    });
                }
            return list;
        });
        _all.RemoveAll(o => o.Source == "Signature");
        _all.AddRange(found);
        UpdateCategories();
        ApplyFilter();
        _host.RuntimeOffsetsChanged();
        int ok = found.Count(f => f.Confidence != "Failed");
        _host.Log($"🔎 Signatures: {ok}/{found.Count} resolved");
        if (userInitiated) _host.Toast($"Signatures: {ok} of {found.Count} resolved", ok == found.Count ? ToastHost.Kind.Success : ToastHost.Kind.Warning);
    }

    private async Task GenerateSignaturesAsync(bool selectedOnly = false)
    {
        if (_module == null || _analysis == null)
        {
            _host.Toast("Discover offsets first (live or from a file) – signatures are built from the module image", ToastHost.Kind.Warning);
            return;
        }
        List<(string Name, uint Address, string Category)> targets;
        if (selectedOnly)
            targets = SelectedOffsets.Where(o => o.Type != "String").Select(o => (o.Name, o.Address, o.Category)).ToList();
        else
        {
            var choice = MessageBox.Show(FindForm(),
                "Create relocation-tolerant signatures so these offsets are found again after a title update.\n\n" +
                "Yes  = every offset database entry inside this module\nNo   = the rows selected in the grid",
                "Generate signatures", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (choice == DialogResult.Cancel) return;
            targets = choice == DialogResult.Yes
                ? _host.StaticOffsets.Where(o => _module.Contains(o.Address)).ToList()
                : SelectedOffsets.Where(o => o.Type != "String").Select(o => (o.Name, o.Address, o.Category)).ToList();
        }
        if (targets.Count == 0) { _host.Toast("Nothing to sign – select rows or load an offset database for this game", ToastHost.Kind.Info); return; }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, "Generating signatures…");
        var m = _module;
        var an = _analysis;
        try
        {
            var (sigs, failed) = await Task.Run(() =>
            {
                var list = new List<SignatureDef>();
                int fail = 0, i = 0;
                foreach (var t in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    Report($"Signing {t.Name}…", (double)i++ / targets.Count);
                    var s = SignatureEngine.Generate(m, an, t.Name, t.Address, t.Category.Replace("Offset DB · ", ""));
                    if (s != null) list.Add(s); else fail++;
                }
                return (list, fail);
            }, ct);

            var profile = _profile ?? new OffsetProfile { Game = _game?.DisplayName ?? m.Name, Module = _game?.ModuleName ?? m.Name, TitleId = _game?.TitleId is > 0 ? _game.TitleId.ToString("X8") : "" };
            foreach (var s in sigs)
            {
                profile.Signatures.RemoveAll(x => x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
                profile.Signatures.Add(s);
            }
            ProfileStore.Save(profile);
            _profile = profile;
            _host.Log($"✍️ Saved {sigs.Count} signatures to {profile.FilePath} ({failed} could not be made unique)");
            _host.Toast($"{sigs.Count} signatures saved{(failed > 0 ? $", {failed} failed" : "")}", failed == 0 ? ToastHost.Kind.Success : ToastHost.Kind.Warning);
            await ApplySignaturesAsync(false);
        }
        catch (OperationCanceledException) { _host.Toast("Cancelled", ToastHost.Kind.Warning); }
        finally { SetBusy(false, $"{_all.Count:N0} offsets"); }
    }

    // ───────────────────────────── results / grid ─────────────────────────────

    private void SetResults(List<DiscoveredOffset> results)
    {
        _all.Clear();
        _all.AddRange(results);
        UpdateCategories();
        ApplyFilter();
        UpdateChips();
        UpdateButtons();
        _host.RuntimeOffsetsChanged();
    }

    private void UpdateCategories()
    {
        var current = _category.SelectedItem as string;
        _category.BeginUpdate();
        _category.Items.Clear();
        _category.Items.Add("All categories");
        foreach (var c in _all.GroupBy(o => o.Category).OrderBy(g => g.Key)) _category.Items.Add(c.Key);
        _category.EndUpdate();
        _category.SelectedItem = current != null && _category.Items.Contains(current) ? current : "All categories";
        UpdateChips();
    }

    private void UpdateChips()
    {
        _chips.SuspendLayout();
        foreach (Control c in _chips.Controls) c.Dispose();
        _chips.Controls.Clear();
        void Chip(string text, Color color)
        {
            var p = new StatusPill { DotColor = color, Margin = new Padding(0, 0, 8, 4) };
            p.Text = text;
            _chips.Controls.Add(p);
        }
        if (_all.Count == 0)
        {
            Chip("No offsets yet", Theme.TextDim);
        }
        else
        {
            int fns = _analysis?.FunctionStarts.Count ?? 0;
            if (fns > 0) Chip($"{fns:N0} functions", Theme.Accent);
            Chip($"{_all.Count(o => o.Category.StartsWith("Named Functions")):N0} named functions", Theme.Success);
            Chip($"{_all.Count(o => o.Category == "Named Globals"):N0} named globals", Theme.Teal);
            Chip($"{_all.Count(o => o.Category == "Key Functions"):N0} key functions", Theme.Purple);
            Chip($"{_all.Count(o => o.Category == "Imports"):N0} imports", Theme.Warning);
            Chip($"{_all.Count(o => o.Category == "String References"):N0} strings", Theme.TextMuted);
            var sig = _all.Count(o => o.Source == "Signature");
            if (sig > 0) Chip($"{_all.Count(o => o.Source == "Signature" && o.Confidence != "Failed")}/{sig} signatures", Theme.Success);
        }
        _chips.ResumeLayout();
    }

    private void ApplyFilter()
    {
        var q = _filter.Text.Trim();
        var cat = _category.SelectedIndex > 0 ? _category.SelectedItem as string : null;
        uint? qAddr = null;
        if (q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(q[2..], NumberStyles.HexNumber, null, out var a)) qAddr = a;
        IEnumerable<DiscoveredOffset> v = _all;
        if (cat != null) v = v.Where(o => o.Category == cat);
        else if (_hideStrings.Checked) v = v.Where(o => o.Category != "String References");
        if (q.Length > 0)
            v = v.Where(o => o.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || o.Notes.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                             (qAddr.HasValue ? o.Address == qAddr || o.Address.ToString("X8").Contains(q[2..], StringComparison.OrdinalIgnoreCase) : o.Address.ToString("X8").Contains(q, StringComparison.OrdinalIgnoreCase)));
        _view = v.ToList();
        if (_sortColumn >= 0) SortView();
        _grid.RowCount = 0;
        _grid.RowCount = _view.Count;
        _grid.Invalidate();
        _count.Text = $"{_view.Count:N0} of {_all.Count:N0}";
    }

    private void SortBy(int col)
    {
        if (_sortColumn == col) _sortDesc = !_sortDesc; else { _sortColumn = col; _sortDesc = false; }
        foreach (DataGridViewColumn c in _grid.Columns) c.HeaderCell.SortGlyphDirection = SortOrder.None;
        _grid.Columns[col].HeaderCell.SortGlyphDirection = _sortDesc ? SortOrder.Descending : SortOrder.Ascending;
        SortView();
        _grid.Invalidate();
    }

    private void SortView()
    {
        Func<DiscoveredOffset, IComparable> key = _sortColumn switch
        {
            0 => o => o.Name, 1 => o => o.Category, 2 => o => o.Address, 3 => o => o.Source, 4 => o => o.Confidence, _ => o => o.Notes
        };
        _view = (_sortDesc ? _view.OrderByDescending(key) : _view.OrderBy(key)).ToList();
    }

    private void Grid_CellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex >= _view.Count) return;
        var o = _view[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => o.Name, 1 => o.Category, 2 => $"0x{o.Address:X8}", 3 => o.Source, 4 => o.Confidence, _ => o.Notes
        };
    }

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex >= _view.Count || e.CellStyle == null) return;
        if (e.ColumnIndex == 4)
        {
            e.CellStyle.ForeColor = _view[e.RowIndex].Confidence switch
            {
                "High" => Theme.Success, "Medium" => Theme.Warning, "Low" => Theme.TextMuted, "Failed" => Theme.Danger, _ => Theme.Text
            };
        }
        else if (e.ColumnIndex == 5) e.CellStyle.ForeColor = Theme.TextMuted;
        else if (e.ColumnIndex == 1) e.CellStyle.ForeColor = Theme.TextMuted;
    }

    private void ShowDetails()
    {
        var c = Current;
        if (c == null)
        {
            _dName.Text = "Select an offset";
            _dMeta.Text = "Double-click a row to open it in the Memory Editor. Right-click for more actions.";
            _dPeek.Text = "";
            _peekTimer.Stop();
            return;
        }
        _dName.Text = c.Name;
        _dMeta.Text = $"0x{c.Address:X8}  ·  {c.Type}  ·  {c.Confidence}\n{c.Category}\n{c.Notes}";
        _ = PeekAsync();
        if (_host.Client?.IsConnected == true) _peekTimer.Start();
    }

    private async Task PeekAsync()
    {
        var c = Current;
        if (c == null) return;
        byte[]? data = null;
        bool live = false;
        var client = _host.Client;
        if (client?.IsConnected == true)
        {
            data = await client.GetMemoryAsync(c.Address, 32);
            live = data != null;
        }
        else _peekTimer.Stop();
        if (data == null && _module != null && _module.Contains(c.Address) && _module.Contains(c.Address + 31))
            data = _module.Data.AsSpan((int)(c.Address - _module.BaseAddress), 32).ToArray();
        if (Current != c) return;
        if (data == null) { _dPeek.Text = "No data (connect to read live memory)"; return; }

        var sb = new StringBuilder();
        sb.AppendLine(live ? "● live from console" : "○ from module image");
        sb.AppendLine();
        for (int i = 0; i < data.Length; i += 8)
            sb.AppendLine($"{c.Address + i:X8}  {BitConverter.ToString(data, i, Math.Min(8, data.Length - i)).Replace('-', ' ')}");
        if (data.Length >= 4)
        {
            uint u = (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);
            sb.AppendLine();
            sb.AppendLine($"uint32  0x{u:X8}  {u}");
            sb.AppendLine($"int32   {(int)u}");
            sb.AppendLine($"float   {BitConverter.Int32BitsToSingle((int)u).ToString("G6", CultureInfo.InvariantCulture)}");
            if (_module?.IsCode(c.Address) == true) sb.AppendLine($"insn    {Ppc.Disassemble(u, c.Address)}");
            else if (_module?.Contains(u) == true) sb.AppendLine($"→ points into {_module.SectionOf(u)?.Name ?? "module"}");
        }
        _dPeek.Text = sb.ToString();
    }

    // ───────────────────────────── actions ─────────────────────────────

    private ContextMenuStrip BuildGridMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("View in Memory Editor", null, (_, _) => { if (Current is { } c) _host.ViewMemory(c.Address); });
        m.Items.Add("Add to watch list", null, (_, _) => { foreach (var o in SelectedOffsets) _host.AddWatch(o.Address, o.Name); });
        m.Items.Add("Insert into script", null, (_, _) => InsertSelectedIntoScript());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Copy address", null, (_, _) => { if (Current is { } c) Clipboard.SetText($"0x{c.Address:X8}"); });
        m.Items.Add("Copy rows", null, (_, _) => CopySelected());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Generate signature for selection", null, async (_, _) => await GenerateSignaturesAsync(selectedOnly: true));
        m.Items.Add("Use selection in Offset DB", null, (_, _) => _host.PushOffsets(SelectedOffsets.ToList()));
        Scripting.ScriptIde.StyleMenu(m);
        return m;
    }

    private void CopySelected()
    {
        var rows = SelectedOffsets.ToList();
        if (rows.Count == 0) return;
        Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(o => $"{o.Name}\t0x{o.Address:X8}\t{o.Category}\t{o.Notes}")));
        _host.Toast($"Copied {rows.Count} row(s)", ToastHost.Kind.Info);
    }

    private void InsertSelectedIntoScript()
    {
        var rows = SelectedOffsets.ToList();
        if (rows.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var o in rows) sb.AppendLine($"var {Scripting.ScriptIde.NormalizeName(o.Name).Trim('_')} = 0x{o.Address:X8}   // {o.Category}");
        _host.InsertScript(sb.ToString());
        _host.Navigate("script");
    }

    private void PushToDatabase()
    {
        var rows = _view.Where(o => o.Category != "String References" && o.Category != "Module" && o.Confidence != "Failed").ToList();
        if (rows.Count == 0) { _host.Toast("Nothing to push – discover offsets first", ToastHost.Kind.Info); return; }
        _host.PushOffsets(rows);
    }

    private void ShowExportMenu(Control anchor)
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Offset file (.txt) – loads in Offset Calculator", null, (_, _) => Export("txt"));
        m.Items.Add("JSON (.json)", null, (_, _) => Export("json"));
        m.Items.Add("C / C++ header (.h)", null, (_, _) => Export("h"));
        m.Items.Add("XScript constants (.xs)", null, (_, _) => Export("xs"));
        m.Items.Add("CSV (.csv)", null, (_, _) => Export("csv"));
        Scripting.ScriptIde.StyleMenu(m);
        m.Show(anchor, new Point(0, anchor.Height));
    }

    private void Export(string kind)
    {
        var rows = _view.Where(o => o.Confidence != "Failed").ToList();
        if (rows.Count == 0) { _host.Toast("Nothing to export", ToastHost.Kind.Info); return; }
        var baseName = string.Concat((_game?.DisplayName ?? "offsets").Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
        using var sfd = new SaveFileDialog { FileName = $"{baseName}_runtime.{kind}", Filter = $"{kind.ToUpperInvariant()} file|*.{kind}|All files|*.*" };
        if (sfd.ShowDialog(FindForm()) != DialogResult.OK) return;
        var sb = new StringBuilder();
        string Ident(string n) { var s = Scripting.ScriptIde.NormalizeName(n).Trim('_'); return s.Length == 0 || char.IsDigit(s[0]) ? "_" + s : s; }
        switch (kind)
        {
            case "txt":
                sb.AppendLine($"// {_game?.DisplayName} – runtime offsets exported {DateTime.Now:yyyy-MM-dd HH:mm}");
                foreach (var g in rows.GroupBy(r => r.Category))
                {
                    sb.AppendLine();
                    sb.AppendLine($"~~{g.Key}~~");
                    foreach (var o in g) sb.AppendLine($"{o.Name.Replace(":", " ").Replace("=", " ")}: 0x{o.Address:X8}");
                }
                break;
            case "json":
                sb.Append(JsonSerializer.Serialize(new { game = _game?.DisplayName, module = _game?.ModuleName, titleId = _game?.TitleId.ToString("X8"), offsets = rows }, new JsonSerializerOptions { WriteIndented = true }));
                break;
            case "h":
                sb.AppendLine("#pragma once");
                sb.AppendLine($"// {_game?.DisplayName} ({_game?.ModuleName}) – generated by Xbox 360 Memory Editor");
                foreach (var g in rows.GroupBy(r => r.Category))
                {
                    sb.AppendLine();
                    sb.AppendLine($"// ── {g.Key} ──");
                    foreach (var o in g.DistinctBy(o => Ident(o.Name))) sb.AppendLine($"#define {Ident(o.Name).ToUpperInvariant(),-48} 0x{o.Address:X8}");
                }
                break;
            case "xs":
                sb.AppendLine($"// {_game?.DisplayName} runtime offsets");
                foreach (var o in rows.DistinctBy(o => Ident(o.Name))) sb.AppendLine($"var {Ident(o.Name)} = 0x{o.Address:X8}");
                break;
            case "csv":
                sb.AppendLine("Name,Category,Address,Source,Confidence,Notes");
                foreach (var o in rows) sb.AppendLine($"\"{o.Name.Replace("\"", "\"\"")}\",\"{o.Category}\",0x{o.Address:X8},{o.Source},{o.Confidence},\"{o.Notes.Replace("\"", "\"\"")}\"");
                break;
        }
        File.WriteAllText(sfd.FileName, sb.ToString());
        _host.Toast($"Exported {rows.Count:N0} offsets to {Path.GetFileName(sfd.FileName)}", ToastHost.Kind.Success);
    }

    // ───────────────────────────── busy state ─────────────────────────────

    private void Report(string status, double p)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            _status.Text = status;
            _progress.Indeterminate = false;
            _progress.Value = p;
        });
    }

    private void SetBusy(bool busy, string status, bool indeterminate = false)
    {
        _status.Text = status;
        _progress.Indeterminate = busy && indeterminate;
        if (!busy) _progress.Value = 0;
        _btnCancel.Visible = busy && !indeterminate;
        foreach (var b in new[] { _btnDetect, _btnDiscover, _btnFile, _btnSigApply, _btnSigGen, _btnPush, _btnExport }) b.Enabled = !busy;
        if (!busy) UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool connected = _host.Client?.IsConnected == true;
        _btnDetect.Enabled = connected;
        _btnDiscover.Enabled = connected;
        _btnSigApply.Enabled = _module != null;
        _btnSigGen.Enabled = _module != null;
        _btnExport.Enabled = _all.Count > 0;
        _btnPush.Enabled = _all.Count > 0;
    }

    public void OnConnectionChanged() => UpdateButtons();
}
