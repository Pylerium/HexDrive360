using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>
/// Owner-drawn live hex editor. Shows <see cref="Rows"/> × 16 bytes starting at <see cref="BaseAddress"/>; bytes that
/// changed since the previous refresh flash and fade, bytes typed by the user are written through
/// <see cref="ByteEdited"/>, known offsets are underlined and frozen bytes are tinted.
/// </summary>
public sealed class HexView : Control
{
    private const int Cols = 16;
    private byte[] _data = Array.Empty<byte>();
    private bool[] _readable = Array.Empty<bool>();
    private long[] _changedAt = Array.Empty<long>();
    private uint _base = 0x82000000;
    private uint _cursor = 0x82000000;
    private uint _selAnchor = 0x82000000;
    private bool _highNibble = true;
    private bool _asciiFocus;
    private int _charW, _rowH;
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 60 };
    private readonly ToolTip _tip = new() { InitialDelay = 250 };
    private string? _lastTip;

    public Func<uint, string?>? SymbolAt { get; set; }     // exact symbol at an address (underline + tooltip)
    public Func<uint, bool>? IsFrozen { get; set; }
    public Func<uint, string?>? Describe { get; set; }     // nearest symbol, for the address column tooltip

    /// <summary>Raised when the visible range changes (scroll / resize / navigation).</summary>
    public event Action? ViewChanged;
    public event Action? CaretMoved;
    /// <summary>The user typed a byte. The view updates optimistically; the handler writes it to the console.</summary>
    public event Action<uint, byte>? ByteEdited;

    public HexView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Font = Theme.Mono;
        BackColor = Theme.Input;
        Theme.Exclude(this);
        _fade.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            if (_changedAt.All(t => now - t > 1500)) _fade.Stop();
            Invalidate();
        };
        Measure();
    }

    public uint BaseAddress => _base;
    public uint CursorAddress => _cursor;
    public uint SelectionStart => Math.Min(_cursor, _selAnchor);
    public uint SelectionLength => (uint)Math.Abs((long)_cursor - _selAnchor) + 1;
    public int Rows => Math.Max(1, (ClientSize.Height - 26) / Math.Max(1, _rowH));
    public int ViewLength => Rows * Cols;

    public byte? ByteAt(uint address)
    {
        var data = _data;
        var readable = _readable;
        long i = (long)address - _base;
        return i >= 0 && i < data.Length && i < readable.Length && readable[i] ? data[i] : null;
    }

    /// <summary>Copies up to <paramref name="count"/> readable bytes at <paramref name="address"/> from the current view.</summary>
    public byte[]? Slice(uint address, int count)
    {
        // snapshot: a refresh swaps the data and readable arrays separately (their lengths can differ for a moment)
        var data = _data;
        var readable = _readable;
        long i = (long)address - _base;
        if (count <= 0 || i < 0 || i + count > data.Length || i + count > readable.Length) return null;
        for (int k = 0; k < count; k++) if (!readable[i + k]) return null;
        return data.AsSpan((int)i, count).ToArray();
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Measure(); }

    private void Measure()
    {
        var sz = TextRenderer.MeasureText("0000000000", Font, Size.Empty, TextFormatFlags.NoPadding);
        _charW = Math.Max(6, sz.Width / 10);
        _rowH = sz.Height + 4;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); ViewChanged?.Invoke(); }

    public void GoTo(uint address, bool select = true)
    {
        uint row = address & ~0xFu;
        if (address < _base || address >= _base + ViewLength) _base = row >= 0x40 ? row - 0x40 : row;
        if (select) { _cursor = _selAnchor = address; _highNibble = true; CaretMoved?.Invoke(); }
        ResetData();
        ViewChanged?.Invoke();
        Invalidate();
    }

    public void Scroll(int rows)
    {
        long nb = (long)_base + rows * Cols;
        _base = (uint)Math.Clamp(nb, 0, 0xFFFFFFF0L);
        ResetData();
        ViewChanged?.Invoke();
        Invalidate();
    }

    private void ResetData()
    {
        _data = Array.Empty<byte>();
        _readable = Array.Empty<bool>();
        _changedAt = Array.Empty<long>();
    }

    /// <summary>Supplies fresh memory for the view; <paramref name="data"/> null = unreadable range.</summary>
    public List<(uint Address, byte Old, byte New)> SetData(uint address, byte[]? data, int length)
    {
        var changes = new List<(uint, byte, byte)>();
        if (address != _base) return changes; // stale read for an old view
        long now = Environment.TickCount64;
        // a partial read (a later 64 KB chunk failed) is shorter than the view: pad it and mark only what was read
        var nd = new byte[length];
        var nr = new bool[length];
        if (data != null)
        {
            int n = Math.Min(data.Length, length);
            Buffer.BlockCopy(data, 0, nd, 0, n);
            Array.Fill(nr, true, 0, n);
        }
        var nc = new long[length];
        for (int i = 0; i < length; i++)
        {
            if (i < _data.Length && _readable[i] && nr[i] && _data[i] != nd[i])
            {
                nc[i] = now;
                changes.Add((address + (uint)i, _data[i], nd[i]));
            }
            else nc[i] = i < _changedAt.Length ? _changedAt[i] : 0;
        }
        _data = nd;
        _readable = nr;
        _changedAt = nc;
        if (changes.Count > 0) _fade.Start();
        Invalidate();
        return changes;
    }

    // ── layout helpers ──
    private int AddrX => 8;
    private int HexX => AddrX + _charW * 11;
    private int AsciiX => HexX + _charW * (Cols * 3 + 2);
    private int HexCellX(int col) => HexX + _charW * (col * 3 + (col >= 8 ? 1 : 0));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        long now = Environment.TickCount64;

        // header
        using (var hb = new SolidBrush(Theme.Surface2)) g.FillRectangle(hb, 0, 0, Width, 22);
        for (int c = 0; c < Cols; c++)
            TextRenderer.DrawText(g, c.ToString("X2"), Font, new Point(HexCellX(c), 3), Theme.TextDim, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "ASCII", Theme.UISmall, new Point(AsciiX, 4), Theme.TextDim, TextFormatFlags.NoPadding);

        uint selLo = SelectionStart, selHi = selLo + SelectionLength - 1;
        int rows = Rows;
        for (int r = 0; r < rows; r++)
        {
            int y = 26 + r * _rowH;
            uint rowAddr = _base + (uint)(r * Cols);
            TextRenderer.DrawText(g, rowAddr.ToString("X8"), Font, new Point(AddrX, y), Theme.TextMuted, TextFormatFlags.NoPadding);
            for (int c = 0; c < Cols; c++)
            {
                int i = r * Cols + c;
                uint a = rowAddr + (uint)c;
                int x = HexCellX(c);
                bool has = i < _data.Length;
                bool readable = has && _readable[i];
                bool sel = a >= selLo && a <= selHi;
                var cell = new Rectangle(x - 2, y - 1, _charW * 2 + 4, _rowH - 2);
                var acell = new Rectangle(AsciiX + c * _charW, y - 1, _charW, _rowH - 2);

                Color? bg = null;
                if (sel) bg = Theme.Selection;
                else if (IsFrozen?.Invoke(a) == true) bg = Theme.Blend(Theme.Input, Theme.Teal, 0.30f);
                else if (has && _changedAt[i] != 0 && now - _changedAt[i] < 1500)
                    bg = Theme.Blend(Theme.Input, Theme.Warning, (float)(0.55 * (1 - (now - _changedAt[i]) / 1500.0)));
                if (bg is { } b)
                {
                    using var br = new SolidBrush(b);
                    g.FillRectangle(br, cell);
                    g.FillRectangle(br, acell);
                }
                if (a == _cursor && Focused)
                {
                    using var p = new Pen(Theme.Accent, 1.5f);
                    if (_asciiFocus) g.DrawRectangle(p, acell); else g.DrawRectangle(p, cell);
                }

                string hex = readable ? _data[i].ToString("X2") : has ? "??" : "··";
                Color fore = !readable ? Theme.TextDim : _data[i] == 0 ? Theme.TextDim : Theme.Text;
                if (sel) fore = Color.White;
                TextRenderer.DrawText(g, hex, Font, new Point(x, y), fore, TextFormatFlags.NoPadding);
                char ch = readable && _data[i] is >= 32 and < 127 ? (char)_data[i] : '.';
                TextRenderer.DrawText(g, ch.ToString(), Font, new Point(AsciiX + c * _charW, y), readable ? (sel ? Color.White : Theme.TextMuted) : Theme.TextDim, TextFormatFlags.NoPadding);

                if (SymbolAt?.Invoke(a) != null)
                {
                    using var up = new Pen(Theme.Accent, 2);
                    g.DrawLine(up, x - 1, y + _rowH - 4, x + _charW * 2 + 1, y + _rowH - 4);
                }
            }
        }
        using var sep = new Pen(Theme.Border);
        g.DrawLine(sep, HexX - _charW, 22, HexX - _charW, Height);
        g.DrawLine(sep, AsciiX - _charW, 22, AsciiX - _charW, Height);
    }

    private uint? HitTest(Point p, out bool ascii)
    {
        ascii = false;
        int r = (p.Y - 26) / Math.Max(1, _rowH);
        if (p.Y < 26 || r >= Rows) return null;
        if (p.X >= AsciiX && p.X < AsciiX + Cols * _charW)
        {
            ascii = true;
            return _base + (uint)(r * Cols + (p.X - AsciiX) / _charW);
        }
        for (int c = 0; c < Cols; c++)
            if (p.X >= HexCellX(c) - 3 && p.X < HexCellX(c) + _charW * 2 + 3) return _base + (uint)(r * Cols + c);
        return null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (HitTest(e.Location, out var ascii) is { } a)
        {
            _asciiFocus = ascii;
            _cursor = a;
            if ((ModifierKeys & Keys.Shift) == 0) _selAnchor = a;
            _highNibble = true;
            CaretMoved?.Invoke();
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var a = HitTest(e.Location, out _);
        if (e.Button == MouseButtons.Left && a is { } drag && drag != _cursor)
        {
            _cursor = drag;
            CaretMoved?.Invoke();
            Invalidate();
        }
        string? tip = a is { } t ? SymbolAt?.Invoke(t) is { } s ? $"{s}  @ 0x{t:X8}" : null : null;
        if (tip == null && e.X < HexX && e.Y >= 26) tip = Describe?.Invoke(_base + (uint)((e.Y - 26) / Math.Max(1, _rowH) * Cols));
        if (tip != _lastTip) { _lastTip = tip; _tip.SetToolTip(this, tip); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Scroll(-Math.Sign(e.Delta) * 3);
        base.OnMouseWheel(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Tab
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool shift = e.Shift;
        uint before = _cursor;
        switch (e.KeyCode)
        {
            case Keys.Left: MoveCaret(-1, shift); break;
            case Keys.Right: MoveCaret(1, shift); break;
            case Keys.Up: MoveCaret(-Cols, shift); break;
            case Keys.Down: MoveCaret(Cols, shift); break;
            case Keys.PageUp: Scroll(-Rows); MoveCaret(-Cols * Rows, shift); break;
            case Keys.PageDown: Scroll(Rows); MoveCaret(Cols * Rows, shift); break;
            case Keys.Tab: _asciiFocus = !_asciiFocus; Invalidate(); break;
            default: base.OnKeyDown(e); return;
        }
        if (before != _cursor) _highNibble = true;
        e.Handled = true;
    }

    private void MoveCaret(int delta, bool extend)
    {
        long n = Math.Clamp((long)_cursor + delta, 0, 0xFFFFFFFFL);
        _cursor = (uint)n;
        if (!extend) _selAnchor = _cursor;
        if (_cursor < _base) Scroll(-(int)(((_base - _cursor) + Cols - 1) / Cols));
        else if (_cursor >= _base + ViewLength) Scroll((int)((_cursor - (_base + (uint)ViewLength)) / Cols) + 1);
        CaretMoved?.Invoke();
        Invalidate();
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (ByteAt(_cursor) is not { } cur) { base.OnKeyPress(e); return; }
        byte? nv = null;
        if (_asciiFocus)
        {
            if (e.KeyChar is >= ' ' and < (char)127) nv = (byte)e.KeyChar;
        }
        else if (Uri.IsHexDigit(e.KeyChar))
        {
            int d = Convert.ToInt32(e.KeyChar.ToString(), 16);
            nv = _highNibble ? (byte)((d << 4) | (cur & 0x0F)) : (byte)((cur & 0xF0) | d);
        }
        if (nv is not { } v) { base.OnKeyPress(e); return; }

        long i = (long)_cursor - _base;
        _data[i] = v;
        ByteEdited?.Invoke(_cursor, v);
        if (_asciiFocus || !_highNibble) { _highNibble = true; MoveCaret(1, false); }
        else _highNibble = false;
        Invalidate();
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fade.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
