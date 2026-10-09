using System.Runtime.CompilerServices;

namespace Trw.Platform;

/// <summary>
/// State that crosses calls in the N64's register calling convention (o32): the floating point registers
/// (arguments in F12/F14, result in F0), the second result register v1, and argument words that do not fit in a0-a3.
/// Statically recompiled MIPS functions and the register adapters of translated C use these.
/// </summary>
/// <remarks>
/// The game runs the FPU in 64-bit mode (Status.FR = 1, set by osCreateThread): 32 independent 64-bit registers. A single is the low
/// 32 bits of a register, a double is the whole register.
/// </remarks>
public static class Cpu
{
    /// <summary>The 32 FPU registers as raw 64-bit values.</summary>
    public static readonly ulong[] Fpr = new ulong[32];

    // single view: the low word
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float S(int n) => BitConverter.UInt32BitsToSingle((uint)Fpr[n]);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float SetS(int n, float v) { Fpr[n] = (Fpr[n] & 0xFFFFFFFF00000000UL) | BitConverter.SingleToUInt32Bits(v); return v; }
    /// <summary>The raw low word (mfc1 / swc1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint W(int n) => (uint)Fpr[n];
    /// <summary>Writes the low word (mtc1 / lwc1); the high word is unchanged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void SetW(int n, uint v) => Fpr[n] = (Fpr[n] & 0xFFFFFFFF00000000UL) | v;

    // double view: the whole register
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static double D(int n) => BitConverter.UInt64BitsToDouble(Fpr[n]);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static double SetD(int n, double v) { Fpr[n] = BitConverter.DoubleToUInt64Bits(v); return v; }

    /// <summary>Coprocessor 0 registers (Count 9, Compare 11, Status 12, Cause 13, EPC 14, ...) and the FPU control/status register.</summary>
    public static readonly uint[] Cp0 = new uint[32];
    public static uint Fcsr;

    /// <summary>Unaligned loads/stores (big-endian semantics): the word containing the address is read/written and merged.</summary>
    public static uint Lwl(uint rt, uint addr) { int k = (int)(addr & 3) * 8; uint w = Mem.U32(addr & ~3u); return (w << k) | (rt & ((1u << k) - 1)); }
    public static uint Lwr(uint rt, uint addr) { int k = 24 - (int)(addr & 3) * 8; uint w = Mem.U32(addr & ~3u); return (w >> k) | (rt & ~(0xFFFFFFFFu >> k)); }
    public static void Swl(uint rt, uint addr) { int k = (int)(addr & 3) * 8; uint w = Mem.U32(addr & ~3u); Mem.Set(addr & ~3u, (rt >> k) | (w & ~(0xFFFFFFFFu >> k))); }
    public static void Swr(uint rt, uint addr) { int k = 24 - (int)(addr & 3) * 8; uint w = Mem.U32(addr & ~3u); Mem.Set(addr & ~3u, (rt << k) | (w & ((1u << k) - 1))); }

    /// <summary>v1: low word of a 64-bit result.</summary>
    public static uint V1;

    /// <summary>Argument words 4.. of the next register-ABI call (index = word number; words 0-3 travel in a0-a3).</summary>
    public static readonly uint[] Stk = new uint[32];

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float U2F(uint v) => BitConverter.UInt32BitsToSingle(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint F2U(float v) => BitConverter.SingleToUInt32Bits(v);

    /// <summary>C's (unsigned)float on the original compiler: values below 2^31 go through trunc.w.s (so negatives wrap to their two's complement bits), larger ones are offset by 2^31.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint TruncU32(float v) => v >= 2147483648f ? (uint)(int)(v - 2147483648f) | 0x80000000u : (uint)(int)v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static uint TruncU32(double v) => v >= 2147483648.0 ? (uint)(int)(v - 2147483648.0) | 0x80000000u : (uint)(int)v;
    public static double U2D(uint hi, uint lo) => BitConverter.UInt64BitsToDouble(((ulong)hi << 32) | lo);
    public static uint D2Hi(double d) => (uint)(BitConverter.DoubleToUInt64Bits(d) >> 32);
    public static uint D2Lo(double d) => (uint)BitConverter.DoubleToUInt64Bits(d);

    /// <summary>MIPS div: no exception on int.MinValue / -1.</summary>
    public static int SDiv(int a, int b) => b == -1 ? unchecked(-a) : a / b;
    public static int SRem(int a, int b) => b == -1 ? 0 : a % b;
    public static long Long(uint hi, uint lo) => (long)(((ulong)hi << 32) | lo);
}
