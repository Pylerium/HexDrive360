using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MemoryEditor.UI;

public enum ButtonVariant { Neutral, Primary, Success, Danger, Warning, Ghost, Purple }

/// <summary>Flat owner-drawn button with an optional icon glyph and rounded corners.</summary>
public class IconButton : Control
{
    private bool _hover, _down;
    private string _glyph = "";
    private ButtonVariant _variant = ButtonVariant.Neutral;

    public IconButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        Font = Theme.UISemibold;
        Height = 32;
        Margin = new Padding(0, 0, 6, 0);
        BackColor = Color.Transparent;
        Theme.Exclude(this);
    }

    [DefaultValue("")] public string Glyph { get => _glyph; set { _glyph = value; Invalidate(); } }
    [DefaultValue(ButtonVariant.Neutral)] public ButtonVariant Variant { get => _variant; set { _variant = value; Invalidate(); } }
    public int Radius { get; set; } = 6;
    public bool AutoWidth { get; set; } = true;
    public string? ToolTipText { get; set; }
    public bool IconOnly => string.IsNullOrEmpty(Text);

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitWidth(); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitWidth(); }
    protected override void OnCreateControl() { base.OnCreateControl(); FitWidth(); }

    private void FitWidth()
    {
        if (!AutoWidth) return;
        int w = 20;
        if (!string.IsNullOrEmpty(Glyph)) w += 16 + (IconOnly ? 0 : 8);
        if (!IconOnly) w += TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width;
        Width = IconOnly ? Math.Max(Height, w) : w;
    }

    private (Color fill, Color fore, Color border) Colors()
    {
        Color baseFill = _variant switch
        {
            ButtonVariant.Primary => Theme.BtnPrimary,
            ButtonVariant.Success => Theme.BtnSuccess,
            ButtonVariant.Danger => Theme.BtnDanger,
            ButtonVariant.Warning => Theme.BtnWarning,
            ButtonVariant.Purple => Theme.BtnPurple,
            ButtonVariant.Ghost => Color.Transparent,
            _ => Theme.Surface2,
        };
        Color fore = _variant is ButtonVariant.Neutral or ButtonVariant.Ghost ? Theme.Text : Color.White;
        Color border = _variant == ButtonVariant.Neutral ? Theme.BorderStrong : Color.Transparent;
        if (!Enabled)
        {
            // disabled buttons stay clearly visible (neutral surface + border + readable text); the old blend
            // into the background made disabled actions vanish, which looked like a cut-off layout
            return (_variant == ButtonVariant.Ghost ? Color.Transparent : Theme.Surface2, Theme.Blend(Theme.TextDim, Theme.TextMuted, 0.4f), Theme.BorderStrong);
        }
        if (_variant == ButtonVariant.Ghost)
            return (_down ? Theme.Surface3 : _hover ? Theme.Surface2 : Color.Transparent, _hover ? Theme.Text : Theme.TextMuted, Color.Transparent);
        if (_down) baseFill = Theme.Darken(baseFill, 0.12f);
        else if (_hover) baseFill = Theme.Lighten(baseFill, 0.10f);
        return (baseFill, fore, border);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var (fill, fore, border) = Colors();
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.RoundRect(r, Radius))
        {
            if (fill.A > 0) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (border.A > 0) using (var p = new Pen(border)) g.DrawPath(p, path);
            if (Focused && ShowFocusCues) using (var p = new Pen(Theme.Accent, 1.5f)) g.DrawPath(p, path);
        }

        int x = 10;
        if (!string.IsNullOrEmpty(Glyph))
        {
            var icon = Theme.Icon(10.5F);
            var gs = TextRenderer.MeasureText(Glyph, icon, Size.Empty, TextFormatFlags.NoPadding);
            int gx = IconOnly ? (Width - gs.Width) / 2 : x;
            TextRenderer.DrawText(g, Glyph, icon, new Point(gx, (Height - gs.Height) / 2 + 1), fore, TextFormatFlags.NoPadding);
            x = gx + gs.Width + 7;
        }
        if (!IconOnly)
        {
            var rect = new Rectangle(string.IsNullOrEmpty(Glyph) ? 0 : x, 0, string.IsNullOrEmpty(Glyph) ? Width : Width - x - 6, Height);
            TextRenderer.DrawText(g, Text, Font, rect, fore,
                (string.IsNullOrEmpty(Glyph) ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // one shared ToolTip for every IconButton (a ToolTip per button leaks native windows)
    private static readonly ToolTip SharedTip = new() { InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 8000 };

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        if (!string.IsNullOrEmpty(ToolTipText)) SharedTip.SetToolTip(this, ToolTipText);
        base.OnMouseEnter(e);
    }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) SharedTip.SetToolTip(this, null);
        base.Dispose(disposing);
    }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }

    public static IconButton Create(string text, string glyph, ButtonVariant v = ButtonVariant.Neutral, EventHandler? onClick = null, string? tip = null)
    {
        var b = new IconButton { Text = text, Glyph = glyph, Variant = v, ToolTipText = tip };
        if (onClick != null) b.Click += onClick;
        return b;
    }
}

