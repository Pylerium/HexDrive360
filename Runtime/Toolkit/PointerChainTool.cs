using System.Globalization;
using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>
/// Multi-level pointer resolver: [[base] + o1] + o2 … → value. Each level is shown so broken links are obvious;
/// refreshes live and can read / write / watch / record / graph the final address.
/// </summary>
public sealed class PointerChainTool : ToolBase
{
    private readonly TextBox _base = T("", 200, "Base address or offset name");
    private readonly TextBox _offsets = T("", 300, "Offsets, e.g. 0x10, 0x4, 0x2C");
    private readonly CheckBox _derefLast = new() { Text = "Dereference last level", AutoSize = true, Margin = new Padding(0, 7, 10, 0) };
    private readonly ComboBox _type = C(90, Enum.GetNames<LiveType>());
    private readonly CheckBox _live = new() { Text = "Live", Checked = true, AutoSize = true, Margin = new Padding(0, 7, 10, 0) };
    private readonly TextBox _write = T("", 160, "New value");
    private readonly RichTextBox _out = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Font = Theme.Mono, BackColor = Theme.CodeBg, ForeColor = Theme.Text, DetectUrls = false };
    private readonly Label _final = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 15F), ForeColor = Theme.Text, BackColor = Color.Transparent, Tag = "keep", Margin = new Padding(0, 4, 0, 6) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private uint? _finalAddress;
    private bool _busy;

    public override string Key => "chain";
    public override string Title => "Pointer Chain";
    public override string Glyph => Theme.Glyph.Link;
    public override string Description => "Follow multi-level pointers live and edit the value at the end";

    public PointerChainTool(IToolHost host) : base(host)
    {
        Theme.Exclude(_out);
        _timer.Tick += async (_, _) => await ResolveAsync(false);
        _live.CheckedChanged += (_, _) => { if (_live.Checked && Visible) _timer.Start(); else _timer.Stop(); };
        foreach (var tb in new[] { _base, _offsets }) tb.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ResolveAsync(true); } };
        _write.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await WriteAsync(); } };

        var r1 = Row(L("Base"), _base, L("Offsets"), _offsets, _derefLast, L("Type"), _type, _live,
            B("Resolve", Theme.Glyph.Play, ButtonVariant.Primary, async (_, _) => await ResolveAsync(true)));
        var r2 = Row(_write, B("Write", Theme.Glyph.Save, ButtonVariant.Warning, async (_, _) => await WriteAsync()),
            B("Memory", Theme.Glyph.Memory, ButtonVariant.Neutral, (_, _) => { if (_finalAddress is { } a) Host.ActiveMemory(a); }),
            B("Watch", Theme.Glyph.Pin, ButtonVariant.Neutral, (_, _) => { if (_finalAddress is { } a) App.AddWatch(a, "Chain " + _base.Text); }),
            B("Record", Theme.Glyph.Record, ButtonVariant.Neutral, (_, _) => { if (_finalAddress is { } a) App.MonitorRegion(a & ~0xFu, 0x100, "pointer chain target"); }),
            B("Graph", Theme.Glyph.Analyze, ButtonVariant.Neutral, (_, _) => { if (_finalAddress is { } a) Host.Graph(a, CurrentType, "Chain " + _base.Text); }),
            B("Copy as script", Theme.Glyph.Code, ButtonVariant.Ghost, (_, _) => App.InsertScript(ScriptText())));
        var finalRow = Row(_final);

        Controls.Add(Card(_out, new Padding(12, 8, 8, 8)));
        Controls.Add(finalRow);
        Controls.Add(r2);
        Controls.Add(r1);
        _final.Text = "Enter a base address and offsets";
    }

    private LiveType CurrentType => LiveValue.Parse(_type.SelectedItem?.ToString() ?? "Int32");

    /// <summary>Called from other tools / pages.</summary>
    public void LoadChain(uint baseAddress, IEnumerable<uint> offsets)
    {
        _base.Text = $"0x{baseAddress:X8}";
        _offsets.Text = string.Join(", ", offsets.Select(o => $"0x{o:X}"));
        _ = ResolveAsync(true);
    }

    public override void OnShown() { if (_live.Checked) _timer.Start(); }
    public override void OnHidden() => _timer.Stop();

    private List<uint>? ParseOffsets()
    {
        var list = new List<uint>();
        foreach (var part in _offsets.Text.Split(new[] { ',', ';', ' ', '>' }, StringSplitOptions.RemoveEmptyEntries))
        {
            try { list.Add(ParseNumber(part.Trim('[', ']', '+'))); }
            catch { return null; }
        }
        return list;
    }

    private async Task ResolveAsync(bool user)
    {
        if (_busy) return;
        var c = user ? RequireClient() : Client;
        if (c == null) return;
        if (!TryAddress(_base.Text, out var addr)) { if (user) App.Toast("Enter a base address or an offset name", ToastHost.Kind.Warning); return; }
        var offs = ParseOffsets();
        if (offs == null) { if (user) App.Toast("Offsets must be numbers (0x10, 16, -0x8 …)", ToastHost.Kind.Warning); return; }

        _busy = true;
        try
        {
            var lines = new List<(string Text, Color Color)>();
            uint cur = addr;
            lines.Add(($"base                      0x{addr:X8}" + Sym(addr), Theme.Text));
            bool ok = true;
            for (int i = 0; i < offs.Count + (_derefLast.Checked ? 1 : 0) && ok; i++)
            {
                var p = await c.ReadUInt32Async(cur);
                if (p == null) { lines.Add(($"[0x{cur:X8}]               unreadable", Theme.Danger)); ok = false; break; }
                if (i < offs.Count)
                {
                    uint next = unchecked(p.Value + offs[i]);
                    bool valid = p.Value >= 0x80000000;
                    lines.Add(($"[0x{cur:X8}] → 0x{p.Value:X8} + 0x{offs[i]:X} = 0x{next:X8}" + Sym(next), valid ? Theme.Text : Theme.Warning));
                    if (!valid) { lines.Add(("   pointer is null / not in title memory – chain broken (object not spawned?)", Theme.Warning)); ok = false; }
                    cur = next;
                }
                else { lines.Add(($"[0x{cur:X8}] → 0x{p.Value:X8}", Theme.Text)); cur = p.Value; }
            }

            if (ok)
            {
                var t = CurrentType;
                var data = await c.GetMemoryAsync(cur, (uint)LiveValue.Size(t));
                _finalAddress = cur;
                _final.Text = data != null ? $"0x{cur:X8}  =  {LiveValue.Format(t, data)}" : $"0x{cur:X8}  =  unreadable";
                _final.ForeColor = data != null ? Theme.Success : Theme.Danger;
            }
            else
            {
                _finalAddress = null;
                _final.Text = "Chain broken";
                _final.ForeColor = Theme.Warning;
            }
            Render(lines);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        finally { _busy = false; }
    }

    private string Sym(uint a) => Host.Symbols.Describe(a) is { } s ? $"   ({s})" : "";

    private void Render(List<(string Text, Color Color)> lines)
    {
        // only repaint when the text changed (live mode ticks 4×/s)
        var text = string.Join("\n", lines.Select(l => l.Text));
        if (_out.Tag as string == text) return;
        _out.Tag = text;
        _out.Clear();
        foreach (var (t, col) in lines)
        {
            _out.SelectionStart = _out.TextLength;
            _out.SelectionColor = col;
            _out.AppendText(t + "\n");
        }
    }

    private async Task WriteAsync()
    {
        var c = RequireClient();
        if (c == null) return;
        await ResolveAsync(true);
        if (_finalAddress is not { } a) { App.Toast("Resolve a valid chain first", ToastHost.Kind.Warning); return; }
        byte[] bytes;
        try { bytes = LiveValue.Encode(CurrentType, _write.Text); }
        catch { App.Toast($"“{_write.Text}” is not a valid {CurrentType}", ToastHost.Kind.Warning); return; }
        if (await c.SetMemoryAsync(a, bytes)) { App.Log($"✅ Wrote {_write.Text} ({CurrentType}) to 0x{a:X8}"); await ResolveAsync(false); }
        else App.Toast("Write failed", ToastHost.Kind.Error);
    }

    private string ScriptText()
    {
        var offs = ParseOffsets() ?? new List<uint>();
        TryAddress(_base.Text, out var b);
        return $"// pointer chain {_base.Text} → {_offsets.Text}\nvar target = walk(0x{b:X8}{string.Concat(offs.Select(o => $", 0x{o:X}"))});\n";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
