using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using MemoryEditor.Library;
using MemoryEditor.UI;

namespace MemoryEditor.Runtime.Toolkit;

public sealed partial class ActiveMemoryTool
{
    private sealed record RangeTag(uint Start, uint Size, string Label);
    private sealed record CategoryTag(string Category);

    // ═════════════════════════════════════════════════════════════════════════
    //  game sync
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Reads everything about the running game: title, modules, sections, regions, and rebuilds the catalog.</summary>
    private async Task SyncAsync(bool user)
    {
        var c = user ? RequireClient() : Client;
        if (c == null) { RebuildCatalog(true); BuildTree(); return; }
        if (_syncing) return;
        _syncing = true;
        try
        {
            var t = await c.GetRunningTitleAsync();
            _titleKey = t is { } tv ? $"{tv.Path}|{tv.Timestamp:X8}" : "";
            _modules = await c.GetModulesAsync();
            var sections = new Dictionary<string, List<XBDMClient.SectionInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _modules)
            {
                try { sections[m.Name] = await c.GetModuleSectionsAsync(m.Name); }
                catch (Exception ex) when (ex is not IOException) { sections[m.Name] = new(); }
            }
            _sections = sections;
            try { _regions = await c.WalkMemoryAsync(); } catch (Exception ex) when (ex is not IOException) { _regions = new(); }

            var path = t?.Path ?? "";
            var file = path.Length > 0 ? path[(path.LastIndexOf('\\') + 1)..] : "";
            var title = _modules.FirstOrDefault(m => file.Length > 0 && m.Name.Equals(file, StringComparison.OrdinalIgnoreCase))
                        ?? _modules.FirstOrDefault(m => m.Base == 0x82000000);
            var name = title != null ? OffsetPuller.FriendlyName(new GameInfo { Path = path, ModuleName = title.Name }) : "Dashboard / no title";
            _game.Text = name;
            RebuildCatalog(true);
            UpdateMeta(title, t?.Timestamp ?? 0);
            BuildTree();

            if (title != null && _hex.ByteAt(_hex.CursorAddress) == null && _back.Count == 0 && _hex.BaseAddress == 0x82000000)
                _hex.GoTo(title.Base, select: true);
            if (user) App.Toast($"Synced {name}: {_modules.Count} modules, {_regions.Count} regions, {_items.Count:N0} offsets", ToastHost.Kind.Success);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        finally { _syncing = false; }
    }

    private void UpdateMeta(XBDMClient.ModuleInfo? title, uint timestamp)
    {
        int dbCount = _items.Count(i => i.Source == "Database");
        var parts = new List<string>();
        if (title != null) parts.Add($"{title.Name} @ 0x{title.Base:X8}");
        if (timestamp != 0) parts.Add($"build {DateTimeOffset.FromUnixTimeSeconds(timestamp):yyyy-MM-dd}");
        parts.Add($"{_modules.Count} modules");
        parts.Add($"{_regions.Count} regions");
        parts.Add($"{_items.Count:N0} offsets");
        _gameMeta.Text = string.Join("  ·  ", parts);
        bool unmapped = dbCount == 0 && _items.Count(i => i.Source == "Runtime") == 0;
        _btnPull.Text = unmapped ? "Pull offsets" : "Re-pull";
        _btnPull.Variant = unmapped ? ButtonVariant.Primary : ButtonVariant.Neutral;
    }

