using System.Drawing.Drawing2D;
using System.Drawing.Text;
using MemoryEditor.UI;

namespace MemoryEditor.Library;

/// <summary>Generated cover art (gradient + monogram) used when a game has no cover image.</summary>
public static class CoverArt
{
    private static readonly Color[][] Palettes =
    {
        new[] { Color.FromArgb(0x3A, 0x7B, 0xF2), Color.FromArgb(0x7A, 0x4C, 0xE8) },
        new[] { Color.FromArgb(0x13, 0x9E, 0x86), Color.FromArgb(0x2B, 0x6C, 0xD8) },
        new[] { Color.FromArgb(0xE0, 0x6A, 0x3B), Color.FromArgb(0xC2, 0x31, 0x6B) },
        new[] { Color.FromArgb(0x5B, 0x9A, 0x2E), Color.FromArgb(0x14, 0x7E, 0x77) },
        new[] { Color.FromArgb(0x9D, 0x4E, 0xDD), Color.FromArgb(0xE0, 0x4F, 0x8E) },
        new[] { Color.FromArgb(0xD6, 0x9A, 0x22), Color.FromArgb(0xD5, 0x52, 0x2E) },
        new[] { Color.FromArgb(0x2F, 0x86, 0xC9), Color.FromArgb(0x1E, 0xB2, 0x9A) },
        new[] { Color.FromArgb(0x5A, 0x63, 0x78), Color.FromArgb(0x2C, 0x33, 0x44) },
    };

    private static int Hash(string s)
    {
        unchecked
        {
            int h = 23;
            foreach (var c in s) h = h * 31 + char.ToLowerInvariant(c);
            return h & 0x7FFFFFFF;
        }
    }

    public static Color AccentFor(string name) => Palettes[Hash(name) % Palettes.Length][0];

    /// <summary>"Call of Duty: Black Ops II" → "BO", "Halo 3" → "H3", "Minecraft" → "MI".</summary>
    public static string Monogram(string name)
    {
        var n = name.Contains(':') ? name[(name.IndexOf(':') + 1)..] : name;
        var words = n.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                     .Where(w => w.Length > 0 && !(w.Length <= 3 && w.All(char.IsLower))).ToList();
        if (words.Count == 0) return "?";
        if (words.Count == 1) return words[0].Length >= 2 ? words[0][..2].ToUpperInvariant() : words[0].ToUpperInvariant();
        return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }

    public static void Draw(Graphics g, Rectangle r, string name, Image? cover, float radius)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundRect(r, radius);
        if (cover != null)
        {
            var state = g.Save();
            g.SetClip(path);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // cover-fit (crop to fill)
            float scale = Math.Max((float)r.Width / cover.Width, (float)r.Height / cover.Height);
            float w = cover.Width * scale, h = cover.Height * scale;
            g.DrawImage(cover, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
            g.Restore(state);
            return;
        }
        var pal = Palettes[Hash(name) % Palettes.Length];
        using (var b = new LinearGradientBrush(r, pal[0], pal[1], 45f)) g.FillPath(b, path);
        using (var sheen = new LinearGradientBrush(r, Color.FromArgb(50, Color.White), Color.FromArgb(0, Color.White), 90f))
            g.FillPath(sheen, path);
        float size = Math.Max(8f, r.Height * 0.34f);
        using var f = new Font("Segoe UI Semibold", size, GraphicsUnit.Pixel);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        TextRenderer.DrawText(g, Monogram(name), f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>Image cache for user supplied cover art (one decoded image per path; disposed with the page).</summary>
public sealed class CoverCache : IDisposable
{
    private readonly Dictionary<string, Image?> _images = new(StringComparer.OrdinalIgnoreCase);

    public Image? Get(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_images.TryGetValue(path, out var img)) return img;
        try
        {
            // load through a copy so the file is not locked on disk
            using var fs = File.OpenRead(path);
            using var tmp = Image.FromStream(fs);
            img = new Bitmap(tmp);
        }
        catch { img = null; }
        _images[path] = img;
        return img;
    }

    public void Invalidate(string path)
    {
        if (_images.Remove(path, out var img)) img?.Dispose();
    }

    public void Dispose()
    {
        foreach (var i in _images.Values) i?.Dispose();
        _images.Clear();
    }
}

/// <summary>Owner-drawn, launcher style list of games.</summary>
public sealed class GameListBox : ListBox
{
    public Func<GameRecord, bool>? IsRunning { get; set; }
    public Func<GameRecord, Image?>? CoverFor { get; set; }
    private int _hover = -1;

    public GameListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 64;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        Theme.Exclude(this);
        Theme.DarkScrollbars(this);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexFromPoint(e.Location);
        if (i != _hover)
        {
            int old = _hover;
            _hover = i;
            if (old >= 0 && old < Items.Count) Invalidate(GetItemRectangle(old));
            if (i >= 0 && i < Items.Count) Invalidate(GetItemRectangle(i));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hover >= 0 && _hover < Items.Count) Invalidate(GetItemRectangle(_hover));
        _hover = -1;
        base.OnMouseLeave(e);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, e.Bounds);
        if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not GameRecord rec) return;

