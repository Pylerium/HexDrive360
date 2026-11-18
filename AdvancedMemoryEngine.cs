using System.Text;
using System.Text.RegularExpressions;

namespace MemoryEditor;

/// <summary>
/// Advanced memory scanning and editing engine similar to Cheat Engine
/// </summary>
public class AdvancedMemoryEngine
{
    private XBDMClient? _client;
    private Dictionary<uint, ScanResult> _scanResults = new();
    private Dictionary<uint, WatchedAddress> _watchList = new();
    private List<MemoryRegion> _memoryRegions = new();

    public event EventHandler<string>? LogMessage;
    public event EventHandler<WatchedAddress>? AddressChanged;

    public enum ScanType
    {
        ExactValue,
        BiggerThan,
        SmallerThan,
        ValueBetween,
        IncreasedValue,
        DecreasedValue,
        ChangedValue,
        UnchangedValue,
        UnknownInitialValue
    }

    public enum ValueType
    {
        Byte,
        Int16,
        Int32,
        Int64,
        Float,
        Double,
        ByteArray,
        String
    }

    public class ScanResult
    {
        public uint Address { get; set; }
        public byte[] Value { get; set; } = Array.Empty<byte>();
        public byte[] PreviousValue { get; set; } = Array.Empty<byte>();
        public ValueType Type { get; set; }
        public string Description { get; set; } = "";
    }

    public class WatchedAddress
    {
        public uint Address { get; set; }
        public ValueType Type { get; set; }
        public string Description { get; set; } = "";
        public byte[] CurrentValue { get; set; } = Array.Empty<byte>();
        public byte[] PreviousValue { get; set; } = Array.Empty<byte>();
        public bool Frozen { get; set; }
        public byte[]? FrozenValue { get; set; }
        public bool IsPointer { get; set; }
        public List<uint> PointerOffsets { get; set; } = new();
    }

    public class MemoryRegion
    {
        public uint StartAddress { get; set; }
        public uint EndAddress { get; set; }
        public uint Size => EndAddress - StartAddress;
        public string Name { get; set; } = "";
        public bool Readable { get; set; }
        public bool Writable { get; set; }
        public bool Executable { get; set; }
    }

    public void SetClient(XBDMClient client)
    {
        _client = client;
    }

    // Clear all scan results
    public void ClearScanResults()
    {
        _scanResults.Clear();
        LogMessage?.Invoke(this, "🆕 Scan results cleared");
    }

    // Get current scan result count
    public int GetScanResultCount() => _scanResults.Count;

    // First scan for exact value
    public async Task<List<ScanResult>> FirstScan(uint startAddress, uint endAddress, ValueType valueType, object searchValue, ScanType scanType = ScanType.ExactValue)
    {
        if (_client == null) throw new InvalidOperationException("Client not set");

        var results = new List<ScanResult>();
        var chunkSize = 65536u; // 64KB chunks

        LogMessage?.Invoke(this, $"🔍 Starting first scan from 0x{startAddress:X8} to 0x{endAddress:X8}");

        for (uint addr = startAddress; addr < endAddress; addr += chunkSize)
        {
            var size = Math.Min(chunkSize, endAddress - addr);
            var memory = await _client.GetMemoryAsync(addr, size);

            if (memory == null) continue;

            // Search through memory
            var matches = SearchMemoryChunk(memory, addr, valueType, searchValue, scanType);
            results.AddRange(matches);

            if (results.Count > 100000) // Limit results
            {
                LogMessage?.Invoke(this, "⚠️ Result limit reached (100,000)");
                break;
            }
        }

        // Store results
        _scanResults.Clear();
        foreach (var result in results)
        {
            _scanResults[result.Address] = result;
        }

        LogMessage?.Invoke(this, $"✅ Found {results.Count} matches");
        return results;
    }

