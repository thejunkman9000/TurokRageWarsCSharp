using Trw.Game.PortHud;

namespace Trw.Game.Tests;

using HudView = Trw.Game.PortHud.HudView;      // Trw.Structs has a struct view of the same name in the enclosing namespace Trw.Game

/// <summary>The port HUD's layout format: expressions, text formats, parsing, element placement (docs/HUD.md). No ROM, RAM or GPU needed.</summary>
public class HudLayoutTests
{
    private static HudValueTable T(params (string k, double v)[] kv) { var t = new HudValueTable(); foreach (var (k, v) in kv) t[k] = v; return t; }

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("0x1FA", 0x1FA)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("10 % 4", 2)]
    [InlineData("7 / 0", 0)]
    [InlineData("-3 * -2", 6)]
    [InlineData("!0", 1)]
    [InlineData("!5", 0)]
    [InlineData("2 < 3 && 3 <= 3", 1)]
    [InlineData("2 > 3 || 4 >= 5", 0)]
    [InlineData("1 == 1 && 2 != 3", 1)]
    [InlineData("true ? 4 : 5", 4)]
    [InlineData("0 ? 4 : 1 ? 6 : 7", 6)]
    [InlineData("min(3, 8) + max(3, 8)", 11)]
    [InlineData("abs(-2) + floor(2.7) + ceil(2.1) + round(2.5)", 10)]
    [InlineData("clamp(15, 0, 10)", 10)]
    public void Expressions(string src, double want) => Assert.Equal(want, HudExpr.Parse(src).Eval(new HudValueTable()), 6);

    [Fact]
    public void ExpressionNamesAndValues()
    {
        var e = HudExpr.Parse("hudShown && !hideHud && ammo1.shown");
        Assert.Equal(new[] { "ammo1.shown", "hideHud", "hudShown" }, e.Names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        Assert.True(e.True(T(("hudShown", 1), ("hideHud", 0), ("ammo1.shown", 1))));
        Assert.False(e.True(T(("hudShown", 1), ("hideHud", 1), ("ammo1.shown", 1))));
        Assert.False(e.True(T()));                                     // unknown names are 0
        Assert.Equal(0x204, HudExpr.Parse("teams ? 0x204 : 0x1FA").Eval(T(("teams", 1))));
        Assert.Equal(-4.5, HudExpr.Parse("-3*kh").Eval(T(("kh", 1.5))), 6);
    }

    [Theory]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("1 ? 2")]
    [InlineData("foo(1)")]
    [InlineData("min(1)")]
    [InlineData("3 $ 4")]
    public void BadExpressions(string src) => Assert.Throws<FormatException>(() => HudExpr.Parse(src));

    [Fact]
    public void Formats()
    {
        var v = T(("frags", 7), ("fraglimit", 20), ("clock", 125.2), ("health", 99.9), ("x", -3));
        Assert.Equal("FRAGS 7/20", new HudFormat("FRAGS {frags}/{fraglimit}").Format(v));
        Assert.Equal("2:06", new HudFormat("{clock:time}").Format(v));
        Assert.Equal("0:02:06", new HudFormat("{clock:htime}").Format(v));
        Assert.Equal("099.9", new HudFormat("{health:000.0}").Format(v));
        Assert.Equal("99", new HudFormat("{health}").Format(v));               // integers by default, truncated
        Assert.Equal("{-3}", new HudFormat("{{{x}}}").Format(v));
        Assert.Equal("yes", new HudFormat("{frags > 5 ? 1 : 0:yes;no;no}").Format(v));    // the format colon follows the ternary
        Assert.Throws<FormatException>(() => new HudFormat("{frags"));
        Assert.Throws<FormatException>(() => new HudFormat("frags}"));
    }

    [Theory]
    [InlineData("#FF8000", 255, 128, 0, 255)]
    [InlineData("#11223344", 0x11, 0x22, 0x33, 0x44)]
    [InlineData("white", 255, 255, 255, 255)]
    public void Colours(string s, int r, int g, int b, int a) => Assert.Equal(new HudColor((byte)r, (byte)g, (byte)b, (byte)a), HudColor.Parse(s));

    [Fact]
    public void ShippedLayoutParses()
    {
        var l = HudLayout.Load(RepoFile("src/Trw.Game/Content/Hud/hud.json"));
        Assert.Empty(l.Warnings);
        var ids = l.Elements.Select(e => e.Id).ToList();
        foreach (var id in new[] { "health", "frags", "frags-icon", "trial", "trial-icon", "ammo1", "ammo1-icon", "clock-min", "clock-sec", "game-view", "game-shared" }) Assert.Contains(id, ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(l.Elements.Where(e => e.Id.StartsWith("example")), e => Assert.False(e.Show));
        Assert.True(l.Elements.Single(e => e.Id == "clock-min").Shared);
    }

    [Fact]
    public void LayoutErrorsAreReportedPerElement()
    {
        var l = HudLayout.Parse("""
            // comments and trailing commas are fine
            { "elements": [
                { "kind": "number", "id": "ok", "value": "health", },
                { "kind": "wobble", "id": "a" },
                { "kind": "number", "id": "b" },
                { "kind": "text", "id": "c", "text": "{1 +}" },
                { "kind": "game", "id": "d", "match": ["nope"] },
                { "kind": "sprite", "id": "e", "sprite": 506, "anchor": "middle" },
                { "kind": "bar", "id": "f", "value": "health", "players": [5] },
            ] }
            """);
        Assert.Equal(new[] { "ok" }, l.Elements.Select(e => e.Id));
        Assert.Equal(6, l.Warnings.Count);
        Assert.Contains(l.Warnings, w => w.Contains("unknown kind \"wobble\""));
        Assert.Contains(l.Warnings, w => w.Contains("\"value\" missing"));
        Assert.Throws<FormatException>(() => HudLayout.Parse("[1, 2]"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => HudLayout.Parse("{ \"elements\": [ "));
    }

    [Fact]
    public void NewKindsRegisterInOneLine()
    {
        HudLayout.Kinds["test-kind"] = () => new TestElement();
        try
        {
            var l = HudLayout.Parse("""{ "elements": [ { "kind": "test-kind", "id": "t", "label": "hi" } ] }""");
            Assert.Equal("hi", Assert.IsType<TestElement>(Assert.Single(l.Elements)).Label);
        }
        finally { HudLayout.Kinds.Remove("test-kind"); }
    }

    private sealed class TestElement : HudElement
    {
        public string Label = "";
        public override void Parse(System.Text.Json.JsonElement e) => Label = HudLayout.Str(e, "label") ?? "";
        public override void Emit(HudContext c) { }
    }

    [Fact]
    public void PlayerCountsAndConditions()
    {
        var l = HudLayout.Parse("""{ "elements": [ { "kind": "rect", "id": "r", "players": [2, 3], "when": "health < 50" } ] }""");
        var e = l.Elements[0];
        var ctx = Ctx(new HudView(0, 0, 0, 0, 480, 178.5f, 0), ("health", 20));
        Assert.False(e.Visible(ctx, 1));
        Assert.True(e.Visible(ctx, 2));
        Assert.False(e.Visible(ctx, 4));
        Assert.False(e.Visible(Ctx(new HudView(0, 0, 0, 0, 480, 178.5f, 0), ("health", 80)), 2));
    }

    private static HudContext Ctx(HudView view, params (string k, double v)[] kv) =>
        new() { View = view, Values = new HudValues(view, 1, kv.Select(p => new KeyValuePair<string, double>(p.k, p.v))), Canvas = new HudCanvas() };

    [Fact]
    public void AnchorsFollowTheViewAtTheGamesProportions()
    {
        // a bar anchored bottom-left, 16 frame units in, 80x5: in 1 player it is 80x5 pixels; in a 2-player half (480x178.5, bottom view) half as tall
        var l = HudLayout.Parse("""{ "elements": [ { "kind": "bar", "id": "b", "value": 50, "max": 100, "anchor": "bottom-left", "x": 16, "y": -14, "width": 80, "height": 5 } ] }""");
        var bar = l.Elements[0];
        var one = Ctx(new HudView(0, 0, 0, 0, 480, 360, 0));
        bar.Emit(one); one.Canvas.Flush();
        var (x0, y0, x1, y1) = Box(one.Canvas);
        Assert.Equal(16f, x0, 3); Assert.Equal(346f, y0, 3); Assert.Equal(56f, x1, 3); Assert.Equal(351f, y1, 3);   // half full: 40 of 80
        var half = Ctx(new HudView(1, 0, 0, 181.5f, 480, 178.5f, 0));
        bar.Emit(half); half.Canvas.Flush();
        (x0, y0, x1, y1) = Box(half.Canvas);
        Assert.Equal(16f, x0, 3); Assert.Equal(56f, x1, 3);
        Assert.Equal(181.5f + 178.5f - 14 * 0.4958f, y0, 1); Assert.Equal(y0 + 5 * 0.4958f, y1, 1);
    }

    [Fact]
    public void GameOriginsUseTheGamesFormulas()
    {
        // func_0021EED8: health number at healthX * 1.5 * sx + view.x - 3 * 1.5 (hi-res), healthY * 1.5 * sy + view.y + view.h - 4 * 1.5
        var view = new HudView(0, 0, 0, 0, 480, 360, 0);
        var ctx = Ctx(view, ("hires", 1), ("kh", 1.5), ("ks", 1), ("health.x", 40), ("health.y", -40), ("score.x", -120), ("score.y", 12));
        var health = new NumberElement { Origin = "health", X = HudExpr.Parse("-3*kh"), Y = HudExpr.Parse("-4*kh"), ScaleX = HudExpr.Parse("kh"), ScaleY = HudExpr.Parse("kh") };
        Assert.Equal((55.5f, 294f, 1.5f, 1.5f), health.Place(ctx));
        // the score's right-hand quirk: x = scoreX * ks * sx + view.x + view.w, 12 pixels further left when beyond 200
        Assert.Equal((480 - 120 - 12f, 12f, 1f, 1f), HudElement.Basis(ctx, "score", "top-left"));
        // a quadrant on the right: 240 wide, so 1 frame unit = 0.5 pixel; 480 - 60 + 240 = 300 -> 288
        var quad = Ctx(new HudView(1, 0, 240, 0, 240, 178.5f, 0), ("hires", 1), ("score.x", -120), ("score.y", 12));
        var (bx, by, ux, uy) = HudElement.Basis(quad, "score", "top-left");
        Assert.Equal(240 + 240 - 60 - 12f, bx, 3); Assert.Equal(12 * 178.5f / 360, by, 3); Assert.Equal(0.5f, ux, 3);
        // the crosshair is centred and not scaled by the view
        Assert.Equal((360f, 89.25f, 1f, 1f), HudElement.Basis(quad, "crosshair", "top-left"));
    }

    [Fact]
    public void CategoriesOfGameDraws()
    {
        Assert.Contains("crosshair", HudCategories.Of(0x2DA));
        Assert.Contains("sprite:0x2DA", HudCategories.Of(0x2DA));
        Assert.Contains("digits", HudCategories.Of(4));
        Assert.Contains("other", HudCategories.Of(0x999));
        Assert.Equal(new[] { "text", "other" }, HudCategories.Of(-1));
        var g = (GameElement)HudLayout.Parse("""{ "elements": [ { "kind": "game", "match": ["flash", "sprite:0x01FA"] } ] }""").Elements[0];
        Assert.True(g.Matches(HudCategories.Of(0x208)));
        Assert.True(g.Matches(HudCategories.Of(0x1FA)));
        Assert.False(g.Matches(HudCategories.Of(0x2DA)));
        Assert.Equal(new[] { 0x1FA }, g.SpriteIds);
    }

    private static (float, float, float, float) Box(HudCanvas c)
    {
        var v = c.Draws.SelectMany(d => d.Vertices).ToArray();
        float sx(float x) => (x + 1) * 0.5f * c.ScreenW;
        float sy(float y) => (1 - y) * 0.5f * c.ScreenH;
        return (v.Min(q => sx(q.X)), v.Min(q => sy(q.Y)), v.Max(q => sx(q.X)), v.Max(q => sy(q.Y)));
    }

    private static string RepoFile(string rel, [System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(here)!); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, rel);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(rel);
    }
}
