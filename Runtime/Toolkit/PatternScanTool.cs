using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>
/// Searches live console memory for byte patterns (with ?? / nibble wildcards), text, integers or floats – in the
/// game module, all title memory, or a custom range – and lets results be re-checked as the game changes.
/// </summary>
public sealed class PatternScanTool : ToolBase
{
    private sealed record Hit(uint Address, string Region, string Preview);

    private readonly ComboBox _mode = C(130, "Byte pattern", "Text (ASCII)", "Text (UTF-16 BE)", "Int32", "Float", "Int16", "Byte");
    private readonly TextBox _query = T("", 340, "7D 88 02 A6 ?? ?? ?? ?? 48 ?? ?? ?1");
    private readonly ComboBox _region = C(170, "Game module", "Game code only", "All game memory (heap + module)", "Custom range");
    private readonly TextBox _start = T("0x82000000", 110);
    private readonly TextBox _size = T("0x01000000", 110);
    private readonly CheckBox _aligned = new() { Text = "4-byte aligned", AutoSize = true, Margin = new Padding(0, 7, 10, 0) };
    private readonly DataGridView _grid = Grid(("Address", 110), ("Region", 170), ("Symbol", 220), ("Bytes", 300));
    private readonly Ui.ThinProgress _progress = new() { Dock = DockStyle.Top, Visible = false };
    private readonly Label _status = L("");
    private readonly IconButton _btnScan, _btnCancel;
    private List<Hit> _hits = new();
    private byte[] _lastValue = Array.Empty<byte>(), _lastMask = Array.Empty<byte>();
    private CancellationTokenSource? _cts;
    private const int MaxHits = 2000;

    public override string Key => "scan";
    public override string Title => "Pattern Scan";
    public override string Glyph => Theme.Glyph.Search;
    public override string Description => "Find byte patterns, text or values in live memory";

