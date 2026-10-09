using System.Text.RegularExpressions;
using Trw.Platform;

namespace Trw.Game.Debug;

/// <summary>
/// A small MIPS III (o32) interpreter over the ROM's own machine code, used as the ground truth for differential tests of the translated
/// code (<see cref="DiffTest"/>): it runs a function from the game's current memory state and the result is compared with what the
/// translated C# does from the same state. Instruction words come from the ROM listing (port/build/asm_func/*.s of the C port).
/// Only what the game's code uses is implemented; anything else aborts the run (the test is then inconclusive, never a false alarm).
/// The FPU registers are the shared <see cref="Cpu.Fpr"/> (FR=1), so a run leaves them where the translated code would.
/// </summary>
public sealed class MipsInterp
{
    public const uint Sentinel = 0xFFFFFFF0;           // return address of the entry function

    /// <summary>code address (low form, 0x00xxxxxx) -> instruction word</summary>
    public static readonly Dictionary<uint, uint> Code = new();
    public static readonly Dictionary<string, uint> FuncAddr = new();
    public static readonly Dictionary<uint, string> FuncName = new();

    /// <summary>Calls that leave the interpreter: hand-written OS/library code reached through the register ABI (<see cref="Funcs"/>).</summary>
    public static readonly HashSet<uint> Externals = new();

    public readonly ulong[] R = new ulong[32];                    // 64-bit registers; 32-bit operations leave sign-extended results
    public ulong Hi, Lo;
    public bool Fcc;
    public string? Abort;
    public int Steps;
    public readonly List<string> ExternalCalls = new();

    public static bool Loaded => Code.Count > 0;

    private static uint Norm(uint a) => a & 0x1FFFFFFFu;

    // ------------------------------------------------------------------------------------------------------------ listing

    private static readonly Regex Ins = new(@"/\*\s*[0-9A-Fa-f]+\s+([0-9A-Fa-f]{8})\s+([0-9A-Fa-f]{8})\s*\*/", RegexOptions.Compiled);

    /// <summary>Reads every function listing of the C port (<c>port/build/asm_func</c>) into <see cref="Code"/> / <see cref="FuncAddr"/>.</summary>
    public static void LoadListing(string cRoot, IEnumerable<string> externalNames)
    {
        var dir = Path.Combine(cRoot, "port", "build", "asm_func");
        foreach (var file in Directory.EnumerateFiles(dir, "*.s"))
        {
            string? current = null;
            foreach (var line in File.ReadLines(file))
            {
                if (line.StartsWith("glabel "))
                {
                    current = line[7..].Trim();
                    if (current.Length == 11 && current.StartsWith("func_")) current = "func_00" + current[5..];
                    continue;
                }
                var m = Ins.Match(line);
                if (!m.Success) continue;
                uint addr = Norm(Convert.ToUInt32(m.Groups[1].Value, 16)), word = Convert.ToUInt32(m.Groups[2].Value, 16);
                Code[addr] = word;
                if (current != null) { if (!FuncAddr.ContainsKey(current)) { FuncAddr[current] = addr; FuncName[addr] = current; } current = null; }
            }
        }
        foreach (var n in externalNames)
            if (FuncAddr.TryGetValue(n, out var a)) Externals.Add(a);
    }

    // ------------------------------------------------------------------------------------------------------------ execution

    private static int S16(uint w) => (short)(w & 0xFFFF);
    public static ulong Sx(uint v) => (ulong)(long)(int)v;           // sign-extend a 32-bit result into a register
    private uint L(uint r) => (uint)R[r];                              // low word of a register

