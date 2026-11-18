using System.Text.RegularExpressions;
using MemoryEditor.Runtime;
using MemoryEditor.UI;

namespace MemoryEditor.Library;

public sealed partial class GameLibraryPage
{
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 4000 };
    private string _lastTitleKey = "";
    private string _pendingTitleKey = "";
    private DateTime _titleChangedAt;
    private bool _watchBusy;
    private string? _launchPullId;
    private PullResult? _lastPull;
    private bool _initialized;

    /// <summary>Raised on the UI thread when a different executable / build starts running: (title key, file name).</summary>
    public event Action<string, string>? TitleChanged;

    // progress reporting is called from worker threads at a high rate – coalesce onto the UI thread
    private string _reportText = "";
    private double _reportValue;
    private int _reportQueued;

    private const uint Bo2TitleId = 0x415608C3;

    // ═════════════════════════════════════════════════════════════════════════
    //  lifecycle
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Seeds the bundled BO2 TU18 file (first run), then restores the last selected game.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            if (!_lib.Settings.BundledImported)
            {
                var bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bo2_tu18_offsets.txt");
                if (File.Exists(bundled))
                {
                    var rec = _lib.Games.FirstOrDefault(g => g.TitleId == Bo2TitleId)
                              ?? new GameRecord { Name = "Call of Duty: Black Ops II", TitleId = Bo2TitleId, ModuleName = "default_mp.xex" };
                    if (!_lib.Games.Contains(rec)) _lib.Games.Add(rec);
                    await AddImportedBuildAsync(rec, bundled, "TU18");
                    _host.Log("📚 Added Black Ops II TU18 offsets to the game library");
                }
                _lib.Settings.BundledImported = true;
                _lib.Save();
            }
        }
        catch (Exception ex) { _host.Log($"⚠️ Could not import the bundled offsets: {ex.Message}"); }

        RefreshList();
        var sel = _lib.Find(_lib.Settings.SelectedGameId) ?? _list.Items.Cast<GameRecord>().FirstOrDefault();
        if (sel != null) await SelectGameAsync(sel);
        else UpdateHero();
    }

    /// <summary>Called by the shell after a successful connection.</summary>
    public async Task OnConnectedAsync()
    {
        var c = _host.Client;
        if (c == null || !c.IsConnected) return;
        UpdateHero();
        try
        {
            SetStatus("Identifying the running game…");
            var t = await c.GetRunningTitleAsync();
            _lastTitleKey = t is { } v ? TitleKey(v.Path, v.Timestamp) : "";
            if (t is { } tv) TitleChanged?.Invoke(_lastTitleKey, Path.GetFileName(tv.Path));
            var g = await OffsetPuller.IdentifyAsync(c, _host.Log);
            if (g != null) await HandleIdentifiedAsync(g, onConnect: true);
            else SetStatus("No game detected – start one and it will be picked up automatically");
        }
        catch (Exception ex) { _host.Log($"⚠️ Game identification failed: {ex.Message}"); }
        finally { UpdateWatcher(); UpdateHero(); }
    }

    public void OnDisconnected()
    {
        if (InvokeRequired) { BeginInvoke(OnDisconnected); return; }
        _watch.Stop();
        _running = null;
        _lastTitleKey = _pendingTitleKey = "";
        _cts?.Cancel();
        RefreshList();
        UpdateHero();
    }

    public void OnConnectionChanged()
    {
        if (InvokeRequired) { BeginInvoke(OnConnectionChanged); return; }
        UpdateWatcher();
        UpdateHero();
    }

    private static string TitleKey(string path, uint ts) => $"{path}|{ts:X8}";

    private void MarkRunning(GameInfo g)
    {
        _running = g;
        RefreshList();
        UpdateHero();
    }

    /// <summary>Decides what to do with an identified title: load its known build, or pull it.</summary>
    private async Task HandleIdentifiedAsync(GameInfo g, bool onConnect)
    {
        MarkRunning(g);
        var rec = _lib.FindOrCreate(g);
        _lib.Save();
        _db.ShowDetected(g);

        bool forced = _launchPullId == rec.Id;
        if (forced) _launchPullId = null;

        var build = rec.FindBuild(g.Timestamp, g.Checksum, g.ModuleName, g.Size);
        bool hasFile = build != null && File.Exists(GameLibrary.ResolvePath(build.OffsetFile));
        bool auto = onConnect ? _lib.Settings.AutoPullOnConnect : _lib.Settings.AutoPullNewBuilds;
        _host.Log($"🎮 Running: {rec.Name} ({g.ModuleName}{(g.Version.Length > 0 ? $" v{g.Version}" : "")}, build 0x{g.Timestamp:X8})" +
                  (hasFile ? $" – known build {build!.Label}" : " – new build"));

        if (forced || (!hasFile && auto))
        {
            await PullRunningAsync(userInitiated: forced, identified: g);
            return;
        }

        if (_lib.Settings.LoadIntoDatabase || _selected == null)
        {
            if (build != null) rec.SelectedBuild = build.Label;
            await SelectGameAsync(rec);
        }
        if (hasFile) _host.Toast($"{rec.Name} · {build!.Label}: {build.OffsetCount:N0} offsets ready", ToastHost.Kind.Success);
        else _host.Toast($"Detected {rec.Name} – press Pull offsets to map this build", ToastHost.Kind.Info);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  game-change watcher
    // ═════════════════════════════════════════════════════════════════════════

    private void UpdateWatcher()
    {
        bool connected = _host.Client?.IsConnected == true;
        bool on = connected && (_lib.Settings.WatchTitleChanges || _launchPullId != null);
        _watch.Interval = Math.Clamp(_lib.Settings.WatchIntervalSeconds, 2, 60) * 1000;
        if (on && !_watch.Enabled) _watch.Start();
        else if (!on && _watch.Enabled) _watch.Stop();
    }

    private async Task WatchTickAsync()
    {
        if (_watchBusy || _busy) return;
        var c = _host.Client;
        if (c == null || !c.IsConnected) { _watch.Stop(); return; }
        _watchBusy = true;
        try
        {
            var t = await c.GetRunningTitleAsync();
            if (t is not { } v) return;
            var key = TitleKey(v.Path, v.Timestamp);
            if (key != _lastTitleKey)
            {
                // a different executable / build: wait until it has finished loading before touching memory
                _lastTitleKey = key;
                _pendingTitleKey = key;
                _titleChangedAt = DateTime.Now;
                _running = null;
                RefreshList();
                UpdateHero();
                _host.Log($"🔄 Game change detected → {Path.GetFileName(v.Path)}");
                TitleChanged?.Invoke(key, Path.GetFileName(v.Path));
                SetStatus($"Game change detected – waiting {_lib.Settings.SettleSeconds}s for {Path.GetFileName(v.Path)} to load…");
                return;
            }
            if (_pendingTitleKey == key && DateTime.Now - _titleChangedAt >= TimeSpan.FromSeconds(_lib.Settings.SettleSeconds))
            {
                _pendingTitleKey = "";
                var g = await OffsetPuller.IdentifyAsync(c, _host.Log);
                if (g != null) await HandleIdentifiedAsync(g, onConnect: false);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { /* console busy / rebooting – next tick */ }
        catch (Exception ex) { _host.Log($"⚠️ Watcher: {ex.Message}"); }
        finally
        {
            _watchBusy = false;
            UpdateWatcher();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  pulling
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Identifies the running game, dumps and analyses it and stores the offsets as a build in the library.</summary>
    public async Task PullRunningAsync(bool userInitiated, GameInfo? identified = null)
    {
        var c = _host.Client;
        if (c == null || !c.IsConnected) { _host.Toast("Connect to the console first", ToastHost.Kind.Warning); return; }
        if (_busy) { if (userInitiated) _host.Toast("Already working – cancel the current operation first", ToastHost.Kind.Info); return; }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, "Identifying the running game…", indeterminate: true);
        try
        {
            var g = identified ?? await OffsetPuller.IdentifyAsync(c, _host.Log);
            if (g == null) { _host.Toast("No running game was detected", ToastHost.Kind.Warning); return; }
            _running = g;
            var rec = _lib.FindOrCreate(g);
            _selected = rec;
            RefreshList();
            UpdateHero();

            var r = await OffsetPuller.PullAsync(c, g, _lib.Settings, Report, ct);
            _lastPull = r;
            var build = await ComposeAndSaveAsync(rec, g, r, "Runtime pull", ct);

            _db.ShowPulled(r);
            rec.SelectedBuild = build.Label;
            _lib.Save();
            await SelectGameAsync(rec);

            var ported = r.SignaturesResolved > 0 ? $" · {r.SignaturesResolved}/{r.SignaturesTotal} curated offsets ported" + (r.PortedFrom.Length > 0 ? $" from {r.PortedFrom}" : "") : "";
            _host.Log($"✅ {rec.Name} · {build.Label}: {build.OffsetCount:N0} offsets pulled in {r.Elapsed.TotalSeconds:0.0}s{ported} → {GameLibrary.ResolvePath(build.OffsetFile)}");
            _host.Toast($"{rec.Name} · {build.Label}: {build.OffsetCount:N0} offsets{ported}", ToastHost.Kind.Success);
            CopyToExportFolder(rec, build);
        }
        catch (OperationCanceledException) { _host.Toast("Pull cancelled", ToastHost.Kind.Warning); }
        catch (Exception ex)
        {
            _host.Log($"❌ Pull failed: {ex}");
            _host.Toast($"Pull failed: {ex.Message}", ToastHost.Kind.Error);
        }
        finally { SetBusy(false, _status.Text); }
    }

    /// <summary>
    /// Writes the pull result as a build of <paramref name="rec"/>. Unverified imported files are checked against the
    /// pulled module first: a match binds the file to this build (and learns it), and the runtime pull is merged
    /// after the curated content instead of replacing it.
    /// </summary>
    private async Task<GameBuild> ComposeAndSaveAsync(GameRecord rec, GameInfo g, PullResult r, string source, CancellationToken ct)
    {
        var build = rec.FindBuild(g.Timestamp, g.Checksum, g.ModuleName, g.Size);

        foreach (var b in rec.Builds.Where(b => b != build && !b.Verified && b.Timestamp == 0 && b.CuratedFile.Length > 0).ToList())
        {
            var path = GameLibrary.ResolvePath(b.CuratedFile);
            if (!File.Exists(path)) continue;
            SetStatus($"Checking {b.Label} offsets against the running build…");
            var ents = await OffsetTextFormat.ReadAsync(path);
            bool versionMatches = OffsetPuller.LabelMatchesVersion(b.Label, g.Version);
            var v = OffsetPuller.Verify(r.Module, r.Analysis, ents, versionMatches);
            _host.Log($"🔍 {rec.Name} {b.Label}: {v.Summary}{(versionMatches ? $", label matches v{g.Version}" : "")} → {(v.Passed ? "matches the running build" : "belongs to a different build")}");
            if (!v.Passed) continue;

            if (build != null) rec.Builds.Remove(build); // an earlier anonymous pull of this build is superseded
            b.Timestamp = g.Timestamp;
            b.Checksum = g.Checksum;
            b.Verified = true;
            build = b;
            _host.Toast($"{rec.Name} {b.Label} offsets verified against the running game", ToastHost.Kind.Success);
            if (_lib.Settings.AutoLearnSignatures) await LearnAsync(r, g, b, ents, ct);
            break;
        }

        if (build == null)
        {
            build = new GameBuild { Label = UniqueLabel(rec, GameBuild.DefaultLabel(g.Timestamp, g.Version)) };
            rec.Builds.Add(build);
        }
        build.Timestamp = g.Timestamp;
        build.Checksum = g.Checksum;
        if (g.Version.Length > 0) build.Version = g.Version;
        build.ModuleName = g.ModuleName;
        build.ModuleBase = g.Base;
        build.ModuleSize = g.Size != 0 ? g.Size : (uint)r.Module.Data.Length;

        var outPath = Path.Combine(GameLibrary.GameDirectory(rec), GameLibrary.SafeFileName(build.Label) + ".txt");
        var curatedPath = GameLibrary.ResolvePath(build.CuratedFile);
        string text;
        int count;
        if (build.CuratedFile.Length > 0 && File.Exists(curatedPath))
        {
            // curated content first (verbatim), then everything the runtime pull adds
            var curatedText = await File.ReadAllTextAsync(curatedPath, ct);
            var curated = await OffsetTextFormat.ReadAsync(curatedPath);
            var names = new HashSet<string>(curated.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            var addrs = new HashSet<uint>(curated.Select(e => e.BaseAddress));
            var extra = r.Entries.Where(e => e.RawValue == null && !names.Contains(e.Name) && !addrs.Contains(e.Address)).ToList();
            build.Source = $"Curated + {source.ToLowerInvariant()}";
            count = curated.Count + extra.Count;
            text = OffsetTextFormat.Write(OffsetPuller.Header(rec, build, count), Array.Empty<OffsetTextFormat.Entry>())
                   + StripHeader(curatedText).TrimEnd() + Environment.NewLine + Environment.NewLine
                   + OffsetTextFormat.Write(new Dictionary<string, string>(), extra, r.CategoryOrder);
        }
        else
        {
            build.Source = r.SignaturesResolved > 0 ? $"{source} · {r.SignaturesResolved} ported" + (r.PortedFrom.Length > 0 ? $" from {r.PortedFrom}" : "") : source;
            count = r.Entries.Count;
            text = OffsetTextFormat.Write(OffsetPuller.Header(rec, build, count), r.Entries, r.CategoryOrder);
        }

        await File.WriteAllTextAsync(outPath, text, ct);
        build.OffsetFile = GameLibrary.MakeRelative(outPath);
        build.OffsetCount = count;
        build.PulledAt = DateTime.Now;
        return build;
    }

    private static string StripHeader(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length && (lines[i].TrimStart().StartsWith("//") || lines[i].Trim().Length == 0)) i++;
        return string.Join(Environment.NewLine, lines.Skip(i));
    }

    private static string UniqueLabel(GameRecord rec, string label)
    {
        if (!rec.Builds.Any(b => b.Label.Equals(label, StringComparison.OrdinalIgnoreCase))) return label;
        for (int i = 2; ; i++)
        {
            var l = $"{label} #{i}";
            if (!rec.Builds.Any(b => b.Label.Equals(l, StringComparison.OrdinalIgnoreCase))) return l;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  signature learning
    // ═════════════════════════════════════════════════════════════════════════

    private async Task LearnAsync(PullResult r, GameInfo g, GameBuild b, IReadOnlyList<OffsetManager.OffsetEntry> entries, CancellationToken ct)
    {
        SetStatus($"Learning signatures from {b.Label} so other title updates can be mapped…");
        var (profile, learned, failed, constants) = await Task.Run(() => OffsetPuller.Learn(r.Module, r.Analysis, g, b.Label, entries, Report, ct), ct);
        _host.Log($"✍️ Learned {learned} signatures from {b.Label} ({failed} not unique, {constants} constants) → {profile.FilePath}");
        _host.Toast($"Learned {learned} offsets from {b.Label} – they are now found automatically in other title updates" + (failed > 0 ? $" ({failed} could not be signed)" : ""),
            failed == 0 ? ToastHost.Kind.Success : ToastHost.Kind.Info);
    }

    private async Task LearnSelectedAsync()
    {
        var c = _host.Client;
        var rec = _selected;
        var b = rec?.Current;
        if (rec == null || b == null) return;
        if (c == null || !c.IsConnected) { _host.Toast("Connect to the console and start this build first", ToastHost.Kind.Warning); return; }
        if (_busy) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, "Checking the running game…", indeterminate: true);
        try
        {
            var g = await OffsetPuller.IdentifyAsync(c, _host.Log);
            if (g == null || !rec.Matches(g)) { _host.Toast($"Start {rec.Name} on the console first – signatures are learned from the running build", ToastHost.Kind.Warning); return; }
            if (b.Timestamp != 0 && b.Timestamp != g.Timestamp)
            {
                _host.Toast($"{b.Label} is not the build that is running – select the running build or launch {b.Label}", ToastHost.Kind.Warning);
                return;
            }

            var r = _lastPull != null && _lastPull.Game.Timestamp == g.Timestamp && _lastPull.Game.ModuleName == g.ModuleName
                ? _lastPull
                : _lastPull = await OffsetPuller.PullAsync(c, g, _lib.Settings, Report, ct);

            var path = GameLibrary.ResolvePath(b.CuratedFile.Length > 0 ? b.CuratedFile : b.OffsetFile);
            var ents = await OffsetTextFormat.ReadAsync(path);
            // learn the curated / hand-made part; generated analysis entries are re-derived automatically anyway
            var curated = ents.Where(e => !IsGeneratedCategory(e.Category)).ToList();
            var v = OffsetPuller.Verify(r.Module, r.Analysis, curated, OffsetPuller.LabelMatchesVersion(b.Label, g.Version));
            if (!v.Passed && MessageBox.Show(FindForm(),
                    $"Only {v.Summary}.\n\nThe file may belong to a different build. Learn anyway?", "Learn signatures",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            if (b.Timestamp == 0) { b.Timestamp = g.Timestamp; b.Checksum = g.Checksum; }
            b.Verified |= v.Passed;
            await LearnAsync(r, g, b, curated, ct);
            _lib.Save();
        }
        catch (OperationCanceledException) { _host.Toast("Cancelled", ToastHost.Kind.Warning); }
        catch (Exception ex) { _host.Toast($"Learning failed: {ex.Message}", ToastHost.Kind.Error); _host.Log($"❌ Learning failed: {ex}"); }
        finally { SetBusy(false, ""); }
    }

    private static bool IsGeneratedCategory(string c) => c is OffsetPuller.CatFunctions or OffsetPuller.CatKeyFunctions or OffsetPuller.CatGlobals
        or OffsetPuller.CatImports or OffsetPuller.CatModule or OffsetPuller.CatLoadedModules or OffsetPuller.CatStrings;

    // ═════════════════════════════════════════════════════════════════════════
    //  launching
    // ═════════════════════════════════════════════════════════════════════════

    private async Task LaunchAsync(bool pullAfter)
    {
        var rec = _selected;
        var c = _host.Client;
        if (rec == null) return;
        if (c == null || !c.IsConnected) { _host.Toast("Connect to the console first", ToastHost.Kind.Warning); return; }
        if (_busy) return;

        if (rec.LaunchPath.Length == 0)
        {
            var p = Prompt.Show(FindForm(), $"Launch {rec.Name}", "Executable on the console (Scan console fills this in automatically)", @"Hdd:\Games\");
            if (string.IsNullOrWhiteSpace(p)) return;
            rec.LaunchPath = p.Trim();
            _lib.Save();
        }

        SetBusy(true, $"Launching {rec.Name}…", indeterminate: true);
        try
        {
            if (await c.LaunchTitleAsync(rec.LaunchPath))
            {
                rec.LastLaunched = DateTime.Now;
                _lib.Save();
                if (pullAfter) _launchPullId = rec.Id;
                _host.Log($"▶️ Launched {rec.Name} ({rec.LaunchPath})");
                _host.Toast(pullAfter ? $"Launching {rec.Name} – offsets are pulled as soon as it has loaded" : $"Launching {rec.Name}", ToastHost.Kind.Success);
            }
            else _host.Toast($"The console refused to launch {rec.LaunchPath} – check the path (⋯ → Set launch path)", ToastHost.Kind.Error);
        }
        catch (Exception ex) when (ex is IOException)
        {
            // some dashboards drop the debug connection while switching titles – the shell reconnects automatically
            if (pullAfter) _launchPullId = rec.Id;
            _host.Toast($"Launching {rec.Name} – the console is switching titles", ToastHost.Kind.Info);
        }
        catch (Exception ex) { _host.Toast($"Launch failed: {ex.Message}", ToastHost.Kind.Error); }
        finally
        {
            SetBusy(false, pullAfter ? "Waiting for the game to load…" : "");
            UpdateWatcher();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  scanning the console
    // ═════════════════════════════════════════════════════════════════════════

    private static readonly HashSet<string> SkipDrives = new(StringComparer.OrdinalIgnoreCase)
    {
        "Flash", "HddX", "SysCache0", "SysCache1", "Media", "DEVKIT", "HddSystemExtPartition", "HddSystemAuxPartition", "INTUSB", "Y", "Z",
    };
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content", "Cache", "$SystemUpdate", "$TitleUpdate", "Compatibility", "Partition1", "System", "Dashboard", "Plugins", "Apps", "Homebrew",
        "$RECYCLE.BIN", "System Volume Information", "Video", "Music", "Pictures",
    };

    private async Task ScanConsoleAsync()
    {
        var c = _host.Client;
        if (c == null || !c.IsConnected) { _host.Toast("Connect to the console first", ToastHost.Kind.Warning); return; }
        if (_busy) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, "Scanning console drives…", indeterminate: true);
        try
        {
            var drives = (await c.GetDrivesAsync()).Where(d => !SkipDrives.Contains(d)).ToList();
            if (drives.Count == 0) drives.Add("Hdd");
            var found = new List<(string Path, string Folder)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int folders = 0;

            async Task<XBDMClient.FileEntry?> FindXexAsync(string dir)
            {
                var files = await c.ListDirectoryAsync(dir);
                folders++;
                var xex = files.Where(f => !f.IsDirectory && f.Name.EndsWith(".xex", StringComparison.OrdinalIgnoreCase)).ToList();
                return xex.FirstOrDefault(f => f.Name.Equals("default.xex", StringComparison.OrdinalIgnoreCase))
                       ?? xex.FirstOrDefault(f => f.Name.Equals("default_mp.xex", StringComparison.OrdinalIgnoreCase))
                       ?? xex.FirstOrDefault();
            }

            foreach (var d in drives)
            {
                foreach (var root in new[] { $@"{d}:\Games", $@"{d}:\" })
                {
                    ct.ThrowIfCancellationRequested();
                    List<XBDMClient.FileEntry> entries;
                    try { entries = await c.ListDirectoryAsync(root); } catch (Exception ex) when (ex is not IOException) { continue; }
                    foreach (var dir in entries.Where(e => e.IsDirectory && !SkipFolders.Contains(e.Name)).Take(250))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!visited.Add(dir.Path)) continue;
                        SetStatus($"Scanning {dir.Path}…  ({folders} folders, {found.Count} games)");
                        var xex = await FindXexAsync(dir.Path);
                        if (xex != null) { found.Add((xex.Path, dir.Name)); continue; }
                        // one level deeper (Games\Title\MP\default_mp.xex)
                        foreach (var sub in (await c.ListDirectoryAsync(dir.Path)).Where(e => e.IsDirectory).Take(8))
                        {
                            var x2 = await FindXexAsync(sub.Path);
                            if (x2 != null) { found.Add((x2.Path, dir.Name)); break; }
                        }
                    }
                }
            }

            int added = 0, updated = 0;
            for (int i = 0; i < found.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (path, folder) = found[i];
                Report($"Reading {folder}…", (double)i / Math.Max(1, found.Count));
                uint titleId = 0;
                try
                {
                    var head = await c.GetFileAsync(path, 0, 0x10000);
                    if (head != null && Xex.XexFile.IsXex(head)) titleId = Xex.XexFile.Parse(head).TitleId;
                }
                catch (Exception ex) when (ex is not IOException) { }

                var rec = _lib.Games.FirstOrDefault(r => titleId != 0 && r.TitleId == titleId)
                          ?? _lib.Games.FirstOrDefault(r => r.LaunchPath.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (rec == null)
                {
                    var name = titleId != 0 && GameDetector.KnownTitles.TryGetValue(titleId, out var known) ? known : folder;
                    _lib.Games.Add(new GameRecord { Name = name, TitleId = titleId, ModuleName = Path.GetFileName(path), LaunchPath = path });
                    added++;
                }
                else if (rec.LaunchPath.Length == 0)
                {
                    rec.LaunchPath = path;
                    updated++;
                }
            }
            _lib.Save();
            RefreshList();
            _host.Log($"🔎 Console scan: {found.Count} games in {folders} folders ({added} new, {updated} updated)");
            _host.Toast(found.Count == 0 ? "No games found in Games folders on the console drives" : $"Found {found.Count} games · {added} new", found.Count == 0 ? ToastHost.Kind.Info : ToastHost.Kind.Success);
        }
        catch (OperationCanceledException) { _host.Toast("Scan cancelled", ToastHost.Kind.Warning); }
        catch (Exception ex) { _host.Toast($"Scan failed: {ex.Message}", ToastHost.Kind.Error); }
        finally { SetBusy(false, ""); }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  importing
    // ═════════════════════════════════════════════════════════════════════════

    private void ShowAddMenu(Control anchor)
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Import offsets file (.txt)…", null, async (_, _) =>
        {
            using var ofd = new OpenFileDialog { Title = "Import offsets", Filter = "Offset files (*.txt)|*.txt|All files (*.*)|*.*", Multiselect = true };
            if (ofd.ShowDialog(FindForm()) == DialogResult.OK) foreach (var f in ofd.FileNames) await ImportOffsetsFileAsync(f);
        });
        m.Items.Add("Analyse XEX / EXE file (offline)…", null, async (_, _) =>
        {
            using var ofd = new OpenFileDialog { Title = "Analyse executable", Filter = "Executables (*.xex;*.exe)|*.xex;*.exe|All files (*.*)|*.*" };
            if (ofd.ShowDialog(FindForm()) == DialogResult.OK) await ImportExecutableAsync(ofd.FileName);
        });
        m.Items.Add("Add game manually…", null, (_, _) =>
        {
            var r = Prompt.ShowTwo(FindForm(), "Add game", "Name", "", "Executable on the console (optional)", @"Hdd:\Games\");
            if (r == null || r.Value.First.Trim().Length == 0) return;
            var rec = new GameRecord { Name = r.Value.First.Trim(), LaunchPath = r.Value.Second.Trim().TrimEnd('\\') == @"Hdd:\Games" ? "" : r.Value.Second.Trim() };
            _lib.Games.Add(rec);
            _lib.Save();
            _ = SelectGameAsync(rec);
        });
        Scripting.ScriptIde.StyleMenu(m);
        m.Show(anchor, new Point(0, anchor.Height));
    }

    public async Task ImportOffsetsFileAsync(string path)
    {
        var ents = await OffsetTextFormat.ReadAsync(path);
        if (ents.Count == 0) { _host.Toast($"{Path.GetFileName(path)} contains no offsets in the Name: 0xADDRESS format", ToastHost.Kind.Error); return; }
        var header = OffsetTextFormat.ReadHeader(path);
        uint titleId = header.TryGetValue("Title ID", out var tid) && uint.TryParse(tid, System.Globalization.NumberStyles.HexNumber, null, out var t) ? t : 0;
        var file = Path.GetFileNameWithoutExtension(path);
        var tu = Regex.Match(file, @"tu[\s_-]*(\d+)", RegexOptions.IgnoreCase);
        string game = header.GetValueOrDefault("Game") ?? (titleId != 0 ? _lib.Games.FirstOrDefault(g => g.TitleId == titleId)?.Name : null) ?? _selected?.Name ?? file;
        string label = header.GetValueOrDefault("Build") ?? (tu.Success ? $"TU{tu.Groups[1].Value}" : "Imported");

        var answer = Prompt.ShowTwo(FindForm(), $"Import {Path.GetFileName(path)} ({ents.Count:N0} offsets)", "Game", game, "Build / title update", label);
        if (answer == null) return;
        game = answer.Value.First.Trim();
        label = answer.Value.Second.Trim();
        if (game.Length == 0) return;

        var rec = _lib.Games.FirstOrDefault(g => titleId != 0 && g.TitleId == titleId)
                  ?? _lib.Games.FirstOrDefault(g => g.Name.Equals(game, StringComparison.OrdinalIgnoreCase));
        if (rec == null) { rec = new GameRecord { Name = game, TitleId = titleId }; _lib.Games.Add(rec); }
        var build = await AddImportedBuildAsync(rec, path, label.Length > 0 ? label : "Imported");
        if (header.TryGetValue("Timestamp", out var ts) && XBDMClient.ParseUInt(ts.Split(' ')[0]) is var tsv and > 0) build.Timestamp = tsv;
        _lib.Save();
        await SelectGameAsync(rec, build);
        _host.Toast($"Imported {ents.Count:N0} offsets as {rec.Name} · {build.Label}. Start that build and pull – it is verified and learned for other title updates.", ToastHost.Kind.Success);
    }

    private async Task<GameBuild> AddImportedBuildAsync(GameRecord rec, string source, string label)
    {
        label = UniqueLabel(rec, label);
        var dest = Path.Combine(GameLibrary.GameDirectory(rec), GameLibrary.SafeFileName(label) + " (imported).txt");
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) File.Copy(source, dest, true);
        var count = (await OffsetTextFormat.ReadAsync(dest)).Count;
        var rel = GameLibrary.MakeRelative(dest);
        var build = new GameBuild { Label = label, CuratedFile = rel, OffsetFile = rel, OffsetCount = count, PulledAt = DateTime.Now, Source = "Imported" };
        rec.Builds.Add(build);
        rec.SelectedBuild = label;
        return build;
    }

    public async Task ImportExecutableAsync(string path)
    {
        if (_busy) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, $"Loading {Path.GetFileName(path)}…", indeterminate: true);
        try
        {
            var module = await Task.Run(() => RuntimeOffsetsPage.LoadModuleFromFile(path), ct);
            var x = module.Xex;
            var g = new GameInfo
            {
                ModuleName = module.Name, Base = module.BaseAddress, Size = (uint)module.Data.Length,
                TitleId = x?.TitleId ?? 0, Timestamp = x?.Timestamp ?? module.Timestamp, Checksum = x?.Checksum ?? 0,
                Version = x?.Version ?? "", Path = path,
            };
            g.DisplayName = g.TitleId != 0 && GameDetector.KnownTitles.TryGetValue(g.TitleId, out var known) ? known
                          : Path.GetFileName(Path.GetDirectoryName(path)) is { Length: > 0 } dir ? dir : module.Name;
            var r = await OffsetPuller.AnalyzeAsync(module, g, _lib.Settings, Report, 0, ct);
            _lastPull = r;
            var rec = _lib.FindOrCreate(g);
            rec.LaunchPath = rec.LaunchPath.StartsWith(@"Hdd:", StringComparison.OrdinalIgnoreCase) || rec.LaunchPath.StartsWith("Usb", StringComparison.OrdinalIgnoreCase) ? rec.LaunchPath : "";
            var build = await ComposeAndSaveAsync(rec, g, r, "Offline analysis", ct);
            _db.ShowPulled(r);
            rec.SelectedBuild = build.Label;
            _lib.Save();
            await SelectGameAsync(rec);
            _host.Toast($"{rec.Name} · {build.Label}: {build.OffsetCount:N0} offsets from {Path.GetFileName(path)}", ToastHost.Kind.Success);
        }
        catch (OperationCanceledException) { _host.Toast("Cancelled", ToastHost.Kind.Warning); }
        catch (Exception ex) { _host.Toast($"Could not analyse {Path.GetFileName(path)}: {ex.Message}", ToastHost.Kind.Error); }
        finally { SetBusy(false, ""); }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  export & management
    // ═════════════════════════════════════════════════════════════════════════

    private void ExportSelected()
    {
        var rec = _selected;
        var b = rec?.Current;
        if (rec == null || b == null) return;
        var src = GameLibrary.ResolvePath(b.OffsetFile);
        if (!File.Exists(src)) { _host.Toast("This build has no offsets file yet", ToastHost.Kind.Info); return; }
        using var sfd = new SaveFileDialog
        {
            Title = "Export offsets", Filter = "Offset files (*.txt)|*.txt",
            FileName = GameLibrary.SafeFileName($"{rec.Name} {b.Label} offsets.txt").Replace(' ', '_').ToLowerInvariant(),
        };
        if (sfd.ShowDialog(FindForm()) != DialogResult.OK) return;
        File.Copy(src, sfd.FileName, true);
        _host.Toast($"Exported {b.OffsetCount:N0} offsets", ToastHost.Kind.Success);
    }

    private void CopyToExportFolder(GameRecord rec, GameBuild b)
    {
        var dir = _lib.Settings.ExportFolder;
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            Directory.CreateDirectory(dir);
            var name = GameLibrary.SafeFileName($"{rec.Name} {b.Label} offsets.txt").Replace(' ', '_').ToLowerInvariant();
            File.Copy(GameLibrary.ResolvePath(b.OffsetFile), Path.Combine(dir, name), true);
            _host.Log($"📤 Copied to {Path.Combine(dir, name)}");
        }
        catch (Exception ex) { _host.Log($"⚠️ Auto-export failed: {ex.Message}"); }
    }

    private void ShowMoreMenu(Control anchor) => ShowMoreMenu(anchor, new Point(0, anchor.Height));

    private void ShowMoreMenu(Control anchor, Point at)
    {
        var rec = _selected;
        var b = rec?.Current;
        var m = new ContextMenuStrip();
        bool connected = _host.Client?.IsConnected == true;

        if (rec != null)
        {
            m.Items.Add("Rename game…", null, (_, _) =>
            {
                var n = Prompt.Show(FindForm(), "Rename game", "Name", rec.Name);
                if (!string.IsNullOrWhiteSpace(n)) { rec.Name = n.Trim(); _lib.Save(); UpdateHero(); }
            });
            if (b != null)
                m.Items.Add($"Rename build “{b.Label}”…", null, (_, _) =>
                {
                    var n = Prompt.Show(FindForm(), "Rename build", "Label (e.g. TU18)", b.Label);
                    if (string.IsNullOrWhiteSpace(n) || n.Trim() == b.Label) return;
                    b.Label = UniqueLabel(rec, n.Trim());
                    rec.SelectedBuild = b.Label;
                    _lib.Save();
                    UpdateHero();
                });
            m.Items.Add("Set launch path…", null, (_, _) =>
            {
                var n = Prompt.Show(FindForm(), "Launch path", "Executable on the console", rec.LaunchPath.Length > 0 ? rec.LaunchPath : @"Hdd:\Games\");
                if (n != null) { rec.LaunchPath = n.Trim(); _lib.Save(); UpdateHero(); }
            });
            m.Items.Add("Set cover image…", null, (_, _) =>
            {
                using var ofd = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
                if (ofd.ShowDialog(FindForm()) != DialogResult.OK) return;
                var dest = Path.Combine(GameLibrary.GameDirectory(rec), "cover" + Path.GetExtension(ofd.FileName).ToLowerInvariant());
                _covers.Invalidate(GameLibrary.ResolvePath(rec.CoverPath));
                _covers.Invalidate(dest);
                File.Copy(ofd.FileName, dest, true);
                rec.CoverPath = dest;
                _lib.Save();
                UpdateHero();
            });
            if (rec.CoverPath.Length > 0)
                m.Items.Add("Remove cover image", null, (_, _) => { _covers.Invalidate(rec.CoverPath); rec.CoverPath = ""; _lib.Save(); UpdateHero(); });
            m.Items.Add(rec.Favorite ? "Remove from favorites" : "Add to favorites", null, (_, _) => { rec.Favorite = !rec.Favorite; _lib.Save(); });
            m.Items.Add(new ToolStripSeparator());
            if (b != null)
            {
                m.Items.Add("Open offsets file", null, (_, _) => Shell(GameLibrary.ResolvePath(b.OffsetFile)));
                m.Items.Add("Import another offsets file for this game…", null, async (_, _) =>
                {
                    using var ofd = new OpenFileDialog { Filter = "Offset files (*.txt)|*.txt|All files (*.*)|*.*" };
                    if (ofd.ShowDialog(FindForm()) == DialogResult.OK) await ImportOffsetsFileAsync(ofd.FileName);
                });
            }
            m.Items.Add("Open game folder", null, (_, _) => Shell(GameLibrary.GameDirectory(rec)));
            m.Items.Add("Export every build…", null, (_, _) => ExportAllBuilds(rec));
        }
        m.Items.Add("Open signature profiles", null, (_, _) => { Directory.CreateDirectory(ProfileStore.Directory); Shell(ProfileStore.Directory); });
        m.Items.Add(_lib.Settings.ExportFolder.Length > 0 ? $"Auto-export folder: {_lib.Settings.ExportFolder}…" : "Auto-export pulled files to a folder…", null, (_, _) =>
        {
            using var fbd = new FolderBrowserDialog { Description = "Every pulled offsets file is also copied here (cancel to turn off)" };
            _lib.Settings.ExportFolder = fbd.ShowDialog(FindForm()) == DialogResult.OK ? fbd.SelectedPath : "";
            _lib.Save();
        });
        if (connected)
        {
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Restart console", null, async (_, _) =>
            {
                if (MessageBox.Show(FindForm(), "Restart the console now?", "Restart", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    await (_host.Client?.RebootAsync() ?? Task.FromResult(false));
            });
        }
        if (rec != null)
        {
            m.Items.Add(new ToolStripSeparator());
            if (b != null)
                m.Items.Add($"Remove build “{b.Label}”", null, (_, _) =>
                {
                    if (MessageBox.Show(FindForm(), $"Remove {b.Label} from {rec.Name}? (the file stays in the game folder)", "Remove build", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    rec.Builds.Remove(b);
                    rec.SelectedBuild = "";
                    _lib.Save();
                    _ = SelectGameAsync(rec);
                });
            m.Items.Add("Remove game from library", null, (_, _) => RemoveSelectedGame());
        }
        Scripting.ScriptIde.StyleMenu(m);
        m.Show(anchor, at);
    }

    private void ExportAllBuilds(GameRecord rec)
    {
        using var fbd = new FolderBrowserDialog { Description = $"Export every {rec.Name} build" };
        if (fbd.ShowDialog(FindForm()) != DialogResult.OK) return;
        int n = 0;
        foreach (var b in rec.Builds)
        {
            var src = GameLibrary.ResolvePath(b.OffsetFile);
            if (!File.Exists(src)) continue;
            File.Copy(src, Path.Combine(fbd.SelectedPath, GameLibrary.SafeFileName($"{rec.Name} {b.Label} offsets.txt").Replace(' ', '_').ToLowerInvariant()), true);
            n++;
        }
        _host.Toast($"Exported {n} build(s)", ToastHost.Kind.Success);
    }

    private void RemoveSelectedGame()
    {
        var rec = _selected;
        if (rec == null) return;
        if (MessageBox.Show(FindForm(), $"Remove {rec.Name} from the library?\n\nIts offset files stay on disk in the Games folder.", "Remove game",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _lib.Games.Remove(rec);
        _selected = null;
        _lib.Save();
        RefreshList();
        var next = _list.Items.Cast<GameRecord>().FirstOrDefault();
        if (next != null) _ = SelectGameAsync(next);
        else UpdateHero();
    }

    private void Shell(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { _host.Toast($"Could not open {path}: {ex.Message}", ToastHost.Kind.Error); }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  busy / progress
    // ═════════════════════════════════════════════════════════════════════════

    private void SetBusy(bool busy, string status, bool indeterminate = false)
    {
        if (InvokeRequired) { BeginInvoke(() => SetBusy(busy, status, indeterminate)); return; }
        _busy = busy;
        _progress.Visible = busy;
        _progress.Indeterminate = busy && indeterminate;
        if (busy && !indeterminate) _progress.Value = 0;
        SetStatus(status);
        UpdateHero();
    }

    private void SetStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text)); return; }
        _status.Text = text;
        _heroStack.Relayout();
    }

    private void Report(string text, double value)
    {
        _reportText = text;
        _reportValue = value;
        if (Interlocked.Exchange(ref _reportQueued, 1) == 1 || IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _reportQueued, 0);
            if (!_busy) return;
            _progress.Indeterminate = false;
            _progress.Value = _reportValue;
            _status.Text = _reportText;
        });
    }
}
