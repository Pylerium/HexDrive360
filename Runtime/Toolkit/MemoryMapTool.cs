using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>Committed memory regions, loaded modules and their sections in one sortable list, with dump / scan / record.</summary>
public sealed class MemoryMapTool : ToolBase
{
    private sealed record Entry(string Kind, string Name, uint Base, uint Size, string Protect)
    {
        public uint End => Base + Size;
    }

    private readonly DataGridView _grid = Grid(("Kind", 80), ("Name", 240), ("Base", 100), ("End", 100), ("Size", 110), ("Protection", 160));
    private readonly TextBox _filter = T("", 220, "Filter (name, kind, address)", mono: false);
    private readonly CheckBox _regions = new() { Text = "Regions", Checked = true, AutoSize = true, Margin = new Padding(0, 7, 8, 0) };
    private readonly CheckBox _modules = new() { Text = "Modules", Checked = true, AutoSize = true, Margin = new Padding(0, 7, 8, 0) };
    private readonly CheckBox _sections = new() { Text = "Sections", Checked = true, AutoSize = true, Margin = new Padding(0, 7, 8, 0) };
    private readonly Label _summary = L("");
    private readonly Ui.ThinProgress _progress = new() { Dock = DockStyle.Top, Visible = false };
    private List<Entry> _all = new(), _view = new();
    private bool _loaded, _busy;
    private int _sortCol = 2;
    private bool _desc;

    public override string Key => "map";
    public override string Title => "Memory Map";
    public override string Glyph => Theme.Glyph.List;
    public override string Description => "Every region, module and section – dump, scan or record any of them";