    /// <summary>Runs from <paramref name="entry"/> until it returns to <see cref="Sentinel"/>. Returns false (with <see cref="Abort"/>) when it cannot continue.</summary>
    public bool Run(uint entry, int maxSteps = 3_000_000)
    {
        uint pc = Norm(entry);
        R[31] = Sentinel;
        while (Steps < maxSteps)
        {
            if (!Code.TryGetValue(pc, out var w)) { Abort = $"no code at {pc:X8}"; return false; }
            Steps++;
            uint op = w >> 26, rs = (w >> 21) & 31, rt = (w >> 16) & 31, rd = (w >> 11) & 31, sa = (w >> 6) & 31, fn = w & 63;
            int imm = S16(w);
            uint target = 0; bool isBranch = false, taken = false, likely = false, link = false, isJump = false;

            switch (op)
            {
                case 0:
                    switch (fn)
                    {
                        case 8: isBranch = true; taken = true; isJump = true; target = L(rs); break;               // jr
                        case 9: isBranch = true; taken = true; isJump = true; link = true; target = L(rs); if (rd != 31) { Abort = "jalr with a link register other than ra"; return false; } break;
                        default: if (!ExecSpecial(w, rs, rt, rd, sa, fn)) return false; pc += 4; continue;
                    }
                    break;
                case 1:
                    isBranch = true; target = pc + 4 + (uint)(imm << 2);
                    switch (rt)
                    {
                        case 0: taken = (long)R[rs] < 0; break;            // bltz
                        case 1: taken = (long)R[rs] >= 0; break;           // bgez
                        case 2: taken = (long)R[rs] < 0; likely = true; break;
                        case 3: taken = (long)R[rs] >= 0; likely = true; break;
                        case 0x10: taken = (long)R[rs] < 0; link = true; break;
                        case 0x11: taken = (long)R[rs] >= 0; link = true; break;
                        default: Abort = $"regimm {rt:X}"; return false;
                    }
                    break;
                case 2: isBranch = true; taken = true; isJump = true; target = (pc & 0xF0000000) | ((w & 0x3FFFFFF) << 2); break;
                case 3: isBranch = true; taken = true; isJump = true; link = true; target = (pc & 0xF0000000) | ((w & 0x3FFFFFF) << 2); break;
                case 4: isBranch = true; target = pc + 4 + (uint)(imm << 2); taken = R[rs] == R[rt]; break;
                case 5: isBranch = true; target = pc + 4 + (uint)(imm << 2); taken = R[rs] != R[rt]; break;
                case 6: isBranch = true; target = pc + 4 + (uint)(imm << 2); taken = (long)R[rs] <= 0; break;
                case 7: isBranch = true; target = pc + 4 + (uint)(imm << 2); taken = (long)R[rs] > 0; break;
                case 0x14: isBranch = true; likely = true; target = pc + 4 + (uint)(imm << 2); taken = R[rs] == R[rt]; break;
                case 0x15: isBranch = true; likely = true; target = pc + 4 + (uint)(imm << 2); taken = R[rs] != R[rt]; break;
                case 0x16: isBranch = true; likely = true; target = pc + 4 + (uint)(imm << 2); taken = (long)R[rs] <= 0; break;
                case 0x17: isBranch = true; likely = true; target = pc + 4 + (uint)(imm << 2); taken = (long)R[rs] > 0; break;
                case 0x11 when rs == 8:        // bc1f / bc1t / bc1fl / bc1tl
                    isBranch = true; target = pc + 4 + (uint)(imm << 2);
                    switch (rt & 3)
                    {
                        case 0: taken = !Fcc; break;
                        case 1: taken = Fcc; break;
                        case 2: taken = !Fcc; likely = true; break;
                        default: taken = Fcc; likely = true; break;
                    }
                    break;
                default:
                    if (!ExecOther(w, op, rs, rt, imm)) return false;
                    pc += 4; continue;
            }

            // a branch: the delay slot runs first (unless it is a not-taken branch-likely)
            uint ra = pc + 8;
            if (link) { R[31] = Sx(ra); }
            if (!(likely && !taken))
            {
                if (!Code.TryGetValue(pc + 4, out var dw)) { Abort = $"no code at delay slot {pc + 4:X8}"; return false; }
                Steps++;
                uint dop = dw >> 26, drs = (dw >> 21) & 31, drt = (dw >> 16) & 31, drd = (dw >> 11) & 31, dsa = (dw >> 6) & 31, dfn = dw & 63;
                bool dslotBranch = dop is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 0x14 or 0x15 or 0x16 or 0x17 || (dop == 0 && dfn is 8 or 9) || (dop == 0x11 && drs == 8);
                if (dslotBranch) { Abort = $"branch in a delay slot at {pc + 4:X8}"; return false; }
                if (dop == 0) { if (!ExecSpecial(dw, drs, drt, drd, dsa, dfn)) return false; }
                else if (!ExecOther(dw, dop, drs, drt, S16(dw))) return false;
            }
            if (!taken) { pc = ra; continue; }

            if (link)             // jal / jalr / bal: either into code we interpret, or out to the hand-written layer
            {
                uint t = Norm(target);
                if (Externals.Contains(t) || !Code.ContainsKey(t))
                {
                    if (!CallExternal(t)) return false;
                    pc = ra;
                    continue;
                }
            }
            else if (isJump && op == 0 && fn == 8 && target == Sentinel) return true;       // jr ra at the entry level
            pc = Norm(target);
        }
        Abort = "step limit";
        return false;
    }

