using System.Text;

namespace Trw.Transpile;

/// <summary>C# parameter/return types of a function, used to coerce arguments at call sites and to build register adapters.</summary>
/// <param name="Structs">for each parameter, the size when it is a struct passed by value (travels as an address in typed calls), else 0</param>
/// <param name="HiddenRet">the first parameter is the result buffer of a struct-returning function</param>
public sealed record Sig(string Ret, string[] Params, int[] Structs, bool HiddenRet)
{
    public Sig(string ret, string[] ps) : this(ret, ps, new int[ps.Length], false) { }

    public string Serialize(string name) =>
        $"{name}|{Ret}|{string.Join(",", Params)}|{string.Join(",", Structs)}|{(HiddenRet ? 1 : 0)}";

    public static (string name, Sig sig) Parse(string line)
    {
        var p = line.Split('|');
        var ps = p[2] == "" ? Array.Empty<string>() : p[2].Split(',');
        var st = p.Length > 3 && p[3] != "" ? p[3].Split(',').Select(int.Parse).ToArray() : new int[ps.Length];
        return (p[0], new Sig(p[1], ps, st, p.Length > 4 && p[4] == "1"));
    }
}

/// <summary>
/// The N64 calling convention (MIPS o32) as C# text: how typed arguments map to words / FPRs and back.
/// Words 0-3 travel in a0-a3, later words on the stack (<c>Cpu.Stk[k]</c>); the first one or two floating point arguments travel in F12/F14
/// when every argument before them is also floating point; 64-bit values take an aligned pair (high word first).
/// </summary>
public static class Abi
{
    public static bool IsFp(string t) => t is "float" or "double";
    public static bool IsWide(string t) => t is "long" or "ulong";

    static string W(int k) => k < 4 ? $"a{k}" : $"Cpu.Stk[{k}]";

    /// <summary>
    /// Body of a register adapter: reads the arguments for <paramref name="sig"/> from registers, calls <paramref name="callee"/>, and
    /// leaves the result where the convention wants it. Returns the statements (the adapter returns the value of v0).
    /// </summary>
    public static string AdapterBody(Sig sig, string callee, string indent)
    {
        var pre = new StringBuilder();
        var post = new StringBuilder();
        var args = new List<string>();
        int word = 0;
        for (int i = 0; i < sig.Params.Length; i++)
        {
            var t = sig.Params[i];
            bool inFpReg = IsFp(t) && (i == 0 || (i == 1 && IsFp(sig.Params[0])));
            if (t == "double") word = (word + 1) & ~1;
            if (IsWide(t)) word = (word + 1) & ~1;
            if (inFpReg)
            {
                args.Add(t == "float" ? $"Cpu.S({12 + 2 * i})" : $"Cpu.D({12 + 2 * i})");
                word += t == "double" ? 2 : 1;
            }
            else if (t == "float") { args.Add($"Cpu.U2F({W(word)})"); word++; }
            else if (t == "double") { args.Add($"Cpu.U2D({W(word)}, {W(word + 1)})"); word += 2; }
            else if (t == "long") { args.Add($"Cpu.Long({W(word)}, {W(word + 1)})"); word += 2; }
            else if (t == "ulong") { args.Add($"(ulong)Cpu.Long({W(word)}, {W(word + 1)})"); word += 2; }
            else if (sig.Structs[i] > 0)
            {
                int size = sig.Structs[i], n = (size + 3) / 4;
                pre.Append($"{indent}uint s{i} = Stack.Alloc({size});\n");
                for (int k = 0; k < n; k++) pre.Append($"{indent}Mem.Set(s{i} + {4 * k}u, {W(word + k)});\n");
                post.Append($"{indent}Stack.Free({size});\n");
                args.Add($"s{i}");
                word += n;
            }
            else { args.Add(t == "uint" ? W(word) : $"({t}){W(word)}"); word++; }
        }
        var call = $"{callee}({string.Join(", ", args)})";
        string ret = sig.Ret switch
        {
            "void" => $"{indent}{call};\n{post}{indent}return 0;\n",
            "float" => $"{indent}Cpu.SetS(0, {call});\n{post}{indent}return 0;\n",
            "double" => $"{indent}Cpu.SetD(0, {call});\n{post}{indent}return 0;\n",
            "long" or "ulong" => $"{indent}var v_ = {call};\n{post}{indent}Cpu.V1 = (uint)v_;\n{indent}return (uint)((ulong)v_ >> 32);\n",
            "uint" => $"{indent}var v_ = {call};\n{post}{indent}return v_;\n",
            _ => $"{indent}var v_ = {call};\n{post}{indent}return (uint)v_;\n",
        };
        return pre + ret;
    }

