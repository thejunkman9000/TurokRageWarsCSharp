namespace Trw.Platform;

/// <summary>
/// Stack for C locals that need a guest address (structs, arrays, address-taken scalars) and for the frames of recompiled MIPS code.
/// Every game thread gets its own region; generated code calls <see cref="Alloc"/> on entry and <see cref="Free"/> in a finally block.
/// </summary>
public static class Stack
{
    [ThreadStatic] private static uint sp;
    [ThreadStatic] private static uint low;

    /// <summary>Points this thread's stack at [<paramref name="baseAddr"/>, <paramref name="baseAddr"/>+<paramref name="size"/>).</summary>
    public static void Init(uint baseAddr, uint size) { low = baseAddr; sp = baseAddr + size; }

    public static uint Alloc(int bytes)
    {
        var n = (uint)((bytes + 7) & ~7);
        if (sp - low < n) throw new InvalidOperationException("guest stack overflow");
        sp -= n;
        return sp;
    }

    public static void Free(int bytes) => sp += (uint)((bytes + 7) & ~7);
}

/// <summary>
/// Code addresses. On the N64 a function pointer is a code address and a call through it uses the register convention, so every
/// translated function registers an adapter that reads its arguments from (a0-a3, <see cref="Cpu.Stk"/>, <see cref="Cpu.F"/>) and
/// leaves its result in the return register (and <see cref="Cpu.F"/>[0] / <see cref="Cpu.V1"/>).
/// </summary>
public static class Funcs
{
    public delegate uint RegFn(uint a0, uint a1, uint a2, uint a3);

    private static readonly Dictionary<uint, RegFn> table = new();
    private static readonly Dictionary<uint, string> names = new();

    public static void Add(uint addr, string name, RegFn fn) { table[addr] = fn; names[addr] = name; }

    public static string NameOf(uint addr) => names.TryGetValue(addr, out var n) ? n : $"{addr:X8}";

    public static bool Has(uint addr) => table.ContainsKey(addr);

    /// <summary>Calls the function at a code address with register-convention arguments.</summary>
    public static uint CallReg(uint addr, uint a0, uint a1, uint a2, uint a3)
    {
        if (!table.TryGetValue(addr, out var fn)) fn = Resolve(addr);
        return fn(a0, a1, a2, a3);
    }

    private static uint[]? sorted;
    private static readonly Dictionary<uint, RegFn> resolved = new();

    /// <summary>
    /// A code address that is no function entry. Data tables hold some of these: a bare <c>jr ra</c> used as a "do nothing" callback, or an entry a few
    /// bytes into a function (its first instructions are dead). Both are legal on the real machine.
    /// </summary>
    private static RegFn Resolve(uint addr)
    {
        lock (resolved)
        {
            if (resolved.TryGetValue(addr, out var r)) return r;
            // ROM offset of the instruction: main image 0x200000.. = ROM 0xC00.., overlay 0x400000.. = ROM 0x16E000..
            long off = addr >= 0x400000 ? addr - 0x400000L + 0x16E000 : addr - 0x200000L + 0xC00;
            var rom = Host.Rom;
            if (off >= 0 && off + 8 <= rom.Length && rom[off] == 0x03 && rom[off + 1] == 0xE0 && rom[off + 2] == 0x00 && rom[off + 3] == 0x08)
                r = (a0, a1, a2, a3) => 0;
            else
            {
                sorted ??= table.Keys.OrderBy(x => x).ToArray();
                int i = Array.BinarySearch(sorted, addr);
                if (i < 0) i = ~i - 1;
                if (i >= 0 && addr - sorted[i] <= 16) { var target = table[sorted[i]]; r = target; }
                else throw new InvalidOperationException($"call through unregistered code address {addr:X8}");
            }
            resolved[addr] = r;
            return r;
        }
    }

    /// <summary>Same, with argument words 4.. (stack words) supplied.</summary>
    public static uint CallRegX(uint addr, uint a0, uint a1, uint a2, uint a3, params uint[] extra)
    {
        for (int i = 0; i < extra.Length; i++) Cpu.Stk[4 + i] = extra[i];
        return CallReg(addr, a0, a1, a2, a3);
    }
}

/// <summary>Helper used by generated code for the C comma operator and for "evaluate A, then yield B".</summary>
public static class C
{
    public static T Seq<T>(object? _, T value) => value;
    public static uint Seq(uint _, uint value) => value;

    // short-lived guest buffers for structs rebuilt from scalar argument words (see Trw.Transpile's MarshalByWords): a ring of 64-byte slots, so
    // nested argument expressions each get their own; the callee copies the struct into its own frame on entry
    public const uint TmpBase = Str.PoolBase + 0xFF000;      // the last 4 KB of the string pool region
    private const int TmpSlots = 64, TmpSlotBytes = 64;
    private static int tmpNext;
    public static uint Tmp(params uint[] words)
    {
        uint addr = TmpBase + (uint)((Interlocked.Increment(ref tmpNext) & (TmpSlots - 1)) * TmpSlotBytes);
        for (int i = 0; i < words.Length; i++) Mem.Set(addr + (uint)(4 * i), words[i]);
        return addr;
    }
}