    // Next scan to filter results
    public async Task<List<ScanResult>> NextScan(ValueType valueType, object searchValue, ScanType scanType)
    {
        if (_client == null) throw new InvalidOperationException("Client not set");
        if (_scanResults.Count == 0) throw new InvalidOperationException("No previous scan results");

        LogMessage?.Invoke(this, $"🔍 Next scan filtering {_scanResults.Count} addresses");

        var filteredResults = new List<ScanResult>();
        var addressList = _scanResults.Keys.ToList();

        // Read current values in batches
        var batchSize = 1000;
        for (int i = 0; i < addressList.Count; i += batchSize)
        {
            var batch = addressList.Skip(i).Take(batchSize);

            foreach (var addr in batch)
            {
                var oldResult = _scanResults[addr];
                var size = GetValueSize(valueType);
                var memory = await _client.GetMemoryAsync(addr, size);

                if (memory == null) continue;

                // Store current value as previous before comparing
                var previousValueCopy = new byte[oldResult.Value.Length];
                Array.Copy(oldResult.Value, previousValueCopy, oldResult.Value.Length);

                var matches = CompareValue(memory, valueType, searchValue, scanType, oldResult.Value);

                if (matches)
                {
                    var newResult = new ScanResult
                    {
                        Address = addr,
                        Value = memory,
                        PreviousValue = previousValueCopy, // Use the copy, not reference
                        Type = valueType,
                        Description = oldResult.Description
                    };
                    filteredResults.Add(newResult);
                }
            }
        }

        // Update results - preserve the filtered list
        _scanResults.Clear();
        foreach (var result in filteredResults)
        {
            _scanResults[result.Address] = result;
        }

        LogMessage?.Invoke(this, $"✅ Filtered to {filteredResults.Count} matches (from {addressList.Count})");
        return filteredResults;
    }

    // Refresh current scan values without filtering
    public async Task<List<ScanResult>> RefreshScanValues()
    {
        if (_client == null) throw new InvalidOperationException("Client not set");
        if (_scanResults.Count == 0) return new List<ScanResult>();

        var refreshedResults = new List<ScanResult>();
        var addressList = _scanResults.Keys.ToList();

        foreach (var addr in addressList)
        {
            var oldResult = _scanResults[addr];
            var size = GetValueSize(oldResult.Type);
            var memory = await _client.GetMemoryAsync(addr, size);

            if (memory != null)
            {
                var refreshed = new ScanResult
                {
                    Address = addr,
                    Value = memory,
                    PreviousValue = oldResult.Value,
                    Type = oldResult.Type,
                    Description = oldResult.Description
                };
                refreshedResults.Add(refreshed);
                _scanResults[addr] = refreshed; // Update stored value
            }
        }

        return refreshedResults;
    }

    // Pointer scan
    public async Task<List<PointerPath>> PointerScan(uint targetAddress, uint maxOffset = 0x2000, int maxLevel = 5)
    {
        if (_client == null) throw new InvalidOperationException("Client not set");

        LogMessage?.Invoke(this, $"🔍 Scanning for pointers to 0x{targetAddress:X8}");

        var pointerPaths = new List<PointerPath>();

        // Scan common memory regions for pointers
        var searchRanges = new[]
        {
            (0x80000000u, 0x82000000u), // Common Xbox 360 ranges
            (0x82000000u, 0x84000000u),
            (0x90000000u, 0x92000000u)
        };

        foreach (var (start, end) in searchRanges)
        {
            await ScanPointerRange(start, end, targetAddress, maxOffset, maxLevel, pointerPaths);
        }

        LogMessage?.Invoke(this, $"✅ Found {pointerPaths.Count} pointer paths");
        return pointerPaths;
    }

    private async Task ScanPointerRange(uint start, uint end, uint target, uint maxOffset, int level, List<PointerPath> results)
    {
        if (_client == null || level <= 0) return;

        var chunkSize = 65536u;
        for (uint addr = start; addr < end; addr += chunkSize)
        {
            var size = Math.Min(chunkSize, end - addr);
            var memory = await _client.GetMemoryAsync(addr, size);
            if (memory == null) continue;

            // Search for potential pointers (4-byte aligned)
            for (int i = 0; i < memory.Length - 3; i += 4)
            {
                var pointerValue = Be.ToUInt32(memory, i);

                // Check if this points near our target
                if (pointerValue >= target - maxOffset && pointerValue <= target + maxOffset)
                {
                    var offset = (int)(target - pointerValue);
                    var path = new PointerPath
                    {
                        BaseAddress = addr + (uint)i,
                        Offsets = new List<int> { offset },
                        Level = 1
                    };
                    results.Add(path);
                }
            }
        }
    }

    public class PointerPath
    {
        public uint BaseAddress { get; set; }
        public List<int> Offsets { get; set; } = new();
        public int Level { get; set; }

        public override string ToString()
        {
            var offsetsStr = string.Join(", ", Offsets.Select(o => $"0x{o:X}"));
            return $"[0x{BaseAddress:X8}] + {offsetsStr}";
        }
    }

    // Add address to watch list
    public void AddToWatchList(uint address, ValueType type, string description = "", bool isPointer = false, List<uint>? pointerOffsets = null)
    {
        var watched = new WatchedAddress
        {
            Address = address,
            Type = type,
            Description = description,
            IsPointer = isPointer,
            PointerOffsets = pointerOffsets ?? new List<uint>()
        };

        _watchList[address] = watched;
    }

