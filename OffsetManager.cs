using System.Text.RegularExpressions;

namespace MemoryEditor;

/// <summary>
/// Manages game memory offsets, including parsing from offset files,
/// calculating pointer-based addresses, and organizing by categories.
/// </summary>
public class OffsetManager
{
    public class OffsetEntry
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public uint BaseAddress { get; set; }
        public uint? Offset { get; set; }
        public string? ByteValues { get; set; }
        public string Description { get; set; } = "";
        public string Type { get; set; } = "Int32"; // Default type
        
        /// <summary>
        /// Infers the correct data type based on name, bytes, and context
        /// </summary>
        public void InferDataType()
        {
            var nameLower = Name.ToLower();
            
            // Analyze byte values first (most accurate)
            if (!string.IsNullOrEmpty(ByteValues))
            {
                var bytes = ByteValues.Replace(" ", "").Replace(",", "");
                
                // Single byte operations
                if (bytes.Length == 2)
                {
                    Type = "Byte";
                    return;
                }
                
                // Check for float patterns (IEEE 754)
                if (bytes.Length == 8)
                {
                    // Common float patterns
                    if (bytes.StartsWith("3F") || bytes.StartsWith("40") || bytes.StartsWith("BF"))
                    {
                        Type = "Float";
                        return;
                    }
                }
                
                // NOP instructions and patches
                if (bytes == "60000000" || bytes == "4E800020")
                {
                    Type = "Bytes";
                    return;
                }
                
                // Multiple bytes
                if (bytes.Length > 8)
                {
                    Type = "Bytes";
                    return;
                }
                
                // Default for 4-byte hex
                Type = "Int32";
                return;
            }
            
            // Analyze name patterns
            if (nameLower.Contains("float") || nameLower.Contains("speed") || 
                nameLower.Contains("gravity") || nameLower.Contains("scale") ||
                nameLower.Contains("multiplier") || nameLower.Contains("fov") ||
                nameLower.Contains("rate") && !nameLower.Contains("firerate"))
            {
                Type = "Float";
                return;
            }
            
            if (nameLower.Contains("bool") || nameLower.Contains("flag") ||
                nameLower.Contains("enable") || nameLower.Contains("disable") ||
                nameLower.Contains("toggle"))
            {
                Type = "Byte";
                return;
            }
            
            if (nameLower.Contains("health") || nameLower.Contains("ammo") ||
                nameLower.Contains("xp") || nameLower.Contains("points") ||
                nameLower.Contains("level") || nameLower.Contains("count") ||
                nameLower.Contains("max") || nameLower.Contains("current"))
            {
                Type = "Int32";
                return;
            }
            
            if (nameLower.Contains("string") || nameLower.Contains("text") ||
                nameLower.Contains("name"))
            {
                Type = "String";
                return;
            }
            
            if (nameLower.Contains("pointer") || nameLower.Contains("address") ||
                nameLower.Contains("base"))
            {
                Type = "UInt32";
                return;
            }
            
            // Category-based inference
            if (Category.Contains("Protection") || Category.Contains("Bypass"))
            {
                Type = "Bytes";
                return;
            }
            
            // Default to Int32 for numeric values
            Type = "Int32";
        }
        
        /// <summary>
        /// Checks if this is a pointer-based offset (e.g., PlayerState + 0x436)
        /// </summary>
        public bool IsPointerBased => Offset.HasValue;
        
        /// <summary>
        /// Gets the final address when a pointer value is provided
        /// </summary>
        public uint CalculateFinalAddress(uint pointerValue)
        {
            if (IsPointerBased)
                return pointerValue + Offset!.Value;
            return BaseAddress;
        }
        
