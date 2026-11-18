using System.Text.RegularExpressions;
using MemoryEditor.Library;
using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>
/// Turns every "X On" / "X Off" byte pair of the loaded offsets file into a live switch (plus single patches, which are
/// restored to the bytes captured before the first write). States are read back from the console.
/// </summary>
public sealed class ModTogglesTool : ToolBase
{
    private sealed class Mod
    {
        public string Name = "";
        public string Category = "";
        public uint Address;
        public byte[] On = Array.Empty<byte>();
        public byte[]? Off;            // null → single patch; original bytes captured on first enable
        public string State = "?";     // ON / OFF / modified / ?
    }

    private static readonly Regex PairRx = new(@"^(.*?)[\s_\-]*\(?\b(on|off|enable|disable|enabled|disabled)\b\)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly DataGridView _grid = Grid(("Mod", 260), ("Category", 160), ("Address", 100), ("State", 90), ("On bytes", 130), ("Off bytes", 130));
    private readonly TextBox _filter = T("", 260, "Filter mods", mono: false);
    private readonly CheckBox _auto = new() { Text = "Live state (1 s)", Checked = true, AutoSize = true, Margin = new Padding(6, 7, 10, 0) };
    private readonly Label _summary = L("");
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Dictionary<uint, byte[]> _originals = new();
    private List<Mod> _all = new();
    private List<Mod> _view = new();
    private int _dbSignature = -1;
    private bool _reading;

    public override string Key => "mods";
    public override string Title => "Mod Toggles";
    public override string Glyph => Theme.Glyph.Bolt;
    public override string Description => "One-click switches for every On/Off patch in the loaded offsets";

    public ModTogglesTool(IToolHost host) : base(host)
    {
        _grid.VirtualMode = true;
        _grid.CellValueNeeded += (_, e) =>
        {
            if ((uint)e.RowIndex >= (uint)_view.Count) return;
            var m = _view[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => m.Name, 1 => m.Category, 2 => $"0x{m.Address:X8}", 3 => m.State,
                4 => OffsetTextFormat.NormalizeBytes(Convert.ToHexString(m.On)),
                _ => m.Off != null ? OffsetTextFormat.NormalizeBytes(Convert.ToHexString(m.Off)) : (_originals.TryGetValue(m.Address, out var o) ? OffsetTextFormat.NormalizeBytes(Convert.ToHexString(o)) + " (orig)" : "original"),
            };
        };
        _grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex != 3 || (uint)e.RowIndex >= (uint)_view.Count || e.CellStyle == null) return;
            e.CellStyle.ForeColor = _view[e.RowIndex].State switch { "ON" => Theme.Success, "OFF" => Theme.TextMuted, "modified" => Theme.Warning, _ => Theme.TextDim };
            e.CellStyle.Font = Theme.UISemibold;
        };
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await ToggleAsync(SelectedMods()); };
        _grid.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Space) { e.SuppressKeyPress = true; await ToggleAsync(SelectedMods()); } };
        _filter.TextChanged += (_, _) => ApplyFilter();
        _timer.Tick += async (_, _) => await ReadStatesAsync();
        _auto.CheckedChanged += (_, _) => { if (_auto.Checked && Visible) _timer.Start(); else _timer.Stop(); };

        var top = Row(
            B("Toggle", Theme.Glyph.Bolt, ButtonVariant.Primary, async (_, _) => await ToggleAsync(SelectedMods()), "Toggle the selected mods (Space / double-click)"),
            B("On", Theme.Glyph.Check, ButtonVariant.Success, async (_, _) => await SetAsync(SelectedMods(), true)),
            B("Off", Theme.Glyph.Close, ButtonVariant.Neutral, async (_, _) => await SetAsync(SelectedMods(), false)),
            B("All off", Theme.Glyph.Refresh, ButtonVariant.Danger, async (_, _) => await SetAsync(_all, false), "Restore every mod to its off / original bytes"),
            B("Read states", Theme.Glyph.Refresh, ButtonVariant.Ghost, async (_, _) => await ReadStatesAsync(true)),
            B("Disassemble", Theme.Glyph.Code, ButtonVariant.Ghost, (_, _) => { if (SelectedMods().FirstOrDefault() is { } m) Host.Disassemble(m.Address); }),
            _filter, _auto, _summary);
        Controls.Add(Card(_grid));
        Controls.Add(top);
    }

    private List<Mod> SelectedMods() =>
        _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i >= 0 && i < _view.Count).Select(i => _view[i]).ToList();

    public override void OnShown()
    {
        Rebuild();
        if (_auto.Checked) _timer.Start();
        _ = ReadStatesAsync();
    }

    public override void OnHidden() => _timer.Stop();

    public override void OnConnectionChanged(bool connected)
    {
        if (!connected) { foreach (var m in _all) m.State = "?"; _originals.Clear(); _grid.Invalidate(); }
    }

    /// <summary>Builds switches from the offset database (pairs share an address; single patches stand alone).</summary>
    private void Rebuild()
    {
        var db = Host.Database;
        int sig = db.Count ^ (db.Count > 0 ? db[0].Name.GetHashCode() : 0);
        if (sig == _dbSignature) return;
        _dbSignature = sig;

        var patches = db.Where(e => e.BaseAddress >= 0x80000000 && OffsetTextFormat.ParseBytes(e.ByteValues) != null).ToList();
        var mods = new List<Mod>();
        foreach (var grp in patches.GroupBy(e => (e.BaseAddress + (e.Offset ?? 0), PairRx.Match(e.Name) is { Success: true } m ? m.Groups[1].Value.Trim().ToLowerInvariant() : e.Name.ToLowerInvariant())))
        {
            var items = grp.ToList();
            var on = items.FirstOrDefault(e => PairRx.Match(e.Name) is { Success: true } m && m.Groups[2].Value.ToLowerInvariant() is "on" or "enable" or "enabled");
            var off = items.FirstOrDefault(e => PairRx.Match(e.Name) is { Success: true } m && m.Groups[2].Value.ToLowerInvariant() is "off" or "disable" or "disabled");
            var first = on ?? items[0];
            var baseName = PairRx.Match(first.Name) is { Success: true } mm ? mm.Groups[1].Value.Trim() : first.Name;
            mods.Add(new Mod
            {
                Name = baseName.Length > 0 ? baseName : first.Name,
                Category = first.Category,
                Address = grp.Key.Item1,
                On = OffsetTextFormat.ParseBytes(first.ByteValues)!,
                Off = off != null && off != first ? OffsetTextFormat.ParseBytes(off.ByteValues) : null,
            });
        }
        _all = mods.OrderBy(m => m.Category).ThenBy(m => m.Name).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var f = _filter.Text.Trim();
        _view = f.Length == 0 ? _all : _all.Where(m => m.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || m.Category.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        _grid.RowCount = 0;
        _grid.RowCount = _view.Count;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int on = _all.Count(m => m.State == "ON");
        _summary.Text = _all.Count == 0
            ? "No “| Bytes:” patches in the loaded offsets – select a game in the Game Library"
            : $"{_all.Count} mods · {on} on";
    }

    private async Task ReadStatesAsync(bool user = false)
    {
        var c = user ? RequireClient() : Client;
        if (c == null || _reading || _view.Count == 0) return;
        _reading = true;
        try
        {
            // read only what is on screen (plus the selection) – keeps polling cheap on large files
            int first = Math.Max(0, _grid.FirstDisplayedScrollingRowIndex);
            var visible = _view.Skip(first).Take(Math.Max(1, _grid.DisplayedRowCount(true)) + 2).Union(SelectedMods()).ToList();
            foreach (var m in visible)
            {
                int len = Math.Max(m.On.Length, m.Off?.Length ?? 0);
                var cur = await c.GetMemoryAsync(m.Address, (uint)len);
                if (cur == null) { m.State = "?"; continue; }
                if (cur.AsSpan(0, m.On.Length).SequenceEqual(m.On)) m.State = "ON";
                else if (m.Off != null && cur.AsSpan(0, m.Off.Length).SequenceEqual(m.Off)) m.State = "OFF";
                else if (m.Off == null && (!_originals.TryGetValue(m.Address, out var o) || cur.AsSpan(0, o.Length).SequenceEqual(o))) m.State = "OFF";
                else m.State = "modified";
            }
            _grid.Invalidate();
            UpdateSummary();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        finally { _reading = false; }
    }

    private async Task ToggleAsync(List<Mod> mods)
    {
        foreach (var m in mods) await SetAsync(new List<Mod> { m }, m.State != "ON", quiet: mods.Count > 1);
        if (mods.Count > 1) App.Toast($"Toggled {mods.Count} mods", ToastHost.Kind.Success);
    }

    private async Task SetAsync(List<Mod> mods, bool on, bool quiet = false)
    {
        var c = RequireClient();
        if (c == null || mods.Count == 0) return;
        int ok = 0, skipped = 0;
        foreach (var m in mods)
        {
            byte[]? bytes;
            if (on)
            {
                if (m.Off == null && !_originals.ContainsKey(m.Address))
                {
                    var orig = await c.GetMemoryAsync(m.Address, (uint)m.On.Length);
                    if (orig != null && !orig.AsSpan().SequenceEqual(m.On)) _originals[m.Address] = orig;
                }
                bytes = m.On;
            }
            else bytes = m.Off ?? _originals.GetValueOrDefault(m.Address);
            if (bytes == null) { skipped++; continue; }
            if (await c.SetMemoryAsync(m.Address, bytes)) { ok++; m.State = on ? "ON" : "OFF"; }
            else App.Log($"❌ Could not write {m.Name} @ 0x{m.Address:X8}");
        }
        _grid.Invalidate();
        UpdateSummary();
        if (!quiet)
        {
            if (mods.Count == 1 && ok == 1) App.Log($"{(on ? "🟢" : "⚪")} {mods[0].Name} {(on ? "ON" : "OFF")}");
            else App.Toast($"{(on ? "Enabled" : "Disabled")} {ok} of {mods.Count}" + (skipped > 0 ? $" ({skipped} single patches never enabled – original unknown)" : ""), ok > 0 ? ToastHost.Kind.Success : ToastHost.Kind.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
