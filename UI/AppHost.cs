using MemoryEditor.Runtime;

namespace MemoryEditor.UI;

/// <summary>Services the main window offers to feature pages.</summary>
public interface IAppHost
{
    XBDMClient? Client { get; }
    void Log(string message);
    void Toast(string message, ToastHost.Kind kind = ToastHost.Kind.Info);
    void ViewMemory(uint address);
    void AddWatch(uint address, string description);
    void InsertScript(string text);
    void OpenScript(string name, string content);
    void PushOffsets(IReadOnlyCollection<DiscoveredOffset> offsets);
    IEnumerable<(string Name, uint Address, string Category)> StaticOffsets { get; }
    void RuntimeOffsetsChanged();
    void SendToStaticAnalysis(string name, byte[] image, uint baseAddress);
    void DiscoverFromImage(ModuleImage module);
    void Navigate(string pageKey);
    /// <summary>Points the Memory Recorder at a region and starts recording it (switches to the recorder page).</summary>
    void MonitorRegion(uint start, uint size, string label);
    /// <summary>Opens the live disassembler (Runtime Tools) at an address.</summary>
    void Disassemble(uint address);
    /// <summary>Opens the pointer-chain resolver (Runtime Tools).</summary>
    void PointerChain(uint baseAddress, IEnumerable<uint> offsets);
    /// <summary>Plots a live value in the Value Graph tool (type: Int32, Float, Int16, Byte, …).</summary>
    void GraphValue(uint address, string type, string label);
    /// <summary>The application-wide freeze list (null while the window is being built).</summary>
    MemoryEditor.Runtime.Toolkit.FreezeService? Freezer { get; }
    /// <summary>Pulls every offset of the running game into the Game Library (same as its Pull button).</summary>
    void PullRunningGame();
    /// <summary>Opens Runtime Tools → Active Memory at an address.</summary>
    void OpenActiveMemory(uint address);
}

/// <summary>Small helpers shared by pages.</summary>
public static class Ui
{
    public static Label Title(string text) => new()
    {
        Text = text, AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, Tag = "keep", BackColor = Color.Transparent
    };

    public static Label Muted(string text) => new()
    {
        Text = text, AutoSize = true, Font = Theme.UI, ForeColor = Theme.TextMuted, Tag = "keep", BackColor = Color.Transparent
    };

    public static Label Glyph(string glyph, float size, Color color) => new()
    {
        Text = glyph, AutoSize = true, Font = Theme.Icon(size), ForeColor = color, Tag = "keep", BackColor = Color.Transparent
    };

    public static Panel Card(DockStyle dock, int height = 0, Padding? padding = null)
    {
        var p = new Panel { Dock = dock, BackColor = Theme.Surface, Padding = padding ?? new Padding(16), Tag = "keep" };
        if (height > 0) p.Height = height;
        p.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
        return p;
    }

    public static Panel Spacer(DockStyle dock, int size) => new() { Dock = dock, Height = size, Width = size, BackColor = Color.Transparent, Tag = "keep" };

    /// <summary>Thin progress bar that matches the theme.</summary>
    public sealed class ThinProgress : Control
    {
        private double _value;
        private bool _indeterminate;
        private readonly System.Windows.Forms.Timer _anim = new() { Interval = 30 };
        private int _phase;
        public ThinProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 4;
            Theme.Exclude(this);
            _anim.Tick += (_, _) => { _phase = (_phase + 6) % Math.Max(1, Width + 120); Invalidate(); };
        }
        public double Value { get => _value; set { _value = Math.Clamp(value, 0, 1); Invalidate(); } }
        public bool Indeterminate { get => _indeterminate; set { _indeterminate = value; if (value) _anim.Start(); else _anim.Stop(); Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Surface2);
            using var b = new SolidBrush(Theme.Accent);
            if (_indeterminate) e.Graphics.FillRectangle(b, _phase - 120, 0, 120, Height);
            else e.Graphics.FillRectangle(b, 0, 0, (float)(Width * _value), Height);
        }
        protected override void Dispose(bool disposing) { if (disposing) _anim.Dispose(); base.Dispose(disposing); }
    }
}