    // Update watched addresses
    public async Task UpdateWatchList()
    {
        if (_client == null) return;

        foreach (var watched in _watchList.Values)
        {
            var effectiveAddress = watched.Address;

            // Resolve pointer chain
            if (watched.IsPointer && watched.PointerOffsets.Count > 0)
            {
                effectiveAddress = await ResolvePointer(watched.Address, watched.PointerOffsets);
            }

            var size = GetValueSize(watched.Type);
            var memory = await _client.GetMemoryAsync(effectiveAddress, size);

            if (memory != null)
            {
                watched.PreviousValue = watched.CurrentValue;
                watched.CurrentValue = memory;

                // Check if value changed
                if (!watched.PreviousValue.SequenceEqual(watched.CurrentValue))
                {
                    AddressChanged?.Invoke(this, watched);
                }

                // Apply frozen value if needed
                if (watched.Frozen && watched.FrozenValue != null)
                {
                    await _client.SetMemoryAsync(effectiveAddress, watched.FrozenValue);
                }
            }
        }
    }

    // Resolve pointer with offsets
    public async Task<uint> ResolvePointer(uint baseAddress, List<uint> offsets)
    {
        if (_client == null) return 0;

        var currentAddress = baseAddress;

        for (int i = 0; i < offsets.Count; i++)
        {
            var memory = await _client.GetMemoryAsync(currentAddress, 4);
            if (memory == null) return 0;

            currentAddress = Be.ToUInt32(memory, 0);
            if (i < offsets.Count - 1)
            {
                currentAddress += offsets[i];
            }
        }

        return currentAddress + offsets[^1];
    }

