using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>Plots up to four live values over time (health, ammo, coordinates, timers …).</summary>
public sealed class ValueGraphTool : ToolBase
{
    private sealed class Series
    {
        public string Label = "";
        public uint Address;
        public LiveType Type;
        public Color Color;
        public readonly double[] Ring = new double[Capacity];
        public readonly DateTime[] Times = new DateTime[Capacity];
        public int Count, Head;
        public double Last;
        public bool Unreadable;

        public void Add(double v, DateTime t)
        {
            Ring[Head] = v;
            Times[Head] = t;
            Head = (Head + 1) % Capacity;
            if (Count < Capacity) Count++;
            Last = v;
        }

        public double this[int i] => Ring[(Head - Count + i + Capacity) % Capacity];
        public DateTime TimeAt(int i) => Times[(Head - Count + i + Capacity) % Capacity];
    }

    private const int Capacity = 1200;
    private static readonly Color[] Palette = { Color.FromArgb(0x4C, 0x8D, 0xFF), Color.FromArgb(0x34, 0xB3, 0x78), Color.FromArgb(0xE8, 0xA2, 0x3C), Color.FromArgb(0x93, 0x74, 0xF0) };

    private readonly List<Series> _series = new();
    private readonly TextBox _addr = T("", 200, "Address or offset name");
    private readonly ComboBox _type = C(90, Enum.GetNames<LiveType>());
    private readonly NumericUpDown _interval = new() { Minimum = 30, Maximum = 5000, Value = 100, Increment = 10, Width = 70, Margin = new Padding(0, 4, 4, 0) };
    private readonly Chart _chart;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private readonly IconButton _pause;
    private bool _paused, _sampling;

    public override string Key => "graph";
    public override string Title => "Value Graph";
    public override string Glyph => Theme.Glyph.Analyze;
    public override string Description => "Plot up to four live values over time";