    /// <summary>
    /// Call-site marshalling for an indirect call: C# text that evaluates to the call's result. <paramref name="argTypes"/>/<paramref name="argValues"/>
    /// are the arguments (struct arguments are addresses with their sizes in <paramref name="argStructs"/>).
    /// </summary>
    public static string IndirectCall(string addr, string retCs, string[] argTypes, string[] argValues, int[] argStructs, string? hiddenRet)
    {
        var pre = new List<string>();
        var words = new List<string>();
        var types = hiddenRet != null ? new[] { "uint" }.Concat(argTypes).ToArray() : argTypes;
        var values = hiddenRet != null ? new[] { hiddenRet }.Concat(argValues).ToArray() : argValues;
        var structs = hiddenRet != null ? new[] { 0 }.Concat(argStructs).ToArray() : argStructs;
        void Put(int k, string v) { while (words.Count <= k) words.Add("0u"); words[k] = v; }
        int word = 0;
        for (int i = 0; i < types.Length; i++)
        {
            var t = types[i];
            bool inFpReg = IsFp(t) && (i == 0 || (i == 1 && IsFp(types[0])));
            if (t == "double" || IsWide(t)) word = (word + 1) & ~1;
            if (inFpReg)
            {
                pre.Add(t == "float" ? $"Cpu.SetS({12 + 2 * i}, {values[i]})" : $"Cpu.SetD({12 + 2 * i}, {values[i]})");
                word += t == "double" ? 2 : 1;
            }
            else if (t == "float") { Put(word, $"Cpu.F2U({values[i]})"); word++; }
            else if (t == "double") { Put(word, $"Cpu.D2Hi({values[i]})"); Put(word + 1, $"Cpu.D2Lo({values[i]})"); word += 2; }
            else if (IsWide(t)) { Put(word, $"(uint)((ulong)({values[i]}) >> 32)"); Put(word + 1, $"(uint)({values[i]})"); word += 2; }
            else if (structs[i] > 0)
            {
                int n = (structs[i] + 3) / 4;
                for (int k = 0; k < n; k++) Put(word + k, $"Mem.U32({values[i]} + {4 * k}u)");
                word += n;
            }
            else { Put(word, t == "uint" ? values[i] : $"(uint)({values[i]})"); word++; }
        }
        while (words.Count < 4) words.Add("0u");
        var inner = words.Count <= 4
            ? $"Funcs.CallReg({addr}, {string.Join(", ", words)})"
            : $"Funcs.CallRegX({addr}, {string.Join(", ", words.Take(4))}, new uint[] {{ {string.Join(", ", words.Skip(4))} }})";
        // the argument words must be evaluated after the FP registers are loaded
        string call = inner;
        foreach (var p in Enumerable.Reverse(pre)) call = $"C.Seq<uint>((object?){p}, {call})";
        return retCs switch
        {
            "void" => call,
            "float" => $"C.Seq<float>((object?){call}, Cpu.S(0))",
            "double" => $"C.Seq<double>((object?){call}, Cpu.D(0))",
            "long" => $"Cpu.Long({call}, Cpu.V1)",
            "ulong" => $"(ulong)Cpu.Long({call}, Cpu.V1)",
            "uint" => call,
            _ => $"({retCs})({call})",
        };
    }
}

