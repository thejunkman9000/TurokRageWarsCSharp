using System.Text.RegularExpressions;

namespace Trw.Transpile;

public enum K { Void, Int, Float, Ptr, Record, Array, Func, Unknown }

/// <summary>A C type as seen by the transpiler (MIPS o32: 4-byte pointers, big-endian, plain char signed).</summary>
public sealed class CType
{
    public K Kind;
    public int Size;
    public bool Signed;
    public CType? Elem;     // pointee / array element
    public int Count;       // array length
    public string Name = "";
    public CType? Ret;                 // K.Func
    public List<CType> Params = new(); // K.Func
    public bool Variadic;

    public static readonly CType Void = new() { Kind = K.Void, Size = 1 };
    public static readonly CType S32 = new() { Kind = K.Int, Size = 4, Signed = true };
    public static readonly CType U32 = new() { Kind = K.Int, Size = 4 };

    public bool IsPtr => Kind == K.Ptr;
    public bool IsInt => Kind == K.Int;
    public bool IsFloat => Kind == K.Float;
    public bool IsScalarMem => Kind is K.Int or K.Float or K.Ptr;

    /// <summary>Size used for pointer arithmetic on a pointer to this type.</summary>
    public int ElemSize => Elem == null ? 1 : Math.Max(1, Elem.Size);

    /// <summary>The C# type used for a value of this type.</summary>
    public string Cs => Kind switch
    {
        K.Void => "void",
        K.Ptr or K.Array or K.Func => "uint",
        K.Float => Size == 4 ? "float" : "double",
        K.Int => (Size, Signed) switch
        {
            (1, true) => "sbyte", (1, false) => "byte",
            (2, true) => "short", (2, false) => "ushort",
            (4, true) => "int", (4, false) => "uint",
            (8, true) => "long", _ => "ulong",
        },
        _ => throw new Unsupported($"C# type for {Kind} {Name}"),
    };

    public string Zero => Kind switch
    {
        K.Float => Size == 4 ? "0f" : "0.0",
        K.Ptr => "0u",
        K.Int when Size == 8 => Signed ? "0L" : "0UL",
        K.Int when !Signed && Size == 4 => "0u",
        _ => "0",
    };

    static CType ParseFunc(string q, AstIndex ix)
    {
        try { return ParseFuncCore(q, ix); }
        catch (ArgumentOutOfRangeException) { throw new Unsupported($"function type syntax: {q}"); }
    }

    static CType ParseFuncCore(string q, AstIndex ix)
    {
        var fn = new CType { Kind = K.Func, Size = 4, Name = q };
        int fq = q.IndexOf('(');
        int star = string.CompareOrdinal(q, fq, "(*)", 0, 3) == 0 ? fq : -1;
        int open = star >= 0 ? star + 3 : q.IndexOf('(');
        var retText = star >= 0 ? q[..star] : q[..q.IndexOf('(')];
        fn.Ret = Parse(retText, ix);
        var ptext = q[(open + 1)..q.LastIndexOf(')')];
        int depth = 0, start = 0;
        var parts = new List<string>();
        for (int i = 0; i <= ptext.Length; i++)
        {
            if (i < ptext.Length && ptext[i] == '(') depth++;
            else if (i < ptext.Length && ptext[i] == ')') depth--;
            else if (i == ptext.Length || (ptext[i] == ',' && depth == 0)) { parts.Add(ptext[start..i].Trim()); start = i + 1; }
        }
        foreach (var p in parts)
        {
            if (p == "" || p == "void") continue;
            if (p == "...") { fn.Variadic = true; continue; }
            fn.Params.Add(Parse(p, ix));
        }
        return fn;
    }

    static readonly Regex Quals = new(@"\b(const|volatile|restrict|register|__restrict)\b\s*", RegexOptions.Compiled);

    public static CType Parse(string q, AstIndex ix)
    {
        q = Quals.Replace(q, "").Trim();
        int fp = q.IndexOf('(');
        int pstar = fp >= 0 && string.CompareOrdinal(q, fp, "(*)", 0, 3) == 0 ? fp : -1;   // only the first parenthesis group describes this type
        if (q.EndsWith(']') && pstar >= 0 && q[(pstar + 3)..].TrimStart().StartsWith('['))   // pointer to array: "f32 (*)[3]"
            return new CType { Kind = K.Ptr, Size = 4, Name = q, Elem = Parse(q[..pstar] + q[(pstar + 3)..].TrimStart(), ix) };
        if (q.EndsWith(']'))
        {
            var lb = q.LastIndexOf('[');
            var inner = q[(lb + 1)..^1];
            var elem = Parse(q[..lb], ix);
            var cnt = int.TryParse(inner, out var c) ? c : 0;
            return new CType { Kind = K.Array, Elem = elem, Count = cnt, Size = elem.Size * cnt, Name = q };
        }
        if (q.Contains('(') && !q.Contains("(unnamed") && !q.Contains("(anonymous"))
        {
            // "void (*)(int)" is a pointer to function; "void (int)" is the function type itself
            int st = pstar;
            if (fp >= 0 && string.CompareOrdinal(q, fp, "(**)", 0, 4) == 0)           // pointer to pointer to function
                return new CType { Kind = K.Ptr, Size = 4, Name = q, Elem = Parse(q.Remove(fp + 1, 1), ix) };
            if (st >= 0 && q[(st + 3)..].TrimStart().StartsWith('['))   // pointer to array: "u8 (*)[4]"
                return new CType { Kind = K.Ptr, Size = 4, Name = q, Elem = Parse(q[..st] + q[(st + 3)..].TrimStart(), ix) };
            var fn = ParseFunc(q, ix);
            return pstar >= 0 ? new CType { Kind = K.Ptr, Size = 4, Elem = fn, Name = q } : fn;
        }
        if (q.EndsWith('*'))
            return new CType { Kind = K.Ptr, Size = 4, Elem = Parse(q[..^1], ix), Name = q };

        if (q.StartsWith("enum ")) return S32;
        switch (q)
        {
            case "void": return Void;
            case "char": case "signed char": return new CType { Kind = K.Int, Size = 1, Signed = true, Name = q };
            case "unsigned char": case "_Bool": return new CType { Kind = K.Int, Size = 1, Name = q };
            case "short": case "short int": return new CType { Kind = K.Int, Size = 2, Signed = true, Name = q };
            case "unsigned short": case "unsigned short int": return new CType { Kind = K.Int, Size = 2, Name = q };
            case "int": case "long": case "long int": return new CType { Kind = K.Int, Size = 4, Signed = true, Name = q };
            case "unsigned int": case "unsigned long": case "unsigned": case "unsigned long int": return new CType { Kind = K.Int, Size = 4, Name = q };
            case "long long": case "long long int": return new CType { Kind = K.Int, Size = 8, Signed = true, Name = q };
            case "unsigned long long": case "unsigned long long int": return new CType { Kind = K.Int, Size = 8, Name = q };
            case "float": return new CType { Kind = K.Float, Size = 4, Name = q };
            case "double": case "long double": return new CType { Kind = K.Float, Size = 8, Name = q };
        }
        if (ix.Typedefs.TryGetValue(q, out var td) && td != q && ix.FindRecord(q) == null) return Parse(td, ix);
        var name = q.StartsWith("struct ") ? q[7..] : q.StartsWith("union ") ? q[6..] : q;
        var rec = ix.FindRecord(name);
        if (rec != null) return new CType { Kind = K.Record, Size = rec.Size, Name = name };
        return new CType { Kind = K.Unknown, Size = 4, Name = q };
    }
}








