using System.Text.Json.Nodes;

namespace Trw.Transpile;

// Emitter part that renders `p->field` / `s.field` / `p->arr[i]` of structs with a generated view (see StructViews.cs) as `new Actor(p).health`.
// Everything here is only reached when `Views` is set (`--emit-views <list>`); the old `Mem.S32(p + 28u)` output stays the default.
public sealed partial class Emitter
{
    /// <summary>The structs that have a view; null = old output.</summary>
    public ViewSet? Views;
    /// <summary>Counters for the report: member accesses rendered through a view.</summary>
    public int ViewHits;

    static JsonNode SkipValueCasts(JsonNode s)
    {
        while (true)
        {
            s = StripParens(s);
            if (KindOf(s) == "ImplicitCastExpr" && (string?)s["castKind"] is "LValueToRValue" or "NoOp") { s = s["inner"]![0]!; continue; }
            return s;
        }
    }

    /// <summary>`e` is a MemberExpr of a struct that has a view: the view expression of the struct it selects from and the member.</summary>
    bool TryMember(JsonNode e, out string view, out VMember m)
    {
        view = ""; m = null!;
        if (Views == null) return false;
        e = StripParens(e);
        if (KindOf(e) != "MemberExpr") return false;
        var f = ix.FieldsById.GetValueOrDefault((string)e["referencedMemberDecl"]!);
        if (f == null || f.Offset < 0) return false;
        var b = Inner(e, 0);
        bool arrow = e["isArrow"]?.GetValue<bool>() == true;
        var bt = TypeOf(b);
        var rt = arrow ? (bt.IsPtr ? bt.Elem : null) : bt;
        if (rt == null || rt.Kind != K.Record) return false;
        var rec = ix.FindRecord(rt.Name);
        if (rec == null || !Views.Avail(ix, rec, out var vi, out var cs)) return false;
        var mm = StructViews.Find(ix, rec, f);
        if (mm == null) return false;
        m = mm;
        view = ViewOf(b, arrow, vi, cs);
        return true;
    }

    /// <summary>The view of the struct that <paramref name="b"/> (pointer when <paramref name="arrow"/>, else the struct lvalue) denotes. Chains through pointer / nested-struct / array members.</summary>
    string ViewOf(JsonNode b, bool arrow, VInfo vi, string cs)
    {
        var s = SkipValueCasts(b);
        if (KindOf(s) == "MemberExpr" && TryMember(s, out var pv, out var pm))
        {
            if (arrow && pm.T.K == 'P' && pm.T.Rec == vi.Name && Views.Unique.Contains(vi.Name)) return $"{pv}.{pm.Name}";
            if (!arrow && pm.T.K == 'N' && Views.CsName(pm.T.Rec, pm.T.Fp) == cs) return $"{pv}.{pm.Name}";
        }
        else if (!arrow && KindOf(s) == "ArraySubscriptExpr" && TryElem(s, out var av, out var ai, out var el) && el.K == 'N' && Views.CsName(el.Rec, el.Fp) == cs)
            return $"{av}[{ai}]";
        return $"new {cs}({(arrow ? Value(b) : Addr(b))})";
    }

    /// <summary>`p->arr[i]` where arr is an array member of a struct with a view: the array view and the index.</summary>
    bool TryElem(JsonNode e, out string arr, out string idx, out TRef elem)
    {
        arr = idx = ""; elem = null!;
        if (Views == null) return false;
        e = StripParens(e);
        if (KindOf(e) != "ArraySubscriptExpr") return false;
        var b = StripParens(Inner(e, 0));
        if (KindOf(b) == "ImplicitCastExpr" && (string?)b["castKind"] == "ArrayToPointerDecay") b = StripParens(Inner(b, 0));
        else return false;
        if (!TryMember(b, out var v, out var m) || m.T.K != 'A') return false;
        arr = $"{v}.{m.Name}";
        idx = $"(int)({Value(Inner(e, 1))})";
        elem = m.T.Elem!;
        return true;
    }

    /// <summary>Read and store text for a scalar / pointer lvalue that has a view, or false.</summary>
    bool TryViewAccess(JsonNode lv, CType t, out string read, out Func<string, string> write)
    {
        read = ""; write = null!;
        if (Views == null) return false;
        lv = StripParens(lv);
        if (TryMember(lv, out var v, out var m))
        {
            bool ok = m.T.K == 'S' ? m.T.Cs == t.Cs : t.IsPtr && m.T.K is 'P' or 'Q' or 'R';
            if (!ok) return false;
            var vn = m.ValueName; var sn = m.SetName(vn);
            read = $"{v}.{vn}";
            write = val => $"{v}.{sn}({val})";
            ViewHits++;
            return true;
        }
        if (TryElem(lv, out var av, out var ai, out var el))
        {
            bool ok = el.K == 'S' ? el.Cs == t.Cs : t.IsPtr && el.K is 'P' or 'Q' or 'R';
            if (!ok) return false;
            read = $"{av}[{ai}]";
            write = val => $"{av}.Set({ai}, {val})";
            ViewHits++;
            return true;
        }
        return false;
    }
}
