using System.Drawing.Drawing2D;
using System.Text;
using MemoryEditor.Runtime;
using MemoryEditor.UI;

namespace MemoryEditor.Xex;

/// <summary>Drag &amp; drop XEX → EXE converter with header inspector and batch mode.</summary>
public sealed class XexToolsPage : UserControl
{
    private readonly IAppHost _host;

    private sealed class Entry
    {
        public string Path = "";
        public XexFile? Xex;
        public string Status = "Pending";
        public string? Output;
        public byte[]? Image;
        public string? Error;
    }

    private readonly List<Entry> _entries = new();
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
    private readonly RichTextBox _details = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = false };
    private readonly CheckBox _chkAuto = new() { Text = "Convert automatically when files are added", Checked = true, AutoSize = true };
    private readonly CheckBox _chkNormalize = new() { Text = "Fix PE section table (loads directly in IDA / Ghidra)", Checked = true, AutoSize = true };
    private readonly CheckBox _chkSameFolder = new() { Text = "Save next to the source file", Checked = true, AutoSize = true };
    private readonly TextBox _outDir = new() { Width = 360, PlaceholderText = "Output folder", Enabled = false };
    private readonly DropZone _drop;

    public XexToolsPage(IAppHost host)
    {
        _host = host;
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        Padding = new Padding(18, 16, 18, 16);
        AllowDrop = true;

        // ── header ──
        var header = Ui.Card(DockStyle.Top, 92, new Padding(18, 14, 18, 10));
        var t = Ui.Title("XEX → EXE converter");
        var s = Ui.Muted("Decrypts (retail / devkit keys) and decompresses (basic + LZX) Xbox 360 executables into a PE image you can load in IDA, Ghidra or the Static Analysis page.");
        var glyph = Ui.Glyph(Theme.Glyph.Convert, 24F, Theme.Accent);
        glyph.Location = new Point(18, 20);
        t.Location = new Point(62, 14);
        s.Location = new Point(64, 44);
        s.MaximumSize = new Size(1100, 40);
        header.Controls.AddRange(new Control[] { glyph, t, s });

        // ── drop zone + options ──
        var top = new Panel { Dock = DockStyle.Top, Height = 205, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0), Tag = "keep" };
        _drop = new DropZone { Dock = DockStyle.Left, Width = 460 };
        _drop.Click += (_, _) => Browse();
        var options = Ui.Card(DockStyle.Fill, 0, new Padding(18, 12, 18, 12));
        var optFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface };
        var outRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 6) };
        var browseOut = IconButton.Create("", Theme.Glyph.Folder, ButtonVariant.Neutral, (_, _) =>
        {
            using var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog(FindForm()) == DialogResult.OK) _outDir.Text = fbd.SelectedPath;
        }, "Choose output folder");
        browseOut.Height = 26;
        outRow.Controls.Add(_outDir);
        outRow.Controls.Add(browseOut);
        _chkSameFolder.CheckedChanged += (_, _) => _outDir.Enabled = !_chkSameFolder.Checked;
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange(new Control[]
        {
            IconButton.Create("Convert selected", Theme.Glyph.Convert, ButtonVariant.Primary, async (_, _) => await ConvertAsync(Selected().ToList())),
            IconButton.Create("Convert all", Theme.Glyph.Convert, ButtonVariant.Neutral, async (_, _) => await ConvertAsync(_entries.ToList())),
            IconButton.Create("Discover offsets", Theme.Glyph.Wand, ButtonVariant.Neutral, async (_, _) => await DiscoverAsync()),
            IconButton.Create("Static Analysis", Theme.Glyph.Analyze, ButtonVariant.Neutral, async (_, _) => await SendToAnalysisAsync()),
            IconButton.Create("Open folder", Theme.Glyph.Folder, ButtonVariant.Ghost, (_, _) => OpenFolder()),
            IconButton.Create("Clear", Theme.Glyph.Clear, ButtonVariant.Ghost, (_, _) => { _entries.Clear(); RefreshList(); _details.Clear(); }),
        });
        optFlow.Controls.AddRange(new Control[] { _chkAuto, _chkNormalize, _chkSameFolder, outRow, buttons });
        options.Controls.Add(optFlow);
        top.Controls.Add(options);
        top.Controls.Add(Ui.Spacer(DockStyle.Left, 12));
        top.Controls.Add(_drop);

        // ── list + details ──
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 8, Orientation = Orientation.Vertical };
        foreach (var (name, w) in new[] { ("File", 260), ("Title ID", 90), ("Version", 90), ("Compression", 130), ("Encryption", 90), ("Status", 260) })
            _list.Columns.Add(name, w);
        _list.SelectedIndexChanged += (_, _) => ShowDetails();
        _list.DoubleClick += async (_, _) => await ConvertAsync(Selected().ToList());
        var listCard = Ui.Card(DockStyle.Fill, 0, new Padding(1));
        listCard.Controls.Add(_list);
        var detailCard = Ui.Card(DockStyle.Fill, 0, new Padding(14, 10, 10, 10));
        _details.Font = Theme.MonoSmall;
        detailCard.Controls.Add(_details);
        split.Panel1.Controls.Add(listCard);
        split.Panel2.Controls.Add(detailCard);
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0), Tag = "keep" };
        body.Controls.Add(split);

        Controls.Add(body);
        Controls.Add(top);
        Controls.Add(header);

        foreach (Control c in new Control[] { this, _drop, _list, _details })
        {
            c.AllowDrop = true;
            c.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) { e.Effect = DragDropEffects.Copy; _drop.Hot = true; } };
            c.DragLeave += (_, _) => _drop.Hot = false;
            c.DragDrop += async (_, e) =>
            {
                _drop.Hot = false;
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) await AddAsync(files);
            };
        }

        Theme.Apply(this);
        bool laid = false;
        Layout += (_, _) => { if (!laid && Width > 600) { laid = true; try { split.SplitterDistance = (int)(split.Width * 0.55); } catch { } } };
        _details.Text = "Drop one or more .xex files (or a folder) above.\n\nSupported: XEX2 retail & devkit, unencrypted, basic and LZX (normal) compression.\nDelta patches (.xexp) need their base executable and are not converted.";
    }

    private IEnumerable<Entry> Selected() =>
        _list.SelectedIndices.Cast<int>().Where(i => i < _entries.Count).Select(i => _entries[i]);

    private void Browse()
    {
        using var ofd = new OpenFileDialog { Filter = "Xbox 360 executables (*.xex)|*.xex|All files|*.*", Multiselect = true };
        if (ofd.ShowDialog(FindForm()) == DialogResult.OK) _ = AddAsync(ofd.FileNames);
    }

    public async Task AddAsync(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) files.AddRange(Directory.EnumerateFiles(p, "*.xex", SearchOption.AllDirectories));
            else if (File.Exists(p)) files.Add(p);
        }
        var added = new List<Entry>();
        foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_entries.Any(e => e.Path.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
            var e = new Entry { Path = f };
            try
            {
                e.Xex = await Task.Run(() => XexFile.Load(f));
                e.Status = e.Xex.IsPatch && e.Xex.CompressionType == 3 ? "Delta patch – not convertible" : "Ready";
            }
            catch (Exception ex) { e.Status = "Not a valid XEX"; e.Error = ex.Message; }
            _entries.Add(e);
            added.Add(e);
        }
        RefreshList();
        if (added.Count > 0)
        {
            _list.SelectedIndices.Clear();
            _list.SelectedIndices.Add(_entries.IndexOf(added[0]));
            _host.Log($"📦 Added {added.Count} XEX file(s)");
            if (_chkAuto.Checked) await ConvertAsync(added.Where(a => a.Status == "Ready").ToList());
        }
    }

    private void RefreshList()
    {
        _list.BeginUpdate();
        int sel = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        _list.Items.Clear();
        foreach (var e in _entries)
        {
            var it = new ListViewItem(Path.GetFileName(e.Path)) { ToolTipText = e.Path };
            it.SubItems.Add(e.Xex?.TitleId is > 0 ? e.Xex.TitleId.ToString("X8") : "—");
            it.SubItems.Add(e.Xex?.Version ?? "—");
            it.SubItems.Add(e.Xex?.CompressionName ?? "—");
            it.SubItems.Add(e.Xex?.EncryptionName ?? "—");
            it.SubItems.Add(e.Status);
            it.UseItemStyleForSubItems = false;
            it.SubItems[5].ForeColor = e.Status.StartsWith("Converted") ? Theme.Success : e.Error != null || e.Status.Contains("not") || e.Status.Contains("Failed") ? Theme.Danger : Theme.TextMuted;
            _list.Items.Add(it);
        }
        if (sel >= 0 && sel < _list.Items.Count) _list.SelectedIndices.Add(sel);
        _list.EndUpdate();
    }

    private void ShowDetails()
    {
        var e = Selected().FirstOrDefault();
        if (e == null) return;
        var sb = new StringBuilder();
        if (e.Xex != null) sb.Append(e.Xex.Describe());
        if (e.Error != null) sb.AppendLine().AppendLine($"Error: {e.Error}");
        if (e.Output != null) sb.AppendLine().AppendLine($"Output          {e.Output}");
        if (e.Image != null)
        {
            var pe = PeImage.TryParse(e.Image, e.Xex?.LoadAddress ?? 0);
            if (pe != null)
            {
                sb.AppendLine();
                sb.AppendLine($"PE image        machine 0x{pe.Machine:X4}{(pe.IsXbox360 ? " (PowerPC BE / Xenon)" : "")}, {pe.Sections.Count} sections");
                foreach (var sct in pe.Sections) sb.AppendLine($"  {sct}  size 0x{sct.VirtualSize:X}");
            }
            if (e.Xex != null)
            {
                var imports = e.Xex.ResolveImports(e.Image);
                if (imports.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"Imports         {imports.Count(i => i.IsThunk)} functions, {imports.Count(i => !i.IsThunk)} variables");
                    foreach (var g in imports.Where(i => i.IsThunk).GroupBy(i => i.Library))
                        sb.AppendLine($"  {g.Key,-16} {g.Count()} thunks (ordinals {string.Join(", ", g.Take(8).Select(i => $"0x{i.Ordinal:X}"))}{(g.Count() > 8 ? ", …" : "")})");
                }
            }
        }
        _details.Text = sb.ToString();
    }

    private string OutputPathFor(Entry e)
    {
        var dir = _chkSameFolder.Checked || string.IsNullOrWhiteSpace(_outDir.Text) ? Path.GetDirectoryName(e.Path)! : _outDir.Text;
        Directory.CreateDirectory(dir);
        var name = Path.GetFileNameWithoutExtension(e.Path);
        if (e.Xex?.TitleId is > 0) name += $"_{e.Xex.TitleId:X8}";
        return Path.Combine(dir, name + ".exe");
    }

    private async Task ConvertAsync(List<Entry> items)
    {
        items = items.Where(i => i.Xex != null && !(i.Xex.IsPatch && i.Xex.CompressionType == 3)).ToList();
        if (items.Count == 0) { _host.Toast("Select one or more valid XEX files", ToastHost.Kind.Info); return; }
        int ok = 0;
        foreach (var e in items)
        {
            e.Status = "Converting…";
            RefreshList();
            try
            {
                bool normalize = _chkNormalize.Checked;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (image, exe) = await Task.Run(() =>
                {
                    var img = e.Xex!.ExtractBasefile();
                    var file = (byte[])img.Clone();
                    if (normalize) PeImage.NormalizeRawPointers(file);
                    return (img, file);
                });
                var outPath = OutputPathFor(e);
                await File.WriteAllBytesAsync(outPath, exe);
                e.Image = image;
                e.Output = outPath;
                e.Error = null;
                e.Status = $"Converted ({e.Xex!.KeyUsed} key, {sw.ElapsedMilliseconds} ms)";
                ok++;
                _host.Log($"✅ {Path.GetFileName(e.Path)} → {outPath} ({exe.Length:N0} bytes)");
            }
            catch (Exception ex)
            {
                e.Status = "Failed";
                e.Error = ex.Message;
                _host.Log($"❌ {Path.GetFileName(e.Path)}: {ex.Message}");
            }
            RefreshList();
            ShowDetails();
        }
        _host.Toast(ok == items.Count ? $"Converted {ok} file(s) to EXE" : $"Converted {ok} of {items.Count} – see the status column",
            ok == items.Count ? ToastHost.Kind.Success : ToastHost.Kind.Warning);
    }

    private async Task<Entry?> EnsureImageAsync()
    {
        var e = Selected().FirstOrDefault();
        if (e == null || e.Xex == null) { _host.Toast("Select a XEX first", ToastHost.Kind.Info); return null; }
        if (e.Image == null) await ConvertAsync(new List<Entry> { e });
        return e.Image == null ? null : e;
    }

    private async Task DiscoverAsync()
    {
        var e = await EnsureImageAsync();
        if (e == null) return;
        var module = ModuleImage.FromPe(Path.GetFileName(e.Path), e.Image!, e.Xex!.LoadAddress, e.Xex);
        _host.DiscoverFromImage(module);
    }

    private async Task SendToAnalysisAsync()
    {
        var e = await EnsureImageAsync();
        if (e == null) return;
        _host.SendToStaticAnalysis(Path.GetFileName(e.Path), e.Image!, e.Xex!.LoadAddress);
    }

    private void OpenFolder()
    {
        var e = Selected().FirstOrDefault();
        var target = e?.Output ?? e?.Path;
        if (target == null) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{target}\""); } catch { }
    }

    private sealed class DropZone : Control
    {
        private bool _hot;
        public bool Hot { get => _hot; set { _hot = value; Invalidate(); } }
        public DropZone()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Theme.Exclude(this);
        }
        protected override void OnMouseEnter(EventArgs e) { Hot = true; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hot = false; base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var path = Theme.RoundRect(r, 10))
            {
                using var b = new SolidBrush(_hot ? Theme.Blend(Theme.Surface, Theme.Accent, 0.10f) : Theme.Surface);
                g.FillPath(b, path);
                using var p = new Pen(_hot ? Theme.Accent : Theme.BorderStrong, 1.5f) { DashStyle = DashStyle.Dash };
                g.DrawPath(p, path);
            }
            var icon = Theme.Icon(28F);
            var gs = TextRenderer.MeasureText(Theme.Glyph.Upload, icon);
            TextRenderer.DrawText(g, Theme.Glyph.Upload, icon, new Point((Width - gs.Width) / 2, 22), _hot ? Theme.Accent : Theme.TextMuted);
            TextRenderer.DrawText(g, "Drop .xex files or folders here", Theme.Heading, new Rectangle(0, 82, Width, 24), Theme.Text, TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, "or click to browse", Theme.UI, new Rectangle(0, 108, Width, 20), Theme.TextMuted, TextFormatFlags.HorizontalCenter);
        }
    }
}
