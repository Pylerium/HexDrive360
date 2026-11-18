using System.Text;
using System.Text.Json;

namespace MemoryEditor;

/// <summary>
/// Advanced script management with templates, categories, and metadata
/// </summary>
public class ScriptManager
{
    public class ScriptMetadata
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Author { get; set; } = Environment.UserName;
        public DateTime Created { get; set; } = DateTime.Now;
        public DateTime Modified { get; set; } = DateTime.Now;
        public string Category { get; set; } = "Custom";
        public List<string> Tags { get; set; } = new();
        public string Version { get; set; } = "1.0";
        public int UsageCount { get; set; } = 0;
    }
    
    public class SavedScript
    {
        public ScriptMetadata Metadata { get; set; } = new();
        public string Content { get; set; } = "";
        public string Template { get; set; } = "Custom";
    }
    
    private readonly string _scriptsDirectory;
    private readonly Dictionary<string, SavedScript> _scriptCache = new();
    
    public ScriptManager()
    {
        _scriptsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts");
        Directory.CreateDirectory(_scriptsDirectory);
    }
    
    /// <summary>
    /// Gets all available script templates
    /// </summary>
    public Dictionary<string, string> GetTemplates()
    {
        return new Dictionary<string, string>
        {
            ["Basic Mod"] = @"// ═══════════════════════════════════════════════════════════
// Basic Mod Template
// Description: Simple value modifications
// ═══════════════════════════════════════════════════════════

// Example: Max Health
PokeInt32(0x82551BB4, 9999);

// Example: Max Ammo
PokeInt32(0x82551C36, 999);

// Add more modifications below
",

            ["Protection Bypass"] = @"// ═══════════════════════════════════════════════════════════
// Protection Bypass Template
// Description: Disable anti-cheat and protections
// ═══════════════════════════════════════════════════════════

// Step 1: Disable VAC
PokeBytes(0x82000000, ""60 00 00 00"");

// Step 2: Bypass Memory Checks
PokeBytes(0x82000100, ""4E 80 00 20"");

// Step 3: Apply your mods safely
wait 1000

// Add your mods here
",

            ["Stat Boost"] = @"// ═══════════════════════════════════════════════════════════
// Stat Boost Template
// Description: Level, XP, and prestige modifications
// ═══════════════════════════════════════════════════════════

// XP Boost
PokeInt32(0x82000A00, 999999);

// Level Up
PokeInt32(0x82000A10, 55);

// Prestige
PokeInt32(0x82000A20, 11);

// Wait for stats to sync
wait 2000
",

            ["Zombie Survival"] = @"// ═══════════════════════════════════════════════════════════
// Zombie Survival Template
// Description: Complete zombie mode setup
// ═══════════════════════════════════════════════════════════

// Godmode
PokeInt32(0x82551BB4, 99999);

// Unlimited Ammo
PokeInt32(0x82551C36, 9999);

// Max Points
PokeInt32(0x82551D00, 999999);

// Unlimited Sprint
PokeBytes(0x82551D20, ""3F 80 00 00"");

// Fast Revive
PokeFloat(0x82551D40, 0.1);
",

            ["Multiplayer Rage"] = @"// ═══════════════════════════════════════════════════════════
// Multiplayer Rage Template  
// Description: Overpowered MP setup (use responsibly!)
// ═══════════════════════════════════════════════════════════

// God Mode
PokeInt32(0x82000000, 99999);

// Super Speed
PokeFloat(0x82000100, 3.0);

// Super Jump
PokeFloat(0x82000200, 2.5);

// Aimbot FOV
PokeFloat(0x82000300, 180.0);

// Wallhack
PokeBytes(0x82000400, ""60 00 00 00"");
",

            ["Advanced Loop"] = @"// ═══════════════════════════════════════════════════════════
// Advanced Loop Template
// Description: Continuous monitoring and modification
// ═══════════════════════════════════════════════════════════

loop
{
    // Keep health maxed
    PokeInt32(0x82551BB4, 9999);
    
    // Keep ammo maxed
    PokeInt32(0x82551C36, 999);
    
    // Wait before next iteration
    wait 100
    
    // Break condition (press a button, etc.)
    if (condition)
        break;
}
",

            ["Conditional Mod"] = @"// ═══════════════════════════════════════════════════════════
// Conditional Mod Template
// Description: Smart conditional modifications
// ═══════════════════════════════════════════════════════════

// Read current health
var health = ReadInt32(0x82551BB4);

// Only mod if health is low
if (health < 50)
{
    log ""Health low, restoring..."";
    PokeInt32(0x82551BB4, 9999);
}

// Read ammo
var ammo = ReadInt32(0x82551C36);

// Refill if below threshold
if (ammo < 30)
{
    log ""Ammo low, refilling..."";
    PokeInt32(0x82551C36, 999);
}
"
        };
    }
    
    /// <summary>
    /// Saves a script with full metadata
    /// </summary>
    public async Task<bool> SaveScriptAsync(SavedScript script)
    {
        try
        {
            var fileName = SanitizeFileName(script.Metadata.Name) + ".json";
            var filePath = Path.Combine(_scriptsDirectory, fileName);
            
            script.Metadata.Modified = DateTime.Now;
            
            var json = JsonSerializer.Serialize(script, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            
            await File.WriteAllTextAsync(filePath, json);
            _scriptCache[script.Metadata.Name] = script;
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving script: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Loads all saved scripts
    /// </summary>
    public async Task<List<SavedScript>> LoadAllScriptsAsync()
    {
        var scripts = new List<SavedScript>();
        
        try
        {
            var files = Directory.GetFiles(_scriptsDirectory, "*.json");
            
            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var script = JsonSerializer.Deserialize<SavedScript>(json);
                    
                    if (script != null)
                    {
                        scripts.Add(script);
                        _scriptCache[script.Metadata.Name] = script;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading script {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading scripts directory: {ex.Message}");
        }
        
        return scripts;
    }
    
    /// <summary>
    /// Gets scripts by category
    /// </summary>
    public List<SavedScript> GetScriptsByCategory(string category)
    {
        return _scriptCache.Values
            .Where(s => s.Metadata.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Metadata.Name)
            .ToList();
    }
    
    /// <summary>
    /// Searches scripts by name, description, or tags
    /// </summary>
    public List<SavedScript> SearchScripts(string query)
    {
        var lowerQuery = query.ToLower();
        
        return _scriptCache.Values
            .Where(s =>
                s.Metadata.Name.ToLower().Contains(lowerQuery) ||
                s.Metadata.Description.ToLower().Contains(lowerQuery) ||
                s.Metadata.Tags.Any(t => t.ToLower().Contains(lowerQuery)))
            .OrderBy(s => s.Metadata.Name)
            .ToList();
    }
    
    /// <summary>
    /// Deletes a script
    /// </summary>
    public bool DeleteScript(string scriptName)
    {
        try
        {
            var fileName = SanitizeFileName(scriptName) + ".json";
            var filePath = Path.Combine(_scriptsDirectory, fileName);
            
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _scriptCache.Remove(scriptName);
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error deleting script: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Exports a script to a file
    /// </summary>
    public async Task<bool> ExportScriptAsync(SavedScript script, string exportPath)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// ═══════════════════════════════════════════════════════════");
            sb.AppendLine($"// Script: {script.Metadata.Name}");
            sb.AppendLine($"// Description: {script.Metadata.Description}");
            sb.AppendLine($"// Author: {script.Metadata.Author}");
            sb.AppendLine($"// Version: {script.Metadata.Version}");
            sb.AppendLine($"// Created: {script.Metadata.Created:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"// Category: {script.Metadata.Category}");
            if (script.Metadata.Tags.Any())
            {
                sb.AppendLine($"// Tags: {string.Join(", ", script.Metadata.Tags)}");
            }
            sb.AppendLine($"// ═══════════════════════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine(script.Content);
            
            await File.WriteAllTextAsync(exportPath, sb.ToString());
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error exporting script: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Increments usage count for a script
    /// </summary>
    public async Task IncrementUsageAsync(string scriptName)
    {
        if (_scriptCache.TryGetValue(scriptName, out var script))
        {
            script.Metadata.UsageCount++;
            await SaveScriptAsync(script);
        }
    }
    
    /// <summary>
    /// Gets most used scripts
    /// </summary>
    public List<SavedScript> GetMostUsedScripts(int count = 10)
    {
        return _scriptCache.Values
            .OrderByDescending(s => s.Metadata.UsageCount)
            .Take(count)
            .ToList();
    }
    
    /// <summary>
    /// Gets recently modified scripts
    /// </summary>
    public List<SavedScript> GetRecentScripts(int count = 10)
    {
        return _scriptCache.Values
            .OrderByDescending(s => s.Metadata.Modified)
            .Take(count)
            .ToList();
    }
    
    /// <summary>
    /// Gets all unique categories
    /// </summary>
    public List<string> GetCategories()
    {
        return _scriptCache.Values
            .Select(s => s.Metadata.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToList();
    }
    
    /// <summary>
    /// Imports a script from file
    /// </summary>
    public async Task<SavedScript?> ImportScriptAsync(string filePath)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath);
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            
            // Try to parse metadata from comments
            var metadata = ParseMetadataFromContent(content, fileName);
            
            return new SavedScript
            {
                Metadata = metadata,
                Content = content,
                Template = "Imported"
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error importing script: {ex.Message}");
            return null;
        }
    }
    
    private ScriptMetadata ParseMetadataFromContent(string content, string defaultName)
    {
        var metadata = new ScriptMetadata { Name = defaultName };
        var lines = content.Split('\n');
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("// Script:"))
                metadata.Name = trimmed.Replace("// Script:", "").Trim();
            else if (trimmed.StartsWith("// Description:"))
                metadata.Description = trimmed.Replace("// Description:", "").Trim();
            else if (trimmed.StartsWith("// Author:"))
                metadata.Author = trimmed.Replace("// Author:", "").Trim();
            else if (trimmed.StartsWith("// Category:"))
                metadata.Category = trimmed.Replace("// Category:", "").Trim();
            else if (trimmed.StartsWith("// Version:"))
                metadata.Version = trimmed.Replace("// Version:", "").Trim();
            else if (trimmed.StartsWith("// Tags:"))
            {
                var tagsStr = trimmed.Replace("// Tags:", "").Trim();
                metadata.Tags = tagsStr.Split(',').Select(t => t.Trim()).ToList();
            }
        }
        
        return metadata;
    }
    
    private string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
