using System.Globalization;
using System.Text;

namespace Trw.Game.PortHud;

/// <summary>Named HUD values of one view (health, frags, clock, ...): <see cref="HudValues"/> reads them from guest RAM, tests use a dictionary.</summary>
public interface IHudValues
{
    /// <summary>The value of <paramref name="name"/>, false when the name is unknown (an unknown name evaluates to 0).</summary>
    bool TryGet(string name, out double value);
}

/// <summary>A dictionary of values (tests, constants).</summary>
public sealed class HudValueTable : Dictionary<string, double>, IHudValues
{
    public HudValueTable() : base(StringComparer.OrdinalIgnoreCase) { }
    public bool TryGet(string name, out double value) => TryGetValue(name, out value);
}

/// <summary>
/// The layout's little expression language: numbers (<c>12</c>, <c>0x1FA</c>, <c>0.75</c>), value names (<c>health</c>, <c>ammo1.shown</c>), <c>true</c> / <c>false</c>,
/// <c>+ - * / %</c>, comparisons <c>== != &lt; &lt;= &gt; &gt;=</c>, <c>! &amp;&amp; ||</c>, <c>c ? a : b</c>, parentheses and the functions
/// <c>min max abs floor ceil round clamp(v,lo,hi)</c>. Booleans are numbers (0 = false). Compiled once, evaluated per view and frame.
/// </summary>
public sealed class HudExpr
{
    private readonly Func<IHudValues, double> eval;
    public string Source { get; }
    /// <summary>The value names the expression reads (for diagnostics and tests).</summary>
    public IReadOnlyCollection<string> Names { get; }

    private HudExpr(string src, Func<IHudValues, double> f, HashSet<string> names) { Source = src; eval = f; Names = names; }

    public double Eval(IHudValues v) => eval(v);
    public bool True(IHudValues v) => eval(v) != 0;

    public static HudExpr Constant(double c) => new(c.ToString(CultureInfo.InvariantCulture), _ => c, new HashSet<string>());

    /// <summary>Parses <paramref name="src"/>; throws <see cref="FormatException"/> with the position of the error.</summary>
    public static HudExpr Parse(string src)
    {
        var p = new Parser(src);
        var f = p.Ternary();
        p.SkipWs();
        if (!p.End) throw p.Error("unexpected '" + p.Rest + "'");
        return new HudExpr(src, f, p.Names);
    }

    private sealed class Parser
    {
        private readonly string s; private int i;
        public readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);
        public Parser(string s) { this.s = s; }
        public bool End => i >= s.Length;
        public string Rest => s[i..];
        public FormatException Error(string m) => new($"expression \"{s}\", position {i + 1}: {m}");
        public void SkipWs() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
        private bool Eat(string t) { SkipWs(); if (string.CompareOrdinal(s, i, t, 0, t.Length) == 0) { i += t.Length; return true; } return false; }
        private bool Peek(string t) { SkipWs(); return string.CompareOrdinal(s, i, t, 0, t.Length) == 0; }