/// <summary>Rounded status pill: colored dot + text.</summary>
public class StatusPill : Control
{
    private Color _dot = Theme.TextDim;
    public StatusPill()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Theme.UISemibold;
        Height = 26;
        Theme.Exclude(this);
    }
    public Color DotColor { get => _dot; set { _dot = value; Invalidate(); } }
    public string Glyph { get; set; } = "";
    public bool Pulse { get; set; }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Width = TextRenderer.MeasureText(Text, Font).Width + (string.IsNullOrEmpty(Glyph) ? 34 : 40);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), (Height - 1) / 2f))
        {
            using var b = new SolidBrush(Theme.Blend(Theme.Surface2, _dot, 0.10f));
            g.FillPath(b, path);
            using var p = new Pen(Theme.Blend(Theme.Border, _dot, 0.35f));
            g.DrawPath(p, path);
        }
        int x = 11;
        if (!string.IsNullOrEmpty(Glyph))
        {
            var icon = Theme.Icon(9.5F);
            TextRenderer.DrawText(g, Glyph, icon, new Point(x - 1, (Height - 14) / 2 + 1), _dot, TextFormatFlags.NoPadding);
            x += 18;
        }
        else
        {
            using var b = new SolidBrush(_dot);
            g.FillEllipse(b, x, Height / 2f - 4, 8, 8);
            x += 16;
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x - 6, Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Vertical navigation rail with grouped items, collapsible to icons only.</summary>
public class NavRail : Control
{
    public sealed class Item
    {
        public string Key { get; init; } = "";
        public string Title { get; init; } = "";
        public string Glyph { get; init; } = "";
        public string Group { get; init; } = "";
        public string Shortcut { get; set; } = "";
        public string Badge { get; set; } = "";
        internal Rectangle Bounds;
    }

    private readonly List<Item> _items = new();
    private Item? _hover;
    private bool _collapsed;
    private readonly ToolTip _tip = new() { InitialDelay = 300 };

    public NavRail()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        Width = ExpandedWidth;
        Theme.Exclude(this);
    }

    public int ExpandedWidth { get; set; } = 212;
    public int CollapsedWidth { get; set; } = 56;
    public Item? Selected { get; private set; }
    public IReadOnlyList<Item> Items => _items;
    public event Action<Item>? ItemSelected;

    public bool Collapsed
    {
        get => _collapsed;
        set { _collapsed = value; Width = value ? CollapsedWidth : ExpandedWidth; Invalidate(); }
    }

    public Item Add(string key, string title, string glyph, string group, string shortcut = "")
    {
        var it = new Item { Key = key, Title = title, Glyph = glyph, Group = group, Shortcut = shortcut };
        _items.Add(it);
        Invalidate();
        return it;
    }

    public void Select(string key, bool raise = true)
    {
        var it = _items.FirstOrDefault(i => i.Key == key);
        if (it == null) return;
        Selected = it;
        Invalidate();
        if (raise) ItemSelected?.Invoke(it);
    }

    public void SetBadge(string key, string badge)
    {
        var it = _items.FirstOrDefault(i => i.Key == key);
        if (it != null) { it.Badge = badge; Invalidate(); }
    }

    private void Layout2()
    {
        int y = 10;
        string? group = null;
        foreach (var it in _items)
        {
            if (it.Group != group)
            {
                group = it.Group;
                y += _collapsed ? 10 : 26;
            }
            it.Bounds = new Rectangle(8, y, Width - 16, 34);
            y += 36;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Layout2();
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);
        using (var p = new Pen(Theme.Border)) g.DrawLine(p, Width - 1, 0, Width - 1, Height);

        var icon = Theme.Icon(12F);
        string? group = null;
        foreach (var it in _items)
        {
            if (it.Group != group)
            {
                group = it.Group;
                if (!_collapsed)
                    TextRenderer.DrawText(g, group.ToUpperInvariant(), Theme.UISmall, new Point(18, it.Bounds.Y - 20), Theme.TextDim);
                else
                    using (var p = new Pen(Theme.Border)) g.DrawLine(p, 16, it.Bounds.Y - 6, Width - 16, it.Bounds.Y - 6);
            }

            bool sel = it == Selected, hov = it == _hover;
            if (sel || hov)
            {
                using var path = Theme.RoundRect(it.Bounds, 6);
                using var b = new SolidBrush(sel ? Theme.Blend(Theme.Surface2, Theme.Accent, 0.18f) : Theme.Surface2);
                g.FillPath(b, path);
            }
            if (sel)
            {
                using var b = new SolidBrush(Theme.Accent);
                using var path = Theme.RoundRect(new RectangleF(it.Bounds.X, it.Bounds.Y + 8, 3, it.Bounds.Height - 16), 1.5f);
                g.FillPath(b, path);
            }
            var fore = sel ? Theme.Text : hov ? Theme.Text : Theme.TextMuted;
            int gx = _collapsed ? it.Bounds.X + (it.Bounds.Width - 18) / 2 : it.Bounds.X + 14;
            TextRenderer.DrawText(g, it.Glyph, icon, new Point(gx, it.Bounds.Y + 9), sel ? Theme.Accent : fore, TextFormatFlags.NoPadding);
            if (!_collapsed)
            {
                var tr = new Rectangle(it.Bounds.X + 44, it.Bounds.Y, it.Bounds.Width - 50, it.Bounds.Height);
                TextRenderer.DrawText(g, it.Title, sel ? Theme.UISemibold : Theme.UI, tr, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                if (!string.IsNullOrEmpty(it.Badge))
                {
                    var bs = TextRenderer.MeasureText(it.Badge, Theme.UISmall);
                    var br = new RectangleF(it.Bounds.Right - bs.Width - 12, it.Bounds.Y + 8, bs.Width + 6, 18);
                    using var path = Theme.RoundRect(br, 9);
                    using var bb = new SolidBrush(Theme.AccentDim);
                    g.FillPath(bb, path);
                    TextRenderer.DrawText(g, it.Badge, Theme.UISmall, Rectangle.Round(br), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            else if (!string.IsNullOrEmpty(it.Badge))
            {
                using var b = new SolidBrush(Theme.Accent);
                g.FillEllipse(b, it.Bounds.Right - 12, it.Bounds.Y + 6, 7, 7);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var it = _items.FirstOrDefault(i => i.Bounds.Contains(e.Location));
        if (it != _hover)
        {
            _hover = it;
            Cursor = it != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
            if (it != null) _tip.SetToolTip(this, it.Title + (it.Shortcut.Length > 0 ? $"  ({it.Shortcut})" : ""));
            else _tip.SetToolTip(this, null);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = null; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        var it = _items.FirstOrDefault(i => i.Bounds.Contains(e.Location));
        if (it != null) Select(it.Key);
        base.OnMouseClick(e);
    }
}

/// <summary>Non-blocking toast notifications stacked in the top-right corner of a form.</summary>
public sealed class ToastHost
{
    public enum Kind { Info, Success, Warning, Error }

    private readonly Form _form;
    private readonly List<Control> _active = new();

    public ToastHost(Form form) { _form = form; form.Resize += (_, _) => Relayout(); }
    public int TopOffset { get; set; } = 64;

    public void Show(string message, Kind kind = Kind.Info, int ms = 3800, string? title = null)
    {
        if (_form.IsDisposed) return;
        if (_form.InvokeRequired) { _form.BeginInvoke(() => Show(message, kind, ms, title)); return; }

        var color = kind switch { Kind.Success => Theme.Success, Kind.Warning => Theme.Warning, Kind.Error => Theme.Danger, _ => Theme.Accent };
        var glyph = kind switch { Kind.Success => Theme.Glyph.Check, Kind.Warning => Theme.Glyph.Warning, Kind.Error => Theme.Glyph.Error, _ => Theme.Glyph.Info };
        title ??= kind switch { Kind.Success => "Done", Kind.Warning => "Heads up", Kind.Error => "Something went wrong", _ => "Info" };

        var toast = new ToastControl(title, message, glyph, color);
        _form.Controls.Add(toast);
        toast.BringToFront();
        _active.Insert(0, toast);
        while (_active.Count > 4) Close(_active[^1]);
        Relayout();

        var timer = new System.Windows.Forms.Timer { Interval = ms };
        timer.Tick += (_, _) => { timer.Dispose(); Close(toast); };
        timer.Start();
        toast.Click += (_, _) => Close(toast);
    }

    private void Close(Control t)
    {
        if (!_active.Remove(t)) return;
        _form.Controls.Remove(t);
        t.Dispose();
        Relayout();
    }

    private void Relayout()
    {
        int y = TopOffset;
        foreach (var t in _active)
        {
            t.Location = new Point(_form.ClientSize.Width - t.Width - 18, y);
            y += t.Height + 8;
        }
    }

    private sealed class ToastControl : Control
    {
        private readonly string _title, _msg, _glyph;
        private readonly Color _color;
        public ToastControl(string title, string msg, string glyph, Color color)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            _title = title; _msg = msg; _glyph = glyph; _color = color;
            Theme.Exclude(this);
            Width = 360;
            var h = TextRenderer.MeasureText(msg, Theme.UI, new Size(Width - 64, 400), TextFormatFlags.WordBreak).Height;
            Height = Math.Min(140, 40 + h);
            Cursor = Cursors.Hand;
            foreach (Control c in Controls) c.Click += (_, e) => OnClick(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8))
            {
                using var b = new SolidBrush(Theme.Surface2);
                g.FillPath(b, path);
                using var p = new Pen(Theme.Blend(Theme.BorderStrong, _color, 0.45f));
                g.DrawPath(p, path);
            }
            using (var b = new SolidBrush(_color)) g.FillRectangle(b, 1, 10, 3, Height - 20);
            var icon = Theme.Icon(13F);
            TextRenderer.DrawText(g, _glyph, icon, new Point(16, 13), _color, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _title, Theme.UISemibold, new Point(44, 10), Theme.Text);
            TextRenderer.DrawText(g, _msg, Theme.UI, new Rectangle(44, 30, Width - 56, Height - 34), Theme.TextMuted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>Ctrl+K command palette with fuzzy search over pages, actions and offsets.</summary>
public sealed class CommandPalette : Form
{
    public sealed class Command
    {
        public string Title { get; init; } = "";
        public string Category { get; init; } = "";
        public string Hint { get; init; } = "";
        public string Glyph { get; init; } = Theme.Glyph.Command;
        public Action Run { get; init; } = () => { };
    }

    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly Func<IEnumerable<Command>> _source;
    private List<Command> _all = new();

    public CommandPalette(Func<IEnumerable<Command>> source)
    {
        _source = source;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = Theme.BorderStrong;
        Padding = new Padding(1);
        Size = new Size(640, 420);

        var inner = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(12), Tag = "keep" };
        Theme.Exclude(inner);
        _search = new TextBox
        {
            Dock = DockStyle.Top, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 12.5F), PlaceholderText = "Type a command, page, offset name or address…"
        };
        Theme.Exclude(_search);
        var sep = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Theme.Surface };
        Theme.Exclude(sep);
        _list = new ListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 38, IntegralHeight = false
        };
        Theme.Exclude(_list);
        Theme.DarkScrollbars(_list);
        _list.DrawItem += DrawItem;
        _list.DoubleClick += (_, _) => Execute();
        _search.TextChanged += (_, _) => Filter();
        inner.Controls.Add(_list);
        inner.Controls.Add(sep);
        inner.Controls.Add(_search);
        Controls.Add(inner);
        Deactivate += (_, _) => Close();
    }

    public static void ShowFor(Form owner, Func<IEnumerable<Command>> source)
    {
        var p = new CommandPalette(source);
        var r = owner.RectangleToScreen(owner.ClientRectangle);
        p.Location = new Point(r.X + (r.Width - p.Width) / 2, r.Y + 70);
        p.Show(owner);
        p._search.Focus();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _all = _source().ToList();
        Filter();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Enter: Execute(); return true;
            case Keys.Down:
                if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1);
                return true;
            case Keys.Up:
                if (_list.Items.Count > 0) _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static int Score(string text, string q)
    {
        if (q.Length == 0) return 1;
        var t = text.ToLowerInvariant();
        q = q.ToLowerInvariant();
        int idx = t.IndexOf(q, StringComparison.Ordinal);
        if (idx == 0) return 1000 - t.Length;
        if (idx > 0) return 700 - idx - t.Length / 4;
        // subsequence
        int ti = 0, score = 0, streak = 0;
        foreach (var ch in q)
        {
            int f = t.IndexOf(ch, ti);
            if (f < 0) return 0;
            streak = f == ti ? streak + 1 : 0;
            score += 5 + streak * 3;
            ti = f + 1;
        }
        return score;
    }

    private void Filter()
    {
        var q = _search.Text.Trim();
        var ranked = _all.Select(c => (c, s: Math.Max(Score(c.Title, q), Score(c.Category + " " + c.Title, q) - 50)))
                         .Where(x => x.s > 0).OrderByDescending(x => x.s).Take(200).Select(x => x.c).ToList();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var c in ranked) _list.Items.Add(c);
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void Execute()
    {
        if (_list.SelectedItem is not Command c) return;
        Close();
        try { c.Run(); } catch { }
    }

    private void DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var c = (Command)_list.Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, e.Bounds);
        if (sel)
        {
            using var path = Theme.RoundRect(new RectangleF(e.Bounds.X + 1, e.Bounds.Y + 1, e.Bounds.Width - 3, e.Bounds.Height - 3), 6);
            using var b = new SolidBrush(Theme.Blend(Theme.Surface2, Theme.Accent, 0.22f));
            g.FillPath(b, path);
        }
        var icon = Theme.Icon(11F);
        TextRenderer.DrawText(g, c.Glyph, icon, new Point(e.Bounds.X + 12, e.Bounds.Y + 12), sel ? Theme.Accent : Theme.TextMuted, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, c.Title, Theme.UI, new Rectangle(e.Bounds.X + 40, e.Bounds.Y, e.Bounds.Width - 200, e.Bounds.Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var right = string.IsNullOrEmpty(c.Hint) ? c.Category : $"{c.Hint}   {c.Category}";
        TextRenderer.DrawText(g, right, Theme.UISmall, new Rectangle(e.Bounds.Right - 190, e.Bounds.Y, 180, e.Bounds.Height), Theme.TextDim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Simple themed input dialog (replaces Microsoft.VisualBasic.Interaction.InputBox).</summary>
public static class Prompt
{
    public static string? Show(IWin32Window? owner, string title, string label, string initial = "", string? secondLabel = null, string secondInitial = "")
        => ShowTwo(owner, title, label, initial, secondLabel, secondInitial)?.First;

    public static (string First, string Second)? ShowTwo(IWin32Window? owner, string title, string label, string initial = "", string? secondLabel = null, string secondInitial = "")
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(440, secondLabel == null ? 150 : 214),
        };
        var lbl = new Label { Text = label, Location = new Point(18, 16), AutoSize = true };
        var tb = new TextBox { Text = initial, Location = new Point(18, 40), Width = 404 };
        f.Controls.Add(lbl);
        f.Controls.Add(tb);
        TextBox? tb2 = null;
        int y = 82;
        if (secondLabel != null)
        {
            f.Controls.Add(new Label { Text = secondLabel, Location = new Point(18, 80), AutoSize = true });
            tb2 = new TextBox { Text = secondInitial, Location = new Point(18, 104), Width = 404 };
            f.Controls.Add(tb2);
            y = 146;
        }
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(246, y + 16), Size = new Size(84, 32), BackColor = Theme.BtnPrimary };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(338, y + 16), Size = new Size(84, 32) };
        f.Controls.Add(ok);
        f.Controls.Add(cancel);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        Theme.Apply(f);
        Theme.ApplyWindowChrome(f);
        tb.SelectAll();
        return f.ShowDialog(owner) == DialogResult.OK ? (tb.Text, tb2?.Text ?? "") : null;
    }
}

/// <summary>
/// Vertical stack: every visible child gets the full width and the height it needs at that width (wrapping flow
/// panels and word-wrapped labels included). The panel resizes itself to fit, so content is never clipped.
/// </summary>
public sealed class StackPanel : Panel
{
    public int Spacing { get; set; } = 6;
    public event Action? HeightChanged;

    public StackPanel()
    {
        BackColor = Color.Transparent;
        Tag = "keep";
        DoubleBuffered = true;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        int w = Math.Max(50, ClientSize.Width - Padding.Horizontal);
        int y = Padding.Top;
        foreach (Control c in Controls) // collection order == order added
        {
            if (!c.Visible) continue;
            int h = PreferredHeight(c, w);
            c.SetBounds(Padding.Left + c.Margin.Left, y + c.Margin.Top, w - c.Margin.Horizontal, h);
            y += c.Margin.Vertical + h + Spacing;
        }
        int total = Math.Max(0, y - Spacing) + Padding.Bottom;
        if (Height != total)
        {
            Height = total;
            HeightChanged?.Invoke();
        }
    }

    private static int PreferredHeight(Control c, int width)
    {
        int w = width - c.Margin.Horizontal;
        if (c is Label l)
        {
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
            return Math.Max(l.Font.Height, TextRenderer.MeasureText(l.Text.Length == 0 ? " " : l.Text, l.Font, new Size(w, int.MaxValue), flags).Height) + l.Padding.Vertical;
        }
        if (c is FlowLayoutPanel f) return f.GetPreferredSize(new Size(w, 0)).Height;
        return c.Height;
    }

    /// <summary>Call after changing text / visibility of children.</summary>
    public void Relayout() => PerformLayout();
}

/// <summary>Human-readable "KEY / value" field (key in small caps above the value). Long values are elided with a tooltip.</summary>
public sealed class FactLabel : Control
{
    private static readonly ToolTip Tip = new() { InitialDelay = 300 };
    private string _key = "", _value = "";
    public int MaxValueWidth { get; set; } = 460;
    public Color ValueColor { get; set; } = Theme.Text;

    public FactLabel(string key, string value = "")
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 22, 4);
        Theme.Exclude(this);
        _key = key.ToUpperInvariant();
        Value = value;
    }

    public string Value
    {
        get => _value;
        set
        {
            _value = value ?? "";
            var kw = TextRenderer.MeasureText(_key, Theme.UISmall).Width;
            var vw = Math.Min(MaxValueWidth, TextRenderer.MeasureText(_value.Length == 0 ? "—" : _value, Theme.UISemibold).Width);
            Size = new Size(Math.Max(kw, vw) + 4, Theme.UISmall.Height + Theme.UISemibold.Height + 4);
            Tip.SetToolTip(this, TextRenderer.MeasureText(_value, Theme.UISemibold).Width > MaxValueWidth ? _value : null);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, _key, Theme.UISmall, new Point(0, 0), Theme.TextDim, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, _value.Length == 0 ? "—" : _value, Theme.UISemibold,
            new Rectangle(0, Theme.UISmall.Height + 2, Width, Theme.UISemibold.Height + 2), ValueColor,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis);
    }
}
