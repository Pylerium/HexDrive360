namespace MemoryEditor.Scripting;

public static class ScriptTemplates
{
    public const string Welcome = @"// ═══════════════════════════════════════════════════════════
//  XScript – Xbox 360 Memory Editor scripting
// ═══════════════════════════════════════════════════════════
//  F5 run · Shift+F5 stop · Ctrl+Space complete · Ctrl+K command palette
//  Hover a function for docs, open the Reference panel for everything.
//
//  Memory   ReadInt32 / ReadFloat / ReadString / Peek ...
//           PokeInt32 / PokeFloat / PokeBytes / Poke / Nop / Blr ...
//  Pointers walk(base, off1, off2) · addoff(base, off)
//  Offsets  offset(""Name"") or just the name: PlayerState + 0x10
//  Flow     if / else · while · for(i=0;i<n;i++) · for i in 0..10
//           loop { } · repeat 5 { } · func name(a, b) { return a }
//  Background  freeze(addr, value) · hook(name, addr, len, ms, fn)
//  Strings  ""Health is $health"" and ""${expr}"" interpolation
// ═══════════════════════════════════════════════════════════

var maxHealth = 999999
var base = 0x82000000

func refill(addr, amount) {
    if ReadInt32(addr) < amount {
        PokeInt32(addr, amount)
        log ""Refilled ${hex(addr)} to $amount""
    }
}

refill(base, maxHealth)
log ""Ready!""
";

    public static readonly Dictionary<string, string> All = new()
    {
        ["Basic Mod"] = @"// Basic Mod – simple value modifications
PokeInt32(0x82551BB4, 9999)   // Max Health
PokeInt32(0x82551C36, 999)    // Max Ammo
log ""Basic mods applied""
",
        ["God Mode"] = @"// God Mode
PokeInt32(0x82000000, 999999)  // Max Health
PokeInt32(0x82000004, 999999)  // Max Shield
PokeInt32(0x82000008, 1)       // Invincibility flag
log ""God mode activated!""
",
        ["Infinite Ammo (freeze)"] = @"// Infinite Ammo – keeps values frozen in the background until Stop
freeze(0x82001000, 9999)          // Current clip
freeze(0x82001004, 9999)          // Reserve ammo
notify(""Infinite ammo enabled – press Stop to disable"")
",
        ["Speed Hack"] = @"// Speed Hack
var speedMultiplier = 2.5
var sprintMultiplier = 1.5
PokeFloat(0x82002000, speedMultiplier)
PokeFloat(0x82002004, sprintMultiplier)
PokeInt32(0x82002008, 1)
log ""Speed hack: ${speedMultiplier}x""
",
        ["Money Mod (loop)"] = @"// Money Mod – loop over consecutive currency slots
var maxMoney = 999999
for (i = 0; i < 3; i++) {
    PokeInt32(0x82003000 + i * 4, maxMoney)
    log ""Currency type $i set to $maxMoney""
}
",
        ["XP Mod"] = @"// XP / Level Mod
var targetLevel = 100
var targetPrestige = 10
PokeInt32(0x82004000, 999999)
PokeInt32(0x82004004, targetLevel)
PokeInt32(0x82004008, targetPrestige)
log ""Level $targetLevel, prestige $targetPrestige""
",
        ["No Recoil"] = @"// No Recoil
PokeFloat(0x82005000, 0.0)   // Recoil X
PokeFloat(0x82005004, 0.0)   // Recoil Y
Nop(0x82005008)               // remove the recoil call
log ""Recoil disabled!""
",
        ["Rapid Fire"] = @"// Rapid Fire
var fireRate = 0.01
if fireRate < 0.1 {
    PokeFloat(0x82006000, fireRate)
    PokeInt32(0x82006004, 1)
    log ""Rapid fire at $fireRate s""
}
",
        ["Unlock All"] = @"// Unlock All
for i in 0..4 {
    PokeInt32(0x82007000 + i * 4, 1)
    wait 50
}
log ""Everything unlocked""
",
        ["Protection Bypass"] = @"// Protection Bypass – patch checks before applying mods
Nop(0x82000000)        // disable check
Blr(0x82000100)        // make the integrity function return immediately
wait 500
log ""Protections patched""
",
        ["Continuous Loop"] = @"// Continuous monitoring – runs until you press Stop
loop {
    if ReadInt32(0x82551BB4) < 100 {
        PokeInt32(0x82551BB4, 9999)
        log ""Health restored at ${now()}""
    }
    wait 100
}
",
        ["Conditional Mod"] = @"// Conditional Mod – read, decide, write
var health = ReadInt32(0x82551BB4)
if health < 50 {
    log ""Health low ($health), restoring…""
    PokeInt32(0x82551BB4, 9999)
} else {
    log ""Health OK ($health)""
}
",
        ["Pointer Chain"] = @"// Pointer chain – follow [[base + 0x10] + 0x24] and write the final field
var player = walk(0x82500000, 0x10, 0x24)
if IsValidPtr(player) {
    PokeInt32(player + 0x1C, 100)
    log ""Player struct at ${hex(player)}""
} else {
    error(""player pointer not valid yet – are you in game?"")
}
",
        ["Hook on change"] = @"// Hook – run a function whenever memory changes
func onChange(bytes) {
    log ""Value changed: $bytes""
}
hook(watchHealth, 0x82551BB4, 4, 100, onChange)
log ""Watching… press Stop to end""
",
        ["Using offsets by name"] = @"// Offsets from the database (Offset Calculator) or Runtime Offsets
// can be used directly by name – spaces become underscores.
if offset_exists(""SV_SendServerCommand"") {
    log ""SV_SendServerCommand is at ${hex(offset('SV_SendServerCommand'))}""
}
// Peek(Cheat_Protection_Calls, 4)
",
    };

    public static readonly (string Name, string Code)[] Snippets =
    {
        ("if", "if condition {\n    \n}\n"),
        ("if / else", "if condition {\n    \n} else {\n    \n}\n"),
        ("for (C style)", "for (i = 0; i < 10; i++) {\n    \n}\n"),
        ("for range", "for i in 0..10 {\n    \n}\n"),
        ("while", "while condition {\n    \n}\n"),
        ("loop until stop", "loop {\n    \n    wait 100\n}\n"),
        ("function", "func name(arg) {\n    return arg\n}\n"),
        ("walk pointer", "var p = walk(0x82000000, 0x10, 0x20)\n"),
        ("freeze value", "freeze(0x82000000, 999)\n"),
        ("hook", "hook(myHook, 0x82000000, 4, 100, \"log \\\"changed\\\"\")\n"),
        ("peek", "Peek(0x82000000, 16)\n"),
        ("poke bytes", "PokeBytes(0x82000000, \"60 00 00 00\")\n"),
    };
}