    // ------------------------------------------------------------------------------------------------------------ integer

    private bool ExecSpecial(uint w, uint rs, uint rt, uint rd, uint sa, uint fn)
    {
        switch (fn)
        {
            case 0: R[rd] = Sx(L(rt) << (int)sa); break;                 // sll (nop when all zero)
            case 2: R[rd] = Sx(L(rt) >> (int)sa); break;
            case 3: R[rd] = Sx((uint)((int)L(rt) >> (int)sa)); break;
            case 4: R[rd] = Sx(L(rt) << (int)(L(rs) & 31)); break;
            case 6: R[rd] = Sx(L(rt) >> (int)(L(rs) & 31)); break;
            case 7: R[rd] = Sx((uint)((int)L(rt) >> (int)(L(rs) & 31))); break;
            case 0x14: R[rd] = R[rt] << (int)(R[rs] & 63); break;                                   // dsllv
            case 0x16: R[rd] = R[rt] >> (int)(R[rs] & 63); break;                                   // dsrlv
            case 0x17: R[rd] = (ulong)((long)R[rt] >> (int)(R[rs] & 63)); break;                    // dsrav
            case 0x10: R[rd] = Hi; break;
            case 0x11: Hi = R[rs]; break;
            case 0x12: R[rd] = Lo; break;
            case 0x13: Lo = R[rs]; break;
            case 0x18: { long p = (long)(int)L(rs) * (int)L(rt); Lo = Sx((uint)p); Hi = Sx((uint)(p >> 32)); break; }
            case 0x19: { ulong p = (ulong)L(rs) * L(rt); Lo = Sx((uint)p); Hi = Sx((uint)(p >> 32)); break; }
            case 0x1A: if (L(rt) == 0) { Abort = "div by zero"; return false; } Lo = Sx((uint)Cpu.SDiv((int)L(rs), (int)L(rt))); Hi = Sx((uint)Cpu.SRem((int)L(rs), (int)L(rt))); break;
            case 0x1B: if (L(rt) == 0) { Abort = "divu by zero"; return false; } Lo = Sx(L(rs) / L(rt)); Hi = Sx(L(rs) % L(rt)); break;
            case 0x1C: { var p = (Int128)(long)R[rs] * (long)R[rt]; Lo = (ulong)p; Hi = (ulong)(p >> 64); break; }         // dmult
            case 0x1D: { var p = (UInt128)R[rs] * R[rt]; Lo = (ulong)p; Hi = (ulong)(p >> 64); break; }                      // dmultu
            case 0x1E: if (R[rt] == 0) { Abort = "ddiv by zero"; return false; } if ((long)R[rt] == -1) { Lo = (ulong)(-(long)R[rs]); Hi = 0; } else { Lo = (ulong)((long)R[rs] / (long)R[rt]); Hi = (ulong)((long)R[rs] % (long)R[rt]); } break;
            case 0x1F: if (R[rt] == 0) { Abort = "ddivu by zero"; return false; } Lo = R[rs] / R[rt]; Hi = R[rs] % R[rt]; break;
            case 0x20: case 0x21: R[rd] = Sx(L(rs) + L(rt)); break;
            case 0x22: case 0x23: R[rd] = Sx(L(rs) - L(rt)); break;
            case 0x24: R[rd] = R[rs] & R[rt]; break;
            case 0x25: R[rd] = R[rs] | R[rt]; break;
            case 0x26: R[rd] = R[rs] ^ R[rt]; break;
            case 0x27: R[rd] = ~(R[rs] | R[rt]); break;
            case 0x2A: R[rd] = (long)R[rs] < (long)R[rt] ? 1u : 0; break;
            case 0x2B: R[rd] = R[rs] < R[rt] ? 1u : 0; break;
            case 0x2C: case 0x2D: R[rd] = R[rs] + R[rt]; break;                                      // dadd / daddu
            case 0x2E: case 0x2F: R[rd] = R[rs] - R[rt]; break;                                      // dsub / dsubu
            case 0x38: R[rd] = R[rt] << (int)sa; break;                                              // dsll
            case 0x3A: R[rd] = R[rt] >> (int)sa; break;                                              // dsrl
            case 0x3B: R[rd] = (ulong)((long)R[rt] >> (int)sa); break;                               // dsra
            case 0x3C: R[rd] = R[rt] << (int)(sa + 32); break;                                       // dsll32
            case 0x3E: R[rd] = R[rt] >> (int)(sa + 32); break;                                       // dsrl32
            case 0x3F: R[rd] = (ulong)((long)R[rt] >> (int)(sa + 32)); break;                        // dsra32
            case 0x0F: break;                                           // sync
            default: Abort = $"special {fn:X2} (word {w:X8})"; return false;
        }
        R[0] = 0;
        return true;
    }