    /// <summary>Follows the console: re-syncs when another game / build starts and picks up newly pulled offsets.</summary>
    private async Task CheckTitleAsync()
    {
        var c = Client;
        if (c == null || _syncing) return;
        try
        {
            var t = await c.GetRunningTitleAsync();
            var key = t is { } tv ? $"{tv.Path}|{tv.Timestamp:X8}" : "";
            if (key != _titleKey)
            {
                App.Log($"🔄 Active Memory: running game changed – re-syncing");
                _changeLog.Clear();
                _changes.RowCount = 0;
                await SyncAsync(false);
                return;
            }
            if (RebuildCatalog(false))
            {
                BuildTree();
                UpdateMeta(_modules.FirstOrDefault(m => m.Base == 0x82000000), t?.Timestamp ?? 0);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  catalog (every known location of the running game)
    // ═════════════════════════════════════════════════════════════════════════

    private bool IsCode(uint a)
    {
        foreach (var list in _sections.Values)
            foreach (var s in list)
                if (a >= s.Base && a < s.Base + s.Size && ((s.Flags & 0x20) != 0 || s.Name.Contains("text", StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    /// <summary>Rebuilds the catalog when the database or runtime offsets changed. Returns true when rebuilt.</summary>
    private bool RebuildCatalog(bool force)
    {
        var db = Host.Database;
        var rt = Host.RuntimeOffsets;
        int sig = HashCode.Combine(db.Count, rt.Count, db.Count > 0 ? db[0].Name : "", rt.Count > 0 ? rt[^1].Address : 0u, _sections.Count);
        if (!force && sig == _catalogSignature) return false;
        _catalogSignature = sig;

        var list = new List<Item>();
        var seen = new HashSet<(uint, string)>();
        foreach (var e in db)
        {
            if (e.BaseAddress < 0x80000000) continue;
            uint addr = unchecked(e.BaseAddress + (e.Offset ?? 0));
            if (!seen.Add((addr, e.Name))) continue;
            var it = new Item { Name = e.Name, Category = e.Category, Source = "Database", Address = addr };
            var patch = OffsetTextFormat.ParseBytes(e.ByteValues);
            if (patch != null) { it.Kind = "Patch"; it.Patch = OffsetTextFormat.NormalizeBytes(e.ByteValues!); it.Length = Math.Max(1, patch.Length); }
            else if (IsCode(addr)) { it.Kind = "Code"; it.Length = 4; }
            else
            {
                it.Type = e.Type switch
                {
                    "Float" => LiveType.Float, "Byte" => LiveType.Byte, "Int16" or "Short" => LiveType.Int16,
                    "Double" => LiveType.Double, "Int64" => LiveType.Int64, _ => LiveType.Int32,
                };
                it.Length = LiveValue.Size(it.Type);
            }
            list.Add(it);
        }
        foreach (var d in rt)
        {
            if (d.Address < 0x80000000 || d.Type is "Info" or "Section") continue;
            if (!seen.Add((d.Address, d.Name))) continue;
            var it = new Item { Name = d.Name, Category = "Runtime · " + d.Category, Source = "Runtime", Address = d.Address };
            switch (d.Type)
            {
                case "Function": it.Kind = "Code"; it.Length = 4; break;
                case "String": it.Kind = "String"; it.Length = 48; break;
                case "Import": it.Type = LiveType.UInt32; break;
                default:
                    if (IsCode(d.Address)) { it.Kind = "Code"; it.Length = 4; }
                    break;
            }
            list.Add(it);
        }
        foreach (var it in list)
            if (_typeOverrides.TryGetValue(it.Address, out var ov) && it.Kind == "Value") { it.Type = ov; it.Length = LiveValue.Size(ov); }

        _items = list;
        var map = new Dictionary<uint, Item>();
        foreach (var it in list) map.TryAdd(it.Address, it);
        _byAddress = map;
        ApplyValuesFilter();
        _hex.Invalidate();
        return true;
    }

    private string NameFor(uint a) => _byAddress.TryGetValue(a, out var it) ? it.Name : Host.Symbols.Describe(a) ?? $"0x{a:X8}";

    // ═════════════════════════════════════════════════════════════════════════
    //  explorer tree
    // ═════════════════════════════════════════════════════════════════════════

    private static string FormatSize(uint s) => s >= 0x100000 ? $"{s / 1048576.0:0.#} MB" : s >= 1024 ? $"{s / 1024.0:0.#} KB" : $"{s} B";

    private void BuildTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var q = _search.Text.Trim();
        if (q.Length > 0) BuildSearchTree(q);
        else
        {
            var pinned = _tree.Nodes.Add($"Pinned ({_pinned.Count})");
            foreach (var a in _pinned) pinned.Nodes.Add(new TreeNode($"{NameFor(a)}  0x{a:X8}") { Tag = a });
            if (_pinned.Count > 0) pinned.Expand();

            var mods = _tree.Nodes.Add($"Modules ({_modules.Count})");
            foreach (var m in _modules.OrderBy(m => m.Base))
            {
                var n = new TreeNode($"{m.Name}  0x{m.Base:X8} · {FormatSize(m.Size)}") { Tag = new RangeTag(m.Base, m.Size, m.Name) };
                mods.Nodes.Add(n);
                foreach (var s in _sections.GetValueOrDefault(m.Name) ?? new())
                    n.Nodes.Add(new TreeNode($"{s.Name}  0x{s.Base:X8} · {FormatSize(s.Size)}") { Tag = new RangeTag(s.Base, s.Size, $"{m.Name} {s.Name}") });
            }
            if (_modules.Count <= 6) mods.Expand();

            var offsets = _tree.Nodes.Add($"Offsets ({_items.Count:N0})");
            foreach (var g in _items.GroupBy(i => i.Category).OrderBy(g => g.Key.StartsWith("Runtime") ? 1 : 0).ThenBy(g => g.Key))
            {
                var cat = new TreeNode($"{g.Key} ({g.Count():N0})") { Tag = new CategoryTag(g.Key) };
                offsets.Nodes.Add(cat);
                cat.Nodes.Add(new TreeNode("…")); // filled on expand (large games have tens of thousands of entries)
            }
            offsets.Expand();

            var regions = _tree.Nodes.Add($"Regions ({_regions.Count})");
            foreach (var r in _regions.OrderBy(r => r.Base))
                regions.Nodes.Add(new TreeNode($"0x{r.Base:X8} · {FormatSize(r.Size)} · {r.ProtectText}") { Tag = new RangeTag(r.Base, r.Size, $"region 0x{r.Base:X8}") });
        }
        _tree.EndUpdate();
    }

    private void BuildSearchTree(string q)
    {
        var root = _tree.Nodes.Add("Results");
        int n = 0;
        if (TryAddress(q, out var addr) && (q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || q.Length >= 6))
        {
            root.Nodes.Add(new TreeNode($"Go to 0x{addr:X8}" + (Host.Symbols.Describe(addr) is { } s ? $"  ({s})" : "")) { Tag = addr });
            foreach (var m in _modules.Where(m => addr >= m.Base && addr < m.Base + m.Size))
                root.Nodes.Add(new TreeNode($"inside {m.Name} (+0x{addr - m.Base:X})") { Tag = new RangeTag(m.Base, m.Size, m.Name) });
        }
        foreach (var m in _modules.Where(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            root.Nodes.Add(new TreeNode($"{m.Name}  0x{m.Base:X8}") { Tag = new RangeTag(m.Base, m.Size, m.Name) });
        foreach (var it in _items)
        {
            if (!it.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && !it.Category.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            root.Nodes.Add(new TreeNode($"{it.Name}  0x{it.Address:X8}") { Tag = it });
            if (++n >= 500) { root.Nodes.Add("… refine the search to see more"); break; }
        }
        root.Text = $"Results ({root.Nodes.Count})";
        root.Expand();
    }

    private void ExpandNode(TreeNode node)
    {
        if (node.Tag is not CategoryTag ct || node.Nodes.Count != 1 || node.Nodes[0].Tag != null) return;
        node.Nodes.Clear();
        var nodes = _items.Where(i => i.Category == ct.Category).Take(5000)
                          .Select(i => new TreeNode($"{i.Name}  0x{i.Address:X8}") { Tag = i }).ToArray();
        node.Nodes.AddRange(nodes);
    }

    private void OpenNode(TreeNode? node)
    {
        switch (node?.Tag)
        {
            case Item it: Open(it.Address); break;
            case RangeTag r: Open(r.Start); break;
            case uint a: Open(a); break;
        }
    }

    private void ShowNodeMenu(TreeNode node, Point at)
    {
        uint? addr = node.Tag switch { Item it => it.Address, RangeTag r => r.Start, uint a => a, _ => null };
        if (addr is not { } a0) return;
        var m = new ContextMenuStrip();
        m.Items.Add("Open in hex view", null, (_, _) => Open(a0));
        if (node.Tag is RangeTag range)
        {
            m.Items.Add($"Record whole range ({FormatSize(Math.Min(range.Size, 0x100000u))})", null, (_, _) => App.MonitorRegion(range.Start, Math.Min(range.Size, 0x100000u), range.Label));
            m.Items.Add("Pattern scan in range", null, (_, _) => Host.ScanRange(range.Start, range.Size));
        }
        else m.Items.Add("Record around (256 B)", null, (_, _) => App.MonitorRegion(Math.Max(0x80000000, a0 - 0x40) & ~0xFu, 0x100, NameFor(a0)));
        m.Items.Add("Disassemble", null, (_, _) => Host.Disassemble(a0 & ~3u));
        m.Items.Add("Add to watch list", null, (_, _) => App.AddWatch(a0, NameFor(a0)));
        m.Items.Add("Graph value", null, (_, _) => Host.Graph(a0, node.Tag is Item i2 && i2.Kind == "Value" ? i2.Type : LiveType.Int32, NameFor(a0)));
        m.Items.Add(_pinned.Contains(a0) ? "Unpin" : "Pin", null, (_, _) => Pin(a0));
        m.Items.Add("Copy address", null, (_, _) => Clipboard.SetText($"0x{a0:X8}"));
        Scripting.ScriptIde.StyleMenu(m);
        m.Show(_tree, at);
    }

    private void Pin(uint a)
    {
        if (!_pinned.Remove(a)) _pinned.Add(a);
        BuildTree();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  hex view
    // ═════════════════════════════════════════════════════════════════════════

    private async Task RefreshHexAsync()
    {
        var c = Client;
        if (c == null || _reading || !Visible) return;
        _reading = true;
        try
        {
            uint start = _hex.BaseAddress;
            int len = _hex.ViewLength;
            var data = await c.GetMemoryAsync(start, (uint)len);
            var changes = _hex.SetData(start, data != null && data.Length >= len ? data : null, len);
            LogByteChanges(changes);
            UpdateHexInfo();
            UpdateInspector();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        finally { _reading = false; }
    }

    /// <summary>Collapses consecutive changed bytes into one log line.</summary>
    private void LogByteChanges(List<(uint Address, byte Old, byte New)> changes)
    {
        if (changes.Count == 0) return;
        var now = DateTime.Now;
        int i = 0;
        while (i < changes.Count)
        {
            int j = i;
            while (j + 1 < changes.Count && changes[j + 1].Address == changes[j].Address + 1 && j - i < 7) j++;
            var a = changes[i].Address;
            AddChange(new Change(now, a, NameFor(a),
                string.Join(" ", changes.Skip(i).Take(j - i + 1).Select(x => x.Old.ToString("X2"))),
                string.Join(" ", changes.Skip(i).Take(j - i + 1).Select(x => x.New.ToString("X2")))));
            i = j + 1;
        }
    }

    private void AddChange(Change c)
    {
        _changeLog.Add(c);
        if (_changeLog.Count > 5000) _changeLog.RemoveRange(0, 1000);
        if (_changesPage.Visible) { _changes.RowCount = _changeLog.Count; _changes.Invalidate(); }
        _tabChanges.Text = $"Changes ({_changeLog.Count:N0})";
    }

    private void UpdateHexInfo()
    {
        uint a = _hex.CursorAddress;
        var mod = _modules.FirstOrDefault(m => a >= m.Base && a < m.Base + m.Size);
        var sec = mod != null ? (_sections.GetValueOrDefault(mod.Name) ?? new()).FirstOrDefault(s => a >= s.Base && a < s.Base + s.Size) : null;
        var reg = _regions.FirstOrDefault(r => a >= r.Base && a < r.End);
        var parts = new List<string> { $"0x{_hex.BaseAddress:X8} – 0x{_hex.BaseAddress + (uint)_hex.ViewLength:X8}", $"cursor 0x{a:X8}" };
        if (Host.Symbols.Describe(a) is { } s) parts.Add(s);
        if (mod != null) parts.Add(mod.Name + (sec != null ? " " + sec.Name : "") + $" +0x{a - mod.Base:X}");
        if (reg != null) parts.Add(reg.ProtectText);
        if (_hex.SelectionLength > 1) parts.Add($"{_hex.SelectionLength} bytes selected");
        parts.Add("type hex to edit · Tab = ASCII");
        _hexInfo.Text = "  " + string.Join("   ·   ", parts);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  inspector
    // ═════════════════════════════════════════════════════════════════════════

    private static readonly string[] InspectorRows = { "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "Float", "Double", "Pointer", "Instruction", "ASCII", "UTF-16" };

    private void UpdateInspector()
    {
        if (_inspector.Rows.Count != InspectorRows.Length)
        {
            _inspector.Rows.Clear();
            foreach (var r in InspectorRows) _inspector.Rows.Add(r, "");
        }
        uint a = _hex.CursorAddress;
        string V(int n, Func<byte[], string> f) => _hex.Slice(a, n) is { } b ? f(b) : "—";
        var vals = new[]
        {
            V(1, b => $"{b[0]}  (0x{b[0]:X2})"),
            V(2, b => BinaryPrimitives.ReadInt16BigEndian(b).ToString()),
            V(2, b => BinaryPrimitives.ReadUInt16BigEndian(b).ToString()),
            V(4, b => BinaryPrimitives.ReadInt32BigEndian(b).ToString()),
            V(4, b => $"{BinaryPrimitives.ReadUInt32BigEndian(b)}  (0x{BinaryPrimitives.ReadUInt32BigEndian(b):X8})"),
            V(8, b => BinaryPrimitives.ReadInt64BigEndian(b).ToString()),
            V(4, b => BinaryPrimitives.ReadSingleBigEndian(b).ToString("0.######", CultureInfo.InvariantCulture)),
            V(8, b => BinaryPrimitives.ReadDoubleBigEndian(b).ToString("0.#########", CultureInfo.InvariantCulture)),
            V(4, b => { uint p = BinaryPrimitives.ReadUInt32BigEndian(b); return p >= 0x80000000 ? $"0x{p:X8}" + (Host.Symbols.Describe(p) is { } s ? $" {s}" : "") : $"0x{p:X8} (not a pointer)"; }),
            V(4, b => Ppc.Disassemble(BinaryPrimitives.ReadUInt32BigEndian(b), a & ~3u)),
            V(Math.Min(32, (int)(_hex.BaseAddress + (uint)_hex.ViewLength - a)), b => { var s = Encoding.ASCII.GetString(b.TakeWhile(x => x != 0).ToArray()); return new string(s.Select(ch => ch is >= ' ' and < (char)127 ? ch : '.').ToArray()); }),
            V(Math.Min(32, (int)(_hex.BaseAddress + (uint)_hex.ViewLength - a)) & ~1, b => { var s = Encoding.BigEndianUnicode.GetString(b); int z = s.IndexOf('\0'); return z >= 0 ? s[..z] : s; }),
        };
        for (int i = 0; i < vals.Length; i++)
        {
            var cell = _inspector.Rows[i].Cells[1];
            if (!Equals(cell.Value, vals[i])) cell.Value = vals[i];
        }
        UpdateHexInfo();
    }

    private LiveType InspectorType() => (_inspector.CurrentCell?.RowIndex ?? 3) switch
    {
        0 => LiveType.Byte, 1 or 2 => LiveType.Int16, 4 or 8 or 9 => LiveType.UInt32, 5 => LiveType.Int64, 6 => LiveType.Float, 7 => LiveType.Double, _ => LiveType.Int32,
    };

    private async Task EditInspectorAsync(int row)
    {
        if (row < 0 || row >= InspectorRows.Length) return;
        uint a = _hex.CursorAddress;
        var current = _inspector.Rows[row].Cells[1].Value?.ToString()?.Split("  ")[0] ?? "";
        var input = Prompt.Show(FindForm(), $"Write {InspectorRows[row]} at 0x{a:X8}", InspectorRows[row] switch
        {
            "Pointer" or "Instruction" => "Hex value (e.g. 82001234 / 60000000)",
            "ASCII" or "UTF-16" => "Text (a terminating zero is written)",
            _ => "Value (decimal or 0x hex)",
        }, current is "—" ? "" : current);
        if (input == null) return;
        byte[] bytes;
        try
        {
            bytes = InspectorRows[row] switch
            {
                "Byte" => LiveValue.Encode(LiveType.Byte, input),
                "Int16" or "UInt16" => LiveValue.Encode(LiveType.Int16, input),
                "Int32" or "UInt32" => LiveValue.Encode(LiveType.Int32, input),
                "Int64" => LiveValue.Encode(LiveType.Int64, input),
                "Float" => LiveValue.Encode(LiveType.Float, input),
                "Double" => LiveValue.Encode(LiveType.Double, input),
                "Pointer" or "Instruction" => LiveValue.Encode(LiveType.UInt32, "0x" + input.Trim().Replace("0x", "").Split(' ')[0]),
                "ASCII" => Encoding.ASCII.GetBytes(input + "\0"),
                _ => Encoding.BigEndianUnicode.GetBytes(input + "\0"),
            };
        }
        catch { App.Toast($"“{input}” is not a valid {InspectorRows[row]}", ToastHost.Kind.Warning); return; }
        await WriteAsync(a, bytes, quiet: false);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  writing / freezing
    // ═════════════════════════════════════════════════════════════════════════

    private async Task WriteAsync(uint address, byte[] bytes, bool quiet)
    {
        var c = RequireClient();
        if (c == null) return;
        bool ok = await c.SetMemoryAsync(address, bytes);
        if (!ok) App.Toast($"Write to 0x{address:X8} failed", ToastHost.Kind.Error);
        else
        {
            if (!quiet) App.Log($"✏️ 0x{address:X8} ← {OffsetTextFormat.NormalizeBytes(Convert.ToHexString(bytes))} ({NameFor(address)})");
            // keep a frozen value in sync with deliberate edits
            if (Freezer?.Get(address) is { } fe && fe.Bytes.Length == bytes.Length) Freezer.Freeze(address, bytes, fe.Label);
        }
        await RefreshHexAsync();
    }

    private async Task WriteBytesPromptAsync()
    {
        uint a = _hex.CursorAddress;
        var s = Prompt.Show(FindForm(), $"Write at 0x{a:X8}", "Hex bytes (e.g. 60 00 00 00)", "");
        var b = OffsetTextFormat.ParseBytes(s);
        if (b == null) { if (s != null) App.Toast("Enter an even number of hex digits", ToastHost.Kind.Warning); return; }
        await WriteAsync(a, b, quiet: false);
    }

    private void FreezeCursor()
    {
        if (Freezer is not { } fz) return;
        uint a = _hex.CursorAddress;
        if (fz.Unfreeze(a)) { App.Log($"🧊 Unfroze 0x{a:X8}"); return; }
        var t = InspectorType();
        var bytes = _hex.SelectionLength > 1 ? _hex.Slice(_hex.SelectionStart, (int)Math.Min(_hex.SelectionLength, 64u)) : _hex.Slice(a, LiveValue.Size(t));
        uint at = _hex.SelectionLength > 1 ? _hex.SelectionStart : a;
        if (bytes == null) { App.Toast("The value is not readable", ToastHost.Kind.Warning); return; }
        fz.Freeze(at, bytes, NameFor(at));
        App.Log($"🧊 Froze 0x{at:X8} = {OffsetTextFormat.NormalizeBytes(Convert.ToHexString(bytes))} ({NameFor(at)})");
    }

    private void ToggleFreeze(Item it)
    {
        if (Freezer is not { } fz) return;
        if (fz.Unfreeze(it.Address)) { App.Log($"🧊 Unfroze {it.Name}"); return; }
        if (it.Last == null) { App.Toast("Wait for the value to be read first", ToastHost.Kind.Info); return; }
        fz.Freeze(it.Address, it.Last, it.Name);
        App.Log($"🧊 Froze {it.Name} = {it.Display}");
    }

    private void FollowPointer()
    {
        if (_hex.Slice(_hex.CursorAddress, 4) is not { } b) return;
        uint p = BinaryPrimitives.ReadUInt32BigEndian(b);
        if (p < 0x80000000) { App.Toast($"0x{p:X8} is not a pointer into title memory", ToastHost.Kind.Info); return; }
        Open(p);
    }

    private void CopySelection()
    {
        var b = _hex.Slice(_hex.SelectionStart, (int)Math.Min(_hex.SelectionLength, 4096u));
        if (b == null) return;
        Clipboard.SetText(OffsetTextFormat.NormalizeBytes(Convert.ToHexString(b)));
        App.Toast($"Copied {b.Length} bytes", ToastHost.Kind.Success);
    }

    private void RecordChangedRange()
    {
        if (_changeLog.Count == 0) { App.Toast("No changes logged yet", ToastHost.Kind.Info); return; }
        uint lo = _changeLog.Min(c => c.Address), hi = _changeLog.Max(c => c.Address);
        uint size = Math.Min(hi - lo + 8, 0x100000u);
        App.MonitorRegion(lo & ~0xFu, size, "Active Memory change range");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  live values table
    // ═════════════════════════════════════════════════════════════════════════

    private void ApplyValuesFilter()
    {
        var f = _valuesFilter.Text.Trim();
        uint lo = _hex.BaseAddress, hi = _hex.BaseAddress + (uint)_hex.ViewLength;
        var cutoff = DateTime.Now.AddSeconds(-10);
        IEnumerable<Item> q = _valuesScope.SelectedIndex switch
        {
            1 => _items.Where(i => i.Address >= lo && i.Address < hi),
            2 => _items.Where(i => i.Kind == "Value"),
            3 => _items.Where(i => i.Kind == "Patch"),
            4 => _items.Where(i => i.ChangedAt > cutoff).OrderByDescending(i => i.ChangedAt),
            5 => _items.Where(i => Freezer?.IsFrozen(i.Address) == true),
            _ => _items,
        };
        if (f.Length > 0)
            q = q.Where(i => i.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || i.Category.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                             i.Address.ToString("X8").Contains(f.Replace("0x", ""), StringComparison.OrdinalIgnoreCase));
        _view = q.ToList();
        _values.RowCount = 0;
        _values.RowCount = _view.Count;
    }

    private void ValuesCellNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if ((uint)e.RowIndex >= (uint)_view.Count) return;
        var it = _view[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => it.Name,
            1 => it.Category,
            2 => $"0x{it.Address:X8}",
            3 => it.Kind == "Value" ? it.Type.ToString() : it.Kind,
            4 => it.Display,
            _ => Freezer?.IsFrozen(it.Address) == true ? "❄ frozen" : "freeze",
        };
    }

    private void ValuesFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if ((uint)e.RowIndex >= (uint)_view.Count || e.CellStyle == null) return;
        var it = _view[e.RowIndex];
        if (e.ColumnIndex == 4)
        {
            if ((DateTime.Now - it.ChangedAt).TotalSeconds < 1.5) e.CellStyle.ForeColor = Theme.Warning;
            else if (it.Kind == "Patch" && it.Display.EndsWith("ON")) e.CellStyle.ForeColor = Theme.Success;
            else if (it.Kind == "Code") e.CellStyle.ForeColor = Color.FromArgb(0xDC, 0xDC, 0xAA);
        }
        else if (e.ColumnIndex == 5) e.CellStyle.ForeColor = Freezer?.IsFrozen(it.Address) == true ? Theme.Teal : Theme.TextDim;
    }

    /// <summary>Polls only the rows on screen; logs value changes into the change log.</summary>
    private async Task RefreshValuesAsync()
    {
        var c = Client;
        if (c == null || _valuesReading || !_valuesPage.Visible || _view.Count == 0) return;
        _valuesReading = true;
        try
        {
            // snapshot: the filter / "changed recently" scope can replace _view while a read is awaited
            var view = _view;
            int first = Math.Clamp(_values.FirstDisplayedScrollingRowIndex, 0, Math.Max(0, view.Count - 1));
            int count = Math.Max(0, Math.Min(view.Count - first, _values.DisplayedRowCount(true) + 1));
            var now = DateTime.Now;
            for (int r = first; r < first + count; r++)
            {
                if (!c.IsConnected || IsDisposed) return;
                var it = view[r];
                var b = await c.GetMemoryAsync(it.Address, (uint)it.Length);
                string display;
                if (b == null) display = "unreadable";
                else display = it.Kind switch
                {
                    "Code" => Ppc.Disassemble(BinaryPrimitives.ReadUInt32BigEndian(b), it.Address),
                    "Patch" => OffsetTextFormat.NormalizeBytes(Convert.ToHexString(b)) + (it.Patch != null && OffsetTextFormat.NormalizeBytes(Convert.ToHexString(b)) == it.Patch ? "   ON" : ""),
                    "String" => "\"" + new string(Encoding.ASCII.GetString(b.TakeWhile(x => x != 0).ToArray()).Select(ch => ch is >= ' ' and < (char)127 ? ch : '.').ToArray()) + "\"",
                    _ => LiveValue.Format(it.Type, b),
                };
                if (it.Last != null && b != null && !b.AsSpan().SequenceEqual(it.Last))
                {
                    it.ChangedAt = now;
                    AddChange(new Change(now, it.Address, it.Name, it.Display, display));
                }
                it.Last = b;
                it.Display = display;
            }
            if (!IsDisposed) _values.Invalidate();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException) { }
        finally { _valuesReading = false; }
    }

    private async Task ValuePushedAsync(DataGridViewCellValueEventArgs e)
    {
        if (e.ColumnIndex != 4 || (uint)e.RowIndex >= (uint)_view.Count) return;
        var it = _view[e.RowIndex];
        var text = e.Value?.ToString()?.Trim() ?? "";
        if (text.Length == 0 || text == it.Display) return;
        byte[]? bytes;
        try
        {
            bytes = it.Kind switch
            {
                "Patch" => text.Equals("on", StringComparison.OrdinalIgnoreCase) ? OffsetTextFormat.ParseBytes(it.Patch) : OffsetTextFormat.ParseBytes(text.Replace("ON", "")),
                "Code" => LiveValue.Encode(LiveType.UInt32, "0x" + text.Replace("0x", "").Replace(" ", "")),
                "String" => Encoding.ASCII.GetBytes(text.Trim('"') + "\0"),
                _ => LiveValue.Encode(it.Type, text.Split("  ")[0]),
            };
        }
        catch { bytes = null; }
        if (bytes == null) { App.Toast($"“{text}” is not valid for {it.Name} ({(it.Kind == "Value" ? it.Type : it.Kind)})", ToastHost.Kind.Warning); return; }
        await WriteAsync(it.Address, bytes, quiet: false);
        it.Last = null; // re-read on the next tick
    }

    private ContextMenuStrip BuildValuesMenu()
    {
        var m = new ContextMenuStrip();
        Item? Sel() => _values.CurrentCell?.RowIndex is int i && i >= 0 && i < _view.Count ? _view[i] : null;
        m.Items.Add("Show in hex view", null, (_, _) => { if (Sel() is { } it) Open(it.Address); });
        m.Items.Add("Freeze / unfreeze", null, (_, _) => { if (Sel() is { } it) ToggleFreeze(it); });
        m.Items.Add("Record around (256 B)", null, (_, _) => { if (Sel() is { } it) App.MonitorRegion(Math.Max(0x80000000, it.Address - 0x40) & ~0xFu, 0x100, it.Name); });
        m.Items.Add("Add to watch list", null, (_, _) => { if (Sel() is { } it) App.AddWatch(it.Address, it.Name); });
        m.Items.Add("Graph", null, (_, _) => { if (Sel() is { } it) Host.Graph(it.Address, it.Kind == "Value" ? it.Type : LiveType.Int32, it.Name); });
        m.Items.Add("Disassemble", null, (_, _) => { if (Sel() is { } it) Host.Disassemble(it.Address & ~3u); });
        m.Items.Add("Pointer chain from here", null, (_, _) => { if (Sel() is { } it) Host.PointerChain(it.Address, Array.Empty<uint>()); });
        var viewAs = new ToolStripMenuItem("View as");
        foreach (var t in Enum.GetValues<LiveType>())
        {
            var type = t;
            viewAs.DropDownItems.Add(type.ToString(), null, (_, _) =>
            {
                if (Sel() is not { } it) return;
                _typeOverrides[it.Address] = type;
                it.Kind = "Value";
                it.Type = type;
                it.Length = LiveValue.Size(type);
                it.Last = null;
                _values.Invalidate();
            });
        }
        m.Items.Add(viewAs);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Copy (offsets format)", null, (_, _) => { if (Sel() is { } it) Clipboard.SetText($"{it.Name}: 0x{it.Address:X8}" + (it.Patch != null ? $" | Bytes: {it.Patch}" : "")); });
        m.Items.Add("Pin in explorer", null, (_, _) => { if (Sel() is { } it) Pin(it.Address); });
        Scripting.ScriptIde.StyleMenu(m);
        return m;
    }
}