    // Import addresses from file
    public async Task<List<WatchedAddress>> ImportAddresses(string filePath)
    {
        var addresses = new List<WatchedAddress>();

        foreach (var line in await File.ReadAllLinesAsync(filePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;

            // Format: Address,Type,Description
            // Example: 0x82000000,Int32,Player Health
            var parts = line.Split(',');
            if (parts.Length < 2) continue;

            if (uint.TryParse(parts[0].Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var address))
            {
                var type = Enum.TryParse<ValueType>(parts[1], out var vt) ? vt : ValueType.Int32;
                var desc = parts.Length > 2 ? parts[2] : "";

                var watched = new WatchedAddress
                {
                    Address = address,
                    Type = type,
                    Description = desc
                };

                addresses.Add(watched);
                _watchList[address] = watched;
            }
        }

        LogMessage?.Invoke(this, $"📥 Imported {addresses.Count} addresses");
        return addresses;
    }

    // Export addresses to file
    public async Task ExportAddresses(string filePath)
    {
        var lines = new List<string>
        {
            "// Memory Editor Address List",
            "// Format: Address,Type,Description"
        };

        foreach (var watched in _watchList.Values)
        {
            var line = $"0x{watched.Address:X8},{watched.Type},{watched.Description}";
            lines.Add(line);
        }

        await File.WriteAllLinesAsync(filePath, lines);
        LogMessage?.Invoke(this, $"💾 Exported {_watchList.Count} addresses");
    }

    // Dissect data structure
    public async Task<List<StructureMember>> DissectStructure(uint baseAddress, uint size)
    {
        if (_client == null) throw new InvalidOperationException("Client not set");

        var memory = await _client.GetMemoryAsync(baseAddress, size);
        if (memory == null) return new List<StructureMember>();

        var members = new List<StructureMember>();

        // Try to identify common patterns
        for (uint offset = 0; offset < size - 4; offset++)
        {
            var addr = baseAddress + offset;

            // Check for pointer
            if (offset % 4 == 0)
            {
                var value = Be.ToUInt32(memory, (int)offset);
                if (IsValidPointer(value))
                {
                    members.Add(new StructureMember
                    {
                        Offset = offset,
                        Address = addr,
                        Type = ValueType.Int32,
                        Name = $"Pointer_{offset:X}",
                        Value = $"0x{value:X8}"
                    });
                }
            }

            // Check for float
            if (offset % 4 == 0 && offset + 3 < size)
            {
                var floatValue = Be.ToSingle(memory, (int)offset);
                if (IsReasonableFloat(floatValue))
                {
                    members.Add(new StructureMember
                    {
                        Offset = offset,
                        Address = addr,
                        Type = ValueType.Float,
                        Name = $"Float_{offset:X}",
                        Value = floatValue.ToString("F2")
                    });
                }
            }
        }

        return members;
    }

    public class StructureMember
    {
        public uint Offset { get; set; }
        public uint Address { get; set; }
        public ValueType Type { get; set; }
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public class MemoryRecording
    {
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public uint StartAddress { get; set; }
        public uint EndAddress { get; set; }
        public List<MemoryChange> Changes { get; set; } = new();
        public bool IsRecording { get; set; }
    }

    public class MemoryChange
    {
        public DateTime Timestamp { get; set; }
        public uint Address { get; set; }
        public byte OldValue { get; set; }
        public byte NewValue { get; set; }
        public TimeSpan TimeSinceStart { get; set; }
    }

    private MemoryRecording? _activeRecording;
    private byte[]? _recordingBaselineMemory;
    private System.Threading.Timer? _recordingTimer;
    private readonly object _recordingLock = new object();
    private bool _isCheckingRecording = false;

    // Base address monitoring
    private MemoryRecording? _baseAddressRecording;
    private byte[]? _baseAddressBaselineMemory;
    private System.Threading.Timer? _baseAddressTimer;
    private bool _isCheckingBaseAddress = false;

    // ── hardening: the recorder polls continuously, so it must adapt to how fast the console answers ──
    /// <summary>Upper bound for stored changes per recording (memory safety for hour-long recordings).</summary>
    public int MaxRecordedChanges { get; set; } = 500_000;
    private int _recordFailures, _baseFailures, _baseMerged;
    private bool _recordCapLogged, _baseCapLogged;

    /// <summary>Reads big ranges in 64 KB pieces with a short pause between them so the game and XBDM stay responsive.</summary>
    private async Task<byte[]?> ReadPacedAsync(uint start, uint size)
    {
        var c = _client;
        if (c == null || !c.IsConnected) return null;
        if (size <= 0x40000) return await c.GetMemoryAsync(start, size);
        var data = new byte[size];
        for (uint off = 0; off < size; off += 0x10000)
        {
            uint n = Math.Min(0x10000u, size - off);
            var part = await c.GetMemoryAsync(start + off, n);
            if (part == null || part.Length < n) return null;   // never compare against a partial read (false changes)
            Buffer.BlockCopy(part, 0, data, (int)off, (int)n);
            await Task.Delay(1);
        }
        return data;
    }

    /// <summary>Next poll delay: proportional to the last read time, backing off while reads fail.</summary>
    private static int NextDelay(TimeSpan took, int failures, int min, int max)
    {
        if (failures > 0) return Math.Min(max, min * (1 << Math.Min(failures, 6)));
        return (int)Math.Clamp(took.TotalMilliseconds * 2, min, max);
    }

    private static void Reschedule(System.Threading.Timer? t, int ms)
    {
        try { t?.Change(ms, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    // Start recording memory changes
    public async Task StartRecording(uint startAddress, uint size)
    {
        if (_client == null) throw new InvalidOperationException("Client not set");

        lock (_recordingLock)
        {
            _activeRecording = new MemoryRecording
            {
                StartTime = DateTime.Now,
                StartAddress = startAddress,
                EndAddress = startAddress + size,
                IsRecording = true
            };
        }

        // Capture baseline
        _recordingBaselineMemory = await ReadPacedAsync(startAddress, size);

        if (_recordingBaselineMemory == null)
        {
            lock (_recordingLock) _activeRecording = null;
            throw new InvalidOperationException($"Failed to read baseline memory at 0x{startAddress:X8} ({MemorySafety.Describe(startAddress)})");
        }

        LogMessage?.Invoke(this, $"📹 Recording started: 0x{startAddress:X8} - 0x{(startAddress + size):X8}");

        // one-shot timer re-armed after every check: the interval follows the console's speed and checks never overlap
        _recordFailures = 0;
        _recordCapLogged = false;
        _recordingTimer?.Dispose();
        _recordingTimer = new System.Threading.Timer(async _ => await CheckRecordingChangesAsync(), null, 50, Timeout.Infinite);
    }

    // Stop recording
    public MemoryRecording? StopRecording()
    {
        lock (_recordingLock)
        {
            if (_activeRecording == null) return null;

            _activeRecording.EndTime = DateTime.Now;
            _activeRecording.IsRecording = false;
            _recordingTimer?.Dispose();
            _recordingTimer = null;

            var recording = _activeRecording;
            _activeRecording = null;
            _recordingBaselineMemory = null;

            LogMessage?.Invoke(this, $"⏹️ Recording stopped: {recording.Changes.Count} changes detected");
            return recording;
        }
    }

    private async Task CheckRecordingChangesAsync()
    {
        // Prevent overlapping checks
        if (_isCheckingRecording) return;

        lock (_recordingLock)
        {
            if (_activeRecording == null || _recordingBaselineMemory == null || !_activeRecording.IsRecording)
                return;

            _isCheckingRecording = true;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            uint startAddress;
            uint size;

            lock (_recordingLock)
            {
                if (_activeRecording == null) return;
                startAddress = _activeRecording.StartAddress;
                size = _activeRecording.EndAddress - _activeRecording.StartAddress;
            }

            if (_client == null || !_client.IsConnected) { _recordFailures = 1; return; }   // link down: idle quietly
            var currentMemory = await ReadPacedAsync(startAddress, size);
            if (currentMemory == null)
            {
                if (++_recordFailures == 10)
                    LogMessage?.Invoke(this, $"⚠️ Recording: 0x{startAddress:X8} is not readable right now – retrying slowly");
                return;
            }
            if (_recordFailures >= 10) LogMessage?.Invoke(this, "✅ Recording: memory readable again");
            _recordFailures = 0;

            // Compare with baseline
            var changes = new List<MemoryChange>();
            for (int i = 0; i < Math.Min(_recordingBaselineMemory.Length, currentMemory.Length); i++)
            {
                if (_recordingBaselineMemory[i] != currentMemory[i])
                {
                    var change = new MemoryChange
                    {
                        Timestamp = DateTime.Now,
                        Address = startAddress + (uint)i,
                        OldValue = _recordingBaselineMemory[i],
                        NewValue = currentMemory[i]
                    };

                    lock (_recordingLock)
                    {
                        if (_activeRecording != null)
                        {
                            change.TimeSinceStart = DateTime.Now - _activeRecording.StartTime;
                        }
                    }

                    changes.Add(change);

                    // Update baseline to new value
                    _recordingBaselineMemory[i] = currentMemory[i];
                }
            }

            // Add all changes in one lock
            if (changes.Count > 0)
            {
                lock (_recordingLock)
                {
                    if (_activeRecording != null)
                    {
                        int room = MaxRecordedChanges - _activeRecording.Changes.Count;
                        if (room > 0) _activeRecording.Changes.AddRange(room >= changes.Count ? changes : changes.GetRange(0, room));
                        else if (!_recordCapLogged)
                        {
                            _recordCapLogged = true;
                            LogMessage?.Invoke(this, $"⚠️ Recording holds {MaxRecordedChanges:N0} changes – new changes are no longer stored (stop and export)");
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore errors during recording
        }
        finally
        {
            _isCheckingRecording = false;
            lock (_recordingLock)
                if (_activeRecording != null) Reschedule(_recordingTimer, NextDelay(sw.Elapsed, _recordFailures, 50, 3000));
        }
    }

    // Get active recording info
    public MemoryRecording? GetActiveRecording()
    {
        lock (_recordingLock)
        {
            // Merge base address changes into main recording if both are active. Only the changes added since the last
            // call are merged (the old Any() scan was O(n²) and froze the UI once a recording held thousands of changes).
            if (_activeRecording != null && _baseAddressRecording != null)
            {
                var src = _baseAddressRecording.Changes;
                if (_baseMerged > src.Count) _baseMerged = 0;
                int room = MaxRecordedChanges - _activeRecording.Changes.Count;
                int take = Math.Min(src.Count - _baseMerged, Math.Max(0, room));
                if (take > 0) _activeRecording.Changes.AddRange(src.GetRange(_baseMerged, take));
                _baseMerged = src.Count;
            }

            return _activeRecording;
        }
    }

    // Start monitoring base address (0x82000000)
    public async Task StartBaseAddressMonitoring(uint baseAddress, uint size)
    {
        if (_client == null) throw new InvalidOperationException("Client not set");

        lock (_recordingLock)
        {
            _baseAddressRecording = new MemoryRecording
            {
                StartTime = DateTime.Now,
                StartAddress = baseAddress,
                EndAddress = baseAddress + size,
                IsRecording = true
            };
        }

        // Capture baseline
        _baseAddressBaselineMemory = await ReadPacedAsync(baseAddress, size);

        if (_baseAddressBaselineMemory == null)
        {
            lock (_recordingLock) _baseAddressRecording = null;
            throw new InvalidOperationException($"Failed to read base address memory at 0x{baseAddress:X8} ({MemorySafety.Describe(baseAddress)})");
        }

        LogMessage?.Invoke(this, $"📍 Base address monitoring started: 0x{baseAddress:X8} - 0x{(baseAddress + size):X8}");

        // Start periodic checking (one-shot, re-armed after each pass – see CheckBaseAddressChangesAsync)
        _baseFailures = 0;
        _baseMerged = 0;
        _baseCapLogged = false;
        _baseAddressTimer?.Dispose();
        _baseAddressTimer = new System.Threading.Timer(async _ => await CheckBaseAddressChangesAsync(), null, 100, Timeout.Infinite);
    }

    // Stop base address monitoring
    public void StopBaseAddressMonitoring()
    {
        lock (_recordingLock)
        {
            if (_baseAddressRecording != null)
            {
                _baseAddressRecording.IsRecording = false;
                _baseAddressTimer?.Dispose();
                _baseAddressTimer = null;

                LogMessage?.Invoke(this, $"⏹️ Base address monitoring stopped: {_baseAddressRecording.Changes.Count} changes detected");

                _baseAddressRecording = null;
                _baseAddressBaselineMemory = null;
            }
        }
    }

    private async Task CheckBaseAddressChangesAsync()
    {
        // Prevent overlapping checks
        if (_isCheckingBaseAddress) return;

        lock (_recordingLock)
        {
            if (_baseAddressRecording == null || _baseAddressBaselineMemory == null || !_baseAddressRecording.IsRecording)
                return;

            _isCheckingBaseAddress = true;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            uint startAddress;
            uint size;

            lock (_recordingLock)
            {
                if (_baseAddressRecording == null) return;
                startAddress = _baseAddressRecording.StartAddress;
                size = _baseAddressRecording.EndAddress - _baseAddressRecording.StartAddress;
            }

            if (_client == null || !_client.IsConnected) { _baseFailures = 1; return; }
            var currentMemory = await ReadPacedAsync(startAddress, size);
            if (currentMemory == null) { _baseFailures++; return; }
            _baseFailures = 0;

            // Compare with baseline
            var changes = new List<MemoryChange>();
            for (int i = 0; i < Math.Min(_baseAddressBaselineMemory.Length, currentMemory.Length); i++)
            {
                if (_baseAddressBaselineMemory[i] != currentMemory[i])
                {
                    var change = new MemoryChange
                    {
                        Timestamp = DateTime.Now,
                        Address = startAddress + (uint)i,
                        OldValue = _baseAddressBaselineMemory[i],
                        NewValue = currentMemory[i]
                    };

                    lock (_recordingLock)
                    {
                        if (_baseAddressRecording != null)
                        {
                            change.TimeSinceStart = DateTime.Now - _baseAddressRecording.StartTime;
                        }
                    }

                    changes.Add(change);

                    // Update baseline to new value
                    _baseAddressBaselineMemory[i] = currentMemory[i];
                }
            }

            // Add all changes in one lock
            if (changes.Count > 0)
            {
                lock (_recordingLock)
                {
                    if (_baseAddressRecording != null)
                    {
                        int room = MaxRecordedChanges - _baseAddressRecording.Changes.Count;
                        if (room > 0) _baseAddressRecording.Changes.AddRange(room >= changes.Count ? changes : changes.GetRange(0, room));
                        else if (!_baseCapLogged)
                        {
                            _baseCapLogged = true;
                            LogMessage?.Invoke(this, $"⚠️ Base monitoring holds {MaxRecordedChanges:N0} changes – new changes are no longer stored");
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore errors during recording
        }
        finally
        {
            _isCheckingBaseAddress = false;
            // big regions are re-read at a rate proportional to how long one pass takes (31 MB every 50 ms locked XBDM up)
            lock (_recordingLock)
                if (_baseAddressRecording != null) Reschedule(_baseAddressTimer, NextDelay(sw.Elapsed, _baseFailures, 100, 5000));
        }
    }

    // Walk structure with detailed analysis
    public async Task<StructureWalkResult> WalkStructure(uint baseAddress, uint size, int maxDepth = 3)
    {
        if (_client == null) throw new InvalidOperationException("Client not set");

        var result = new StructureWalkResult
        {
            BaseAddress = baseAddress,
            Size = size,
            Members = new List<StructureMember>()
        };

        var memory = await _client.GetMemoryAsync(baseAddress, size);
        if (memory == null) return result;

        // Analyze structure
        await AnalyzeStructureRecursive(baseAddress, memory, 0, maxDepth, result.Members, new HashSet<uint>());

        return result;
    }

    private async Task AnalyzeStructureRecursive(uint baseAddress, byte[] memory, int currentDepth, int maxDepth, List<StructureMember> members, HashSet<uint> visitedAddresses)
    {
        if (currentDepth >= maxDepth || visitedAddresses.Contains(baseAddress))
            return;

        visitedAddresses.Add(baseAddress);

        for (uint offset = 0; offset < memory.Length - 4; offset += 4) // 4-byte aligned
        {
            var addr = baseAddress + offset;

            // Check for pointer
            if (offset + 3 < memory.Length)
            {
                var value = Be.ToUInt32(memory, (int)offset);

                if (IsValidPointer(value))
                {
                    members.Add(new StructureMember
                    {
                        Offset = offset,
                        Address = addr,
                        Type = ValueType.Int32,
                        Name = $"Pointer_{offset:X}",
                        Value = $"0x{value:X8} → [...]"
                    });

                    // Try to follow pointer
                    if (currentDepth < maxDepth - 1)
                    {
                        try
                        {
                            var pointedMemory = await _client!.GetMemoryAsync(value, 64);
                            if (pointedMemory != null)
                            {
                                await AnalyzeStructureRecursive(value, pointedMemory, currentDepth + 1, maxDepth, members, visitedAddresses);
                            }
                        }
                        catch { }
                    }

                    continue;
                }
            }

            // Check for float
            if (offset + 3 < memory.Length)
            {
                var floatValue = Be.ToSingle(memory, (int)offset);
                if (IsReasonableFloat(floatValue))
                {
                    members.Add(new StructureMember
                    {
                        Offset = offset,
                        Address = addr,
                        Type = ValueType.Float,
                        Name = $"Float_{offset:X}",
                        Value = floatValue.ToString("F2")
                    });
                }
            }

            // Check for int32
            if (offset + 3 < memory.Length)
            {
                var intValue = Be.ToInt32(memory, (int)offset);
                if (intValue > -1000000 && intValue < 1000000) // Reasonable range
                {
                    members.Add(new StructureMember
                    {
                        Offset = offset,
                        Address = addr,
                        Type = ValueType.Int32,
                        Name = $"Int32_{offset:X}",
                        Value = intValue.ToString()
                    });
                }
            }

            // Check for string
            if (offset + 8 < memory.Length)
            {
                var text = ExtractPossibleString(memory, (int)offset);
                if (!string.IsNullOrEmpty(text) && text.Length > 3)
                {
                    members.Add(new StructureMember
                    {
                        Offset = offset,
                        Address = addr,
                        Type = ValueType.String,
                        Name = $"String_{offset:X}",
                        Value = $"\"{text}\""
                    });
                }
            }
        }
    }

    public class StructureWalkResult
    {
        public uint BaseAddress { get; set; }
        public uint Size { get; set; }
        public List<StructureMember> Members { get; set; } = new();
    }

    private string ExtractPossibleString(byte[] memory, int offset)
    {
        var sb = new StringBuilder();
        for (int i = offset; i < Math.Min(offset + 64, memory.Length); i++)
        {
            var b = memory[i];
            if (b == 0) break; // Null terminator
            if (b < 32 || b > 126) return ""; // Not printable
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private bool IsValidPointer(uint value)
    {
        // Xbox 360 valid address ranges
        return (value >= 0x80000000 && value <= 0x9FFFFFFF);
    }

    private bool IsReasonableFloat(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value) &&
               value >= -1000000f && value <= 1000000f;
    }

    private List<ScanResult> SearchMemoryChunk(byte[] memory, uint baseAddress, ValueType type, object searchValue, ScanType scanType)
    {
        var results = new List<ScanResult>();
        var valueSize = GetValueSize(type);

        for (int i = 0; i <= memory.Length - valueSize; i++)
        {
            var slice = new byte[valueSize];
            Array.Copy(memory, i, slice, 0, valueSize);

            if (CompareValue(slice, type, searchValue, scanType, null))
            {
                var resultValue = new byte[valueSize];
                Array.Copy(memory, i, resultValue, 0, valueSize);

                results.Add(new ScanResult
                {
                    Address = baseAddress + (uint)i,
                    Value = resultValue,
                    Type = type
                });
            }
        }

        return results;
    }

    private bool CompareValue(byte[] currentValue, ValueType type, object searchValue, ScanType scanType, byte[]? previousValue)
    {
        switch (scanType)
        {
            case ScanType.ExactValue:
                return CompareExact(currentValue, type, searchValue);
            case ScanType.ChangedValue:
                if (previousValue == null || previousValue.Length != currentValue.Length) return false;
                return !currentValue.SequenceEqual(previousValue);
            case ScanType.UnchangedValue:
                if (previousValue == null || previousValue.Length != currentValue.Length) return false;
                return currentValue.SequenceEqual(previousValue);
            case ScanType.IncreasedValue:
                if (previousValue == null || previousValue.Length != currentValue.Length) return false;
                return CompareNumeric(currentValue, previousValue, type) > 0;
            case ScanType.DecreasedValue:
                if (previousValue == null || previousValue.Length != currentValue.Length) return false;
                return CompareNumeric(currentValue, previousValue, type) < 0;
            case ScanType.BiggerThan:
                return CompareToThreshold(currentValue, type, searchValue, 1);
            case ScanType.SmallerThan:
                return CompareToThreshold(currentValue, type, searchValue, -1);
            case ScanType.UnknownInitialValue:
                return true; // Accept all for initial scan
            default:
                return false;
        }
    }

    private bool CompareToThreshold(byte[] value, ValueType type, object threshold, int direction)
    {
        try
        {
            switch (type)
            {
                case ValueType.Byte:
                    var b = value[0];
                    var tb = Convert.ToByte(threshold);
                    return direction > 0 ? b > tb : b < tb;
                case ValueType.Int16:
                    var s = Be.ToInt16(value, 0);
                    var ts = Convert.ToInt16(threshold);
                    return direction > 0 ? s > ts : s < ts;
                case ValueType.Int32:
                    var i = Be.ToInt32(value, 0);
                    var ti = Convert.ToInt32(threshold);
                    return direction > 0 ? i > ti : i < ti;
                case ValueType.Int64:
                    var l = Be.ToInt64(value, 0);
                    var tl = Convert.ToInt64(threshold);
                    return direction > 0 ? l > tl : l < tl;
                case ValueType.Float:
                    var f = Be.ToSingle(value, 0);
                    var tf = Convert.ToSingle(threshold);
                    return direction > 0 ? f > tf : f < tf;
                case ValueType.Double:
                    var d = Be.ToDouble(value, 0);
                    var td = Convert.ToDouble(threshold);
                    return direction > 0 ? d > td : d < td;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private bool CompareExact(byte[] value, ValueType type, object searchValue)
    {
        try
        {
            switch (type)
            {
                case ValueType.Byte:
                    return value[0] == Convert.ToByte(searchValue);
                case ValueType.Int16:
                    return Be.ToInt16(value, 0) == Convert.ToInt16(searchValue);
                case ValueType.Int32:
                    return Be.ToInt32(value, 0) == Convert.ToInt32(searchValue);
                case ValueType.Int64:
                    return Be.ToInt64(value, 0) == Convert.ToInt64(searchValue);
                case ValueType.Float:
                    return Math.Abs(Be.ToSingle(value, 0) - Convert.ToSingle(searchValue)) < 0.001f;
                case ValueType.Double:
                    return Math.Abs(Be.ToDouble(value, 0) - Convert.ToDouble(searchValue)) < 0.001;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private int CompareNumeric(byte[] current, byte[] previous, ValueType type)
    {
        try
        {
            switch (type)
            {
                case ValueType.Byte:
                    return current[0].CompareTo(previous[0]);
                case ValueType.Int16:
                    return Be.ToInt16(current, 0).CompareTo(Be.ToInt16(previous, 0));
                case ValueType.Int32:
                    return Be.ToInt32(current, 0).CompareTo(Be.ToInt32(previous, 0));
                case ValueType.Int64:
                    return Be.ToInt64(current, 0).CompareTo(Be.ToInt64(previous, 0));
                case ValueType.Float:
                    return Be.ToSingle(current, 0).CompareTo(Be.ToSingle(previous, 0));
                case ValueType.Double:
                    return Be.ToDouble(current, 0).CompareTo(Be.ToDouble(previous, 0));
                default:
                    return 0;
            }
        }
        catch
        {
            return 0;
        }
    }

    private uint GetValueSize(ValueType type)
    {
        return type switch
        {
            ValueType.Byte => 1,
            ValueType.Int16 => 2,
            ValueType.Int32 => 4,
            ValueType.Int64 => 8,
            ValueType.Float => 4,
            ValueType.Double => 8,
            _ => 4
        };
    }

    public string FormatValue(byte[] value, ValueType type)
    {
        try
        {
            return type switch
            {
                ValueType.Byte => value[0].ToString(),
                ValueType.Int16 => Be.ToInt16(value, 0).ToString(),
                ValueType.Int32 => Be.ToInt32(value, 0).ToString(),
                ValueType.Int64 => Be.ToInt64(value, 0).ToString(),
                ValueType.Float => Be.ToSingle(value, 0).ToString("F2"),
                ValueType.Double => Be.ToDouble(value, 0).ToString("F2"),
                ValueType.ByteArray => BitConverter.ToString(value).Replace("-", " "),
                _ => BitConverter.ToString(value)
            };
        }
        catch
        {
            return "Error";
        }
    }
}

/// <summary>
/// Big-endian readers with the BitConverter call shape. Xbox 360 memory is big-endian; the engine previously used
/// BitConverter (little-endian on PC), so scans, comparisons, pointer walks and displayed values were byte-swapped.
/// </summary>
internal static class Be
{
    public static short ToInt16(byte[] b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(o));
    public static ushort ToUInt16(byte[] b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
    public static int ToInt32(byte[] b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(o));
    public static uint ToUInt32(byte[] b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
    public static long ToInt64(byte[] b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(o));
    public static ulong ToUInt64(byte[] b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o));
    public static float ToSingle(byte[] b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(o));
    public static double ToDouble(byte[] b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(o));
}