        /// <summary>
        /// Returns a display string for the offset
        /// </summary>
        public string GetDisplayAddress()
        {
            if (IsPointerBased)
                return $"0x{BaseAddress:X8} + 0x{Offset:X}";
            return $"0x{BaseAddress:X8}";
        }
    }
    
    private readonly Dictionary<string, List<OffsetEntry>> _offsetsByCategory = new();
    private readonly Dictionary<string, OffsetEntry> _offsetsByName = new();
    private readonly Dictionary<string, uint> _knownPointers = new();
    
    public IReadOnlyDictionary<string, List<OffsetEntry>> Categories => _offsetsByCategory;
    public IReadOnlyDictionary<string, uint> KnownPointers => _knownPointers;
    
    /// <summary>
    /// Loads offsets from the specified file path
    /// </summary>
    public async Task<bool> LoadFromFileAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Offset file not found: {filePath}");
            }
            
            var lines = await File.ReadAllLinesAsync(filePath);
            ParseOffsetFile(lines);
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading offset file: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Parses the offset file content
    /// </summary>
    private void ParseOffsetFile(string[] lines)
    {
        _offsetsByCategory.Clear();
        _offsetsByName.Clear();
        _knownPointers.Clear();
        
        string currentCategory = "General";
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            
            // Skip empty lines and pure comments
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//"))
                continue;
            
            // Category headers (e.g., ~~Cheat Protection~~)
            if (trimmed.StartsWith("~~") && trimmed.EndsWith("~~"))
            {
                currentCategory = trimmed.Trim('~', ' ');
                if (!_offsetsByCategory.ContainsKey(currentCategory))
                {
                    _offsetsByCategory[currentCategory] = new List<OffsetEntry>();
                }
                continue;
            }
            
            // Parse offset entries
            ParseOffsetLine(trimmed, currentCategory);
        }
    }
    
    /// <summary>
    /// Parses a single offset line
    /// </summary>
    private void ParseOffsetLine(string line, string category)
    {
        // Pattern 1: Name: 0x12345678 | Bytes: ...
        // Pattern 2: Name: 0x12345678
        // Pattern 3: Name: 0x12345678 + 0x436
        // Pattern 4: Name = 0x12345678
        
        // Remove inline comments
        var commentIndex = line.IndexOf("//");
        if (commentIndex > 0)
        {
            line = line.Substring(0, commentIndex).Trim();
        }
        
        // Try to extract name and address
        var colonSplit = line.Split(new[] { ':', '=' }, 2);
        if (colonSplit.Length != 2)
            return;
        
        var name = colonSplit[0].Trim();
        var remainder = colonSplit[1].Trim();
        
        // Extract bytes if present
        string? byteValues = null;
        var pipeSplit = remainder.Split('|');
        if (pipeSplit.Length > 1)
        {
            remainder = pipeSplit[0].Trim();
            var bytePart = pipeSplit[1].Trim();
            if (bytePart.ToLower().Contains("bytes:"))
            {
                byteValues = bytePart.Replace("Bytes:", "", StringComparison.OrdinalIgnoreCase).Trim();
            }
        }
        
        // Check for pointer-based offsets (e.g., 0x83551A10 + 0x436)
        if (remainder.Contains("+"))
        {
            var parts = remainder.Split('+');
            if (parts.Length == 2)
            {
                if (TryParseHex(parts[0].Trim(), out uint baseAddr) &&
                    TryParseHex(parts[1].Trim(), out uint offset))
                {
                    var entry = new OffsetEntry
                    {
                        Name = name,
                        Category = category,
                        BaseAddress = baseAddr,
                        Offset = offset,
                        ByteValues = byteValues,
                        Description = ""
                    };
                    
                    entry.InferDataType(); // Auto-detect type
                    AddEntry(entry);
                    
                    // Track known pointer bases
                    var baseName = $"0x{baseAddr:X8}";
                    if (!_knownPointers.ContainsKey(baseName))
                    {
                        _knownPointers[baseName] = baseAddr;
                    }
                }
            }
        }
        else
        {
            // Simple address
            if (TryParseHex(remainder, out uint address))
            {
                var entry = new OffsetEntry
                {
                    Name = name,
                    Category = category,
                    BaseAddress = address,
                    Offset = null,
                    ByteValues = byteValues,
                    Description = ""
                };
                
                entry.InferDataType(); // Auto-detect type
                AddEntry(entry);
                
                // Track special pointers like PlayerState and gEntity
                if (name.Equals("PlayerState", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("gEntity", StringComparison.OrdinalIgnoreCase))
                {
                    _knownPointers[name] = address;
                }
            }
        }
    }
    
    /// <summary>
    /// Adds an entry to the manager
    /// </summary>
    private void AddEntry(OffsetEntry entry)
    {
        if (!_offsetsByCategory.ContainsKey(entry.Category))
        {
            _offsetsByCategory[entry.Category] = new List<OffsetEntry>();
        }
        
        _offsetsByCategory[entry.Category].Add(entry);
        _offsetsByName[entry.Name] = entry;
    }
    
    /// <summary>
    /// Adds or replaces an entry (used when runtime-discovered offsets are pushed into the database).
    /// </summary>
    public void Upsert(OffsetEntry entry)
    {
        if (_offsetsByName.TryGetValue(entry.Name, out var existing) && _offsetsByCategory.TryGetValue(existing.Category, out var list))
            list.Remove(existing);
        if (entry.Type == "Int32" && string.IsNullOrEmpty(entry.ByteValues)) entry.InferDataType();
        AddEntry(entry);
    }

    /// <summary>
    /// Tries to parse a hex string to uint
    /// </summary>
    private bool TryParseHex(string hex, out uint result)
    {
        result = 0;
        hex = hex.Trim();
        
        // Handle 0x prefix
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex.Substring(2);
        }
        
        // Remove any spaces or commas
        hex = hex.Replace(" ", "").Replace(",", "");
        
        return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out result);
    }
    
    /// <summary>
    /// Gets an offset entry by name
    /// </summary>
    public OffsetEntry? GetOffset(string name)
    {
        return _offsetsByName.TryGetValue(name, out var entry) ? entry : null;
    }
    
    /// <summary>
    /// Gets all offsets in a category
    /// </summary>
    public List<OffsetEntry> GetCategory(string category)
    {
        return _offsetsByCategory.TryGetValue(category, out var list) ? list : new List<OffsetEntry>();
    }
    
    /// <summary>
    /// Searches for offsets matching a query
    /// </summary>
    public List<OffsetEntry> Search(string query)
    {
        var results = new List<OffsetEntry>();
        var lowerQuery = query.ToLower();
        
        foreach (var entry in _offsetsByName.Values)
        {
            if (entry.Name.ToLower().Contains(lowerQuery) ||
                entry.Category.ToLower().Contains(lowerQuery))
            {
                results.Add(entry);
            }
        }
        
        return results;
    }
    
    /// <summary>
    /// Gets all offset entries
    /// </summary>
    /// <summary>Number of entries without allocating a copy (hot path for status/UI refresh).</summary>
    public int Count => _offsetsByName.Count;

    public List<OffsetEntry> GetAllOffsets()
    {
        return _offsetsByName.Values.ToList();
    }
    
    /// <summary>
    /// Exports offsets to a script format
    /// </summary>
    public string ExportToScript(List<OffsetEntry> entries, Dictionary<uint, uint>? pointerValues = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// ═══════════════════════════════════════════════════════════");
        sb.AppendLine("// 🎮 Auto-Generated Offset Script");
        sb.AppendLine("// ═══════════════════════════════════════════════════════════");
        sb.AppendLine();
        
        foreach (var entry in entries)
        {
            sb.AppendLine($"// {entry.Name} ({entry.Category})");
            
            uint finalAddress;
            if (entry.IsPointerBased && pointerValues != null && pointerValues.TryGetValue(entry.BaseAddress, out uint pointerValue))
            {
                finalAddress = entry.CalculateFinalAddress(pointerValue);
                sb.AppendLine($"// Base: 0x{entry.BaseAddress:X8} + Offset: 0x{entry.Offset:X} = Final: 0x{finalAddress:X8}");
            }
            else
            {
                finalAddress = entry.BaseAddress;
                sb.AppendLine($"// Address: 0x{finalAddress:X8}");
            }
            
            if (!string.IsNullOrEmpty(entry.ByteValues))
            {
                sb.AppendLine($"PokeBytes(0x{finalAddress:X8}, \"{entry.ByteValues}\");");
            }
            else
            {
                sb.AppendLine($"// PokeInt32(0x{finalAddress:X8}, value);");
            }
            
            sb.AppendLine();
        }
        
        return sb.ToString();
    }
}
