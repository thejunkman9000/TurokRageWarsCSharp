using System.Globalization;
using System.Text.Json;

namespace Trw.Game.PortHud;

/// <summary>
/// The HUD layout file (docs/HUD.md): <c>{ "version": 1, "elements": [ { "kind": "number", ... }, ... ] }</c>, JSON with comments and trailing commas allowed.
/// Every element names its <c>kind</c>; <see cref="Kinds"/> maps kinds to classes, so a new kind is one small class plus one line here.
/// </summary>
public sealed class HudLayout
{
    public int Version = 1;
    public readonly List<HudElement> Elements = new();
    public readonly List<string> Warnings = new();
    public string Source = "";

    /// <summary>Element kinds: name -&gt; factory.</summary>
    public static readonly Dictionary<string, Func<HudElement>> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["number"] = () => new NumberElement(),
        ["text"] = () => new TextElement(),
        ["sprite"] = () => new SpriteElement(),
        ["bar"] = () => new BarElement(),
        ["rect"] = () => new RectElement(),
        ["crosshair"] = () => new CrosshairElement(),
        ["game"] = () => new GameElement(),
    };

    public static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static HudLayout Load(string path)
    {
        var l = Parse(File.ReadAllText(path));
        l.Source = path;
        return l;
    }

    /// <summary>Parses a layout. Errors in one element are reported in <see cref="Warnings"/> and that element is left out; a broken file throws.</summary>
    public static HudLayout Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, JsonOptions);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("the layout must be a JSON object with an \"elements\" array");
        var l = new HudLayout();
        if (root.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number) l.Version = ver.GetInt32();
        if (!root.TryGetProperty("elements", out var els) || els.ValueKind != JsonValueKind.Array) throw new FormatException("\"elements\" array missing");
        int n = 0;
        foreach (var e in els.EnumerateArray())
        {
            n++;
            string where = $"element {n}";
            try
            {
                if (e.ValueKind != JsonValueKind.Object) throw new FormatException("not an object");
                string kind = Str(e, "kind") ?? throw new FormatException("\"kind\" missing");
                if (!Kinds.TryGetValue(kind, out var make)) throw new FormatException($"unknown kind \"{kind}\" (known: {string.Join(", ", Kinds.Keys)})");
                var el = make();
                el.Kind = kind;
                el.Id = Str(e, "id") ?? $"{kind}{n}";
                where = $"element {n} ({el.Id})";
                el.ParseCommon(e);
                el.Parse(e);
                l.Elements.Add(el);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException or KeyNotFoundException)
            {
                l.Warnings.Add($"{where}: {ex.Message}");
            }
        }
        return l;
    }

    // ---- JSON helpers shared by the element classes

    public static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null } : null;

    public static bool Bool(JsonElement e, string name, bool def) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Number => v.GetDouble() != 0, _ => def } : def;

    public static int Int(JsonElement e, string name, int def)
    {
        string? s = Str(e, name);
        if (s == null) return def;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToInt32(s[2..], 16);
        return (int)double.Parse(s, CultureInfo.InvariantCulture);
    }

    /// <summary>A number or an expression string.</summary>
    public static HudExpr Expr(JsonElement e, string name, double def) => e.TryGetProperty(name, out var v) ? ExprOf(v) : HudExpr.Constant(def);

    public static HudExpr ExprOf(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => HudExpr.Constant(v.GetDouble()),
        JsonValueKind.String => HudExpr.Parse(v.GetString()!),
        JsonValueKind.True => HudExpr.Constant(1),
        JsonValueKind.False => HudExpr.Constant(0),
        _ => throw new FormatException($"number or expression expected, got {v.ValueKind}"),
    };

    public static HudColor Color(JsonElement e, string name, HudColor def)
    {
        if (!e.TryGetProperty(name, out var v)) return def;
        if (v.ValueKind == JsonValueKind.String) return HudColor.Parse(v.GetString()!);
        if (v.ValueKind == JsonValueKind.Array)
        {
            var a = v.EnumerateArray().Select(x => (byte)Math.Clamp(x.GetDouble(), 0, 255)).ToArray();
            if (a.Length is 3 or 4) return new HudColor(a[0], a[1], a[2], a.Length == 4 ? a[3] : (byte)255);
        }
        throw new FormatException($"\"{name}\": colour expected (\"#RRGGBB\", \"#RRGGBBAA\", a name or [r,g,b,a])");
    }
}
