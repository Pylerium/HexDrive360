using System.Collections.Concurrent;

namespace MemoryEditor.Runtime.Toolkit;

/// <summary>
/// One shared freeze list for the whole application. A single background loop re-writes every frozen value
/// (big-endian bytes, exactly as given) while the console is connected.
///
/// Replaces the old per-address <c>Task.Run</c> loops, which raced on a plain Dictionary, kept running after a
/// disconnect and wrote values in little-endian order.
/// </summary>
public sealed class FreezeService : IDisposable
{
    public sealed record Entry(uint Address, byte[] Bytes, string Label);

    private readonly ConcurrentDictionary<uint, Entry> _items = new();
    private readonly Func<XBDMClient?> _client;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <summary>Raised (on the thread that changed the list) when entries are added or removed.</summary>
    public event Action? Changed;

    public int IntervalMs { get; set; } = 100;

    /// <summary>Optional log sink (thread-safe).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Title the current list belongs to (see <see cref="SwitchTitle"/>).</summary>
    public string TitleKey { get; private set; } = "";
    private readonly Dictionary<string, List<Entry>> _parked = new();
    private int _failures;
    public int Count => _items.Count;
    public IReadOnlyCollection<Entry> Items => _items.Values.OrderBy(e => e.Address).ToList();

    public FreezeService(Func<XBDMClient?> client)
    {
        _client = client;
        _loop = Task.Run(LoopAsync);
    }

    public bool IsFrozen(uint address) => _items.ContainsKey(address);

    /// <summary>True when any frozen entry covers <paramref name="address"/>.</summary>
    public bool Covers(uint address)
    {
        foreach (var e in _items.Values)
            if (address >= e.Address && address < e.Address + (uint)e.Bytes.Length) return true;
        return false;
    }

    public Entry? Get(uint address) => _items.GetValueOrDefault(address);

    public void Freeze(uint address, byte[] bytes, string label)
    {
        _items[address] = new Entry(address, bytes.ToArray(), label);
        Changed?.Invoke();
    }

    public bool Unfreeze(uint address)
    {
        bool removed = _items.TryRemove(address, out _);
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void Clear()
    {
        if (_items.IsEmpty) return;
        _items.Clear();
        Changed?.Invoke();
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Math.Clamp(IntervalMs, 20, 5000), ct); }
            catch (OperationCanceledException) { return; }
            if (_items.IsEmpty) continue;
            var c = _client();
            if (c == null || !c.IsConnected) continue;
            bool failed = false;
            foreach (var e in _items.Values)
            {
                if (ct.IsCancellationRequested) return;
                if (!c.IsConnected) { failed = true; break; }
                try { if (!await c.SetMemoryAsync(e.Address, e.Bytes)) failed = true; }
                catch { failed = true; /* transient – the shell handles reconnects; keep the freeze list */ }
            }

            // back off while writes fail (console busy, title switching, link down) instead of hammering XBDM
            if (failed)
            {
                _failures++;
                if (_failures == 5) Log?.Invoke($"⚠️ Freeze writes are failing – backing off (the {_items.Count} frozen value(s) stay armed)");
                int wait = Math.Min(5000, 250 * _failures);
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
            }
            else
            {
                if (_failures >= 5) Log?.Invoke("✅ Freeze writes recovered");
                _failures = 0;
            }
        }
    }

    /// <summary>
    /// Called when the running title changes. Frozen values belong to one game build – writing them into another game
    /// corrupts it – so the current list is parked under its title and the list saved for <paramref name="titleKey"/>
    /// (if that build ran before in this session) is restored automatically.
    /// </summary>
    public void SwitchTitle(string titleKey, string displayName = "")
    {
        titleKey ??= "";
        if (titleKey == TitleKey) return;
        if (titleKey.Length == 0) return;               // title unknown (menu / loading) – keep everything as is
        if (TitleKey.Length == 0)
        {
            // first identification of this session: the existing list belongs to this game
            TitleKey = titleKey;
            if (_parked.Remove(titleKey, out var early)) foreach (var e in early) _items.TryAdd(e.Address, e);
            return;
        }
        var current = _items.Values.ToList();
        if (current.Count > 0)
        {
            _parked[TitleKey] = current;
            Log?.Invoke($"🧊 Paused {current.Count} frozen value(s) – the running game changed");
        }
        _items.Clear();
        TitleKey = titleKey;
        if (_parked.Remove(titleKey, out var back) && back.Count > 0)
        {
            foreach (var e in back) _items[e.Address] = e;
            Log?.Invoke($"🧊 Restored {back.Count} frozen value(s) for {(displayName.Length > 0 ? displayName : "this game")}");
        }
        _failures = 0;
        Changed?.Invoke();
    }

    /// <summary>Number of frozen values parked for other titles.</summary>
    public int ParkedCount => _parked.Values.Sum(l => l.Count);

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(500); } catch { }
        _cts.Dispose();
    }
}