    public MemoryMapTool(IToolHost host) : base(host)
    {
        _grid.VirtualMode = true;
        foreach (int i in new[] { 2, 3, 4 }) _grid.Columns[i].DefaultCellStyle.Font = Theme.Mono;
        foreach (DataGridViewColumn col in _grid.Columns) col.SortMode = DataGridViewColumnSortMode.Programmatic;
        _grid.CellValueNeeded += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)_view.Count) return;
            var x = _view[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => x.Kind, 1 => x.Name, 2 => $"0x{x.Base:X8}", 3 => $"0x{x.End:X8}",
                4 => x.Size >= 0x100000 ? $"{x.Size / 1048576.0:0.0} MB" : $"{x.Size / 1024.0:0.#} KB", _ => x.Protect,
            };
        };
        _grid.CellFormatting += (sender, e) =>
        {
            if ((uint)e.RowIndex >= (uint)_view.Count || e.CellStyle == null || e.ColumnIndex != 0) return;
            e.CellStyle.ForeColor = _view[e.RowIndex].Kind switch { "Module" => Theme.Accent, "Section" => Theme.Teal, _ => Theme.TextMuted };
        };
        _grid.ColumnHeaderMouseClick += (_, e) => { _desc = _sortCol == e.ColumnIndex && !_desc; _sortCol = e.ColumnIndex; ApplyFilter(); };
        _grid.CellDoubleClick += (_, e) => { if (Sel() is { } x) Host.ActiveMemory(x.Base); };
        _filter.TextChanged += (_, _) => ApplyFilter();
        foreach (var cb in new[] { _regions, _modules, _sections }) cb.CheckedChanged += (_, _) => ApplyFilter();

        var top = Row(B("Refresh", Theme.Glyph.Refresh, ButtonVariant.Primary, async (_, _) => await LoadAsync(true)), _filter, _regions, _modules, _sections, _summary);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0) };
        actions.Controls.AddRange(new Control[]
        {
            B("Memory", Theme.Glyph.Memory, ButtonVariant.Neutral, (_, _) => { if (Sel() is { } x) Host.ActiveMemory(x.Base); }),
            B("Disassemble", Theme.Glyph.Code, ButtonVariant.Neutral, (_, _) => { if (Sel() is { } x) Host.Disassemble(x.Base); }),
            B("Scan in region", Theme.Glyph.Search, ButtonVariant.Neutral, (_, _) => { if (Sel() is { } x) Host.ScanRange(x.Base, x.Size); }),
            B("Record region", Theme.Glyph.Record, ButtonVariant.Neutral, (_, _) => { if (Sel() is { } x) App.MonitorRegion(x.Base, Math.Min(x.Size, 0x100000u), $"{x.Kind} {x.Name}"); }, "Monitor in the Memory Recorder (first 1 MB)"),
            B("Dump to file…", Theme.Glyph.Download, ButtonVariant.Neutral, async (_, _) => await DumpAsync()),
            B("Copy", Theme.Glyph.Copy, ButtonVariant.Ghost, (_, _) => { if (Sel() is { } x) Clipboard.SetText($"{x.Name}: 0x{x.Base:X8} - 0x{x.End:X8} (0x{x.Size:X})"); }),
        });
        Controls.Add(Card(_grid));
        Controls.Add(actions);
        Controls.Add(_progress);
        Controls.Add(top);
        _summary.Text = "Press Refresh to read the console's memory map";
    }

    private Entry? Sel() => _grid.CurrentCell?.RowIndex is int i && i >= 0 && i < _view.Count ? _view[i] : null;

    public override void OnShown() { if (!_loaded && Client != null) _ = LoadAsync(false); }
    public override void OnConnectionChanged(bool connected) { if (!connected) { _loaded = false; } }

    private async Task LoadAsync(bool user)
    {
        var c = user ? RequireClient() : Client;
        if (c == null || _busy) return;
        _busy = true;
        _progress.Visible = true;
        _progress.Indeterminate = true;
        try
        {
            var list = new List<Entry>();
            foreach (var r in await c.WalkMemoryAsync())
                list.Add(new Entry("Region", RegionName(r.Base), r.Base, r.Size, r.ProtectText));
            foreach (var m in await c.GetModulesAsync())
            {
                list.Add(new Entry("Module", m.Name, m.Base, m.Size, ""));
                foreach (var s in await c.GetModuleSectionsAsync(m.Name))
                    list.Add(new Entry("Section", $"{m.Name} {s.Name}", s.Base, s.Size, $"flags 0x{s.Flags:X}"));
            }
            _all = list;
            _loaded = true;
            ApplyFilter();
            long committed = list.Where(x => x.Kind == "Region").Sum(x => (long)x.Size);
            _summary.Text = $"{list.Count(x => x.Kind == "Region")} regions ({committed / 1048576.0:0} MB committed) · {list.Count(x => x.Kind == "Module")} modules · {list.Count(x => x.Kind == "Section")} sections";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { if (user) App.Toast("The console did not answer walkmem", ToastHost.Kind.Warning); }
        finally { _busy = false; _progress.Visible = false; }
    }

    private static string RegionName(uint b) => b switch
    {
        < 0x40000000 => "low / system",
        < 0x80000000 => "virtual heap (64 KB pages)",
        < 0x90000000 => "image (4 KB pages)",
        < 0xA0000000 => "image (64 KB pages)",
        < 0xC0000000 => "physical (64 KB)",
        < 0xE0000000 => "physical (16 MB)",
        _ => "physical (4 KB)",
    };

    private void ApplyFilter()
    {
        var f = _filter.Text.Trim();
        IEnumerable<Entry> q = _all.Where(x => (x.Kind == "Region" && _regions.Checked) || (x.Kind == "Module" && _modules.Checked) || (x.Kind == "Section" && _sections.Checked));
        if (f.Length > 0)
        {
            var hex = f.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? f[2..] : f;
            bool isAddr = uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var a) && hex.Length >= 6;
            q = q.Where(x => x.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || x.Kind.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                             x.Protect.Contains(f, StringComparison.OrdinalIgnoreCase) || (isAddr && a >= x.Base && a < x.End));
        }
        Func<Entry, IComparable> key = _sortCol switch { 0 => x => x.Kind, 1 => x => x.Name, 3 => x => x.End, 4 => x => x.Size, 5 => x => x.Protect, _ => x => x.Base };
        _view = (_desc ? q.OrderByDescending(key) : q.OrderBy(key)).ToList();
        _grid.RowCount = 0;
        _grid.RowCount = _view.Count;
    }

    private async Task DumpAsync()
    {
        var c = RequireClient();
        if (c == null || Sel() is not { } x) return;
        if (x.Size > 0x10000000) { App.Toast("Region is larger than 256 MB – dump a smaller one", ToastHost.Kind.Warning); return; }
        using var sfd = new SaveFileDialog { Filter = "Binary (*.bin)|*.bin", FileName = $"{string.Concat(x.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch))}_{x.Base:X8}.bin" };
        if (sfd.ShowDialog(FindForm()) != DialogResult.OK) return;
        _progress.Visible = true;
        _progress.Indeterminate = false;
        try
        {
            var (data, failed) = await c.ReadLargeAsync(x.Base, x.Size, new Progress<double>(p => _progress.Value = p));
            await File.WriteAllBytesAsync(sfd.FileName, data);
            App.Toast($"Dumped {x.Size / 1024:N0} KB{(failed > 0 ? $" ({failed} unreadable chunks zero-filled)" : "")}", failed == 0 ? ToastHost.Kind.Success : ToastHost.Kind.Warning);
            App.Log($"💾 Dumped 0x{x.Base:X8}-0x{x.End:X8} → {sfd.FileName}");
        }
        catch (Exception ex) { App.Toast($"Dump failed: {ex.Message}", ToastHost.Kind.Error); }
        finally { _progress.Visible = false; }
    }
}
