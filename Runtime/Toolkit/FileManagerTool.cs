using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>
/// Console file manager over XBDM: browse every drive, download / upload (drag &amp; drop, folders recursive),
/// create folders, rename, delete, launch executables and hand XEX files to the Game Library for offline analysis.
/// </summary>
public sealed class FileManagerTool : ToolBase
{
    private readonly FlowLayoutPanel _drives = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 6) };
    private readonly TextBox _path = T(@"Hdd:\", 420, @"Hdd:\Games");
    private readonly DataGridView _grid = Grid(("Name", 360), ("Size", 110), ("Type", 110), ("Modified", 150));
    private readonly Ui.ThinProgress _progress = new() { Dock = DockStyle.Top, Visible = false };
    private readonly Label _status = L("");
    private readonly IconButton _btnCancel;
    private readonly Stack<string> _history = new();
    private List<XBDMClient.FileEntry> _entries = new();
    private CancellationTokenSource? _cts;
    private bool _busy, _drivesLoaded;
    private int _sortCol;
    private bool _desc;

    public override string Key => "files";
    public override string Title => "File Manager";
    public override string Glyph => Theme.Glyph.Folder;
    public override string Description => "Browse, upload and download console files";

    public FileManagerTool(IToolHost host) : base(host)
    {
        _grid.VirtualMode = true;
        _grid.AllowDrop = true;
        foreach (DataGridViewColumn c in _grid.Columns) c.SortMode = DataGridViewColumnSortMode.Programmatic;
        _grid.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        _grid.Columns[1].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex >= _entries.Count) return;
            var f = _entries[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => (f.IsDirectory ? "📁  " : "      ") + f.Name,
                1 => f.IsDirectory ? "" : HumanSize(f.Size),
                2 => f.IsDirectory ? "Folder" : KindOf(f.Name),
                _ => f.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "",
            };
        };
        _grid.CellFormatting += (sender, e) =>
        {
            if (e.RowIndex < _entries.Count && e.CellStyle != null && e.ColumnIndex == 0 && _entries[e.RowIndex].IsDirectory) e.CellStyle.ForeColor = Theme.Accent;
        };
        _grid.ColumnHeaderMouseClick += (_, e) => { _desc = _sortCol == e.ColumnIndex && !_desc; _sortCol = e.ColumnIndex; SortEntries(); };
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await OpenAsync(_entries[e.RowIndex]); };
        _grid.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && Sel() is { } f) { e.SuppressKeyPress = true; await OpenAsync(f); }
            else if (e.KeyCode == Keys.Back) { e.SuppressKeyPress = true; await UpAsync(); }
            else if (e.KeyCode == Keys.Delete) { e.SuppressKeyPress = true; await DeleteAsync(); }
            else if (e.KeyCode == Keys.F2) { e.SuppressKeyPress = true; await RenameAsync(); }
            else if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; await LoadAsync(_path.Text, false); }
        };
        _grid.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        _grid.DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) await UploadAsync(files); };
        _grid.ContextMenuStrip = BuildMenu();
        _path.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await LoadAsync(_path.Text, true); } };

        _btnCancel = B("Cancel", Theme.Glyph.Stop, ButtonVariant.Danger, (_, _) => _cts?.Cancel());
        _btnCancel.Visible = false;

        var nav = Row(
            B("", Theme.Glyph.ChevronUp, ButtonVariant.Neutral, async (_, _) => await UpAsync(), "Parent folder (Backspace)"),
            B("Back", Theme.Glyph.Refresh, ButtonVariant.Ghost, async (_, _) => { if (_history.Count > 0) await LoadAsync(_history.Pop(), false); }),
            _path,
            B("Go", Theme.Glyph.Play, ButtonVariant.Primary, async (_, _) => await LoadAsync(_path.Text, true)),
            B("", Theme.Glyph.Refresh, ButtonVariant.Ghost, async (_, _) => await LoadAsync(_path.Text, false), "Refresh (F5)"));
        var actions = Row(
            B("Download", Theme.Glyph.Download, ButtonVariant.Primary, async (_, _) => await DownloadAsync(), "Save the selection to this PC (folders included)"),
            B("Upload…", Theme.Glyph.Upload, ButtonVariant.Neutral, async (_, _) => await UploadPickAsync(false), "Upload files into this folder (or drag files onto the list)"),
            B("Upload folder…", Theme.Glyph.Folder, ButtonVariant.Neutral, async (_, _) => await UploadPickAsync(true)),
            B("New folder", Theme.Glyph.Add, ButtonVariant.Neutral, async (_, _) => await NewFolderAsync()),
            B("Rename", Theme.Glyph.Format, ButtonVariant.Neutral, async (_, _) => await RenameAsync(), "F2"),
            B("Delete", Theme.Glyph.Delete, ButtonVariant.Danger, async (_, _) => await DeleteAsync(), "Del"),
            B("Launch", Theme.Glyph.Play, ButtonVariant.Success, async (_, _) => await LaunchAsync(), "Start the selected .xex"),
            B("Analyse", Theme.Glyph.Wand, ButtonVariant.Neutral, async (_, _) => await AnalyseAsync(), "Download an .xex and map its offsets in the Game Library"),
            _btnCancel, _status);

        Controls.Add(Card(_grid));
        Controls.Add(_progress);
        Controls.Add(actions);
        Controls.Add(nav);
        Controls.Add(_drives);
        _status.Text = "Connect to browse the console's drives";
    }

    public override void OnShown()
    {
        if (!_drivesLoaded && Client != null) _ = LoadDrivesAsync();
    }

    public override void OnConnectionChanged(bool connected)
    {
        if (!connected) { _cts?.Cancel(); _drivesLoaded = false; _drives.Controls.Clear(); }
        else if (Visible) _ = LoadDrivesAsync();
    }

    private XBDMClient.FileEntry? Sel() => _grid.CurrentCell?.RowIndex is int i && i >= 0 && i < _entries.Count ? _entries[i] : null;
    private List<XBDMClient.FileEntry> SelAll() =>
        _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i >= 0 && i < _entries.Count).OrderBy(i => i).Select(i => _entries[i]).ToList();

    private static string HumanSize(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / 1024.0:0.#} KB",
        _ => $"{b} B",
    };

    private static string KindOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".xex" => "Executable",
        ".xexp" => "Title update patch",
        ".dll" or ".xexdll" => "Library",
        ".bin" => "Binary",
        ".ini" or ".cfg" or ".txt" or ".xml" or ".json" => "Text / config",
        ".png" or ".jpg" or ".bmp" or ".dds" => "Image",
        ".xzp" or ".xpr" => "Resources",
        "" => "File",
        var x => x.TrimStart('.').ToUpperInvariant() + " file",
    };

    private static string Join(string dir, string name) => dir.TrimEnd('\\') + "\\" + name;

    // ── navigation ──

    private async Task LoadDrivesAsync()
    {
        var c = Client;
        if (c == null) return;
        try
        {
            var drives = await c.GetDrivesAsync();
            _drives.Controls.Clear();
            _drives.Controls.Add(L("Drives"));
            foreach (var d in drives.OrderBy(d => d.StartsWith("Hdd", StringComparison.OrdinalIgnoreCase) ? 0 : d.StartsWith("Usb", StringComparison.OrdinalIgnoreCase) ? 1 : 2).ThenBy(d => d))
            {
                var drive = d;
                _drives.Controls.Add(B(drive, Theme.Glyph.Folder, ButtonVariant.Neutral, async (_, _) => await LoadAsync($@"{drive}:\", true)));
            }
            Theme.Apply(_drives);
            _drivesLoaded = true;
            if (_entries.Count == 0) await LoadAsync(drives.Any(x => x.Equals("Hdd", StringComparison.OrdinalIgnoreCase)) ? @"Hdd:\" : $@"{drives.FirstOrDefault() ?? "Hdd"}:\", false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
    }

    private async Task LoadAsync(string path, bool pushHistory)
    {
        var c = RequireClient();
        if (c == null || _busy) return;
        path = XBDMClient.ToXbdmPath(path.Trim());
        if (path.Length == 0) return;
        if (!path.EndsWith("\\")) path += "\\";
        try
        {
            var list = await c.ListDirectoryAsync(path);
            if (list.Count == 0 && !(await c.SendRawAsync($"dirlist name=\"{path}\"")).Ok)
            {
                App.Toast($"{path} does not exist or cannot be listed", ToastHost.Kind.Warning);
                return;
            }
            if (pushHistory && _path.Tag is string prev && prev != path) _history.Push(prev);
            _path.Text = path;
            _path.Tag = path;
            _entries = list;
            SortEntries();
            int files = list.Count(f => !f.IsDirectory);
            _status.Text = $"{list.Count(f => f.IsDirectory)} folders · {files} files · {HumanSize(list.Where(f => !f.IsDirectory).Sum(f => f.Size))}";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { App.Toast($"Could not list {path}: {ex.Message}", ToastHost.Kind.Error); }
    }

    private void SortEntries()
    {
        Func<XBDMClient.FileEntry, IComparable> key = _sortCol switch
        {
            1 => f => f.Size,
            2 => f => f.IsDirectory ? "" : KindOf(f.Name),
            3 => f => f.Modified ?? DateTime.MinValue,
            _ => f => f.Name.ToLowerInvariant(),
        };
        var dirs = _entries.Where(f => f.IsDirectory);
        var files = _entries.Where(f => !f.IsDirectory);
        _entries = (_desc ? dirs.OrderByDescending(key) : dirs.OrderBy(key)).Concat(_desc ? files.OrderByDescending(key) : files.OrderBy(key)).ToList();
        _grid.RowCount = 0;
        _grid.RowCount = _entries.Count;
    }

    private async Task UpAsync()
    {
        var p = (_path.Tag as string ?? _path.Text).TrimEnd('\\');
        int i = p.LastIndexOf('\\');
        if (i <= 0) return;
        await LoadAsync(p[..(i + 1)], true);
    }

    private async Task OpenAsync(XBDMClient.FileEntry f)
    {
        if (f.IsDirectory) await LoadAsync(f.Path, true);
        else if (f.Name.EndsWith(".xex", StringComparison.OrdinalIgnoreCase)) await LaunchAsync();
        else await DownloadAsync();
    }

    // ── transfers ──

    private void BeginBusy(string text)
    {
        _busy = true;
        _cts = new CancellationTokenSource();
        _progress.Visible = true;
        _progress.Value = 0;
        _btnCancel.Visible = true;
        _status.Text = text;
    }

    private void EndBusy()
    {
        _busy = false;
        _progress.Visible = false;
        _btnCancel.Visible = false;
    }

    private async Task DownloadAsync()
    {
        var c = RequireClient();
        var sel = SelAll();
        if (c == null || sel.Count == 0 || _busy) return;
        string? target;
        if (sel.Count == 1 && !sel[0].IsDirectory)
        {
            using var sfd = new SaveFileDialog { FileName = sel[0].Name, Title = "Download from console" };
            if (sfd.ShowDialog(FindForm()) != DialogResult.OK) return;
            target = sfd.FileName;
        }
        else
        {
            using var fbd = new FolderBrowserDialog { Description = "Download the selection into…" };
            if (fbd.ShowDialog(FindForm()) != DialogResult.OK) return;
            target = fbd.SelectedPath;
        }

        BeginBusy("Preparing download…");
        long total = 0;
        int count = 0;
        try
        {
            async Task Get(XBDMClient.FileEntry f, string local)
            {
                _cts!.Token.ThrowIfCancellationRequested();
                if (f.IsDirectory)
                {
                    Directory.CreateDirectory(local);
                    foreach (var child in await c.ListDirectoryAsync(f.Path)) await Get(child, Path.Combine(local, child.Name));
                    return;
                }
                _status.Text = $"Downloading {f.Name} ({HumanSize(f.Size)})…";
                await using (var fs = File.Create(local))
                {
                    total += await c.DownloadFileAsync(f.Path, fs, new Progress<(long Done, long Total)>(p => _progress.Value = p.Total == 0 ? 1 : (double)p.Done / p.Total), _cts.Token);
                }
                count++;
            }

            if (sel.Count == 1 && !sel[0].IsDirectory) await Get(sel[0], target);
            else foreach (var f in sel) await Get(f, Path.Combine(target, f.Name));
            App.Toast($"Downloaded {count} file(s) · {HumanSize(total)}", ToastHost.Kind.Success);
            App.Log($"⬇️ Downloaded {count} file(s) ({HumanSize(total)}) → {target}");
        }
        catch (OperationCanceledException) { App.Toast("Download cancelled", ToastHost.Kind.Warning); }
        catch (Exception ex) { App.Toast($"Download failed: {ex.Message}", ToastHost.Kind.Error); }
        finally { EndBusy(); _status.Text = $"{count} file(s) downloaded"; }
    }

    private async Task UploadPickAsync(bool folder)
    {
        if (folder)
        {
            using var fbd = new FolderBrowserDialog { Description = "Upload this folder into the current console folder" };
            if (fbd.ShowDialog(FindForm()) == DialogResult.OK) await UploadAsync(new[] { fbd.SelectedPath });
        }
        else
        {
            using var ofd = new OpenFileDialog { Multiselect = true, Title = "Upload to console" };
            if (ofd.ShowDialog(FindForm()) == DialogResult.OK) await UploadAsync(ofd.FileNames);
        }
    }

    private async Task UploadAsync(string[] localPaths)
    {
        var c = RequireClient();
        if (c == null || _busy || localPaths.Length == 0) return;
        var dir = _path.Tag as string ?? _path.Text;
        BeginBusy("Uploading…");
        int count = 0;
        long total = 0;
        try
        {
            async Task Put(string local, string remote)
            {
                _cts!.Token.ThrowIfCancellationRequested();
                if (Directory.Exists(local))
                {
                    await c.MakeDirectoryAsync(remote);
                    foreach (var e in Directory.EnumerateFileSystemEntries(local)) await Put(e, Join(remote, Path.GetFileName(e)));
                    return;
                }
                var info = new FileInfo(local);
                _status.Text = $"Uploading {info.Name} ({HumanSize(info.Length)})…";
                await using var fs = File.OpenRead(local);
                await c.UploadFileAsync(remote, fs, info.Length, new Progress<(long Done, long Total)>(p => _progress.Value = p.Total == 0 ? 1 : (double)p.Done / p.Total), _cts.Token);
                count++;
                total += info.Length;
            }
            foreach (var p in localPaths) await Put(p, Join(dir, Path.GetFileName(p.TrimEnd('\\'))));
            App.Toast($"Uploaded {count} file(s) · {HumanSize(total)}", ToastHost.Kind.Success);
            App.Log($"⬆️ Uploaded {count} file(s) ({HumanSize(total)}) → {dir}");
        }
        catch (OperationCanceledException) { App.Toast("Upload cancelled", ToastHost.Kind.Warning); }
        catch (Exception ex) { App.Toast($"Upload failed: {ex.Message}", ToastHost.Kind.Error); }
        finally { EndBusy(); await LoadAsync(dir, false); }
    }

    // ── file operations ──

    private async Task NewFolderAsync()
    {
        var c = RequireClient();
        if (c == null) return;
        var name = Prompt.Show(FindForm(), "New folder", "Folder name", "New Folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        var r = await c.MakeDirectoryAsync(Join(_path.Tag as string ?? _path.Text, name.Trim()));
        if (!r.Ok) App.Toast($"Could not create the folder: {r.Message}", ToastHost.Kind.Error);
        await LoadAsync(_path.Text, false);
    }

    private async Task RenameAsync()
    {
        var c = RequireClient();
        if (c == null || Sel() is not { } f) return;
        var name = Prompt.Show(FindForm(), "Rename", "New name", f.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == f.Name) return;
        var r = await c.RenameAsync(f.Path, Join(_path.Tag as string ?? _path.Text, name.Trim()));
        if (!r.Ok) App.Toast($"Rename failed: {r.Message}", ToastHost.Kind.Error);
        await LoadAsync(_path.Text, false);
    }

    private async Task DeleteAsync()
    {
        var c = RequireClient();
        var sel = SelAll();
        if (c == null || sel.Count == 0 || _busy) return;
        var what = sel.Count == 1 ? $"“{sel[0].Name}”" + (sel[0].IsDirectory ? " and everything inside it" : "") : $"{sel.Count} items";
        if (MessageBox.Show(FindForm(), $"Permanently delete {what} from the console?\n\nThis cannot be undone.", "Delete from console",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        BeginBusy("Deleting…");
        int n = 0;
        try
        {
            async Task Del(XBDMClient.FileEntry f)
            {
                _cts!.Token.ThrowIfCancellationRequested();
                if (f.IsDirectory) foreach (var child in await c.ListDirectoryAsync(f.Path)) await Del(child); // XBDM only removes empty folders
                var r = await c.DeleteAsync(f.Path, f.IsDirectory);
                if (r.Ok) n++; else App.Log($"❌ Could not delete {f.Path}: {r.Message}");
            }
            foreach (var f in sel) await Del(f);
            App.Toast($"Deleted {n} item(s)", ToastHost.Kind.Success);
        }
        catch (OperationCanceledException) { App.Toast("Stopped", ToastHost.Kind.Warning); }
        catch (Exception ex) { App.Toast($"Delete failed: {ex.Message}", ToastHost.Kind.Error); }
        finally { EndBusy(); await LoadAsync(_path.Text, false); }
    }

    private async Task LaunchAsync()
    {
        var c = RequireClient();
        if (c == null || Sel() is not { IsDirectory: false } f) return;
        if (!f.Name.EndsWith(".xex", StringComparison.OrdinalIgnoreCase)) { App.Toast("Select an .xex executable to launch", ToastHost.Kind.Info); return; }
        try
        {
            if (await c.LaunchTitleAsync(f.Path)) App.Toast($"Launching {f.Name}", ToastHost.Kind.Success);
            else App.Toast("The console refused to launch it", ToastHost.Kind.Error);
        }
        catch (IOException) { App.Toast($"Launching {f.Name} – the console is switching titles", ToastHost.Kind.Info); }
    }

    /// <summary>Downloads an executable to a temp file and maps it offline through the Runtime Offsets / Game Library pipeline.</summary>
    private async Task AnalyseAsync()
    {
        var c = RequireClient();
        if (c == null || _busy || Sel() is not { IsDirectory: false } f) return;
        if (!(f.Name.EndsWith(".xex", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            App.Toast("Select an .xex (or .exe) to analyse", ToastHost.Kind.Info);
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "MemoryEditor", "console");
        Directory.CreateDirectory(dir);
        var local = Path.Combine(dir, f.Name);
        BeginBusy($"Downloading {f.Name} for analysis…");
        try
        {
            await using (var fs = File.Create(local))
                await c.DownloadFileAsync(f.Path, fs, new Progress<(long Done, long Total)>(p => _progress.Value = p.Total == 0 ? 1 : (double)p.Done / p.Total), _cts!.Token);
            var module = await Task.Run(() => RuntimeOffsetsPage.LoadModuleFromFile(local));
            App.DiscoverFromImage(module);
            App.Log($"🔬 {f.Name} downloaded – analysing offline ({HumanSize(f.Size)})");
        }
        catch (OperationCanceledException) { App.Toast("Cancelled", ToastHost.Kind.Warning); }
        catch (Exception ex) { App.Toast($"Could not analyse {f.Name}: {ex.Message}", ToastHost.Kind.Error); }
        finally { EndBusy(); }
    }

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Open", null, async (_, _) => { if (Sel() is { } f) await OpenAsync(f); });
        m.Items.Add("Download…", null, async (_, _) => await DownloadAsync());
        m.Items.Add("Launch", null, async (_, _) => await LaunchAsync());
        m.Items.Add("Analyse offsets", null, async (_, _) => await AnalyseAsync());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Rename…", null, async (_, _) => await RenameAsync());
        m.Items.Add("Delete", null, async (_, _) => await DeleteAsync());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Copy path", null, (_, _) => { if (Sel() is { } f) Clipboard.SetText(f.Path); });
        Scripting.ScriptIde.StyleMenu(m);
        return m;
    }
}
