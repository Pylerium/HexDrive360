using System.Text;
using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>Thread list with start routines resolved to symbols, per-thread suspend / resume and whole-game pause.</summary>
public sealed class ThreadsTool : ToolBase
{
    private sealed class Info
    {
        public uint Id;
        public string Name = "";
        public uint Start, StackBase, StackLimit, Tls;
        public int Priority, Suspend;
    }

    private readonly DataGridView _grid = Grid(("Thread", 90), ("Name", 180), ("Start", 100), ("Start symbol", 220), ("Priority", 70), ("Suspended", 80), ("Stack", 200), ("TLS", 100));
    private readonly CheckBox _live = new() { Text = "Live (2 s)", AutoSize = true, Margin = new Padding(0, 7, 10, 0) };
    private readonly Label _summary = L("");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private List<Info> _threads = new();
    private bool _busy;

    public override string Key => "threads";
    public override string Title => "Threads";
    public override string Glyph => Theme.Glyph.Cpu;
    public override string Description => "List, suspend and resume threads, or pause the whole game";

    public ThreadsTool(IToolHost host) : base(host)
    {
        _grid.VirtualMode = true;
        foreach (int i in new[] { 0, 2, 6, 7 }) _grid.Columns[i].DefaultCellStyle.Font = Theme.Mono;
        _grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex >= _threads.Count) return;
            var t = _threads[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => $"0x{t.Id:X}", 1 => t.Name, 2 => t.Start != 0 ? $"0x{t.Start:X8}" : "", 3 => t.Start != 0 ? Host.Symbols.Describe(t.Start) ?? "" : "",
                4 => t.Priority.ToString(), 5 => t.Suspend > 0 ? $"yes ({t.Suspend})" : "",
                6 => t.StackBase != 0 ? $"0x{t.StackLimit:X8}–0x{t.StackBase:X8}" : "", _ => t.Tls != 0 ? $"0x{t.Tls:X8}" : "",
            };
        };
        _grid.CellFormatting += (sender, e) =>
        {
            if (e.RowIndex < _threads.Count && e.CellStyle != null && _threads[e.RowIndex].Suspend > 0) e.CellStyle.ForeColor = Theme.Warning;
        };
        _grid.CellDoubleClick += (_, e) => { if (Sel() is { Start: > 0 } t) Host.Disassemble(t.Start); };
        _timer.Tick += async (_, _) => await LoadAsync(false);
        _live.CheckedChanged += (_, _) => { if (_live.Checked && Visible) _timer.Start(); else _timer.Stop(); };

        var top = Row(
            B("Refresh", Theme.Glyph.Refresh, ButtonVariant.Primary, async (_, _) => await LoadAsync(true)),
            B("Suspend", Theme.Glyph.Stop, ButtonVariant.Warning, async (_, _) => await ForSelectedAsync(true)),
            B("Resume", Theme.Glyph.Play, ButtonVariant.Success, async (_, _) => await ForSelectedAsync(false)),
            B("Pause game", Theme.Glyph.Stop, ButtonVariant.Danger, async (_, _) => await PauseAsync(true), "Halt every title thread (XBDM stop)"),
            B("Resume game", Theme.Glyph.Play, ButtonVariant.Success, async (_, _) => await PauseAsync(false), "Continue execution (XBDM go)"),
            B("Start routine", Theme.Glyph.Code, ButtonVariant.Ghost, (_, _) => { if (Sel() is { Start: > 0 } t) Host.Disassemble(t.Start); }),
            B("Stack", Theme.Glyph.Memory, ButtonVariant.Ghost, (_, _) => { if (Sel() is { StackLimit: > 0 } t) Host.ActiveMemory(t.StackLimit); }),
            _live, _summary);
        Controls.Add(Card(_grid));
        Controls.Add(top);
    }

    private Info? Sel() => _grid.CurrentCell?.RowIndex is int i && i >= 0 && i < _threads.Count ? _threads[i] : null;
    private List<Info> SelAll() => _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i >= 0 && i < _threads.Count).Select(i => _threads[i]).ToList();

    public override void OnShown() { _ = LoadAsync(false); if (_live.Checked) _timer.Start(); }
    public override void OnHidden() => _timer.Stop();

    private async Task LoadAsync(bool user)
    {
        var c = user ? RequireClient() : Client;
        if (c == null || _busy) return;
        _busy = true;
        try
        {
            var list = new List<Info>();
            foreach (var id in await c.GetThreadsAsync())
            {
                var d = await c.GetThreadInfoAsync(id);
                var t = new Info
                {
                    Id = id,
                    Start = XBDMClient.ParseUInt(d.GetValueOrDefault("start")),
                    StackBase = XBDMClient.ParseUInt(d.GetValueOrDefault("base")),
                    StackLimit = XBDMClient.ParseUInt(d.GetValueOrDefault("limit")),
                    Tls = XBDMClient.ParseUInt(d.GetValueOrDefault("tlsbase")),
                    Priority = unchecked((int)XBDMClient.ParseUInt(d.GetValueOrDefault("priority"))),
                    Suspend = (int)XBDMClient.ParseUInt(d.GetValueOrDefault("suspend")),
                };
                // thread names live in title memory (nameaddr / namelen)
                uint na = XBDMClient.ParseUInt(d.GetValueOrDefault("nameaddr")), nl = XBDMClient.ParseUInt(d.GetValueOrDefault("namelen"));
                if (na != 0 && nl is > 0 and < 128 && await c.GetMemoryAsync(na, nl) is { } nb) t.Name = Encoding.ASCII.GetString(nb).TrimEnd('\0');
                list.Add(t);
            }
            int keep = _grid.CurrentCell?.RowIndex ?? -1;
            _threads = list;
            if (_grid.RowCount != list.Count) { _grid.RowCount = 0; _grid.RowCount = list.Count; }
            _grid.Invalidate();
            if (keep >= 0 && keep < list.Count) _grid.CurrentCell = _grid.Rows[keep].Cells[0];
            _summary.Text = $"{list.Count} threads · {list.Count(t => t.Suspend > 0)} suspended";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        finally { _busy = false; }
    }

    private async Task ForSelectedAsync(bool suspend)
    {
        var c = RequireClient();
        var sel = SelAll();
        if (c == null || sel.Count == 0) return;
        int ok = 0;
        foreach (var t in sel) if (suspend ? await c.SuspendThreadAsync(t.Id) : await c.ResumeThreadAsync(t.Id)) ok++;
        App.Log($"{(suspend ? "⏸" : "▶")} {(suspend ? "Suspended" : "Resumed")} {ok} thread(s)");
        await LoadAsync(true);
    }

    private async Task PauseAsync(bool stop)
    {
        var c = RequireClient();
        if (c == null) return;
        bool ok = stop ? await c.StopExecutionAsync() : await c.GoAsync();
        App.Toast(ok ? (stop ? "Game paused – press Resume game to continue" : "Game resumed") : "The console refused the request", ok ? ToastHost.Kind.Success : ToastHost.Kind.Warning);
        await LoadAsync(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
