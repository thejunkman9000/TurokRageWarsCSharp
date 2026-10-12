// Typed scalar pointer views for the generated struct views.
using System.Runtime.CompilerServices;

namespace Trw.Platform;

/// <summary>Shared constant for the generated struct views (src/Trw.Game/Generated/Structs).</summary>
public static class ViewConst
{
    public const MethodImplOptions Inl = MethodImplOptions.AggressiveInlining;
}

/// <summary>A guest pointer to sbyte (no length): <c>p[i]</c> reads/writes element i. Null is address 0.</summary>
public readonly struct S8Ptr
{
    public readonly uint P;
    [MethodImpl(ViewConst.Inl)] public S8Ptr(uint p) { P = p; }
    public bool IsNull { [MethodImpl(ViewConst.Inl)] get => P == 0; }
    public sbyte this[int i]
    {
        [MethodImpl(ViewConst.Inl)] get => Mem.S8(P + (uint)(i * 1));
        [MethodImpl(ViewConst.Inl)] set => Mem.Set(P + (uint)(i * 1), value);
    }
    /// <summary>Store usable on an rvalue view (C# forbids indexer/property setters on struct rvalues).</summary>
    [MethodImpl(ViewConst.Inl)] public sbyte Set(int i, sbyte v) => Mem.Set(P + (uint)(i * 1), v);
    [MethodImpl(ViewConst.Inl)] public S8Ptr Add(int n) => new(P + (uint)(n * 1));
    [MethodImpl(ViewConst.Inl)] public static implicit operator uint(S8Ptr v) => v.P;
    [MethodImpl(ViewConst.Inl)] public static explicit operator S8Ptr(uint p) => new(p);
}

/// <summary>A guest pointer to byte (no length): <c>p[i]</c> reads/writes element i. Null is address 0.</summary>
public readonly struct U8Ptr
{
    public readonly uint P;
    [MethodImpl(ViewConst.Inl)] public U8Ptr(uint p) { P = p; }
    public bool IsNull { [MethodImpl(ViewConst.Inl)] get => P == 0; }
    public byte this[int i]
    {
        [MethodImpl(ViewConst.Inl)] get => Mem.U8(P + (uint)(i * 1));
        [MethodImpl(ViewConst.Inl)] set => Mem.Set(P + (uint)(i * 1), value);
    }
    /// <summary>Store usable on an rvalue view (C# forbids indexer/property setters on struct rvalues).</summary>
    [MethodImpl(ViewConst.Inl)] public byte Set(int i, byte v) => Mem.Set(P + (uint)(i * 1), v);
    [MethodImpl(ViewConst.Inl)] public U8Ptr Add(int n) => new(P + (uint)(n * 1));
    [MethodImpl(ViewConst.Inl)] public static implicit operator uint(U8Ptr v) => v.P;
    [MethodImpl(ViewConst.Inl)] public static explicit operator U8Ptr(uint p) => new(p);
}

/// <summary>A guest pointer to short (no length): <c>p[i]</c> reads/writes element i. Null is address 0.</summary>
public readonly struct S16Ptr
{
    public readonly uint P;
    [MethodImpl(ViewConst.Inl)] public S16Ptr(uint p) { P = p; }
    public bool IsNull { [MethodImpl(ViewConst.Inl)] get => P == 0; }
    public short this[int i]
    {
        [MethodImpl(ViewConst.Inl)] get => Mem.S16(P + (uint)(i * 2));
        [MethodImpl(ViewConst.Inl)] set => Mem.Set(P + (uint)(i * 2), value);
    }
    /// <summary>Store usable on an rvalue view (C# forbids indexer/property setters on struct rvalues).</summary>
    [MethodImpl(ViewConst.Inl)] public short Set(int i, short v) => Mem.Set(P + (uint)(i * 2), v);
    [MethodImpl(ViewConst.Inl)] public S16Ptr Add(int n) => new(P + (uint)(n * 2));
    [MethodImpl(ViewConst.Inl)] public static implicit operator uint(S16Ptr v) => v.P;
    [MethodImpl(ViewConst.Inl)] public static explicit operator S16Ptr(uint p) => new(p);
}

