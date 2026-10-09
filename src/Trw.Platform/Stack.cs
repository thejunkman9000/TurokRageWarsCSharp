namespace Trw.Platform;

/// <summary>
/// Stack for C locals that need a guest address (structs, arrays, address-taken scalars). Every game thread gets its own region;
/// generated code calls <see cref="Alloc"/> on entry and <see cref="Free"/> in a finally block.
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

/// <summary>Code addresses. The ROM keeps function pointers in data tables as guest addresses; this maps them to the C# methods.</summary>
public static class Funcs
{
    private static readonly Dictionary<uint, Delegate> table = new();
    private static readonly Dictionary<uint, string> names = new();

    public static void Add(uint addr, Delegate d) { table[addr] = d; names[addr] = d.Method.Name; }

    public static T Get<T>(uint addr) where T : Delegate
    {
        if (!table.TryGetValue(addr, out var d))
            throw new InvalidOperationException($"call through unregistered code address {addr:X8}");
        if (d is T t) return t;
        throw new InvalidOperationException($"{d.Method.Name} ({addr:X8}) is {d.GetType().Name}, but the call site expects {typeof(T).Name}");
    }

    public static string NameOf(uint addr) => names.TryGetValue(addr, out var n) ? n : $"{addr:X8}";
}

/// <summary>Helper used by generated code for the C comma operator.</summary>
public static class C
{
    public static T Seq<T>(object? _, T value) => value;
}