        bool sel = (e.State & DrawItemState.Selected) != 0, hov = e.Index == _hover;
        var card = new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 3, e.Bounds.Width - 12, e.Bounds.Height - 6);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (sel || hov)
        {
            using var path = Theme.RoundRect(card, 8);
            using var b = new SolidBrush(sel ? Theme.Blend(Theme.Surface2, Theme.Accent, 0.20f) : Theme.Surface2);
            g.FillPath(b, path);
            if (sel) { using var p = new Pen(Theme.Blend(Theme.Border, Theme.Accent, 0.5f)); g.DrawPath(p, path); }
        }

        var tile = new Rectangle(card.X + 8, card.Y + 7, card.Height - 14, card.Height - 14);
        CoverArt.Draw(g, tile, rec.Name, CoverFor?.Invoke(rec), 7);

        bool running = IsRunning?.Invoke(rec) == true;
        int tx = tile.Right + 10, right = card.Right - 10;
        if (running)
        {
            var pill = new RectangleF(right - 58, card.Y + 9, 58, 18);
            using var path = Theme.RoundRect(pill, 9);
            using var b = new SolidBrush(Theme.Blend(Theme.Surface2, Theme.Success, 0.25f));
            g.FillPath(b, path);
            using var dot = new SolidBrush(Theme.Success);
            g.FillEllipse(dot, pill.X + 7, pill.Y + 6, 6, 6);
            TextRenderer.DrawText(g, "Running", Theme.UISmall, new Rectangle((int)pill.X + 14, (int)pill.Y, (int)pill.Width - 16, (int)pill.Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            right -= 64;
        }
        if (rec.Favorite)
        {
            TextRenderer.DrawText(g, "★", Theme.UI, new Rectangle(right - 16, card.Y + 8, 16, 18), Theme.Warning, TextFormatFlags.HorizontalCenter);
            right -= 18;
        }

        TextRenderer.DrawText(g, rec.Name, sel ? Theme.UISemibold : Theme.UI, new Rectangle(tx, card.Y + 9, right - tx, 20),
            Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        var b2 = rec.Current;
        string sub = b2 == null
            ? (rec.LaunchPath.Length > 0 ? "Not pulled yet · ready to launch" : "Not pulled yet")
            : $"{b2.Label} · {b2.OffsetCount:N0} offsets" + (rec.Builds.Count > 1 ? $" · {rec.Builds.Count} builds" : "");
        TextRenderer.DrawText(g, sub, Theme.UISmall, new Rectangle(tx, card.Y + 30, card.Right - 10 - tx, 18),
            Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Large cover tile for the selected game.</summary>
public sealed class CoverTile : Control
{
    public string GameName { get; set; } = "";
    public Image? Cover { get; set; }

    public CoverTile()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Theme.Exclude(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Surface);
        if (GameName.Length == 0)
        {
            using var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12);
            using var p = new Pen(Theme.BorderStrong) { DashStyle = DashStyle.Dash };
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(p, path);
            TextRenderer.DrawText(e.Graphics, Theme.Glyph.Game, Theme.Icon(26F), ClientRectangle, Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        CoverArt.Draw(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), GameName, Cover, 12);
    }
}
