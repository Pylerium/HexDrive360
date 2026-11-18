namespace MemoryEditor.Runtime;

/// <summary>Compact PowerPC disassembler covering the instructions that matter when reading Xenon game code.</summary>
public static class Ppc
{
    private static string R(uint n) => n == 1 ? "r1" : $"r{n}";
    private static string F(uint n) => $"f{n}";
    private static string H(int v) => v < 0 ? $"-0x{-v:X}" : $"0x{v:X}";

    public static string Disassemble(uint w, uint pc)
    {
        uint op = w >> 26;
        uint rd = (w >> 21) & 31, ra = (w >> 16) & 31, rb = (w >> 11) & 31;
        uint uimm = w & 0xFFFF;
        int simm = (short)uimm;

        switch (op)
        {
            case 14: return ra == 0 ? $"li {R(rd)}, {H(simm)}" : $"addi {R(rd)}, {R(ra)}, {H(simm)}";
            case 15: return ra == 0 ? $"lis {R(rd)}, 0x{uimm:X}" : $"addis {R(rd)}, {R(ra)}, 0x{uimm:X}";
            case 12: return $"addic {R(rd)}, {R(ra)}, {H(simm)}";
            case 13: return $"addic. {R(rd)}, {R(ra)}, {H(simm)}";
            case 7: return $"mulli {R(rd)}, {R(ra)}, {H(simm)}";
            case 8: return $"subfic {R(rd)}, {R(ra)}, {H(simm)}";
            case 10: return $"cmpl{((rd & 1) != 0 ? "d" : "w")}i cr{rd >> 2}, {R(ra)}, 0x{uimm:X}";
            case 11: return $"cmp{((rd & 1) != 0 ? "d" : "w")}i cr{rd >> 2}, {R(ra)}, {H(simm)}";
            case 24: return w == 0x60000000 ? "nop" : $"ori {R(ra)}, {R(rd)}, 0x{uimm:X}";
            case 25: return $"oris {R(ra)}, {R(rd)}, 0x{uimm:X}";
            case 26: return $"xori {R(ra)}, {R(rd)}, 0x{uimm:X}";
            case 27: return $"xoris {R(ra)}, {R(rd)}, 0x{uimm:X}";
            case 28: return $"andi. {R(ra)}, {R(rd)}, 0x{uimm:X}";
            case 29: return $"andis. {R(ra)}, {R(rd)}, 0x{uimm:X}";
            case 32: return $"lwz {R(rd)}, {H(simm)}({R(ra)})";
            case 33: return $"lwzu {R(rd)}, {H(simm)}({R(ra)})";
            case 34: return $"lbz {R(rd)}, {H(simm)}({R(ra)})";
            case 35: return $"lbzu {R(rd)}, {H(simm)}({R(ra)})";
            case 36: return $"stw {R(rd)}, {H(simm)}({R(ra)})";
            case 37: return $"stwu {R(rd)}, {H(simm)}({R(ra)})";
            case 38: return $"stb {R(rd)}, {H(simm)}({R(ra)})";
            case 39: return $"stbu {R(rd)}, {H(simm)}({R(ra)})";
            case 40: return $"lhz {R(rd)}, {H(simm)}({R(ra)})";
            case 42: return $"lha {R(rd)}, {H(simm)}({R(ra)})";
            case 44: return $"sth {R(rd)}, {H(simm)}({R(ra)})";
            case 46: return $"lmw {R(rd)}, {H(simm)}({R(ra)})";
            case 47: return $"stmw {R(rd)}, {H(simm)}({R(ra)})";
            case 48: return $"lfs {F(rd)}, {H(simm)}({R(ra)})";
            case 50: return $"lfd {F(rd)}, {H(simm)}({R(ra)})";
            case 52: return $"stfs {F(rd)}, {H(simm)}({R(ra)})";
            case 54: return $"stfd {F(rd)}, {H(simm)}({R(ra)})";
            case 58:
            {
                int ds = (short)(uimm & ~3u);
                return (w & 3) switch { 0 => $"ld {R(rd)}, {H(ds)}({R(ra)})", 1 => $"ldu {R(rd)}, {H(ds)}({R(ra)})", _ => $"lwa {R(rd)}, {H(ds)}({R(ra)})" };
            }
            case 62:
            {
                int ds = (short)(uimm & ~3u);
                return (w & 3) == 1 ? $"stdu {R(rd)}, {H(ds)}({R(ra)})" : $"std {R(rd)}, {H(ds)}({R(ra)})";
            }
            case 18:
            {
                uint target = PpcAnalyzer.BranchTarget(pc, w);
                return $"b{((w & 1) != 0 ? "l" : "")}{((w & 2) != 0 ? "a" : "")} 0x{target:X8}";
            }
            case 16:
            {
                int bd = (short)(w & 0xFFFC);
                uint target = (w & 2) != 0 ? (uint)bd : unchecked(pc + (uint)bd);
                return $"{BcName(rd, ra)}{((w & 1) != 0 ? "l" : "")} cr{ra >> 2}, 0x{target:X8}";
            }
            case 19:
            {
                uint xo = (w >> 1) & 0x3FF;
                bool lk = (w & 1) != 0;
                if (xo == 16) return (rd & 0x14) == 0x14 ? (lk ? "blrl" : "blr") : $"{BcName(rd, ra)}lr{(lk ? "l" : "")}";
                if (xo == 528) return (rd & 0x14) == 0x14 ? (lk ? "bctrl" : "bctr") : $"{BcName(rd, ra)}ctr{(lk ? "l" : "")}";
                if (xo == 150) return "isync";
                return $".long 0x{w:X8}";
            }
            case 21:
            {
                uint sh = rb, mb = (w >> 6) & 31, me = (w >> 1) & 31;
                if (mb == 0 && me == 31 - sh) return $"slwi {R(ra)}, {R(rd)}, {sh}";
                if (me == 31 && sh == 32 - mb && sh != 0) return $"srwi {R(ra)}, {R(rd)}, {mb}";
                if (sh == 0 && me == 31) return $"clrlwi {R(ra)}, {R(rd)}, {mb}";
                return $"rlwinm {R(ra)}, {R(rd)}, {sh}, {mb}, {me}";
            }
            case 20: return $"rlwimi {R(ra)}, {R(rd)}, {rb}, {(w >> 6) & 31}, {(w >> 1) & 31}";
            case 31:
            {
                uint xo = (w >> 1) & 0x3FF;
                return xo switch
                {
                    444 when rd == rb => $"mr {R(ra)}, {R(rd)}",
                    444 => $"or {R(ra)}, {R(rd)}, {R(rb)}",
                    266 => $"add {R(rd)}, {R(ra)}, {R(rb)}",
                    40 => $"subf {R(rd)}, {R(ra)}, {R(rb)}",
                    235 => $"mullw {R(rd)}, {R(ra)}, {R(rb)}",
                    491 => $"divw {R(rd)}, {R(ra)}, {R(rb)}",
                    459 => $"divwu {R(rd)}, {R(ra)}, {R(rb)}",
                    104 => $"neg {R(rd)}, {R(ra)}",
                    28 => $"and {R(ra)}, {R(rd)}, {R(rb)}",
                    316 => $"xor {R(ra)}, {R(rd)}, {R(rb)}",
                    24 => $"slw {R(ra)}, {R(rd)}, {R(rb)}",
                    536 => $"srw {R(ra)}, {R(rd)}, {R(rb)}",
                    824 => $"srawi {R(ra)}, {R(rd)}, {rb}",
                    954 => $"extsb {R(ra)}, {R(rd)}",
                    922 => $"extsh {R(ra)}, {R(rd)}",
                    986 => $"extsw {R(ra)}, {R(rd)}",
                    26 => $"cntlzw {R(ra)}, {R(rd)}",
                    0 => $"cmpw cr{rd >> 2}, {R(ra)}, {R(rb)}",
                    32 => $"cmplw cr{rd >> 2}, {R(ra)}, {R(rb)}",
                    23 => $"lwzx {R(rd)}, {R(ra)}, {R(rb)}",
                    87 => $"lbzx {R(rd)}, {R(ra)}, {R(rb)}",
                    151 => $"stwx {R(rd)}, {R(ra)}, {R(rb)}",
                    215 => $"stbx {R(rd)}, {R(ra)}, {R(rb)}",
                    339 => ((rb << 5) | ra) switch { 8 => $"mflr {R(rd)}", 9 => $"mfctr {R(rd)}", 1 => $"mfxer {R(rd)}", var s => $"mfspr {R(rd)}, {s}" },
                    467 => ((rb << 5) | ra) switch { 8 => $"mtlr {R(rd)}", 9 => $"mtctr {R(rd)}", 1 => $"mtxer {R(rd)}", var s => $"mtspr {s}, {R(rd)}" },
                    19 => $"mfcr {R(rd)}",
                    598 => "sync",
                    854 => "eieio",
                    1014 => $"dcbz {R(ra)}, {R(rb)}",
                    278 => $"dcbt {R(ra)}, {R(rb)}",
                    _ => $".long 0x{w:X8}   (op31 xo {xo})"
                };
            }
            case 0 when w == 0: return ".long 0";
        }
        return $".long 0x{w:X8}";
    }

    private static string BcName(uint bo, uint bi)
    {
        uint cond = bi & 3;
        bool t = (bo & 8) != 0;
        if ((bo & 0x14) == 0x14) return "b";
        if ((bo & 4) == 0) return (bo & 2) != 0 ? "bdz" : "bdnz";
        return cond switch
        {
            0 => t ? "blt" : "bge",
            1 => t ? "bgt" : "ble",
            2 => t ? "beq" : "bne",
            _ => t ? "bso" : "bns"
        };
    }
}
