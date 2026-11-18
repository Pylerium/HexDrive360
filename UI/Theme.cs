using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;

namespace MemoryEditor.UI;

/// <summary>
/// Application-wide dark theme. <see cref="Apply"/> restyles any control tree (including controls created
/// later – ControlAdded is hooked) so legacy pages pick up the look without being rewritten.
/// </summary>
public static class Theme
{
    // ── palette ──
    public static readonly Color Bg = Color.FromArgb(0x14, 0x16, 0x1B);
    public static readonly Color Surface = Color.FromArgb(0x1C, 0x1F, 0x26);
    public static readonly Color Surface2 = Color.FromArgb(0x23, 0x27, 0x30);
    public static readonly Color Surface3 = Color.FromArgb(0x2B, 0x30, 0x3B);
    public static readonly Color Input = Color.FromArgb(0x12, 0x14, 0x19);
    public static readonly Color Border = Color.FromArgb(0x2E, 0x33, 0x3E);
    public static readonly Color BorderStrong = Color.FromArgb(0x3C, 0x43, 0x51);
    public static readonly Color Text = Color.FromArgb(0xE4, 0xE7, 0xEE);
    public static readonly Color TextMuted = Color.FromArgb(0x98, 0x9F, 0xAE);
    public static readonly Color TextDim = Color.FromArgb(0x6A, 0x72, 0x82);
    public static readonly Color Accent = Color.FromArgb(0x4C, 0x8D, 0xFF);
    public static readonly Color AccentDim = Color.FromArgb(0x24, 0x3B, 0x66);
    public static readonly Color Success = Color.FromArgb(0x34, 0xB3, 0x78);
    public static readonly Color Warning = Color.FromArgb(0xE8, 0xA2, 0x3C);
    public static readonly Color Danger = Color.FromArgb(0xEC, 0x5B, 0x5F);
    public static readonly Color Purple = Color.FromArgb(0x93, 0x74, 0xF0);
    public static readonly Color Teal = Color.FromArgb(0x26, 0xB5, 0xA8);
    public static readonly Color CodeBg = Color.FromArgb(0x10, 0x12, 0x17);
    public static readonly Color Selection = Color.FromArgb(0x26, 0x4F, 0x9A);

    // button fills (slightly deeper than the accent colors so white text stays readable)
    public static readonly Color BtnPrimary = Color.FromArgb(0x35, 0x74, 0xE8);
    public static readonly Color BtnSuccess = Color.FromArgb(0x23, 0x8F, 0x5E);
    public static readonly Color BtnDanger = Color.FromArgb(0xC9, 0x45, 0x4A);
    public static readonly Color BtnWarning = Color.FromArgb(0xC2, 0x7D, 0x22);
    public static readonly Color BtnPurple = Color.FromArgb(0x74, 0x57, 0xD6);
    public static readonly Color BtnTeal = Color.FromArgb(0x1A, 0x8E, 0x85);
    public static readonly Color BtnNeutral = Color.FromArgb(0x2C, 0x31, 0x3C);

    // ── fonts ──
    public static readonly Font UI = new("Segoe UI", 9F);
    public static readonly Font UISmall = new("Segoe UI", 8.25F);
    public static readonly Font UISemibold = new("Segoe UI Semibold", 9F);
    public static readonly Font UIBold = new("Segoe UI Semibold", 9.75F);
    public static readonly Font Title = new("Segoe UI Semibold", 13F);
    public static readonly Font Heading = new("Segoe UI Semibold", 10.5F);
    public static readonly Font Mono = CreateMono(9.5F);
    public static readonly Font MonoSmall = CreateMono(8.75F);
    public static readonly string IconFontName = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<float, Font> IconCache = new();

    /// <summary>
    /// Returns a shared, cached icon font. Do NOT dispose the result – it is reused by every
    /// owner-drawn control (creating a GDI font per paint was a major source of handle churn).
    /// </summary>
    public static Font Icon(float size = 11F) => IconCache.GetOrAdd(size, s => new Font(IconFontName, s));