    private bool ExecOther(uint w, uint op, uint rs, uint rt, int imm)
    {
        uint ea = L(rs) + (uint)imm;
        switch (op)
        {
            case 8: case 9: R[rt] = Sx(L(rs) + (uint)imm); break;
            case 0x18: case 0x19: R[rt] = R[rs] + (ulong)(long)imm; break;                          // daddi / daddiu
            case 0xA: R[rt] = (long)R[rs] < imm ? 1u : 0; break;
            case 0xB: R[rt] = R[rs] < (ulong)(long)imm ? 1u : 0; break;
            case 0xC: R[rt] = R[rs] & (w & 0xFFFF); break;
            case 0xD: R[rt] = R[rs] | (w & 0xFFFF); break;
            case 0xE: R[rt] = R[rs] ^ (w & 0xFFFF); break;
            case 0xF: R[rt] = Sx((w & 0xFFFF) << 16); break;
            case 0x1A: { int k = (int)(ea & 7) * 8; ulong d = Mem.U64(ea & ~7u); R[rt] = (d << k) | (R[rt] & ((1UL << k) - 1)); break; }                              // ldl
            case 0x1B: { int k = 56 - (int)(ea & 7) * 8; ulong d = Mem.U64(ea & ~7u); R[rt] = (d >> k) | (R[rt] & ~(~0UL >> k)); break; }                           // ldr
            case 0x20: R[rt] = (ulong)(long)Mem.S8(ea); break;
            case 0x21: R[rt] = (ulong)(long)Mem.S16(ea); break;
            case 0x22: R[rt] = Sx(Cpu.Lwl(L(rt), ea)); break;
            case 0x23: R[rt] = Sx(Mem.U32(ea)); break;
            case 0x24: R[rt] = Mem.U8(ea); break;
            case 0x25: R[rt] = Mem.U16(ea); break;
            case 0x26: R[rt] = Sx(Cpu.Lwr(L(rt), ea)); break;
            case 0x27: R[rt] = Mem.U32(ea); break;                                                    // lwu
            case 0x28: Mem.Set(ea, (byte)R[rt]); break;
            case 0x29: Mem.Set(ea, (ushort)R[rt]); break;
            case 0x2A: Cpu.Swl(L(rt), ea); break;
            case 0x2B: Mem.Set(ea, L(rt)); break;
            case 0x2C: { int k = (int)(ea & 7) * 8; ulong d = Mem.U64(ea & ~7u); Mem.Set(ea & ~7u, (R[rt] >> k) | (d & ~(~0UL >> k))); break; }                     // sdl
            case 0x2D: { int k = 56 - (int)(ea & 7) * 8; ulong d = Mem.U64(ea & ~7u); Mem.Set(ea & ~7u, (R[rt] << k) | (d & ((1UL << k) - 1))); break; }                // sdr
            case 0x2E: Cpu.Swr(L(rt), ea); break;
            case 0x2F: break;                                           // cache
            case 0x31: Cpu.SetW((int)rt, Mem.U32(ea)); break;           // lwc1
            case 0x35: Cpu.Fpr[rt] = Mem.U64(ea); break;                // ldc1
            case 0x37: R[rt] = Mem.U64(ea); break;                      // ld
            case 0x39: Mem.Set(ea, Cpu.W((int)rt)); break;              // swc1
            case 0x3D: Mem.Set(ea, Cpu.Fpr[rt]); break;                // sdc1
            case 0x3F: Mem.Set(ea, R[rt]); break;                      // sd
            case 0x11: if (!ExecCop1(w, rs, rt)) return false; break;
            case 0x10:                                                  // cop0: only the register moves (interrupt masking in libultra's inlined critical sections)
                if (rs == 0) { R[rt] = Sx(Cpu.Cp0[(w >> 11) & 31]); break; }
                if (rs == 4) { Cpu.Cp0[(w >> 11) & 31] = L(rt); break; }
                Abort = $"cop0 rs={rs:X} (word {w:X8})"; return false;
            default: Abort = $"opcode {op:X2} (word {w:X8})"; return false;
        }
        R[0] = 0;
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------ FPU

    private static int RoundMode => (int)(Cpu.Fcsr & 3);

    private static double RoundTo(double x, int mode) => mode switch
    {
        0 => Math.Round(x, MidpointRounding.ToEven),
        1 => Math.Truncate(x),
        2 => Math.Ceiling(x),
        _ => Math.Floor(x),
    };

    private static uint ToW(double x, int mode)
    {
        if (double.IsNaN(x)) return 0x7FFFFFFF;
        double r = RoundTo(x, mode);
        if (r >= 2147483648.0) return 0x7FFFFFFF;
        if (r < -2147483648.0) return 0x7FFFFFFF;
        return (uint)(int)r;
    }

    private bool ExecCop1(uint w, uint rs, uint rt)
    {
        uint fs = (w >> 11) & 31, fd = (w >> 6) & 31, ft = rt, fn = w & 63;
        switch (rs)
        {
            case 0: R[rt] = Sx(Cpu.W((int)fs)); R[0] = 0; return true;                // mfc1
            case 1: R[rt] = Cpu.Fpr[fs]; R[0] = 0; return true;                      // dmfc1
            case 5: Cpu.Fpr[fs] = R[rt]; return true;                                  // dmtc1
            case 2: R[rt] = Sx(fs == 31 ? (Cpu.Fcsr & ~0x800000u) | (Fcc ? 0x800000u : 0) : 0); R[0] = 0; return true;         // cfc1
            case 4: Cpu.SetW((int)fs, L(rt)); return true;                         // mtc1
            case 6: if (fs == 31) { Cpu.Fcsr = L(rt); Fcc = (L(rt) & 0x800000) != 0; } return true;     // ctc1
            case 0x10: return ExecFloat(fn, fs, ft, fd, false);
            case 0x11: return ExecFloat(fn, fs, ft, fd, true);
            case 0x14: return ExecFromWord(fn, fs, fd);
            default: Abort = $"cop1 rs={rs:X} (word {w:X8})"; return false;
        }
    }

    private bool ExecFromWord(uint fn, uint fs, uint fd)
    {
        int v = (int)Cpu.W((int)fs);
        switch (fn)
        {
            case 0x20: Cpu.SetS((int)fd, (float)v); return true;                    // cvt.s.w
            case 0x21: Cpu.SetD((int)fd, (double)v); return true;                   // cvt.d.w
            default: Abort = $"cvt from w fn={fn:X}"; return false;
        }
    }

    private bool ExecFloat(uint fn, uint fs, uint ft, uint fd, bool dbl)
    {
        if (dbl)
        {
            double a = Cpu.D((int)fs), b = Cpu.D((int)ft);
            switch (fn)
            {
                case 0: Cpu.SetD((int)fd, a + b); return true;
                case 1: Cpu.SetD((int)fd, a - b); return true;
                case 2: Cpu.SetD((int)fd, a * b); return true;
                case 3: Cpu.SetD((int)fd, a / b); return true;
                case 4: Cpu.SetD((int)fd, Math.Sqrt(a)); return true;
                case 5: Cpu.SetD((int)fd, Math.Abs(a)); return true;
                case 6: Cpu.Fpr[fd] = Cpu.Fpr[fs]; return true;
                case 7: Cpu.SetD((int)fd, -a); return true;
                case 0xC: Cpu.SetW((int)fd, ToW(a, 0)); return true;               // round.w.d
                case 0xD: Cpu.SetW((int)fd, ToW(a, 1)); return true;               // trunc.w.d
                case 0xE: Cpu.SetW((int)fd, ToW(a, 2)); return true;               // ceil.w.d
                case 0xF: Cpu.SetW((int)fd, ToW(a, 3)); return true;               // floor.w.d
                case 0x20: Cpu.SetS((int)fd, (float)a); return true;               // cvt.s.d
                case 0x24: Cpu.SetW((int)fd, ToW(a, RoundMode)); return true;      // cvt.w.d
                default:
                    if (fn >= 0x30) { Fcc = Compare(fn, a, b); return true; }
                    Abort = $"double fn={fn:X}"; return false;
            }
        }
        float x = Cpu.S((int)fs), y = Cpu.S((int)ft);
        switch (fn)
        {
            case 0: Cpu.SetS((int)fd, x + y); return true;
            case 1: Cpu.SetS((int)fd, x - y); return true;
            case 2: Cpu.SetS((int)fd, x * y); return true;
            case 3: Cpu.SetS((int)fd, x / y); return true;
            case 4: Cpu.SetS((int)fd, MathF.Sqrt(x)); return true;
            case 5: Cpu.SetW((int)fd, Cpu.W((int)fs) & 0x7FFFFFFF); return true;
            case 6: Cpu.SetW((int)fd, Cpu.W((int)fs)); return true;
            case 7: Cpu.SetW((int)fd, Cpu.W((int)fs) ^ 0x80000000); return true;
            case 0xC: Cpu.SetW((int)fd, ToW(x, 0)); return true;
            case 0xD: Cpu.SetW((int)fd, ToW(x, 1)); return true;
            case 0xE: Cpu.SetW((int)fd, ToW(x, 2)); return true;
            case 0xF: Cpu.SetW((int)fd, ToW(x, 3)); return true;
            case 0x21: Cpu.SetD((int)fd, (double)x); return true;                   // cvt.d.s
            case 0x24: Cpu.SetW((int)fd, ToW(x, RoundMode)); return true;           // cvt.w.s
            default:
                if (fn >= 0x30) { Fcc = Compare(fn, x, y); return true; }
                Abort = $"single fn={fn:X}"; return false;
        }
    }

    private static bool Compare(uint fn, double a, double b)
    {
        bool unordered = double.IsNaN(a) || double.IsNaN(b);
        bool less = !unordered && a < b, equal = !unordered && a == b;
        uint cond = fn & 15;
        return ((cond & 4) != 0 && less) || ((cond & 2) != 0 && equal) || ((cond & 1) != 0 && unordered);
    }

    // ------------------------------------------------------------------------------------------------------------ calls out

    /// <summary>A call to hand-written code: through the register ABI, with the stack words the callee may read.</summary>
    private bool CallExternal(uint target)
    {
        if (!Funcs.Has(target)) { Abort = $"call to {FuncName.GetValueOrDefault(target, target.ToString("X8"))}: no translated implementation registered"; return false; }
        string name = FuncName.GetValueOrDefault(target, target.ToString("X8"));
        if (!AllowedExternals.Contains(name)) { Abort = $"external call {name}"; return false; }
        ExternalCalls.Add(name);
        uint sp = L(29);
        for (int i = 0; i < 12; i++) Cpu.Stk[4 + i] = Mem.U32(sp + 16 + (uint)(4 * i));
        uint v0 = Funcs.CallReg(target, L(4), L(5), L(6), L(7));
        R[2] = Sx(v0); R[3] = Sx(Cpu.V1);
        return true;
    }

    /// <summary>Hand-written functions that are safe to run twice from the same state (no blocking, no hardware state).</summary>
    public static readonly HashSet<string> AllowedExternals = new()
    {
        "__osDisableInt", "__osRestoreInt", "osInvalDCache", "osInvalICache", "osWritebackDCache", "osWritebackDCacheAll", "osVirtualToPhysical",
        "__divdi3", "__moddi3", "__udivdi3", "__umoddi3", "__floatdisf", "__floatdidf", "__cmpdi2", "__ucmpdi2", "__fixsfdi", "__fixunssfdi",
        "osGetTime",
    };
}