/// <summary>A guest pointer to ushort (no length): <c>p[i]</c> reads/writes element i. Null is address 0.</summary>
public readonly struct U16Ptr
{
    public readonly uint P;
    [MethodImpl(ViewConst.Inl)] public U16Ptr(uint p) { P = p; }
    public bool IsNull { [MethodImpl(ViewConst.Inl)] get => P == 0; }
    public ushort this[int i]
    {
        [MethodImpl(ViewConst.Inl)] get => Mem.U16(P + (uint)(i * 2));
        [MethodImpl(ViewConst.Inl)] set => Mem.Set(P + (uint)(i * 2), value);
    }
    /// <summary>Store usable on an rvalue view (C# forbids indexer/property setters on struct rvalues).</summary>
    [MethodImpl(ViewConst.Inl)] public ushort Set(int i, ushort v) => Mem.Set(P + (uint)(i * 2), v);
    [MethodImpl(ViewConst.Inl)] public U16Ptr Add(int n) => new(P + (uint)(n * 2));
    [MethodImpl(ViewConst.Inl)] public static implicit operator uint(U16Ptr v) => v.P;
    [MethodImpl(ViewConst.Inl)] public static explicit operator U16Ptr(uint p) => new(p);
}

/// <summary>A guest pointer to int (no length): <c>p[i]</c> reads/writes element i. Null is address 0.</summary>
public readonly struct S32Ptr
{
    public readonly uint P;
    [MethodImpl(ViewConst.Inl)] public S32Ptr(uint p) { P = p; }
    public bool IsNull { [MethodImpl(ViewConst.Inl)] get => P == 0; }
    public int this[int i]
    {
        [MethodImpl(ViewConst.Inl)] get => Mem.S32(P + (uint)(i * 4));
        [MethodImpl(ViewConst.Inl)] set => Mem.Set(P + (uint)(i * 4), value);
    }
    /// <summary>Store usable on an rvalue view (C# forbids indexer/property setters on struct rvalues).</summary>
    [MethodImpl(ViewConst.Inl)] public int Set(int i, int v) => Mem.Set(P + (uint)(i * 4), v);
    [MethodImpl(ViewConst.Inl)] public S32Ptr Add(int n) => new(P + (uint)(n * 4));
    [MethodImpl(ViewConst.Inl)] public static implicit operator uint(S32Ptr v) => v.P;
    [MethodImpl(ViewConst.Inl)] public static explicit operator S32Ptr(uint p) => new(p);
}

/// <summary>A guest pointer to uint (no length): <c>p[i]</c> reads/writes element i. Null is address 0.</summary>
public readonly struct U32Ptr
{
    public readonly uint P;
    [MethodImpl(ViewConst.Inl)] public U32Ptr(uint p) { P = p; }
    public bool IsNull { [MethodImpl(ViewConst.Inl)] get => P == 0; }
    public uint this[int i]
    {
        [MethodImpl(ViewConst.Inl)] get => Mem.U32(P + (uint)(i * 4));
        [MethodImpl(ViewConst.Inl)] set => Mem.Set(P + (uint)(i * 4), value);
    }
    /// <summary>Store usable on an rvalue view (C# forbids indexer/property setters on struct rvalues).</summary>
    [MethodImpl(ViewConst.Inl)] public uint Set(int i, uint v) => Mem.Set(P + (uint)(i * 4), v);
    [MethodImpl(ViewConst.Inl)] public U32Ptr Add(int n) => new(P + (uint)(n * 4));
    [MethodImpl(ViewConst.Inl)] public static implicit operator uint(U32Ptr v) => v.P;
    [MethodImpl(ViewConst.Inl)] public static explicit operator U32Ptr(uint p) => new(p);
}

/// <summary>A guest pointer to long (no length): <c>p[i]</c> reads/writes element i. Null is address 0.</summary>
public readonly struct S64Ptr
{
    public readonly uint P;
    [MethodImpl(ViewConst.Inl)] public S64Ptr(uint p) { P = p; }
    public bool IsNull { [MethodImpl(ViewConst.Inl)] get => P == 0; }
    public long this[int i]
    {
        [MethodImpl(ViewConst.Inl)] get => Mem.S64(P + (uint)(i * 8));
        [MethodImpl(ViewConst.Inl)] set => Mem.Set(P + (uint)(i * 8), value);
    }
    /// <summary>Store usable on an rvalue view (C# forbids indexer/property setters on struct rvalues).</summary>
    [MethodImpl(ViewConst.Inl)] public long Set(int i, long v) => Mem.Set(P + (uint)(i * 8), v);
    [MethodImpl(ViewConst.Inl)] public S64Ptr Add(int n) => new(P + (uint)(n * 8));
    [MethodImpl(ViewConst.Inl)] public static implicit operator uint(S64Ptr v) => v.P;
    [MethodImpl(ViewConst.Inl)] public static explicit operator S64Ptr(uint p) => new(p);
}