        public Func<IHudValues, double> Ternary()
        {
            var c = Or();
            if (!Eat("?")) return c;
            var a = Ternary();
            if (!Eat(":")) throw Error("':' expected");
            var b = Ternary();
            return v => c(v) != 0 ? a(v) : b(v);
        }
        private Func<IHudValues, double> Or()
        {
            var l = And();
            while (Eat("||")) { var a = l; var r = And(); l = v => a(v) != 0 || r(v) != 0 ? 1 : 0; }
            return l;
        }
        private Func<IHudValues, double> And()
        {
            var l = Cmp();
            while (Eat("&&")) { var a = l; var r = Cmp(); l = v => a(v) != 0 && r(v) != 0 ? 1 : 0; }
            return l;
        }
        private Func<IHudValues, double> Cmp()
        {
            var l = Add();
            foreach (var op in new[] { "==", "!=", "<=", ">=", "<", ">" })
            {
                if (!Eat(op)) continue;
                var a = l; var r = Add();
                return op switch
                {
                    "==" => v => a(v) == r(v) ? 1 : 0, "!=" => v => a(v) != r(v) ? 1 : 0,
                    "<=" => v => a(v) <= r(v) ? 1 : 0, ">=" => v => a(v) >= r(v) ? 1 : 0,
                    "<" => v => a(v) < r(v) ? 1 : 0, _ => v => a(v) > r(v) ? 1 : 0,
                };
            }
            return l;
        }
        private Func<IHudValues, double> Add()
        {
            var l = Mul();
            while (true)
            {
                if (Eat("+")) { var a = l; var r = Mul(); l = v => a(v) + r(v); }
                else if (Eat("-")) { var a = l; var r = Mul(); l = v => a(v) - r(v); }
                else return l;
            }
        }
        private Func<IHudValues, double> Mul()
        {
            var l = Unary();
            while (true)
            {
                if (Eat("*")) { var a = l; var r = Unary(); l = v => a(v) * r(v); }
                else if (Eat("/")) { var a = l; var r = Unary(); l = v => { double d = r(v); return d == 0 ? 0 : a(v) / d; }; }
                else if (Eat("%")) { var a = l; var r = Unary(); l = v => { double d = r(v); return d == 0 ? 0 : a(v) % d; }; }
                else return l;
            }
        }
        private Func<IHudValues, double> Unary()
        {
            if (Peek("!=")) throw Error("operand expected");
            if (Eat("!")) { var a = Unary(); return v => a(v) == 0 ? 1 : 0; }
            if (Eat("-")) { var a = Unary(); return v => -a(v); }
            if (Eat("+")) return Unary();
            return Primary();
        }
        private Func<IHudValues, double> Primary()
        {
            SkipWs();
            if (End) throw Error("operand expected");
            if (Eat("("))
            {
                var e = Ternary();
                if (!Eat(")")) throw Error("')' expected");
                return e;
            }
            char c = s[i];
            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int st = i;
                if (c == '0' && i + 1 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X'))
                {
                    i += 2; while (i < s.Length && Uri.IsHexDigit(s[i])) i++;
                    if (i == st + 2) throw Error("hex digits expected");
                    double h = Convert.ToInt64(s[(st + 2)..i], 16);
                    return _ => h;
                }
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (!double.TryParse(s[st..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) throw Error("bad number");
                return _ => d;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int st = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.')) i++;
                string name = s[st..i];
                if (name.Equals("true", StringComparison.OrdinalIgnoreCase)) return _ => 1;
                if (name.Equals("false", StringComparison.OrdinalIgnoreCase)) return _ => 0;
                if (Eat("("))
                {
                    var args = new List<Func<IHudValues, double>>();
                    if (!Eat(")"))
                    {
                        do args.Add(Ternary()); while (Eat(","));
                        if (!Eat(")")) throw Error("')' expected");
                    }
                    return Function(name, args);
                }
                Names.Add(name);
                return v => v.TryGet(name, out var x) ? x : 0;
            }
            throw Error($"unexpected '{c}'");
        }
        private Func<IHudValues, double> Function(string name, List<Func<IHudValues, double>> a)
        {
            void Need(int n) { if (a.Count != n) throw Error($"{name}() takes {n} argument(s)"); }
            switch (name.ToLowerInvariant())
            {
                case "min": Need(2); return v => Math.Min(a[0](v), a[1](v));
                case "max": Need(2); return v => Math.Max(a[0](v), a[1](v));
                case "abs": Need(1); return v => Math.Abs(a[0](v));
                case "floor": Need(1); return v => Math.Floor(a[0](v));
                case "ceil": Need(1); return v => Math.Ceiling(a[0](v));
                case "round": Need(1); return v => Math.Round(a[0](v), MidpointRounding.AwayFromZero);
                case "clamp": Need(3); return v => Math.Clamp(a[0](v), a[1](v), Math.Max(a[1](v), a[2](v)));
                default: throw Error($"unknown function {name}()");
            }
        }
    }
}

/// <summary>
/// A text with placeholders: <c>"FRAGS {frags} / {fraglimit}"</c>. A placeholder is <c>{expression}</c> or <c>{expression:format}</c>; the format is a .NET
/// numeric format (<c>0</c>, <c>00</c>, <c>F1</c>, <c>+0;-0</c>) or <c>time</c> (seconds as m:ss) / <c>htime</c> (h:mm:ss). <c>{{</c> and <c>}}</c> are literal braces.
/// Values print as integers (truncated toward zero) unless a format says otherwise.
/// </summary>
public sealed class HudFormat
{
    private readonly List<(string? lit, HudExpr? expr, string? fmt)> parts = new();
    public string Source { get; }

    public HudFormat(string src)
    {
        Source = src;
        var lit = new StringBuilder();
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            if (c == '{' && i + 1 < src.Length && src[i + 1] == '{') { lit.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < src.Length && src[i + 1] == '}') { lit.Append('}'); i++; continue; }
            if (c == '}') throw new FormatException($"text \"{src}\": '}}' without '{{' (write }}}} for a brace)");
            if (c != '{') { lit.Append(c); continue; }
            int end = src.IndexOf('}', i + 1);
            if (end < 0) throw new FormatException($"text \"{src}\": '{{' without '}}'");
            string body = src[(i + 1)..end];
            // the format follows the last ':' that is not part of a ?: ternary inside the expression
            int colon = FormatColon(body);
            string e = colon >= 0 ? body[..colon] : body, f = colon >= 0 ? body[(colon + 1)..] : "";
            if (lit.Length > 0) { parts.Add((lit.ToString(), null, null)); lit.Clear(); }
            parts.Add((null, HudExpr.Parse(e), f.Length > 0 ? f : null));
            i = end;
        }
        if (lit.Length > 0) parts.Add((lit.ToString(), null, null));
    }

    private static int FormatColon(string body)
    {
        int q = 0;
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == '?') q++;
            else if (body[i] == ':') { if (q > 0) q--; else return i; }
        }
        return -1;
    }

    public string Format(IHudValues v)
    {
        var sb = new StringBuilder();
        foreach (var (lit, expr, fmt) in parts)
        {
            if (lit != null) { sb.Append(lit); continue; }
            double x = expr!.Eval(v);
            sb.Append(FormatValue(x, fmt));
        }
        return sb.ToString();
    }

    public static string FormatValue(double x, string? fmt)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) x = 0;
        if (fmt == null) return ((long)x).ToString(CultureInfo.InvariantCulture);
        if (fmt.Equals("time", StringComparison.OrdinalIgnoreCase))
        {
            long s = Math.Max(0, (long)Math.Ceiling(x));
            return $"{s / 60}:{s % 60:D2}";
        }
        if (fmt.Equals("htime", StringComparison.OrdinalIgnoreCase))
        {
            long s = Math.Max(0, (long)Math.Ceiling(x));
            return $"{s / 3600}:{s / 60 % 60:D2}:{s % 60:D2}";
        }
        return x.ToString(fmt, CultureInfo.InvariantCulture);
    }
}