    public PatternScanTool(IToolHost host) : base(host)
    {
        _grid.VirtualMode = true;
        _grid.Columns[0].DefaultCellStyle.Font = Theme.Mono;
        _grid.Columns[3].DefaultCellStyle.Font = Theme.MonoSmall;
        _grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex >= _hits.Count) return;
            var h = _hits[e.RowIndex];
            e.Value = e.ColumnIndex switch { 0 => $"0x{h.Address:X8}", 1 => h.Region, 2 => Host.Symbols.Describe(h.Address) ?? "", _ => h.Preview };
        };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < _hits.Count) Host.ActiveMemory(_hits[e.RowIndex].Address); };
        _mode.SelectedIndexChanged += (_, _) =>
        {
            _query.PlaceholderText = _mode.SelectedIndex switch { 0 => "7D 88 02 A6 ?? ?? ?? ?? 48 ?? ?? ?1", 1 or 2 => "text to find", 4 => "100.0", _ => "1337 or 0x539" };
            _aligned.Checked = _mode.SelectedIndex is 3 or 4;
        };
        _region.SelectedIndexChanged += (_, _) => _start.Enabled = _size.Enabled = _region.SelectedIndex == 3;
        _start.Enabled = _size.Enabled = false;
        _query.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ScanAsync(); } };

        _btnScan = B("Scan", Theme.Glyph.Search, ButtonVariant.Primary, async (_, _) => await ScanAsync());
        _btnCancel = B("Cancel", Theme.Glyph.Stop, ButtonVariant.Danger, (_, _) => _cts?.Cancel());
        _btnCancel.Visible = false;

        var r1 = Row(L("Find"), _mode, _query, _aligned, _btnScan, _btnCancel);
        var r2 = Row(L("In"), _region, L("Start"), _start, L("Size"), _size, _status);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0) };
        actions.Controls.AddRange(new Control[]
        {
            B("Re-check", Theme.Glyph.Refresh, ButtonVariant.Neutral, async (_, _) => await RecheckAsync(), "Keep only results that still match (like a next scan)"),
            B("Memory", Theme.Glyph.Memory, ButtonVariant.Neutral, (_, _) => { if (Sel() is { } h) Host.ActiveMemory(h.Address); }),
            B("Disassemble", Theme.Glyph.Code, ButtonVariant.Neutral, (_, _) => { if (Sel() is { } h) Host.Disassemble(h.Address); }),
            B("Watch", Theme.Glyph.Pin, ButtonVariant.Neutral, (_, _) => { foreach (var h in SelAll().Take(50)) App.AddWatch(h.Address, $"Scan 0x{h.Address:X8}"); }),
            B("Graph", Theme.Glyph.Analyze, ButtonVariant.Neutral, (_, _) => { if (Sel() is { } h) Host.Graph(h.Address, _mode.SelectedIndex == 4 ? LiveType.Float : LiveType.Int32, $"0x{h.Address:X8}"); }),
            B("Record", Theme.Glyph.Record, ButtonVariant.Neutral, (_, _) => { if (Sel() is { } h) App.MonitorRegion(h.Address & ~0xFu, 0x100, $"scan hit 0x{h.Address:X8}"); }),
            B("Copy", Theme.Glyph.Copy, ButtonVariant.Ghost, (_, _) => { var s = SelAll(); if (s.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, s.Select(h => $"0x{h.Address:X8}"))); }),
        });
        Controls.Add(Card(_grid));
        Controls.Add(actions);
        Controls.Add(_progress);
        Controls.Add(r2);
        Controls.Add(r1);
    }

    private Hit? Sel() => SelAll().FirstOrDefault();
    private List<Hit> SelAll() => _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i >= 0 && i < _hits.Count).OrderBy(i => i).Select(i => _hits[i]).ToList();

    public void SetRange(uint start, uint size)
    {
        _region.SelectedIndex = 3;
        _start.Text = $"0x{start:X8}";
        _size.Text = $"0x{size:X}";
        _query.Focus();
    }

    public override void OnConnectionChanged(bool connected) { if (!connected) _cts?.Cancel(); }

    /// <summary>Builds value / mask arrays for the query (mask byte 0 = wildcard).</summary>
    private bool BuildPattern(out byte[] value, out byte[] mask)
    {
        value = mask = Array.Empty<byte>();
        var q = _query.Text;
        try
        {
            switch (_mode.SelectedIndex)
            {
                case 0:
                    var p = BytePattern.Parse(q);
                    value = p.Value; mask = p.Mask; break;
                case 1: value = Encoding.ASCII.GetBytes(q); break;
                case 2: value = Encoding.BigEndianUnicode.GetBytes(q); break;
                case 3: value = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(value, unchecked((int)ParseNumber(q))); break;
                case 4: value = new byte[4]; BinaryPrimitives.WriteSingleBigEndian(value, float.Parse(q, CultureInfo.InvariantCulture)); break;
                case 5: value = new byte[2]; BinaryPrimitives.WriteInt16BigEndian(value, unchecked((short)ParseNumber(q))); break;
                case 6: value = new[] { unchecked((byte)ParseNumber(q)) }; break;
            }
        }
        catch (Exception ex) { App.Toast($"Invalid search: {ex.Message}", ToastHost.Kind.Warning); return false; }
        if (mask.Length == 0) mask = Enumerable.Repeat((byte)0xFF, value.Length).ToArray();
        if (value.Length == 0 || mask.All(m => m == 0)) { App.Toast("Enter something to search for", ToastHost.Kind.Warning); return false; }
        return true;
    }

    private async Task<List<(uint Start, uint Size, string Name)>> RangesAsync(XBDMClient c)
    {
        var list = new List<(uint, uint, string)>();
        switch (_region.SelectedIndex)
        {
            case 0:
            case 1:
            {
                var mods = await c.GetModulesAsync();
                var m = mods.FirstOrDefault(x => x.Base == 0x82000000) ?? mods.LastOrDefault(x => x.Name.EndsWith(".xex", StringComparison.OrdinalIgnoreCase));
                if (m == null) break;
                if (_region.SelectedIndex == 1)
                {
                    foreach (var s in await c.GetModuleSectionsAsync(m.Name))
                        if ((s.Flags & 0x20) != 0 || s.Name.Contains("text", StringComparison.OrdinalIgnoreCase)) list.Add((s.Base, s.Size, $"{m.Name} {s.Name}"));
                    if (list.Count > 0) break;
                }
                list.Add((m.Base, m.Size, m.Name));
                break;
            }
            case 2:
            {
                // game memory only: the title heap and the game module. System modules, uncached / write-combined
                // (GPU) and guard pages are skipped – bulk reads of those can lock up the console.
                var mods = await c.GetModulesAsync();
                var title = mods.FirstOrDefault(x => x.Base == 0x82000000);
                foreach (var r in await c.WalkMemoryAsync())
                {
                    if ((r.Protect & 0x01) != 0 || (r.Protect & 0x700) != 0) continue;
                    if (r.Base >= 0x40000000 && r.End <= 0x80000000) list.Add((r.Base, r.Size, $"heap {r.ProtectText}"));
                    else if (title != null && r.End > title.Base && r.Base < title.Base + title.Size)
                    {
                        uint s0 = Math.Max(r.Base, title.Base), e0 = Math.Min(r.End, title.Base + title.Size);
                        list.Add((s0, e0 - s0, $"{title.Name} {r.ProtectText}"));
                    }
                }
                break;
            }
            default:
                if (TryAddress(_start.Text, out var st)) list.Add((st, ParseNumber(_size.Text), "custom"));
                break;
        }
        return list;
    }

    private async Task ScanAsync()
    {
        var c = RequireClient();
        if (c == null || !BuildPattern(out var value, out var mask)) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _btnScan.Enabled = false;
        _btnCancel.Visible = true;
        _progress.Visible = true;
        _progress.Value = 0;
        _hits = new List<Hit>();
        _grid.RowCount = 0;
        _lastValue = value; _lastMask = mask;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long scanned = 0;
        try
        {
            var ranges = await RangesAsync(c);
            if (ranges.Count == 0) { App.Toast("Nothing to scan – is a game running?", ToastHost.Kind.Warning); return; }
            long total = ranges.Sum(r => (long)r.Size);
            var pat = new BytePattern(value, mask);
            int step = _aligned.Checked ? 4 : 1;
            const uint chunk = 0x10000;
            int overlap = value.Length - 1;

            foreach (var (start, size, name) in ranges)
            {
                for (uint off = 0; off < size && _hits.Count < MaxHits; off += chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    uint n = (uint)Math.Min(chunk + overlap, size - off);
                    var data = await c.GetMemoryAsync(start + off, n);
                    scanned += Math.Min(chunk, size - off);
                    if (data != null)
                    {
                        int limit = (int)Math.Min(chunk, n) - 1;
                        foreach (var i in pat.FindAll(data, 0, limit, MaxHits - _hits.Count, step == 4))
                        {
                            uint addr = start + off + (uint)i;
                            if (step == 4 && (addr & 3) != 0) continue;
                            _hits.Add(new Hit(addr, name, Preview(data, i)));
                        }
                    }
                    _progress.Value = (double)scanned / total;
                    _status.Text = $"{scanned / 1048576.0:0.0} / {total / 1048576.0:0.0} MB · {scanned / 1048576.0 / Math.Max(0.1, sw.Elapsed.TotalSeconds):0.0} MB/s · {_hits.Count} hits";
                    if ((off / chunk) % 16 == 0) _grid.RowCount = _hits.Count;
                }
            }
            _status.Text = $"{_hits.Count}{(_hits.Count >= MaxHits ? "+" : "")} hits in {scanned / 1048576.0:0.0} MB · {sw.Elapsed.TotalSeconds:0.0}s";
        }
        catch (OperationCanceledException) { _status.Text = $"Cancelled · {_hits.Count} hits so far"; }
        catch (Exception ex) { App.Toast($"Scan failed: {ex.Message}", ToastHost.Kind.Error); }
        finally
        {
            _grid.RowCount = _hits.Count;
            _grid.Invalidate();
            _btnScan.Enabled = true;
            _btnCancel.Visible = false;
            _progress.Visible = false;
        }
    }

    private async Task RecheckAsync()
    {
        var c = RequireClient();
        if (c == null || _hits.Count == 0) return;
        if (BuildPattern(out var v, out var m)) { _lastValue = v; _lastMask = m; }
        var keep = new List<Hit>();
        foreach (var h in _hits)
        {
            var d = await c.GetMemoryAsync(h.Address, (uint)_lastValue.Length);
            if (d != null && new BytePattern(_lastValue, _lastMask).MatchAt(d, 0)) keep.Add(h with { Preview = Preview(d, 0) });
        }
        App.Log($"🔎 Re-check: {keep.Count} of {_hits.Count} still match");
        _hits = keep;
        _grid.RowCount = 0;
        _grid.RowCount = _hits.Count;
        _status.Text = $"{_hits.Count} hits after re-check";
    }

    private static string Preview(byte[] d, int i)
    {
        int n = Math.Min(16, d.Length - i);
        var sb = new StringBuilder();
        for (int k = 0; k < n; k++) sb.Append(d[i + k].ToString("X2")).Append(' ');
        sb.Append(' ');
        for (int k = 0; k < n; k++) sb.Append(d[i + k] is >= 32 and < 127 ? (char)d[i + k] : '.');
        return sb.ToString();
    }
}