/// <summary>A guest pointer to ulong (no length): <c>p[i]</c> reads/writes element i. Null is address 0.</summary>
public readonly struct U64Ptr
{
    public readonly uint P;
    [MethodImpl(ViewConst.Inl)] public U64Ptr(uint p) { P = p; }
    public bool IsNull { [MethodImpl(ViewConst.Inl)] get => P == 0; }
    public ulong this[int i]
    {
        [MethodImpl(ViewConst.Inl)] get => Mem.U64(P + (uint)(i * 8));
        [MethodImpl(ViewConst.Inl)] set => Mem.Set(P + (uint)(i * 8), value);
    }
    /// <summary>Store usable on an rvalue view (C# forbids indexer/property setters on struct rvalues).</summary>
    [MethodImpl(ViewConst.Inl)] public ulong Set(int i, ulong v) => Mem.Set(P + (uint)(i * 8), v);
    [MethodImpl(ViewConst.Inl)] public U64Ptr Add(int n) => new(P + (uint)(n * 8));
    [MethodImpl(ViewConst.Inl)] public static implicit operator uint(U64Ptr v) => v.P;
    [MethodImpl(ViewConst.Inl)] public static explicit operator U64Ptr(uint p) => new(p);
}

/// <summary>A guest pointer to float (no length): <c>p[i]</c> reads/writes element i. Null is address 0.</summary>
public readonly struct F32Ptr
{
    public readonly uint P;
    [MethodImpl(ViewConst.Inl)] public F32Ptr(uint p) { P = p; }
    public bool IsNull { [MethodImpl(ViewConst.Inl)] get => P == 0; }
    public float this[int i]
    {
        [MethodImpl(ViewConst.Inl)] get => Mem.F32(P + (uint)(i * 4));
        [MethodImpl(ViewConst.Inl)] set => Mem.Set(P + (uint)(i * 4), value);
    }
    /// <summary>Store usable on an rvalue view (C# forbids indexer/property setters on struct rvalues).</summary>
    [MethodImpl(ViewConst.Inl)] public float Set(int i, float v) => Mem.Set(P + (uint)(i * 4), v);
    [MethodImpl(ViewConst.Inl)] public F32Ptr Add(int n) => new(P + (uint)(n * 4));
    [MethodImpl(ViewConst.Inl)] public static implicit operator uint(F32Ptr v) => v.P;
    [MethodImpl(ViewConst.Inl)] public static explicit operator F32Ptr(uint p) => new(p);
}

/// <summary>A guest pointer to double (no length): <c>p[i]</c> reads/writes element i. Null is address 0.</summary>
public readonly struct F64Ptr
{
    public readonly uint P;
    [MethodImpl(ViewConst.Inl)] public F64Ptr(uint p) { P = p; }
    public bool IsNull { [MethodImpl(ViewConst.Inl)] get => P == 0; }
    public double this[int i]
    {
        [MethodImpl(ViewConst.Inl)] get => Mem.F64(P + (uint)(i * 8));
        [MethodImpl(ViewConst.Inl)] set => Mem.Set(P + (uint)(i * 8), value);
    }
    /// <summary>Store usable on an rvalue view (C# forbids indexer/property setters on struct rvalues).</summary>
    [MethodImpl(ViewConst.Inl)] public double Set(int i, double v) => Mem.Set(P + (uint)(i * 8), v);
    [MethodImpl(ViewConst.Inl)] public F64Ptr Add(int n) => new(P + (uint)(n * 8));
    [MethodImpl(ViewConst.Inl)] public static implicit operator uint(F64Ptr v) => v.P;
    [MethodImpl(ViewConst.Inl)] public static explicit operator F64Ptr(uint p) => new(p);
}
