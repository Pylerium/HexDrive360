using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>
/// Live PowerPC disassembly of console memory with symbol names, resolved lis/addi constants, branch following with
/// history, and quick patches (nop, blr, return 0/1, custom word) that can always be restored.
/// </summary>
public sealed class DisassemblerTool : ToolBase
{
    private const int Words = 256;

    private readonly TextBox _addr = T("", 200, "Address or offset name");
    private readonly DataGridView _grid = Grid(("Address", 110), ("Bytes", 100), ("Instruction", 300), ("Comment", 300));
    private readonly CheckBox _live = new() { Text = "Live (1 s)", AutoSize = true, Margin = new Padding(0, 7, 10, 0) };
    private readonly Label _info = L("");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Stack<uint> _back = new(), _fwd = new();
    private readonly Dictionary<uint, uint> _originals = new();   // patched address → original word
    private uint _start;
    private uint[] _words = Array.Empty<uint>();
    private string[] _comments = Array.Empty<string>();
    private bool _loading;

    public override string Key => "disasm";
    public override string Title => "Disassembler";
    public override string Glyph => Theme.Glyph.Code;
    public override string Description => "Live PowerPC disassembly with branch following and quick patches";

    public DisassemblerTool(IToolHost host) : base(host)
    {
        _grid.VirtualMode = true;
        _grid.MultiSelect = true;
        _grid.Columns[0].DefaultCellStyle.Font = Theme.Mono;
        _grid.Columns[1].DefaultCellStyle.Font = Theme.MonoSmall;
        _grid.Columns[2].DefaultCellStyle.Font = Theme.Mono;
        _grid.CellValueNeeded += CellValue;
        _grid.CellFormatting += (sender, e) =>
        {
            if (e.RowIndex >= _words.Length || e.CellStyle == null) return;
            uint pc = _start + (uint)e.RowIndex * 4;
            uint w = _words[e.RowIndex];
            if (_originals.ContainsKey(pc)) e.CellStyle.ForeColor = Theme.Warning;
            else if (e.ColumnIndex == 2 && IsBranch(w, out _)) e.CellStyle.ForeColor = Color.FromArgb(0xDC, 0xDC, 0xAA);
            else if (e.ColumnIndex == 2 && w == 0x4E800020) e.CellStyle.ForeColor = Theme.Purple;
            else if (e.ColumnIndex == 3) e.CellStyle.ForeColor = Host.Symbols.NameAt(pc) != null ? Theme.Accent : Theme.TextMuted;
        };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Follow(e.RowIndex); };
        _grid.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Follow(_grid.CurrentCell?.RowIndex ?? -1); }
            else if (e.KeyCode == Keys.Back) { e.SuppressKeyPress = true; await BackAsync(); }
            else if (e.KeyCode == Keys.N && e.Control) { e.SuppressKeyPress = true; await PatchSelectedAsync(_ => 0x60000000, "nop"); }
        };
        _grid.ContextMenuStrip = BuildMenu();
        _addr.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await GoAsync(); } };
        _timer.Tick += async (_, _) => await LoadAsync(_start, false);
        _live.CheckedChanged += (_, _) => { if (_live.Checked && Visible) _timer.Start(); else _timer.Stop(); };

        var top = Row(L("Address"), _addr,
            B("Go", Theme.Glyph.Play, ButtonVariant.Primary, async (_, _) => await GoAsync()),
            B("", Theme.Glyph.ChevronUp, ButtonVariant.Ghost, async (_, _) => await LoadAsync(_start - Words * 2, true), "Scroll up"),
            B("", Theme.Glyph.ChevronDown, ButtonVariant.Ghost, async (_, _) => await LoadAsync(_start + Words * 2, true), "Scroll down"),
            B("Back", Theme.Glyph.Refresh, ButtonVariant.Ghost, async (_, _) => await BackAsync(), "Back (Backspace)"),
            B("Forward", Theme.Glyph.Refresh, ButtonVariant.Ghost, async (_, _) => await ForwardAsync()),
            _live, _info);
        var patches = Row(
            B("NOP", Theme.Glyph.Close, ButtonVariant.Warning, async (_, _) => await PatchSelectedAsync(_ => 0x60000000, "nop"), "Replace with nop (Ctrl+N)"),
            B("Return", Theme.Glyph.Upload, ButtonVariant.Warning, async (_, _) => await PatchSelectedAsync(_ => 0x4E800020, "blr"), "blr – return immediately"),
            B("Return 0", Theme.Glyph.Upload, ButtonVariant.Warning, async (_, _) => await PatchReturnAsync(0), "li r3,0 ; blr at the selected instruction"),
            B("Return 1", Theme.Glyph.Upload, ButtonVariant.Warning, async (_, _) => await PatchReturnAsync(1), "li r3,1 ; blr at the selected instruction"),
            B("Write word…", Theme.Glyph.Save, ButtonVariant.Neutral, async (_, _) => await WriteWordAsync()),
            B("Restore", Theme.Glyph.Refresh, ButtonVariant.Success, async (_, _) => await RestoreAsync(false), "Restore the original instructions of the selection"),
            B("Restore all", Theme.Glyph.Refresh, ButtonVariant.Ghost, async (_, _) => await RestoreAsync(true)),
            B("Copy", Theme.Glyph.Copy, ButtonVariant.Ghost, (_, _) => CopySelection()));
        Controls.Add(Card(_grid));
        Controls.Add(patches);
        Controls.Add(top);
        _info.Text = "Enter an address, an offset name, or open from another tool";
    }

    public override void OnShown() { if (_live.Checked) _timer.Start(); }
    public override void OnHidden() => _timer.Stop();

    public async Task ShowAsync(uint address)
    {
        if (_words.Length > 0) { _back.Push(_start + (uint)Math.Max(0, _grid.CurrentCell?.RowIndex ?? 0) * 4); _fwd.Clear(); }
        await LoadAsync(Math.Max(0x80000000, (address & ~3u) - 0x40), true, address);
    }

    private async Task GoAsync()
    {
        var a = AddressOrToast(_addr);
        if (a != null) await ShowAsync(a.Value);
    }

    private async Task BackAsync()
    {
        if (_back.Count == 0) return;
        _fwd.Push(_start + (uint)Math.Max(0, _grid.CurrentCell?.RowIndex ?? 0) * 4);
        var a = _back.Pop();
        await LoadAsync(Math.Max(0x80000000, a - 0x40), true, a);
    }

    private async Task ForwardAsync()
    {
        if (_fwd.Count == 0) return;
        _back.Push(_start + (uint)Math.Max(0, _grid.CurrentCell?.RowIndex ?? 0) * 4);
        var a = _fwd.Pop();
        await LoadAsync(Math.Max(0x80000000, a - 0x40), true, a);
    }

    private async Task LoadAsync(uint start, bool user, uint? select = null)
    {
        if (_loading) return;
        var c = user ? RequireClient() : Client;
        if (c == null) return;
        _loading = true;
        try
        {
            start &= ~3u;
            var data = await c.GetMemoryAsync(start, Words * 4);
            if (data == null) { if (user) App.Toast($"0x{start:X8} is not readable", ToastHost.Kind.Warning); return; }
            var words = new uint[Words];
            for (int i = 0; i < Words; i++) words[i] = (uint)(data[i * 4] << 24 | data[i * 4 + 1] << 16 | data[i * 4 + 2] << 8 | data[i * 4 + 3]);
            bool same = start == _start && words.AsSpan().SequenceEqual(_words);
            _start = start;
            _words = words;
            if (!same) _comments = BuildComments(start, words);
            _addr.Text = $"0x{(select ?? start):X8}";
            _info.Text = (Host.Symbols.Describe(select ?? start) is { } s ? s + "  ·  " : "") + $"0x{start:X8} – 0x{start + Words * 4:X8}" + (_originals.Count > 0 ? $"  ·  {_originals.Count} patched" : "");
            if (_grid.RowCount != Words) _grid.RowCount = Words;
            _grid.Invalidate();
            if (select is { } sel && sel >= start && sel < start + Words * 4)
            {
                int row = (int)((sel - start) / 4);
                _grid.ClearSelection();
                _grid.CurrentCell = _grid.Rows[row].Cells[2];
                _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, row - 6);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        finally { _loading = false; }
    }

    /// <summary>Symbols, branch targets and constants materialised by lis + addi/ori/load pairs.</summary>
    private string[] BuildComments(uint start, uint[] words)
    {
        var res = new string[words.Length];
        var lisHi = new uint?[32];
        for (int i = 0; i < words.Length; i++)
        {
            uint pc = start + (uint)i * 4, w = words[i];
            uint op = w >> 26, rd = (w >> 21) & 31, ra = (w >> 16) & 31;
            var parts = new List<string>();
            if (Host.Symbols.NameAt(pc) is { } here) parts.Add($"◆ {here}");
            if (IsBranch(w, out _) && BranchTarget(pc, w) is var target)
                parts.Add($"→ 0x{target:X8}" + (Host.Symbols.Describe(target) is { } s ? $" {s}" : ""));
            else if (op == 15 && ra == 0) lisHi[rd] = (w & 0xFFFF) << 16;
            else if ((op is 14 or 32 or 34 or 36 or 38 or 40 or 42 or 44 or 48 or 50 or 52 or 54 or 24) && lisHi[op == 24 ? rd : ra] is { } hi)
            {
                uint v = op == 24 ? hi | (w & 0xFFFF) : unchecked(hi + (uint)(short)(w & 0xFFFF));
                parts.Add($"= 0x{v:X8}" + (Host.Symbols.Describe(v) is { } s2 ? $" {s2}" : ""));
                if (op is 14 or 24) lisHi[op == 24 ? ra : rd] = null;
            }
            if (w == 0x4E800020) Array.Clear(lisHi);
            res[i] = string.Join("   ", parts);
        }
        return res;
    }

    private void CellValue(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex >= _words.Length) return;
        uint pc = _start + (uint)e.RowIndex * 4, w = _words[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => $"0x{pc:X8}",
            1 => $"{w >> 24:X2} {(w >> 16) & 0xFF:X2} {(w >> 8) & 0xFF:X2} {w & 0xFF:X2}",
            2 => Ppc.Disassemble(w, pc),
            _ => (_originals.TryGetValue(pc, out var o) ? $"patched (was {Ppc.Disassemble(o, pc)})   " : "") + (e.RowIndex < _comments.Length ? _comments[e.RowIndex] : ""),
        };
    }

    private static bool IsBranch(uint w, out uint op)
    {
        op = w >> 26;
        return op is 16 or 18; // bc / b, bl (blr/bctr are op 19 and have no static target)
    }

    private uint BranchTarget(uint pc, uint w)
    {
        uint op = w >> 26;
        bool abs = (w & 2) != 0;
        int disp;
        if (op == 18)
        {
            disp = (int)(w & 0x03FFFFFC);
            if ((disp & 0x02000000) != 0) disp |= unchecked((int)0xFC000000);
        }
        else disp = (short)(w & 0xFFFC);
        return abs ? (uint)disp : unchecked(pc + (uint)disp);
    }

    private void Follow(int row)
    {
        if (row < 0 || row >= _words.Length) return;
        uint pc = _start + (uint)row * 4, w = _words[row];
        if (IsBranch(w, out _)) { _ = ShowAsync(BranchTarget(pc, w)); return; }
        // follow a materialised constant (lis/addi) into the memory viewer
        if (_comments[row].Contains("= 0x") && TryAddress(_comments[row][(_comments[row].IndexOf("= 0x") + 2)..].Split(' ')[0], out var a)) Host.ActiveMemory(a);
    }

    private List<uint> SelectedPcs() =>
        _grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.RowIndex).Distinct().Where(r => r >= 0 && r < _words.Length)
             .OrderBy(r => r).Select(r => _start + (uint)r * 4).ToList();

    private async Task<bool> WriteWordAsync(XBDMClient c, uint pc, uint w)
    {
        int row = (int)((pc - _start) / 4);
        uint before = row >= 0 && row < _words.Length ? _words[row] : (await c.ReadUInt32Async(pc) ?? 0);
        _originals.TryAdd(pc, before);
        var ok = await c.SetMemoryAsync(pc, new[] { (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w });
        if (ok && _originals[pc] == w) _originals.Remove(pc);
        return ok;
    }

    private async Task PatchSelectedAsync(Func<uint, uint> word, string what)
    {
        var c = RequireClient();
        var pcs = SelectedPcs();
        if (c == null || pcs.Count == 0) return;
        int ok = 0;
        foreach (var pc in pcs) if (await WriteWordAsync(c, pc, word(pc))) ok++;
        App.Log($"🩹 {what} × {ok} at 0x{pcs[0]:X8}{(pcs.Count > 1 ? $" (+{pcs.Count - 1})" : "")}");
        await LoadAsync(_start, true, pcs[0]);
    }

    private async Task PatchReturnAsync(int value)
    {
        var c = RequireClient();
        var pcs = SelectedPcs();
        if (c == null || pcs.Count == 0) return;
        uint pc = pcs[0];
        await WriteWordAsync(c, pc, 0x38600000 | (uint)(value & 0xFFFF)); // li r3,value
        await WriteWordAsync(c, pc + 4, 0x4E800020);                      // blr
        App.Log($"🩹 return {value} at 0x{pc:X8}");
        await LoadAsync(_start, true, pc);
    }

    private async Task WriteWordAsync()
    {
        var c = RequireClient();
        var pcs = SelectedPcs();
        if (c == null || pcs.Count == 0) return;
        int row = (int)((pcs[0] - _start) / 4);
        var s = Prompt.Show(FindForm(), $"Write at 0x{pcs[0]:X8}", "Instruction word (hex), e.g. 60000000", $"{_words[row]:X8}");
        if (s == null) return;
        if (!uint.TryParse(s.Trim().Replace("0x", "").Replace(" ", ""), System.Globalization.NumberStyles.HexNumber, null, out var w)) { App.Toast("Enter 8 hex digits", ToastHost.Kind.Warning); return; }
        await WriteWordAsync(c, pcs[0], w);
        await LoadAsync(_start, true, pcs[0]);
    }

    private async Task RestoreAsync(bool all)
    {
        var c = RequireClient();
        if (c == null) return;
        var pcs = all ? _originals.Keys.ToList() : SelectedPcs().Where(_originals.ContainsKey).ToList();
        if (pcs.Count == 0) { App.Toast(all ? "Nothing was patched here" : "The selection is not patched", ToastHost.Kind.Info); return; }
        int ok = 0;
        foreach (var pc in pcs)
        {
            uint o = _originals[pc];
            if (await c.SetMemoryAsync(pc, new[] { (byte)(o >> 24), (byte)(o >> 16), (byte)(o >> 8), (byte)o })) { _originals.Remove(pc); ok++; }
        }
        App.Toast($"Restored {ok} instruction(s)", ToastHost.Kind.Success);
        await LoadAsync(_start, true);
    }

    private void CopySelection()
    {
        var pcs = SelectedPcs();
        if (pcs.Count == 0) return;
        var lines = pcs.Select(pc => { int r = (int)((pc - _start) / 4); return $"0x{pc:X8}  {_words[r]:X8}  {Ppc.Disassemble(_words[r], pc),-34} {_comments[r]}".TrimEnd(); });
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Follow branch / constant", null, (_, _) => Follow(_grid.CurrentCell?.RowIndex ?? -1));
        m.Items.Add("View in memory", null, (_, _) => { if (SelectedPcs().FirstOrDefault() is var pc and > 0) Host.ActiveMemory(pc); });
        m.Items.Add("Record this function region (1 KB)", null, (_, _) => { if (SelectedPcs().FirstOrDefault() is var pc and > 0) App.MonitorRegion(pc & ~0xFu, 0x400, $"code @ 0x{pc:X8}"); });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("NOP", null, async (_, _) => await PatchSelectedAsync(_ => 0x60000000, "nop"));
        m.Items.Add("Return (blr)", null, async (_, _) => await PatchSelectedAsync(_ => 0x4E800020, "blr"));
        m.Items.Add("Restore original", null, async (_, _) => await RestoreAsync(false));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Copy", null, (_, _) => CopySelection());
        Scripting.ScriptIde.StyleMenu(m);
        return m;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
