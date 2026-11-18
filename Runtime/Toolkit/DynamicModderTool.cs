using System.Buffers.Binary;
using System.Globalization;
using MemoryEditor.Runtime.Debug;
using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>
/// Dynamic Modder – finds variables by behaviour instead of by name or pattern:
///   1. Delta Tracker: baseline snapshot, then idle passes (drop what changes on its own) and action passes
///      (keep what changed when you acted).
///   2. Heuristics: rank survivors by float plausibility, XYZ / velocity clustering and consistency.
///   3. Register interception: a data watchpoint reveals the instruction that writes the value, its object base
///      register and field offset.
///   4. Generated controls: slider / value / freeze (Method A) and "block writer" NOP with restore (Method B).
/// Works on whatever game is running – no per-game scripts.
/// </summary>
public sealed class DynamicModderTool : ToolBase
{
    private sealed class Candidate
    {
        public uint Address;
        public uint Raw;
        public DeltaHeuristics.Guess Guess = new("", 0, "", LiveType.Int32);
        public int Hits;
        public string Region = "";
    }

    private readonly DeltaEngine _engine = new();
    private readonly ComboBox _scope = C(230, "Game memory (heap + globals)", "Title heap only", "Game globals (.data / .bss)", "Custom range");
    private readonly TextBox _start = T("0x40000000", 110);
    private readonly TextBox _size = T("0x01000000", 110);
    private readonly NumericUpDown _maxMb = new() { Minimum = 8, Maximum = 512, Value = 64, Increment = 16, Width = 70, Margin = new Padding(0, 4, 4, 0) };
    private readonly ComboBox _valueType = C(90, "Auto", "Float", "Int32");
    private readonly Label _step = new() { AutoSize = false, Height = 44, Dock = DockStyle.Top, Font = new Font("Segoe UI Semibold", 11F), ForeColor = Theme.Text, BackColor = Theme.Bg, Tag = "keep", UseMnemonic = false };
    private readonly Label _status = L("");
    private readonly Label _history = new() { AutoSize = false, Dock = DockStyle.Top, Height = 20, Font = Theme.UISmall, ForeColor = Theme.TextDim, BackColor = Theme.Bg, Tag = "keep", UseMnemonic = false };
    private readonly Ui.ThinProgress _progress = new() { Dock = DockStyle.Top, Visible = false };
    private readonly DataGridView _grid = Grid(("Address", 100), ("Region", 120), ("Looks like", 90), ("Value", 140), ("Cluster", 120), ("Confidence", 80), ("Hits", 50));
    private readonly FlowLayoutPanel _cards = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(0, 0, 4, 0) };
    private readonly IconButton _btnBaseline, _btnIdle, _btnAutoIdle, _btnChanged, _btnUp, _btnDown, _btnCancel;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly Dictionary<uint, uint> _patched = new();   // NOP'd instruction address → original word
    private List<Candidate> _view = new();
    private List<XBDMClient.MemoryRegion> _regions = new();
    private CancellationTokenSource? _cts;
    private bool _busy, _refreshing;

    public override string Key => "dynamic";
    public override string Title => "Dynamic Modder";
    public override string Glyph => Theme.Glyph.Wand;
    public override string Description => "Find variables by what you do in game";

    public DynamicModderTool(IToolHost host) : base(host)
    {
        _btnBaseline = B("1 · Start calibration", Theme.Glyph.Play, ButtonVariant.Primary, async (_, _) => await BaselineAsync(), "Snapshot the game's memory while nothing is happening");
        _btnIdle = B("2 · I'm idle", Theme.Glyph.Pin, ButtonVariant.Neutral, async (_, _) => await FilterAsync(DeltaFilter.Unchanged, 1), "Drop everything that changes on its own (timers, audio, animation)");
        _btnAutoIdle = B("Idle ×5", Theme.Glyph.Refresh, ButtonVariant.Ghost, async (_, _) => await FilterAsync(DeltaFilter.Unchanged, 5), "Five idle passes, 0.5 s apart");
        _btnChanged = B("3 · I did it – changed", Theme.Glyph.Bolt, ButtonVariant.Success, async (_, _) => await FilterAsync(DeltaFilter.Changed, 1), "Keep only what changed while you acted");
        _btnUp = B("went up", Theme.Glyph.ChevronUp, ButtonVariant.Neutral, async (_, _) => await FilterAsync(DeltaFilter.Increased, 1), "Keep values that increased (speed up, health gained…)");
        _btnDown = B("went down", Theme.Glyph.ChevronDown, ButtonVariant.Neutral, async (_, _) => await FilterAsync(DeltaFilter.Decreased, 1), "Keep values that decreased (damage taken, ammo used…)");
        _btnCancel = B("Cancel", Theme.Glyph.Stop, ButtonVariant.Danger, (_, _) => _cts?.Cancel());
        _btnCancel.Visible = false;

        _scope.SelectedIndexChanged += (_, _) => _start.Enabled = _size.Enabled = _scope.SelectedIndex == 3;
        _start.Enabled = _size.Enabled = false;
        _valueType.SelectedIndexChanged += (_, _) => _engine.ValueType = (DeltaValueType)_valueType.SelectedIndex;

        _grid.VirtualMode = true;
        _grid.Columns[0].DefaultCellStyle.Font = Theme.Mono;
        _grid.Columns[3].DefaultCellStyle.Font = Theme.Mono;
        _grid.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        _grid.CellValueNeeded += CellValue;
        _grid.CellFormatting += (sender, e) =>
        {
            if ((uint)e.RowIndex >= (uint)_view.Count || e.CellStyle == null || e.ColumnIndex != 5) return;
            int c = _view[e.RowIndex].Guess.Confidence;
            e.CellStyle.ForeColor = c >= 70 ? Theme.Success : c >= 45 ? Theme.Warning : Theme.TextDim;
        };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < _view.Count) AddCard(_view[e.RowIndex]); };
        _grid.ContextMenuStrip = BuildMenu();
        _timer.Tick += async (_, _) => await RefreshLiveAsync();
        _cards.Resize += (_, _) => { foreach (Control card in _cards.Controls) card.Width = Math.Max(280, _cards.ClientSize.Width - 12); };

        var scopeRow = Row(L("Search in"), _scope, L("Start"), _start, L("Size"), _size, L("Max MB"), _maxMb, L("Values"), _valueType);
        var steps = Row(_btnBaseline, _btnIdle, _btnAutoIdle, _btnChanged, _btnUp, _btnDown,
            B("Reset", Theme.Glyph.Clear, ButtonVariant.Ghost, (_, _) => ResetAll()), _btnCancel, _status);

        // candidates (left) · discovered variables (right)
        var candActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0) };
        candActions.Controls.AddRange(new Control[]
        {
            B("Add control", Theme.Glyph.Add, ButtonVariant.Primary, (_, _) => { foreach (var c in Sel().Take(8)) AddCard(c); }, "Create a live control for the selection (double-click)"),
            B("Find writer", Theme.Glyph.Target, ButtonVariant.Neutral, async (_, _) => { if (Sel().FirstOrDefault() is { } c) await TraceAsync(AddCard(c), XbdmDebugger.Access.Write); }, "Watchpoint: which instruction writes this value"),
            B("Active Memory", Theme.Glyph.Memory, ButtonVariant.Ghost, (_, _) => { if (Sel().FirstOrDefault() is { } c) Host.ActiveMemory(c.Address); }),
            B("Graph", Theme.Glyph.Analyze, ButtonVariant.Ghost, (_, _) => { if (Sel().FirstOrDefault() is { } c) Host.Graph(c.Address, c.Guess.Type, $"0x{c.Address:X8}"); }),
            B("Record", Theme.Glyph.Record, ButtonVariant.Ghost, (_, _) => { if (Sel().FirstOrDefault() is { } c) App.MonitorRegion(c.Address & ~0xFu, 0x100, $"candidate 0x{c.Address:X8}"); }),
        });
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Tag = "keep" };
        left.Controls.Add(Card(_grid));
        left.Controls.Add(candActions);

        var right = new Panel { Dock = DockStyle.Right, Width = 430, BackColor = Theme.Bg, Padding = new Padding(10, 0, 0, 0), Tag = "keep" };
        var rightHead = Row(L("DISCOVERED VARIABLES"),
            B("Restore all patches", Theme.Glyph.Refresh, ButtonVariant.Ghost, async (_, _) => await RestoreAllAsync(), "Undo every 'block writer' NOP"),
            B("Save all", Theme.Glyph.Save, ButtonVariant.Ghost, (_, _) => SaveAll(), "Add every variable to the offset database"));
        right.Controls.Add(_cards);
        right.Controls.Add(rightHead);
        var split = new Splitter { Dock = DockStyle.Right, Width = 5, BackColor = Theme.Bg, MinSize = 300, MinExtra = 360 };

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 8, 0, 0), Tag = "keep" };
        body.Controls.Add(left);
        body.Controls.Add(split);
        body.Controls.Add(right);

        Controls.Add(body);
        Controls.Add(_progress);
        Controls.Add(_history);
        Controls.Add(steps);
        Controls.Add(scopeRow);
        Controls.Add(_step);
        Resize += (_, _) => right.Width = Math.Clamp(Width / 3, 320, 520);
        UpdateStep();
    }

    public override void OnShown() => _timer.Start();
    public override void OnHidden() => _timer.Stop();
    public override void OnConnectionChanged(bool connected) { if (!connected) _cts?.Cancel(); UpdateStep(); }

    // ═════════════════════════════════════════════════════════════════════════
    //  calibration (Delta Tracker)
    // ═════════════════════════════════════════════════════════════════════════

    private void UpdateStep()
    {
        long n = _engine.Candidates;
        _step.Text = !_engine.HasBaseline
            ? "  Step 1 – stand still in game, then press Start calibration."
            : n > 200_000 ? $"  {n:N0} candidates – press I'm idle while nothing happens, then act and press I did it. Repeat to narrow down."
            : n > 0 ? $"  {n:N0} candidates – keep alternating idle / action, or pick from the ranked list and add a control."
            : "  No candidates left – press Reset and start again (try a different value type or scope).";
        bool connected = Client != null;
        _btnBaseline.Enabled = connected && !_busy;
        _btnIdle.Enabled = _btnAutoIdle.Enabled = _btnChanged.Enabled = _btnUp.Enabled = _btnDown.Enabled = connected && !_busy && _engine.HasBaseline && n > 0;
        _btnCancel.Visible = _busy;
        _history.Text = "  " + string.Join("   →   ", _engine.History.TakeLast(6));
    }

    /// <summary>Address ranges that belong to the running game (title heap + the game module image).</summary>
    private (uint Start, uint End)? _titleImage;

    /// <summary>
    /// Memory the calibration may read. Only the game's own memory is used: the title heap (0x40000000–0x7FFFFFFF)
    /// and the writable sections of the game module. System modules (kernel, XAM, XBDM), anything else in the
    /// 0x80000000–0x9FFFFFFF image range, and uncached / write-combined (GPU) or guard pages are never touched –
    /// reading those over the debug link can lock up the console.
    /// </summary>
    private async Task<List<(uint Start, uint Size)>> RangesAsync(XBDMClient c)
    {
        if (_scope.SelectedIndex == 3)
        {
            if (!TryAddress(_start.Text, out var st)) throw new FormatException("Start must be an address");
            uint sz = ParseNumber(_size.Text);
            if (!IsGameMemory(st) || !IsGameMemory(st + Math.Max(sz, 1) - 1))
            {
                if (MessageBox.Show(FindForm(), "That range is outside the game's own memory (system or GPU memory).\n\nReading it can freeze the console. Continue anyway?",
                        "Custom range", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return new();
            }
            return new() { (st, sz) };
        }

        _regions = await c.WalkMemoryAsync();
        var modules = await c.GetModulesAsync();
        var running = await c.GetRunningTitleAsync();
        var file = running?.Path is { Length: > 0 } p ? p[(p.LastIndexOf('\\') + 1)..] : "";
        var title = modules.FirstOrDefault(m => file.Length > 0 && m.Name.Equals(file, StringComparison.OrdinalIgnoreCase))
                    ?? modules.FirstOrDefault(m => m.Base == 0x82000000);
        _titleImage = title != null ? (title.Base, title.Base + title.Size) : null;

        static bool SafeWritable(XBDMClient.MemoryRegion r) =>
            (r.Protect & 0xFF) is 0x04 or 0x08 or 0x40 or 0x80     // read-write
            && (r.Protect & 0x100) == 0                              // not a guard page
            && (r.Protect & 0x200) == 0                              // not uncached
            && (r.Protect & 0x400) == 0;                             // not write-combined (GPU)

        var heap = _regions.Where(r => SafeWritable(r) && r.Base >= 0x40000000 && r.End <= 0x80000000).OrderBy(r => r.Base)
                           .Select(r => (r.Base, r.Size)).ToList();
        var globals = new List<(uint Base, uint Size)>();
        if (_titleImage is { } img)
            foreach (var r in _regions.Where(SafeWritable))
            {
                uint s0 = Math.Max(r.Base, img.Start), e0 = Math.Min(r.End, img.End);
                if (e0 > s0) globals.Add((s0, e0 - s0));
            }

        var pick = _scope.SelectedIndex switch
        {
            1 => heap,
            2 => globals,
            _ => globals.Concat(heap).ToList(),   // globals first (small, stable), then heap
        };
        if (_scope.SelectedIndex == 2 && _titleImage == null) App.Toast("Could not find the game module – use Title heap or Custom", ToastHost.Kind.Warning);

        long cap = (long)_maxMb.Value * 1048576, total = 0;
        var list = new List<(uint, uint)>();
        foreach (var r in pick)
        {
            if (total >= cap) break;
            uint size = (uint)Math.Min(r.Size, cap - total);
            list.Add((r.Base, size));
            total += size;
        }
        long all = pick.Sum(r => (long)r.Size);
        App.Log($"🧭 Dynamic Modder scope: {list.Count} regions, {total / 1048576.0:0.0} MB of {all / 1048576.0:0.0} MB game memory" +
                (title != null ? $" ({title.Name} @ 0x{title.Base:X8})" : ""));
        return list;
    }

    /// <summary>True for the title heap and the game module image – the only memory these tools write without asking.</summary>
    internal bool IsGameMemory(uint a) =>
        (a >= 0x40000000 && a < 0x80000000) || (_titleImage is { } img ? a >= img.Start && a < img.End : a >= 0x82000000 && a < 0x90000000);

    /// <summary>Asks before writing outside the game's own memory (system memory writes can crash the console).</summary>
    internal bool ConfirmWrite(uint a, string what)
    {
        if (IsGameMemory(a)) return true;
        return MessageBox.Show(FindForm(), $"0x{a:X8} is outside the game's memory (system / kernel area).\n\n{what} there can crash or freeze the console. Continue?",
            "Write outside game memory", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private async Task BaselineAsync()
    {
        var c = RequireClient();
        if (c == null || _busy) return;
        if (!Begin("Taking baseline snapshot…")) return;
        try
        {
            var ranges = await RangesAsync(c);
            if (ranges.Count == 0) { App.Toast("No writable memory found in that scope", ToastHost.Kind.Warning); return; }
            await _engine.BaselineAsync(new XbdmMemorySource(c), ranges, Progress(), _cts!.Token);
            App.Log($"🧭 Dynamic Modder baseline: {_engine.Candidates:N0} words");
            Rebuild();
        }
        catch (OperationCanceledException) { _engine.Reset(); App.Toast("Calibration cancelled", ToastHost.Kind.Warning); }
        catch (Exception ex) { App.Toast($"Baseline failed: {ex.Message}", ToastHost.Kind.Error); }
        finally { End(); }
    }

    private async Task FilterAsync(DeltaFilter filter, int passes)
    {
        var c = RequireClient();
        if (c == null || _busy || !_engine.HasBaseline) return;
        if (!Begin($"{filter} pass…")) return;
        try
        {
            for (int i = 0; i < passes; i++)
            {
                if (i > 0) await Task.Delay(500, _cts!.Token);
                await _engine.FilterAsync(new XbdmMemorySource(c), filter, Progress(), _cts!.Token);
            }
            Rebuild();
            if (_engine.Candidates == 0) App.Toast("Nothing matched – reset and try again", ToastHost.Kind.Warning);
        }
        catch (OperationCanceledException) { App.Toast("Pass cancelled – candidates were partially filtered", ToastHost.Kind.Warning); Rebuild(); }
        catch (Exception ex) { App.Toast($"Filter failed: {ex.Message}", ToastHost.Kind.Error); }
        finally { End(); }
    }

    private bool Begin(string text)
    {
        _busy = true;
        _cts = new CancellationTokenSource();
        _progress.Visible = true;
        _progress.Value = 0;
        _status.Text = text;
        UpdateStep();
        return true;
    }

    private void End()
    {
        _busy = false;
        _progress.Visible = false;
        _status.Text = _engine.HasBaseline ? $"{_engine.Candidates:N0} candidates after {_engine.Passes} passes" : "";
        UpdateStep();
    }

    private IProgress<(string, double)> Progress() => new Progress<(string Text, double Value)>(p => { _status.Text = p.Text; _progress.Value = p.Value; });

    private void ResetAll()
    {
        _cts?.Cancel();
        _engine.Reset();
        _view = new();
        _grid.RowCount = 0;
        _status.Text = "";
        UpdateStep();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ranking (heuristics)
    // ═════════════════════════════════════════════════════════════════════════

    private string RegionOf(uint a)
    {
        if (Host.Symbols.Describe(a) is { } s) return s;
        var r = _regions.FirstOrDefault(x => a >= x.Base && a < x.End);
        return r == null ? "" : a >= 0x80000000 ? "image data" : "heap";
    }

    /// <summary>Ranks the candidates (all of them when few, otherwise the first 50 000) by heuristic confidence.</summary>
    private void Rebuild()
    {
        long n = _engine.Candidates;
        int take = n <= 50_000 ? (int)n : 50_000;
        var list = new List<Candidate>(Math.Min(take, 50_000));
        foreach (var (a, raw) in _engine.Enumerate(take))
        {
            var g = DeltaHeuristics.Classify(a, raw, _engine.WordAt, _engine.IsAlive, _engine.ActionHits(a));
            list.Add(new Candidate { Address = a, Raw = raw, Guess = g, Hits = _engine.ActionHits(a) });
        }
        _view = list.OrderByDescending(c => c.Guess.Confidence).ThenByDescending(c => c.Hits).ThenBy(c => c.Address).Take(5000).ToList();
        foreach (var c in _view) c.Region = RegionOf(c.Address);
        _grid.RowCount = 0;
        _grid.RowCount = _view.Count;
        UpdateStep();
    }

    private void CellValue(object? sender, DataGridViewCellValueEventArgs e)
    {
        if ((uint)e.RowIndex >= (uint)_view.Count) return;
        var c = _view[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => $"0x{c.Address:X8}",
            1 => c.Region,
            2 => c.Guess.Kind,
            3 => Format(c.Raw, c.Guess.Type),
            4 => c.Guess.Cluster,
            5 => $"{c.Guess.Confidence}%",
            _ => c.Hits > 0 ? c.Hits.ToString() : "",
        };
    }

    private static string Format(uint raw, LiveType t)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, raw);
        return t == LiveType.Float ? BinaryPrimitives.ReadSingleBigEndian(b).ToString("0.####", CultureInfo.InvariantCulture)
             : t == LiveType.UInt32 ? $"0x{raw:X8}" : ((int)raw).ToString();
    }

    private List<Candidate> Sel() =>
        _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i >= 0 && i < _view.Count).OrderBy(i => i).Select(i => _view[i]).ToList();

    /// <summary>Refreshes the visible candidate rows and every variable card.</summary>
    private async Task RefreshLiveAsync()
    {
        var c = Client;
        if (c == null || _busy || _refreshing || !Visible) return;
        _refreshing = true;
        try
        {
            var view = _view;   // snapshot – a re-sort / filter can replace the list while a read is awaited
            if (view.Count > 0)
            {
                int first = Math.Clamp(_grid.FirstDisplayedScrollingRowIndex, 0, view.Count - 1);
                int count = Math.Max(0, Math.Min(view.Count - first, _grid.DisplayedRowCount(true) + 1));
                for (int i = first; i < first + count && c.IsConnected; i++)
                    if (await c.ReadUInt32Async(view[i].Address) is { } v) view[i].Raw = v;
                if (!IsDisposed) _grid.Invalidate();
            }
            foreach (var card in _cards.Controls.OfType<VariableCard>().ToList())
                if (c.IsConnected && !card.IsDisposed) await card.RefreshAsync(c);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException) { }
        finally { _refreshing = false; }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  generated controls
    // ═════════════════════════════════════════════════════════════════════════

    private VariableCard AddCard(Candidate c)
    {
        var existing = _cards.Controls.OfType<VariableCard>().FirstOrDefault(x => x.Address == c.Address);
        if (existing != null) { _cards.ScrollControlIntoView(existing); return existing; }
        var name = Host.Symbols.NameAt(c.Address) ?? (c.Guess.Cluster.Length > 0 ? $"{c.Guess.Cluster} @ {c.Address:X8}" : $"{c.Guess.Kind} @ {c.Address:X8}");
        var card = new VariableCard(this, c.Address, c.Guess.Type, name, c.Raw) { Width = Math.Max(300, _cards.ClientSize.Width - 12) };
        _cards.Controls.Add(card);
        _cards.Controls.SetChildIndex(card, 0);
        Theme.Apply(card);
        App.Log($"🎛️ Control created for 0x{c.Address:X8} ({c.Guess.Kind}{(c.Guess.Cluster.Length > 0 ? ", " + c.Guess.Cluster : "")})");
        return card;
    }

    internal async Task TraceAsync(VariableCard card, XbdmDebugger.Access kind)
    {
        var c = RequireClient();
        if (c == null || _busy) return;
        if (!Begin($"Watching 0x{card.Address:X8} – make the value change in game (15 s)…")) return;
        _progress.Indeterminate = true;
        try
        {
            var hits = await XbdmDebugger.TraceAsync(c, card.Address, 4, kind, TimeSpan.FromSeconds(15), 4, App.Log, _cts!.Token);
            card.SetHits(hits);
            if (hits.Count == 0) App.Toast("Nothing accessed it in 15 s – change the value in game while watching", ToastHost.Kind.Info);
            else App.Toast($"Found {hits.Count} instruction(s) that {(kind == XbdmDebugger.Access.Write ? "write" : "read")} it", ToastHost.Kind.Success);
        }
        catch (OperationCanceledException) { App.Toast("Watch cancelled", ToastHost.Kind.Warning); }
        catch (Exception ex)
        {
            App.Toast($"Watchpoint failed: {ex.Message}", ToastHost.Kind.Error);
            App.Log($"❌ Watchpoint failed (the debug monitor must support data breakpoints): {ex.Message}");
        }
        finally { _progress.Indeterminate = false; End(); }
    }

    internal async Task<bool> BlockWriterAsync(TraceHit hit)
    {
        var c = RequireClient();
        if (c == null) return false;
        if (!ConfirmWrite(hit.Pc, "Patching code")) return false;
        if (!_patched.ContainsKey(hit.Pc)) _patched[hit.Pc] = hit.Instruction;
        bool ok = await c.SetMemoryAsync(hit.Pc, new byte[] { 0x60, 0, 0, 0 });
        App.Log(ok ? $"🩹 Blocked writer at 0x{hit.Pc:X8} ({hit.Disassembly} → nop)" : $"❌ Could not patch 0x{hit.Pc:X8}");
        return ok;
    }

    internal async Task<bool> RestoreWriterAsync(uint pc)
    {
        var c = RequireClient();
        if (c == null || !_patched.TryGetValue(pc, out var orig)) return false;
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, orig);
        bool ok = await c.SetMemoryAsync(pc, b);
        if (ok) { _patched.Remove(pc); App.Log($"↩️ Restored 0x{pc:X8}"); }
        return ok;
    }

    internal bool IsPatched(uint pc) => _patched.ContainsKey(pc);

    private async Task RestoreAllAsync()
    {
        int n = 0;
        foreach (var pc in _patched.Keys.ToList()) if (await RestoreWriterAsync(pc)) n++;
        foreach (var card in _cards.Controls.OfType<VariableCard>()) card.RefreshHits();
        App.Toast(n == 0 ? "Nothing to restore" : $"Restored {n} instruction(s)", ToastHost.Kind.Success);
    }

    internal void Save(VariableCard card)
    {
        var notes = card.Hits.FirstOrDefault(h => h.IsStore) is { } w
            ? $"written by 0x{w.Pc:X8} ({w.Disassembly}); object base 0x{w.BaseValue:X8} + 0x{w.Offset:X}"
            : "found by Dynamic Modder";
        App.PushOffsets(new[] { new DiscoveredOffset { Name = card.VarName, Category = "Dynamic Modder", Address = card.Address, Type = "Global", Source = "Dynamic", Confidence = "Medium", Notes = notes } });
    }

    private void SaveAll()
    {
        var cards = _cards.Controls.OfType<VariableCard>().ToList();
        if (cards.Count == 0) { App.Toast("Create some controls first", ToastHost.Kind.Info); return; }
        App.PushOffsets(cards.Select(card => new DiscoveredOffset
        {
            Name = card.VarName, Category = "Dynamic Modder", Address = card.Address, Type = "Global", Source = "Dynamic", Confidence = "Medium",
            Notes = card.Hits.FirstOrDefault(h => h.IsStore) is { } w ? $"written by 0x{w.Pc:X8} ({w.Disassembly}); base 0x{w.BaseValue:X8} + 0x{w.Offset:X}" : "found by Dynamic Modder",
        }).ToList());
    }

    internal void Remove(VariableCard card)
    {
        _cards.Controls.Remove(card);
        card.Dispose();
    }

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Add control", null, (_, _) => { foreach (var c in Sel().Take(8)) AddCard(c); });
        m.Items.Add("Find writer (watchpoint)", null, async (_, _) => { if (Sel().FirstOrDefault() is { } c) await TraceAsync(AddCard(c), XbdmDebugger.Access.Write); });
        m.Items.Add("Find reader (watchpoint)", null, async (_, _) => { if (Sel().FirstOrDefault() is { } c) await TraceAsync(AddCard(c), XbdmDebugger.Access.Read); });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Open in Active Memory", null, (_, _) => { if (Sel().FirstOrDefault() is { } c) Host.ActiveMemory(c.Address); });
        m.Items.Add("Graph", null, (_, _) => { if (Sel().FirstOrDefault() is { } c) Host.Graph(c.Address, c.Guess.Type, $"0x{c.Address:X8}"); });
        m.Items.Add("Add to watch list", null, (_, _) => { foreach (var c in Sel().Take(20)) App.AddWatch(c.Address, $"Dynamic 0x{c.Address:X8}"); });
        m.Items.Add("Copy address", null, (_, _) => { if (Sel().FirstOrDefault() is { } c) Clipboard.SetText($"0x{c.Address:X8}"); });
        Scripting.ScriptIde.StyleMenu(m);
        return m;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cts?.Cancel(); _timer.Dispose(); }
        base.Dispose(disposing);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  variable card (Phase 4 UI)
    // ═════════════════════════════════════════════════════════════════════════

    internal sealed class VariableCard : Panel
    {
        private readonly DynamicModderTool _tool;
        private readonly Label _title = new() { AutoSize = false, Height = 22, Dock = DockStyle.Top, Font = Theme.UISemibold, ForeColor = Theme.Text, BackColor = Color.Transparent, Tag = "keep", UseMnemonic = false, Cursor = Cursors.Hand };
        private readonly Label _value = new() { AutoSize = false, Height = 30, Dock = DockStyle.Top, Font = new Font("Segoe UI Semibold", 15F), ForeColor = Theme.Accent, BackColor = Color.Transparent, Tag = "keep", UseMnemonic = false };
        private readonly TrackBar _slider = new() { Dock = DockStyle.Top, Height = 32, TickStyle = TickStyle.None, Minimum = 0, Maximum = 1000, BackColor = Theme.Surface };
        private readonly Label _range = new() { AutoSize = false, Height = 16, Dock = DockStyle.Top, Font = Theme.UISmall, ForeColor = Theme.TextDim, BackColor = Color.Transparent, Tag = "keep", Cursor = Cursors.Hand };
        private readonly TextBox _input = new() { Width = 120, Font = Theme.Mono, Margin = new Padding(0, 3, 6, 0) };
        private readonly CheckBox _freeze = new() { Text = "Freeze", AutoSize = true, Margin = new Padding(4, 6, 6, 0) };
        private readonly FlowLayoutPanel _writers = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface };
        private double _min, _max, _original;
        private bool _sliding, _writing, _writeConfirmed;
        private double? _pending;

        public uint Address { get; }
        public LiveType Type { get; private set; }
        public string VarName { get; private set; }
        public List<TraceHit> Hits { get; private set; } = new();

        public VariableCard(DynamicModderTool tool, uint address, LiveType type, string name, uint raw)
        {
            _tool = tool;
            Address = address;
            Type = type is LiveType.Float or LiveType.Int32 or LiveType.Int16 or LiveType.Byte ? type : LiveType.Int32;
            VarName = name;
            BackColor = Theme.Surface;
            Padding = new Padding(12, 8, 12, 10);
            Margin = new Padding(0, 0, 0, 10);
            AutoSize = false;
            Tag = "keep";
            Paint += (_, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); };

            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, raw);
            _original = LiveValue.ToDouble(Type, b[..LiveValue.Size(Type)]);
            SetRange(_original);
            Current = _original;
            _value.Text = Fmt(_original);
            _input.Text = Fmt(_original);
            if (_max > _min) _slider.Value = (int)Math.Clamp((_original - _min) / (_max - _min) * 1000, 0, 1000);

            _title.Text = $"{name}   ·   0x{address:X8} {Type}";
            _title.Click += (_, _) =>
            {
                var n = Prompt.Show(FindForm(), "Rename variable", "Name", VarName);
                if (!string.IsNullOrWhiteSpace(n)) { VarName = n.Trim(); _title.Text = $"{VarName}   ·   0x{Address:X8} {Type}"; }
            };
            _range.Click += (_, _) =>
            {
                var r = Prompt.ShowTwo(FindForm(), "Slider range", "Minimum", _min.ToString(CultureInfo.InvariantCulture), "Maximum", _max.ToString(CultureInfo.InvariantCulture));
                if (r != null && double.TryParse(r.Value.First, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) &&
                    double.TryParse(r.Value.Second, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi) && hi > lo)
                { _min = lo; _max = hi; UpdateRangeLabel(); }
            };
            _slider.Scroll += (_, _) => { _sliding = true; Queue(_min + (_max - _min) * _slider.Value / 1000.0); };
            _slider.MouseUp += (_, _) => _sliding = false;
            _input.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SetFromInput(); } };
            _freeze.CheckedChanged += (_, _) => ApplyFreeze();

            var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Theme.Surface };
            row.Controls.AddRange(new Control[]
            {
                _input,
                Btn("Set", ButtonVariant.Primary, (_, _) => SetFromInput()),
                Btn("×0.5", ButtonVariant.Ghost, (_, _) => Queue(Current * 0.5)),
                Btn("×2", ButtonVariant.Ghost, (_, _) => Queue(Current * 2)),
                Btn("×10", ButtonVariant.Ghost, (_, _) => Queue(Current * 10)),
                Btn("Reset", ButtonVariant.Ghost, (_, _) => Queue(_original)),
                _freeze,
            });
            var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Theme.Surface, Padding = new Padding(0, 4, 0, 0) };
            row2.Controls.AddRange(new Control[]
            {
                Btn("Find writer", ButtonVariant.Neutral, async (_, _) => await _tool.TraceAsync(this, XbdmDebugger.Access.Write)),
                Btn("Find reader", ButtonVariant.Ghost, async (_, _) => await _tool.TraceAsync(this, XbdmDebugger.Access.Read)),
                Btn("Memory", ButtonVariant.Ghost, (_, _) => _tool.Host.ActiveMemory(Address)),
                Btn("Graph", ButtonVariant.Ghost, (_, _) => _tool.Host.Graph(Address, Type, VarName)),
                Btn("Record", ButtonVariant.Ghost, (_, _) => _tool.App.MonitorRegion(Address & ~0xFu, 0x100, VarName)),
                Btn("Save", ButtonVariant.Ghost, (_, _) => _tool.Save(this)),
                Btn("Remove", ButtonVariant.Ghost, (_, _) => { if (_freeze.Checked) _tool.App.Freezer?.Unfreeze(Address); _tool.Remove(this); }),
            });

            // dock order: last added = top
            Controls.Add(_writers);
            Controls.Add(row2);
            Controls.Add(row);
            Controls.Add(_range);
            Controls.Add(_slider);
            Controls.Add(_value);
            Controls.Add(_title);
            Layout += (_, _) => Height = Controls.Cast<Control>().Where(c => c.Visible).Sum(c => c.Height + c.Margin.Vertical) + Padding.Vertical;
        }

        private static IconButton Btn(string text, ButtonVariant v, EventHandler click)
        {
            var b = IconButton.Create(text, "", v, click);
            b.Height = 28;
            b.Margin = new Padding(0, 2, 4, 0);
            return b;
        }

        private double Current { get; set; }

        private void SetRange(double v)
        {
            double a = Math.Abs(v);
            if (Type == LiveType.Float) { _min = v < 0 ? -Math.Max(a * 4, 10) : 0; _max = Math.Max(a * 4, 10); }
            else { _min = v < 0 ? -Math.Max(a * 4, 100) : 0; _max = Math.Max(a * 4, 100); }
            UpdateRangeLabel();
        }

        private void UpdateRangeLabel() => _range.Text = $"range {Fmt(_min)} … {Fmt(_max)}   (click to change)   ·   original {Fmt(_original)}";

        private string Fmt(double v) => Type == LiveType.Float ? v.ToString("0.###", CultureInfo.InvariantCulture) : ((long)Math.Round(v)).ToString();

        private void SetFromInput()
        {
            var t = _input.Text.Trim();
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) Queue(v);
            else if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(t[2..], NumberStyles.HexNumber, null, out var h)) Queue(h);
            else _tool.App.Toast($"“{t}” is not a number", ToastHost.Kind.Warning);
        }

        private byte[] Encode(double v)
        {
            var b = new byte[LiveValue.Size(Type)];
            switch (Type)
            {
                case LiveType.Float: BinaryPrimitives.WriteSingleBigEndian(b, (float)v); break;
                case LiveType.Int16: BinaryPrimitives.WriteInt16BigEndian(b, (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue)); break;
                case LiveType.Byte: b[0] = (byte)Math.Clamp(Math.Round(v), 0, 255); break;
                default: BinaryPrimitives.WriteInt32BigEndian(b, (int)Math.Clamp(Math.Round(v), int.MinValue, int.MaxValue)); break;
            }
            return b;
        }

        /// <summary>Writes are coalesced: while one is in flight only the latest requested value is kept.</summary>
        private async void Queue(double v)
        {
            if (v > _max) { _max = v; UpdateRangeLabel(); }
            if (v < _min) { _min = v; UpdateRangeLabel(); }
            _pending = v;
            if (_writing) return;
            _writing = true;
            try
            {
                while (_pending is { } next)
                {
                    _pending = null;
                    var c = _tool.Client;
                    if (c == null) break;
                    if (!_writeConfirmed) { if (!_tool.ConfirmWrite(Address, "Writing")) break; _writeConfirmed = true; }
                    var bytes = Encode(next);
                    await c.SetMemoryAsync(Address, bytes);
                    if (_freeze.Checked) _tool.App.Freezer?.Freeze(Address, bytes, VarName);
                    Current = next;
                    _value.Text = Fmt(next);
                }
            }
            finally { _writing = false; }
        }

        private void ApplyFreeze()
        {
            var fz = _tool.App.Freezer;
            if (fz == null) return;
            if (_freeze.Checked) fz.Freeze(Address, Encode(Current), VarName);
            else fz.Unfreeze(Address);
        }

        public async Task RefreshAsync(XBDMClient c)
        {
            var b = await c.GetMemoryAsync(Address, (uint)LiveValue.Size(Type));
            if (b == null) { _value.Text = "unreadable"; return; }
            Current = LiveValue.ToDouble(Type, b);
            _value.Text = Fmt(Current);
            if (!_sliding && _max > _min)
                _slider.Value = (int)Math.Clamp((Current - _min) / (_max - _min) * 1000, 0, 1000);
            if (!_input.Focused) _input.Text = Fmt(Current);
            if (_freeze.Checked != (_tool.App.Freezer?.IsFrozen(Address) == true)) _freeze.Checked = _tool.App.Freezer?.IsFrozen(Address) == true;
        }

        public void SetHits(List<TraceHit> hits)
        {
            foreach (var h in hits)
            {
                var existing = Hits.FirstOrDefault(x => x.Pc == h.Pc);
                if (existing != null) existing.Count += h.Count; else Hits.Add(h);
            }
            RefreshHits();
        }

        public void RefreshHits()
        {
            _writers.SuspendLayout();
            _writers.Controls.Clear();
            foreach (var h in Hits)
            {
                var line = new Label
                {
                    AutoSize = true, MaximumSize = new Size(Math.Max(200, Width - 30), 0), Font = Theme.MonoSmall, BackColor = Color.Transparent, Tag = "keep", UseMnemonic = false,
                    ForeColor = _tool.IsPatched(h.Pc) ? Theme.Warning : Theme.TextMuted, Margin = new Padding(0, 6, 0, 2),
                    Text = $"0x{h.Pc:X8}  {h.Summary}" + (h.Count > 1 ? $"  ×{h.Count}" : "") + (_tool.IsPatched(h.Pc) ? "  [blocked]" : ""),
                };
                var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Theme.Surface, Margin = new Padding(0) };
                var hit = h;
                if (h.IsStore)
                    actions.Controls.Add(_tool.IsPatched(h.Pc)
                        ? Btn("Restore writer", ButtonVariant.Success, async (_, _) => { await _tool.RestoreWriterAsync(hit.Pc); RefreshHits(); })
                        : Btn("Block writer (NOP)", ButtonVariant.Warning, async (_, _) => { await _tool.BlockWriterAsync(hit); RefreshHits(); }));
                actions.Controls.Add(Btn("Disassemble", ButtonVariant.Ghost, (_, _) => _tool.Host.Disassemble(hit.Pc)));
                if (h.BaseValue >= 0x40000000)
                {
                    actions.Controls.Add(Btn("Object base", ButtonVariant.Ghost, (_, _) => _tool.Host.ActiveMemory(hit.BaseValue)));
                    actions.Controls.Add(Btn("Pointer chain", ButtonVariant.Ghost, (_, _) => _tool.Host.PointerChain(hit.BaseValue, Array.Empty<uint>())));
                }
                _writers.Controls.Add(line);
                _writers.Controls.Add(actions);
            }
            Theme.Apply(_writers);
            _writers.ResumeLayout(true);
            PerformLayout();
        }
    }
}