    private static Font CreateMono(float size) =>
        FontExists("Cascadia Mono") ? new Font("Cascadia Mono", size) :
        FontExists("Cascadia Code") ? new Font("Cascadia Code", size) : new Font("Consolas", size + 0.5F);

    public static bool FontExists(string name)
    {
        try
        {
            using var f = new Font(name, 10F);
            return f.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Segoe Fluent / MDL2 glyphs used across the app.</summary>
    public static class Glyph
    {
        public const string Memory = "";
        public const string Search = "";
        public const string Record = "";
        public const string Calculator = "";
        public const string Code = "";
        public const string Analyze = "";
        public const string Cpu = "";
        public const string Build = "";
        public const string Tools = "";
        public const string Settings = "";
        public const string Plug = "";
        public const string Unplug = "";
        public const string Play = "";
        public const string Stop = "";
        public const string Save = "";
        public const string Open = "";
        public const string Add = "";
        public const string Delete = "";
        public const string Refresh = "";
        public const string Game = "";
        public const string Target = "";
        public const string Package = "";
        public const string Convert = "";
        public const string Menu = "";
        public const string ChevronDown = "";
        public const string ChevronUp = "";
        public const string Terminal = "";
        public const string Clear = "";
        public const string Copy = "";
        public const string Check = "";
        public const string Warning = "";
        public const string Error = "";
        public const string Info = "";
        public const string Close = "";
        public const string Bolt = "";
        public const string List = "";
        public const string Wand = "";
        public const string Download = "";
        public const string Upload = "";
        public const string Folder = "";
        public const string Document = "";
        public const string Command = "";
        public const string Filter = "";
        public const string Pin = "";
        public const string Link = "";
        public const string Format = "";
        public const string Bug = "";
        public const string Snowflake = "";
        public const string Signature = "";
        public const string Fingerprint = "";
        public const string Registers = "";
    }

    // ── native dark mode ──
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    public static void ApplyWindowChrome(Form f)
    {
        void Do()
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
                int caption = ColorTranslator.ToWin32(Bg);
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);   // caption color (Win11)
                int border = ColorTranslator.ToWin32(Border);
                DwmSetWindowAttribute(f.Handle, 34, ref border, 4);    // border color (Win11)
            }
            catch { }
        }
        if (f.IsHandleCreated) Do(); else f.HandleCreated += (_, _) => Do();
    }

    public static void DarkScrollbars(Control c)
    {
        void Do() { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } }
        if (c.IsHandleCreated) Do(); else c.HandleCreated += (_, _) => Do();
    }

    // ── color helpers ──
    public static Color Lighten(Color c, float amount) =>
        Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * amount), (int)(c.G + (255 - c.G) * amount), (int)(c.B + (255 - c.B) * amount));

    public static Color Darken(Color c, float amount) =>
        Color.FromArgb(c.A, (int)(c.R * (1 - amount)), (int)(c.G * (1 - amount)), (int)(c.B * (1 - amount)));

    public static Color Blend(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    private static bool IsLight(Color c) => c.GetBrightness() > 0.70f;

    /// <summary>Maps a legacy (Material) button color to the theme's semantic button fill.</summary>
    public static Color MapButtonColor(Color c)
    {
        if (c.IsEmpty || c == SystemColors.Control || c == Color.Transparent || c.A == 0) return BtnNeutral;
        float sat = c.GetSaturation(), hue = c.GetHue(), bri = c.GetBrightness();
        if (sat < 0.25f || bri < 0.18f) return BtnNeutral;
        if (hue < 15 || hue >= 340) return BtnDanger;
        if (hue < 55) return BtnWarning;
        if (hue < 165) return BtnSuccess;
        if (hue < 200) return BtnTeal;
        if (hue < 245) return BtnPrimary;
        if (hue < 340) return BtnPurple;
        return BtnNeutral;
    }

    /// <summary>Maps legacy foreground accent colors to theme colors (keeps meaning, fixes contrast).</summary>
    public static Color MapForeColor(Color c)
    {
        if (c.IsEmpty || c == SystemColors.ControlText || c == SystemColors.WindowText) return Text;
        float sat = c.GetSaturation(), hue = c.GetHue(), bri = c.GetBrightness();
        if (sat < 0.2f) return bri < 0.5f ? (bri < 0.25f ? Text : TextMuted) : (bri > 0.85f ? Text : TextMuted);
        if (hue < 15 || hue >= 340) return Danger;
        if (hue < 55) return Warning;
        if (hue < 165) return Success;
        if (hue < 200) return Teal;
        if (hue < 260) return Accent;
        return Purple;
    }

    // ── text clean-up ──
    public static string StripEmoji(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (char.IsHighSurrogate(ch) && i + 1 < s.Length) { i++; continue; }
            if (ch is '️' or '‍' or '⃣') continue;
            if ((ch >= '←' && ch <= '⇿') || (ch >= '⌀' && ch <= '⏿') || (ch >= '①' && ch <= '⓿') ||
                (ch >= '■' && ch <= '➿') || (ch >= '⤀' && ch <= '⥿') || (ch >= '⬀' && ch <= '⯿') ||
                ch is '〰' or '〽' or '㊗' or '㊙' or '©' or '®' or '™')
                continue;
            sb.Append(ch);
        }
        var r = sb.ToString().Trim();
        while (r.Contains("  ")) r = r.Replace("  ", " ");
        return r;
    }

    // ── styling ──
    private const string Styled = "themed";

    /// <summary>Marks a control so the theme engine leaves it alone (custom painted controls).</summary>
    public static void Exclude(Control c) => c.AccessibleDescription = "theme:skip";
    private static bool Skipped(Control c) => c.AccessibleDescription == "theme:skip";

    public static void Apply(Control root)
    {
        StyleTree(root);
    }

    private static void StyleTree(Control c)
    {
        if (Skipped(c)) return;
        Style(c);
        foreach (Control child in c.Controls) StyleTree(child);
        if (c.Tag as string != Styled && c is not (ComboBox or NumericUpDown or DataGridView or ScintillaNET.Scintilla))
        {
            c.ControlAdded += (_, e) => { if (e.Control != null) StyleTree(e.Control); };
        }
    }

    private static void Style(Control c)
    {
        switch (c)
        {
            case Form f:
                f.BackColor = Bg;
                f.ForeColor = Text;
                f.Font = UI;
                break;

            case Button b:
                StyleButton(b);
                break;

            case CheckBox cb:
                cb.FlatStyle = FlatStyle.Flat;
                cb.FlatAppearance.BorderColor = BorderStrong;
                cb.FlatAppearance.CheckedBackColor = Accent;
                cb.ForeColor = Text;
                cb.BackColor = Color.Transparent;
                cb.Font = UI;
                cb.Text = StripEmoji(cb.Text);
                if (cb.Appearance == Appearance.Normal) AttachCheckPainter(cb);
                break;

            case RadioButton rb:
                rb.FlatStyle = FlatStyle.Flat;
                rb.ForeColor = Text;
                rb.BackColor = Color.Transparent;
                rb.Font = UI;
                break;

            case Label l:
            {
                if (l.Tag as string == "keep") break;
                bool title = l.Font.Bold && l.Font.Size >= 9.9f;
                l.Text = StripEmoji(l.Text);
                l.BackColor = Color.Transparent;
                l.ForeColor = title ? Text : MapForeColor(l.ForeColor);
                l.Font = title ? Heading : l.Font.Italic ? new Font("Segoe UI", 9F, FontStyle.Italic) : l.Font.Bold ? UISemibold : UI;
                break;
            }

            case RichTextBox rtb:
            {
                bool code = !IsLight(rtb.BackColor) || rtb.Font.Name is "Consolas" or "Courier New";
                rtb.BackColor = code ? CodeBg : Surface2;
                rtb.ForeColor = code ? MapCodeFore(rtb.ForeColor) : Text;
                rtb.BorderStyle = BorderStyle.None;
                if (rtb.Font.Name is "Consolas" or "Courier New") rtb.Font = Mono;
                else if (!code) rtb.Font = UI;
                DarkScrollbars(rtb);
                break;
            }

            case TextBox tb:
                tb.BackColor = Input;
                tb.ForeColor = Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                if (!FacePainted.TryGetValue(tb, out _)) FacePainted.Add(tb, new BorderPainter(tb));
                if (tb.Font.Name is "Consolas" or "Courier New") tb.Font = Mono;
                else tb.Font = UI;
                if (tb.Multiline) DarkScrollbars(tb);
                break;

            case ComboBox cmb:
                cmb.FlatStyle = FlatStyle.Flat;
                cmb.BackColor = Input;
                cmb.ForeColor = Text;
                cmb.Font = UI;
                DarkScrollbars(cmb);
                if (cmb.DropDownStyle == ComboBoxStyle.DropDownList && cmb.DrawMode == DrawMode.Normal) AttachComboPainter(cmb);
                if (cmb.DropDownStyle == ComboBoxStyle.DropDownList && !FacePainted.TryGetValue(cmb, out _)) FacePainted.Add(cmb, new ComboFacePainter(cmb));
                break;

            case NumericUpDown num:
                num.BackColor = Input;
                num.ForeColor = Text;
                num.BorderStyle = BorderStyle.FixedSingle;
                num.Font = num.Font.Name is "Consolas" ? Mono : UI;
                break;

            case ListBox lb:
                lb.BackColor = Input;
                lb.ForeColor = Text;
                lb.BorderStyle = BorderStyle.None;
                if (lb.Font.Name is "Consolas" or "Courier New") lb.Font = Mono; else lb.Font = UI;
                DarkScrollbars(lb);
                break;

            case ListView lv:
                lv.BackColor = Input;
                lv.ForeColor = Text;
                lv.BorderStyle = BorderStyle.None;
                lv.Font = UI;
                if (!lv.OwnerDraw && lv.View == View.Details)
                {
                    lv.OwnerDraw = true;
                    lv.DrawColumnHeader += (_, e) =>
                    {
                        using var b = new SolidBrush(Surface2);
                        e.Graphics.FillRectangle(b, e.Bounds);
                        using var p = new Pen(Border);
                        e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                        e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 5, e.Bounds.Right - 1, e.Bounds.Bottom - 6);
                        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", UISemibold, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                            TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    };
                    lv.DrawItem += (_, e) => e.DrawDefault = true;
                    lv.DrawSubItem += (_, e) => e.DrawDefault = true;
                }
                DarkScrollbars(lv);
                break;

            case TreeView tv:
                tv.BackColor = Input;
                tv.ForeColor = Text;
                tv.BorderStyle = BorderStyle.None;
                tv.LineColor = BorderStrong;
                tv.Font = UI;
                DarkScrollbars(tv);
                break;

            case DataGridView g:
                StyleGrid(g);
                break;

            case GroupBox gb:
                gb.ForeColor = TextMuted;
                gb.BackColor = Surface;
                gb.FlatStyle = FlatStyle.Flat;
                gb.Font = UISemibold;
                gb.Text = " " + StripEmoji(gb.Text) + " ";
                break;

            case SplitContainer sc:
                sc.BackColor = Border;
                sc.Panel1.BackColor = Bg;
                sc.Panel2.BackColor = Bg;
                break;

            case TabControl:
                break;

            case TabPage tp:
                tp.BackColor = Bg;
                tp.ForeColor = Text;
                tp.Text = StripEmoji(tp.Text);
                break;

            case PictureBox pb:
                if (IsLight(pb.BackColor)) pb.BackColor = Surface2;
                break;

            case ScintillaNET.Scintilla:
                break;

            case Panel p:
                StylePanel(p);
                break;

            default:
                if (c is FlowLayoutPanel or TableLayoutPanel || c.GetType() == typeof(Control) || c is UserControl)
                {
                    if (c.BackColor == SystemColors.Control || IsLight(c.BackColor)) c.BackColor = c.Parent?.BackColor ?? Bg;
                    c.ForeColor = Text;
                }
                break;
        }
    }

    // controls that already received a one-time handler (prevents duplicate handlers when Apply runs again)
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> Painted = new();

    /// <summary>
    /// The native flat check box draws a white box with a ForeColor tick – invisible on a dark theme, so every
    /// option looked unchecked. Paint a themed box and the caption over the native rendering instead.
    /// </summary>
    private static void AttachCheckPainter(CheckBox cb)
    {
        if (Painted.TryGetValue(cb, out _)) return;
        Painted.Add(cb, cb);
        cb.Paint += (_, e) =>
        {
            var g = e.Graphics;
            var back = EffectiveBack(cb);
            g.Clear(back);
            float scale = cb.DeviceDpi / 96f;
            int box = (int)Math.Round(14 * scale);
            int y = cb.CheckAlign is ContentAlignment.TopLeft ? 1 : (cb.ClientSize.Height - box) / 2;
            var r = new RectangleF(0.5f, y + 0.5f, box - 1, box - 1);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            bool on = cb.CheckState != CheckState.Unchecked;
            using (var path = RoundRect(r, 3 * scale))
            {
                using var fill = new SolidBrush(!cb.Enabled ? Surface2 : on ? Accent : Input);
                g.FillPath(fill, path);
                using var pen = new Pen(on && cb.Enabled ? Accent : cb.Focused ? Accent : BorderStrong);
                g.DrawPath(pen, path);
            }
            if (cb.CheckState == CheckState.Checked)
            {
                using var pen = new Pen(Color.White, 1.8f * scale) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                g.DrawLines(pen, new[]
                {
                    new PointF(r.X + box * 0.24f, r.Y + box * 0.52f),
                    new PointF(r.X + box * 0.42f, r.Y + box * 0.70f),
                    new PointF(r.X + box * 0.76f, r.Y + box * 0.30f),
                });
            }
            else if (cb.CheckState == CheckState.Indeterminate)
            {
                using var b = new SolidBrush(Color.White);
                g.FillRectangle(b, r.X + box * 0.25f, r.Y + box * 0.45f, box * 0.5f, Math.Max(2, box * 0.12f));
            }
            int tx = box + (int)(6 * scale);
            TextRenderer.DrawText(g, cb.Text, cb.Font, new Rectangle(tx, 0, cb.ClientSize.Width - tx, cb.ClientSize.Height),
                cb.Enabled ? cb.ForeColor : TextDim, back, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        cb.CheckedChanged += (_, _) => cb.Invalidate();
        cb.EnabledChanged += (_, _) => cb.Invalidate();

        // the themed box is wider than the native glyph: reserve room so auto-sized labels are never truncated
        void Fit()
        {
            if (!cb.AutoSize) return;
            float sc = cb.DeviceDpi / 96f;
            int need = (int)Math.Round(14 * sc) + (int)(6 * sc) + TextRenderer.MeasureText(cb.Text, cb.Font, Size.Empty, TextFormatFlags.NoPrefix).Width + 4;
            cb.MinimumSize = new Size(need, 0);
        }
        cb.TextChanged += (_, _) => Fit();
        cb.FontChanged += (_, _) => Fit();
        Fit();
    }

    // native painters are kept alive by the control they decorate
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, NativeWindow> FacePainted = new();

    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);

    /// <summary>Paints the closed face of a drop-down list after Windows does (dark in every state, incl. disabled).</summary>
    private sealed class ComboFacePainter : NativeWindow
    {
        private readonly ComboBox _c;

        public ComboFacePainter(ComboBox c)
        {
            _c = c;
            if (c.IsHandleCreated) AssignHandle(c.Handle);
            c.HandleCreated += (_, _) => AssignHandle(c.Handle);
            c.HandleDestroyed += (_, _) => ReleaseHandle();
            EventHandler inv = (_, _) => c.Invalidate();
            c.SelectedIndexChanged += inv;
            c.EnabledChanged += inv;
            c.GotFocus += inv;
            c.LostFocus += inv;
            c.DropDownClosed += inv;
            c.MouseLeave += inv;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F && _c.IsHandleCreated && !_c.IsDisposed) Paint(); // WM_PAINT
        }

        private void Paint()
        {
            using var g = Graphics.FromHwnd(_c.Handle);
            var r = _c.ClientRectangle;
            bool en = _c.Enabled, focus = _c.Focused || _c.DroppedDown;
            using (var b = new SolidBrush(en ? Input : Surface2)) g.FillRectangle(b, r);
            using (var p = new Pen(focus ? Accent : BorderStrong)) g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
            int aw = Math.Max(16, (int)(18 * _c.DeviceDpi / 96f));
            var text = _c.SelectedIndex >= 0 ? _c.GetItemText(_c.SelectedItem) : _c.Text;
            TextRenderer.DrawText(g, text, _c.Font, new Rectangle(6, 0, r.Width - aw - 8, r.Height), en ? Text : TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Glyph.ChevronDown, Icon(7.5F), new Rectangle(r.Width - aw - 2, 0, aw, r.Height), en ? TextMuted : TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Replaces the bright native 1-px border of single-line text boxes with a themed one (accent when focused).</summary>
    private sealed class BorderPainter : NativeWindow
    {
        private readonly TextBox _t;

        public BorderPainter(TextBox t)
        {
            _t = t;
            if (t.IsHandleCreated) AssignHandle(t.Handle);
            t.HandleCreated += (_, _) => AssignHandle(t.Handle);
            t.HandleDestroyed += (_, _) => ReleaseHandle();
            EventHandler redraw = (_, _) => { if (t.IsHandleCreated) RedrawWindow(t.Handle, IntPtr.Zero, IntPtr.Zero, 0x0400 | 0x0001 | 0x0100); };
            t.GotFocus += redraw;
            t.LostFocus += redraw;
            t.EnabledChanged += redraw;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x0085 || _t.BorderStyle != BorderStyle.FixedSingle || !_t.IsHandleCreated) return; // WM_NCPAINT
            var dc = GetWindowDC(_t.Handle);
            if (dc == IntPtr.Zero) return;
            try
            {
                using var g = Graphics.FromHdc(dc);
                using var p = new Pen(_t.Focused ? Accent : BorderStrong);
                g.DrawRectangle(p, 0, 0, _t.Width - 1, _t.Height - 1);
            }
            finally { ReleaseDC(_t.Handle, dc); }
        }
    }


    /// <summary>
    /// Owner-draws drop-down lists so the face and the list stay dark – including when disabled (Windows paints
    /// disabled flat combo boxes white, which looked broken on the dark theme).
    /// </summary>
    private static void AttachComboPainter(ComboBox cmb)
    {
        if (Painted.TryGetValue(cmb, out _)) return;
        Painted.Add(cmb, cmb);
        cmb.DrawMode = DrawMode.OwnerDrawFixed;
        cmb.ItemHeight = Math.Max(cmb.ItemHeight, cmb.Font.Height + 6);
        cmb.DrawItem += (_, e) =>
        {
            bool face = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool selected = (e.State & DrawItemState.Selected) != 0 && !face;
            var back = !cmb.Enabled ? Surface2 : selected ? Selection : Input;
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
            string text = e.Index >= 0 && e.Index < cmb.Items.Count ? cmb.GetItemText(cmb.Items[e.Index]) : "";
            var fore = !cmb.Enabled ? TextDim : selected ? Color.White : Text;
            TextRenderer.DrawText(e.Graphics, text, cmb.Font, new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        cmb.EnabledChanged += (_, _) => cmb.Invalidate();
    }

    private static Color EffectiveBack(Control c)
    {
        for (Control? p = c; p != null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return Bg;
    }

    private static Color MapCodeFore(Color c)
    {
        if (c.IsEmpty) return Text;
        // legacy neon green / white consoles → soft code text
        if (c.G > 200 && c.R < 80) return Color.FromArgb(0x9A, 0xDB, 0xA8);
        return IsLight(c) ? Color.FromArgb(0xD6, 0xDA, 0xE3) : Text;
    }

    private static void StylePanel(Panel p)
    {
        if (p.Tag as string == "keep") return;
        bool card = p.BorderStyle == BorderStyle.FixedSingle || (IsLight(p.BackColor) && p.BackColor != SystemColors.Control);
        if (card)
        {
            p.BorderStyle = BorderStyle.None;
            p.BackColor = Surface;
            if (p.Tag as string != Styled)
            {
                p.Paint += (_, e) =>
                {
                    using var pen = new Pen(Border);
                    e.Graphics.DrawRectangle(pen, 0, 0, p.ClientSize.Width - 1, p.ClientSize.Height - 1);
                };
                p.Tag ??= Styled;
            }
        }
        else if (p.BackColor == SystemColors.Control || IsLight(p.BackColor))
        {
            p.BackColor = p.Parent is Panel pp && pp.BackColor == Surface ? Surface : Bg;
        }
        else if (p.BackColor.GetBrightness() < 0.25f && p.BackColor != Bg && p.BackColor != Surface && p.BackColor != Surface2)
        {
            // legacy dark headers (45,45,48) → surface
            p.BackColor = Surface2;
        }
    }

    public static void StyleButton(Button b, Color? fill = null)
    {
        if (b.Tag as string == "keep") return;
        var bg = fill ?? MapButtonColor(b.BackColor);
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.BackColor = bg;
        b.ForeColor = bg == BtnNeutral ? Text : Color.White;
        b.FlatAppearance.BorderSize = bg == BtnNeutral ? 1 : 0;
        b.FlatAppearance.BorderColor = BorderStrong;
        b.FlatAppearance.MouseOverBackColor = Lighten(bg, 0.12f);
        b.FlatAppearance.MouseDownBackColor = Darken(bg, 0.12f);
        b.Font = UISemibold;
        b.Cursor = Cursors.Hand;
        b.Text = StripEmoji(b.Text);
        b.TextAlign = ContentAlignment.MiddleCenter;
    }

    public static void StyleGrid(DataGridView g)
    {
        g.EnableHeadersVisualStyles = false;
        g.BackgroundColor = Input;
        g.BorderStyle = BorderStyle.None;
        g.GridColor = Border;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.RowHeadersVisible = false;
        g.Font = UI;

        g.ColumnHeadersDefaultCellStyle.BackColor = Surface2;
        g.ColumnHeadersDefaultCellStyle.ForeColor = TextMuted;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Surface2;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextMuted;
        g.ColumnHeadersDefaultCellStyle.Font = UISemibold;
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 30;

        g.DefaultCellStyle.BackColor = Input;
        g.DefaultCellStyle.ForeColor = Text;
        g.DefaultCellStyle.SelectionBackColor = Selection;
        g.DefaultCellStyle.SelectionForeColor = Color.White;
        g.DefaultCellStyle.Padding = new Padding(4, 0, 0, 0);
        g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(0x17, 0x1A, 0x20);
        g.AlternatingRowsDefaultCellStyle.ForeColor = Text;
        g.RowTemplate.Height = 26;
        g.RowsDefaultCellStyle.BackColor = Input;

        foreach (DataGridViewColumn col in g.Columns)
        {
            col.HeaderText = StripEmoji(col.HeaderText);
            if (col.DefaultCellStyle.Font?.Name is "Consolas" or "Courier New") col.DefaultCellStyle.Font = Mono;
        }
        DarkScrollbars(g);
        foreach (Control child in g.Controls) DarkScrollbars(child);
        g.Tag ??= Styled;
    }

    // ── drawing helpers ──
    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        if (radius <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