    public ValueGraphTool(IToolHost host) : base(host)
    {
        _chart = new Chart(this) { Dock = DockStyle.Fill };
        _timer.Tick += async (_, _) => await SampleAsync();
        _interval.ValueChanged += (_, _) => _timer.Interval = (int)_interval.Value;
        _addr.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddFromInputs(); } };
        _pause = B("Pause", Theme.Glyph.Stop, ButtonVariant.Neutral, (_, _) => { _paused = !_paused; _pause!.Text = _paused ? "Resume" : "Pause"; _pause.Glyph = _paused ? Theme.Glyph.Play : Theme.Glyph.Stop; });

        var top = Row(L("Address"), _addr, L("Type"), _type,
            B("Add series", Theme.Glyph.Add, ButtonVariant.Primary, (_, _) => AddFromInputs(), "Up to four series"),
            L("Every"), _interval, L("ms"), _pause,
            B("Clear", Theme.Glyph.Clear, ButtonVariant.Neutral, (_, _) => { foreach (var s in _series) { s.Count = 0; s.Head = 0; } _chart.Invalidate(); }),
            B("Remove all", Theme.Glyph.Delete, ButtonVariant.Ghost, (_, _) => { _series.Clear(); _chart.Invalidate(); }),
            B("Export CSV", Theme.Glyph.Upload, ButtonVariant.Ghost, (_, _) => ExportCsv()));
        Controls.Add(Card(_chart));
        Controls.Add(top);
    }

    public void Add(uint address, LiveType type, string label)
    {
        if (_series.Any(s => s.Address == address && s.Type == type)) return;
        if (_series.Count >= Palette.Length) _series.RemoveAt(0);
        var used = _series.Select(s => s.Color).ToHashSet();
        _series.Add(new Series { Address = address, Type = type, Label = label, Color = Palette.First(c => !used.Contains(c)) });
        _chart.Invalidate();
    }

    private void AddFromInputs()
    {
        var a = AddressOrToast(_addr);
        if (a == null) return;
        var label = Host.Symbols.NameAt(a.Value) ?? (_addr.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? $"0x{a:X8}" : _addr.Text.Trim());
        Add(a.Value, LiveValue.Parse(_type.SelectedItem?.ToString() ?? "Int32"), label);
    }

    public override void OnShown() => _timer.Start();
    public override void OnHidden() => _timer.Stop();

    private async Task SampleAsync()
    {
        if (_paused || _sampling || _series.Count == 0) return;
        var c = Client;
        if (c == null) return;
        _sampling = true;
        try
        {
            var now = DateTime.Now;
            foreach (var s in _series.ToList())
            {
                var data = await c.GetMemoryAsync(s.Address, (uint)LiveValue.Size(s.Type));
                s.Unreadable = data == null;
                if (data != null) s.Add(LiveValue.ToDouble(s.Type, data), now);
            }
            _chart.Invalidate();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        finally { _sampling = false; }
    }

    private void ExportCsv()
    {
        if (_series.Count == 0 || _series.All(s => s.Count == 0)) { App.Toast("Nothing recorded yet", ToastHost.Kind.Info); return; }
        using var sfd = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"values_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
        if (sfd.ShowDialog(FindForm()) != DialogResult.OK) return;
        var sb = new StringBuilder("time," + string.Join(",", _series.Select(s => $"\"{s.Label} (0x{s.Address:X8})\"")) + "\n");
        int n = _series.Max(s => s.Count);
        for (int i = 0; i < n; i++)
        {
            var refS = _series.First(s => s.Count == n);
            sb.Append(refS.TimeAt(i).ToString("HH:mm:ss.fff"));
            foreach (var s in _series)
            {
                int j = i - (n - s.Count);
                sb.Append(',').Append(j >= 0 ? s[j].ToString(CultureInfo.InvariantCulture) : "");
            }
            sb.Append('\n');
        }
        File.WriteAllText(sfd.FileName, sb.ToString());
        App.Toast($"Exported {n} samples", ToastHost.Kind.Success);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    private sealed class Chart : Control
    {
        private readonly ValueGraphTool _t;
        public Chart(ValueGraphTool t)
        {
            _t = t;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Theme.Exclude(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Input);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var series = _t._series;
            if (series.Count == 0)
            {
                TextRenderer.DrawText(g, "Add an address to start plotting.\nTip: double-click a value in other tools, or use Graph in Pointer Chain.", Theme.UI, ClientRectangle, Theme.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            var plot = new Rectangle(64, 16 + series.Count * 22, Width - 84, Height - 40 - series.Count * 22);
            if (plot.Width < 50 || plot.Height < 40) return;

            // legend with current / min / max / avg
            int y = 10;
            foreach (var s in series)
            {
                using (var b = new SolidBrush(s.Color)) g.FillRectangle(b, 14, y + 5, 12, 4);
                string stats = s.Count == 0 ? (s.Unreadable ? "unreadable" : "waiting…")
                    : $"now {Fmt(s.Last)}   min {Fmt(Enumerable.Range(0, s.Count).Min(i => s[i]))}   max {Fmt(Enumerable.Range(0, s.Count).Max(i => s[i]))}   avg {Fmt(Enumerable.Range(0, s.Count).Average(i => s[i]))}";
                TextRenderer.DrawText(g, $"{s.Label}  ·  0x{s.Address:X8} {s.Type}", Theme.UISemibold, new Point(32, y), Theme.Text);
                TextRenderer.DrawText(g, stats, Theme.UI, new Point(32 + TextRenderer.MeasureText($"{s.Label}  ·  0x{s.Address:X8} {s.Type}", Theme.UISemibold).Width + 12, y), Theme.TextMuted);
                y += 22;
            }

            // shared scale
            double min = double.MaxValue, max = double.MinValue;
            foreach (var s in series)
                for (int i = 0; i < s.Count; i++) { min = Math.Min(min, s[i]); max = Math.Max(max, s[i]); }
            if (min == double.MaxValue) { min = 0; max = 1; }
            if (Math.Abs(max - min) < 1e-9) { max += 1; min -= 1; }
            double pad = (max - min) * 0.08;
            min -= pad; max += pad;

            using var grid = new Pen(Theme.Border);
            for (int k = 0; k <= 4; k++)
            {
                int gy = plot.Bottom - plot.Height * k / 4;
                g.DrawLine(grid, plot.Left, gy, plot.Right, gy);
                TextRenderer.DrawText(g, Fmt(min + (max - min) * k / 4), Theme.UISmall, new Rectangle(0, gy - 8, plot.Left - 6, 16), Theme.TextDim, TextFormatFlags.Right);
            }

            foreach (var s in series)
            {
                if (s.Count < 2) continue;
                var pts = new PointF[s.Count];
                for (int i = 0; i < s.Count; i++)
                    pts[i] = new PointF(plot.Right - (float)(s.Count - 1 - i) * plot.Width / (Capacity - 1),
                                        (float)(plot.Bottom - (s[i] - min) / (max - min) * plot.Height));
                using var pen = new Pen(s.Color, 1.8f);
                g.DrawLines(pen, pts);
            }
            TextRenderer.DrawText(g, $"last {Capacity} samples · every {_t._timer.Interval} ms", Theme.UISmall, new Point(plot.Left, plot.Bottom + 6), Theme.TextDim);
        }

        private static string Fmt(double v) => Math.Abs(v) >= 1e6 || (v != 0 && Math.Abs(v) < 1e-3) ? v.ToString("0.###e0", CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
