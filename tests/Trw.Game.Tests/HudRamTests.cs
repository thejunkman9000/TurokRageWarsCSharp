using Trw.Game.PortHud;
using Trw.Platform;

namespace Trw.Game.Tests;

/// <summary>The HUD's read-only RAM side on a hand-built guest memory: section tags from the camera list, the sprite lookup through the resource
/// cache, the sprite decode and its quarter-pixel placement. (One class: the tests share the guest memory.)</summary>
public class HudRamTests
{
    private const uint CamA = 0x80300000, CamB = 0x80301000, Data = 0x80340000;

    static HudRamTests()
    {
        if (!Mem.IsMapped(0x80000000)) Mem.Map(0x80000000, 0x800000);
        // two cameras in the list at 0x801450A8 (next at +4)
        Mem.Set(0x801450A8, CamA); Mem.Set(CamA + 4, CamB); Mem.Set(CamB + 4, 0u);
        // the HUD library D_8011FE88: key table (+0x9C) {?, n = 2, keys 4, 0x1FA}, offset table (+0x5C), base (+0x2C)
        Mem.Set(GameSprites.Library + 0x9C, 0x80310000u); Mem.Set(0x80310004, 2); Mem.Set(0x80310008, 4); Mem.Set(0x8031000C, 0x1FA);
        Mem.Set(GameSprites.Library + 0x5C, 0x80310100u); Mem.Set(0x80310104, 0x100); Mem.Set(0x80310108, 0x200); Mem.Set(0x8031010C, 0x300);
        Mem.Set(GameSprites.Library + 0x2C, 0x00700000u);
        // the resource cache: bucket of key 0x00700200 -> entry -> node -> data
        uint key = 0x00700200, buckets = 0x80320000, mask = 0xFF;
        Mem.Set(GameSprites.HashBuckets, buckets); Mem.Set(GameSprites.HashMask, mask);
        uint b = buckets + (((key << 5) ^ (key >> 1) ^ (key >> 9) ^ (key >> 17)) & mask) * 16;
        Mem.Set(b, key); Mem.Set(b + 4, 0x80330000u); Mem.Set(b + 0xC, 0u);
        Mem.Set(0x80330000, 0x80330100u); Mem.Set(0x80330100, Data);
        // sprite 0x1FA: one frame (kind 0) of one 2x2 cell: RGBA 5551 colour, then the 4-bit alpha plane
        Mem.Set(Data, (short)1); Mem.Set(Data + 8, 0x10); Mem.Set(Data + 12, (byte)0);
        uint tab = Data + 0x10;
        Mem.Set(tab, (short)1); Mem.Set(tab + 2, (short)1);
        Mem.Set(tab + 8, 2); Mem.Set(tab + 12, 2);
        Mem.Set(tab + 16, (short)unchecked((short)0xF801)); Mem.Set(tab + 18, (short)0x07C1); Mem.Set(tab + 20, (short)0x003F); Mem.Set(tab + 22, (short)0x0000);
        Mem.Set(tab + 24, (byte)0xF0); Mem.Set(tab + 25, (byte)0x8F);
    }

    [Fact]
    public void SectionsTagsAndSprites()
    {
        // tags: a camera's 2D projection (+0x448, +0x40 on odd frames) starts its section, its 3D projection (+0x380) ends it
        Assert.Equal(1, HudTagger.Marker(CamA + 0x448));
        Assert.Equal(2, HudTagger.Marker(CamB + 0x448 + 0x40));
        Assert.Equal(0, HudTagger.Marker(CamA + 0x380));
        Assert.Equal(0, HudTagger.Marker(CamB + 0x3C0));
        Assert.Equal(HudTagger.Shared, HudTagger.Marker(HudTagger.SharedRacer + 0x448));
        Assert.Null(HudTagger.Marker(0x80400000));

        // the lookup finds the data through the cache; an id that is not in the key table, or not loaded, is not found
        Assert.Equal(Data, GameSprites.Locate(0x1FA));
        Assert.Equal(0u, GameSprites.Locate(0x1FB));
        Assert.Equal(0u, GameSprites.Locate(4));                    // in the table, but its key is not in the cache

        var spr = GameSprites.Get(0x1FA);
        Assert.NotNull(spr);
        var cell = Assert.Single(spr!.Get(0)!.Cells);
        Assert.Equal((2, 2), (cell.W, cell.H));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 0, 0, 0 }, cell.Tex0!.Rgba);       // red, green, blue, clear
        Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 0, 0, 0, 136, 136, 136, 136, 255, 255, 255, 255 }, cell.Tex1!.Rgba);   // I4: 15, 0, 8, 15
        Assert.Equal(0x1FA, GameSprites.Owner(cell.Colour, new[] { 4, 0x1FA }));
        Assert.Equal(-1, GameSprites.Owner(0x80400000, new[] { 4, 0x1FA }));

        // drawn like func_002AA724: corners truncated to quarter pixels, the texel step to 1/1024
        var view = new HudView(0, CamA, 0, 0, 480, 360, 0);
        var ctx = new HudContext { View = view, Values = new HudValues(view, 1, Array.Empty<KeyValuePair<string, double>>()), Canvas = new HudCanvas() };
        Assert.True(SpriteElement.DrawSprite(ctx, 0x1FA, 0, 10.3f, 20f, 1.5f, 1.5f, HudColor.White, 1f));
        ctx.Canvas.Flush();
        var v = Assert.Single(ctx.Canvas.Draws).Vertices;
        static float X(float x) => (x + 1) * 0.5f * 480;
        static float Y(float y) => (1 - y) * 0.5f * 360;
        Assert.Equal(10.25f, v.Min(q => X(q.X)), 3);                // (int)(10.3 * 4) / 4
        Assert.Equal(13.25f, v.Max(q => X(q.X)), 3);                // (int)((10.3 + 3) * 4) / 4
        Assert.Equal(20f, v.Min(q => Y(q.Y)), 3); Assert.Equal(23f, v.Max(q => Y(q.Y)), 3);
        Assert.Equal(3f * (int)(1024 / 1.5f) / 1024f / 2, v.Max(q => q.U), 4);
        Assert.True(ctx.Canvas.Draws[0].State.Linear);              // scaled: bilinear like the game's draw; 1:1 rectangles are point sampled
        Assert.Same(cell.Tex0, ctx.Canvas.Draws[0].State.Tex0);
    }
}
