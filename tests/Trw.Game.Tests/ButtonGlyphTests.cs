using Trw.Game;
using Trw.Game.Glyphs;

namespace Trw.Game.Tests;

/// <summary>The button glyphs (docs/BUTTON_GLYPHS.md): the binding query over the port's input mapping, the glyph chosen per device, pad naming, rasterizing.</summary>
public class ButtonGlyphTests
{
    [Fact]
    public void KeyboardBindingsFollowTheMapping()
    {
        Assert.Equal(new PhysicalInput(InputSource.Key, "E"), Bindings.Best(N64Button.A, InputDeviceKind.KeyboardMouse, gameplay: false));
        Assert.Equal(new PhysicalInput(InputSource.Key, "Enter"), Bindings.Best(N64Button.Start, InputDeviceKind.KeyboardMouse, gameplay: false));
        Assert.Equal(new PhysicalInput(InputSource.Key, "X"), Bindings.Best(N64Button.B, InputDeviceKind.KeyboardMouse, gameplay: false));
        Assert.Equal(new PhysicalInput(InputSource.Key, "Space"), Bindings.Best(N64Button.R, InputDeviceKind.KeyboardMouse, gameplay: true));
        Assert.Equal(new PhysicalInput(InputSource.Key, "W"), Bindings.Best(N64Button.CUp, InputDeviceKind.KeyboardMouse, gameplay: true));
        // in the menus W moves the cursor; the C buttons are I J K L there
        Assert.Equal(new PhysicalInput(InputSource.Key, "I"), Bindings.Best(N64Button.CUp, InputDeviceKind.KeyboardMouse, gameplay: false));
        Assert.Equal(new PhysicalInput(InputSource.Key, "Right"), Bindings.Best(N64Button.StickRight, InputDeviceKind.KeyboardMouse, gameplay: false));
        // Z in play: the left mouse button is bound too, the key wins
        Assert.Equal(InputSource.Key, Bindings.Best(N64Button.Z, InputDeviceKind.KeyboardMouse, gameplay: true)!.Value.Source);
        Assert.Contains(new PhysicalInput(InputSource.Mouse, "Left"), Bindings.For(N64Button.Z, gameplay: true));
    }

    [Fact]
    public void PadBindingsFollowTheMapping()
    {
        Assert.Equal(new PhysicalInput(InputSource.Pad, "A"), Bindings.Best(N64Button.A, InputDeviceKind.Xbox, gameplay: false));
        Assert.Equal(new PhysicalInput(InputSource.Pad, "Start"), Bindings.Best(N64Button.Start, InputDeviceKind.Xbox, gameplay: false));
        Assert.Equal(new PhysicalInput(InputSource.Pad, "RT"), Bindings.Best(N64Button.Z, InputDeviceKind.Xbox, gameplay: true));
        Assert.Equal(new PhysicalInput(InputSource.Pad, "LSUp"), Bindings.Best(N64Button.CUp, InputDeviceKind.Xbox, gameplay: true));
    }

    [Fact]
    public void GlyphPerDevice()
    {
        Assert.Equal(new GlyphSet.Glyph("keyboard/keycap", "ENTER"), GlyphSet.Resolve(N64Button.Start, InputDeviceKind.KeyboardMouse, false));
        Assert.Equal(new GlyphSet.Glyph("xbox/a", null), GlyphSet.Resolve(N64Button.A, InputDeviceKind.Xbox, false));
        Assert.Equal(new GlyphSet.Glyph("playstation/cross", null), GlyphSet.Resolve(N64Button.A, InputDeviceKind.PlayStation, false));
        Assert.Equal(new GlyphSet.Glyph("playstation/options", null), GlyphSet.Resolve(N64Button.Start, InputDeviceKind.PlayStation, false));
        Assert.Equal(new GlyphSet.Glyph("playstation/r2", null), GlyphSet.Resolve(N64Button.Z, InputDeviceKind.PlayStation, true));
        Assert.Equal(new GlyphSet.Glyph("n64/c_up", null), GlyphSet.Resolve(N64Button.CUp, InputDeviceKind.N64, false));
        Assert.Equal(new GlyphSet.Glyph("touch/start", null), GlyphSet.Resolve(N64Button.Start, InputDeviceKind.Touch, false));
        Assert.Equal(new GlyphSet.Glyph("keyboard/arrows", null), GlyphSet.Resolve(N64Button.Stick, InputDeviceKind.KeyboardMouse, false));
        Assert.Equal("SPACE", GlyphSet.KeyLabel("Space"));
        Assert.Equal("NUM 8", GlyphSet.KeyLabel("NumPad8"));
        Assert.Equal("5", GlyphSet.KeyLabel("D5"));
    }

    [Theory]
    [InlineData("Xbox 360 Controller", "", InputDeviceKind.Xbox)]
    [InlineData("PS4 Controller", "", InputDeviceKind.PlayStation)]
    [InlineData("DualSense Wireless Controller", "", InputDeviceKind.PlayStation)]
    [InlineData("Wireless Controller", "030000004c050000cc09000000000000", InputDeviceKind.PlayStation)]
    [InlineData("Nintendo Switch Online N64 Controller", "", InputDeviceKind.N64)]
    [InlineData("Mayflash N64 Controller Adapter", "", InputDeviceKind.N64)]
    [InlineData("Generic USB Joystick", "", InputDeviceKind.Xbox)]
    public void PadNames(string name, string id, InputDeviceKind expected) => Assert.Equal(expected, DeviceReport.Classify(name, id));

    [Fact]
    public void EveryGlyphRasterizes()
    {
        foreach (var file in Directory.GetFiles(GlyphRaster.Root, "*.svg", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(GlyphRaster.Root, file).Replace('\\', '/')[..^4];
            var img = GlyphRaster.Get(name, name == "keyboard/keycap" ? "ENTER" : null, 48);
            Assert.True(img != null, name);
            Assert.Equal(48, img!.Texture.Height);
            Assert.Contains(img.Texture.Rgba.Where((_, i) => i % 4 == 3), a => a > 200);      // something opaque was drawn
        }
        // a longer label makes a wider keycap
        Assert.True(GlyphRaster.Get("keyboard/keycap", "SPACE", 48)!.Aspect > GlyphRaster.Get("keyboard/keycap", "E", 48)!.Aspect + 0.5f);
    }
}
