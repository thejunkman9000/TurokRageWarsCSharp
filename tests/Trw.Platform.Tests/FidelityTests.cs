using Trw.Platform;
using Trw.Platform.Gfx;
using Xunit;

namespace Trw.Platform.Tests;

/// <summary>Tests that touch the shared guest memory above 0x110 run one after the other (MemTests / CpuTests only use the first 0x108 bytes).</summary>
[CollectionDefinition("GuestMemoryHigh", DisableParallelization = true)]
public class GuestMemoryHighCollection { }

public class RdpMathTests
{
    [Theory]
    [InlineData(0, 1f)]
    [InlineData(1, 0.5f)]
    [InlineData(5, 1f / 32)]
    [InlineData(10, 1f / 1024)]
    [InlineData(11, 32f)]       // 16 - 11 = 5: s << 5
    [InlineData(15, 2f)]
    public void ShiftFieldIsARightShiftUpTo10AndALeftShiftBeyond(int shift, float factor) => Assert.Equal(factor, RdpMath.ShiftFactor(shift));

    [Theory]
    [InlineData(0, 0, 2)]   // no mask: clamp whatever the flags say
    [InlineData(0, 3, 2)]
    [InlineData(5, 0, 0)]   // wrap
    [InlineData(5, 1, 1)]   // mirror
    [InlineData(5, 2, 2)]   // clamp
    [InlineData(5, 3, 2)]   // clamp + mirror = clamp
    public void AddressModeFollowsMaskAndClampMirrorBits(int mask, int cm, int expected) => Assert.Equal(expected, RdpMath.AddressMode(mask, cm));

    [Fact]
    public void FogBlenderIsRecognisedOnlyInTheCycleOneColourFogForm()
    {
        uint shade = (3u << 30) | (2u << 26);                 // G_RM_FOG_SHADE_A: P = fog colour, A = shade alpha, M = input, B = 1-A
        uint fogA = (3u << 30) | (1u << 26);                  // G_RM_FOG_PRIM_A: A = fog alpha
        Assert.True(RdpMath.FogMix(0, shade, true, out bool a1)); Assert.False(a1);
        Assert.True(RdpMath.FogMix(0, fogA, true, out bool a2)); Assert.True(a2);
        Assert.False(RdpMath.FogMix(0, shade, false, out _));                  // TRW_NOFOG
        Assert.False(RdpMath.FogMix(2u << 20, shade, true, out _));            // copy cycle
        Assert.False(RdpMath.FogMix(0, shade | (1u << 22), true, out _));      // second input is not the framebuffer colour
        Assert.False(RdpMath.FogMix(0, (3u << 30) | (3u << 26), true, out _)); // alpha input 3 (zero)
    }

    [Fact]
    public void FogFactorIsClampedToTheByteRange()
    {
        Assert.Equal(0f, RdpMath.FogAlpha(-1f, 100, 0));
        Assert.Equal(1f, RdpMath.FogAlpha(1f, 1000, 0));
        Assert.Equal(0.5f, RdpMath.FogAlpha(0.5f, 255, 0), 3);
        Assert.Equal(100f / 255f, RdpMath.FogAlpha(0f, 3, 100), 5);
    }

    [Theory]
    [InlineData(0u, 0u, -1f)]
    [InlineData(2u << 20, 0u, 0.01f)]               // copy cycle
    [InlineData(0u, 1u, 0.5f)]                       // alpha compare against the blend colour
    [InlineData(0u, 0x1000u, 0.3f)]                  // coverage-from-alpha / alpha-compare dither bits
    [InlineData(0u, 0x4001u, 0.02f)]                 // blender on lowers an existing test to 0.02
    [InlineData(0u, 0x4000u, -1f)]                   // blender on without a test: none
    public void AlphaThresholdSelection(uint omH, uint omL, float expected) => Assert.Equal(expected, RdpMath.AlphaThreshold(omH, omL));

    [Fact]
    public void DepthClearQuadNeverAlphaTests() => Assert.Equal(-1f, RdpMath.AlphaThreshold(2u << 20, 0x1001, true));

    [Theory]
    [InlineData(0u, 0x4000u, true)]
    [InlineData(1u << 20, 0x4000u, true)]
    [InlineData(2u << 20, 0x4000u, false)]
    [InlineData(0u, 0x0000u, false)]
    public void BlendNeedsForceBlenderAndNoCopyCycle(uint omH, uint omL, bool expected) => Assert.Equal(expected, RdpMath.BlendEnabled(omH, omL));

    [Fact]
    public void DepthStateComesFromGeometryModeAndZModes()
    {
        Assert.Equal(0, RdpMath.DepthStateIndex(0, 0x30));       // no G_ZBUFFER: nothing
        Assert.Equal(3, RdpMath.DepthStateIndex(1, 0x30));
        Assert.Equal(2, RdpMath.DepthStateIndex(1, 0x10));
        Assert.Equal(1, RdpMath.DepthStateIndex(1, 0x20));
    }

    [Fact]
    public void ScissorMapsTenTwoUnitsToTheRenderTarget()
    {
        Assert.Equal((0, 0, 1920, 1440), RdpMath.ScissorRect(0, 0, 480 * 4, 360 * 4, 480, 360, 1920, 1440));
        Assert.Equal((960, 720, 960, 720), RdpMath.ScissorRect(240 * 4, 180 * 4, 480 * 4, 360 * 4, 480, 360, 1920, 1440));
        Assert.Equal((100, 100, 0, 0), RdpMath.ScissorRect(100, 100, 0, 0, 480, 360, 1920, 1440));     // inverted rectangle collapses instead of going negative
    }

    [Fact]
    public void OneToOneRectsAreSampledWithPointFiltering()
    {
        Assert.True(RdpMath.ForcePointForOneToOneRect(1024, 1024));
        Assert.False(RdpMath.ForcePointForOneToOneRect(512, 1024));
    }
}

[Collection("GuestMemoryHigh")]
public class DisplayListFidelityTests
{
    static DisplayListFidelityTests() { if (!Mem.IsMapped(0x80000000)) Mem.Map(0x80000000, 0x1000); }

    private const uint Task = 0x80000040, Dl = 0x80000200, Verts = 0x80000400;
    private static uint cursor;
    private static void Begin(uint at) { cursor = at; }
    private static void Op(uint w0, uint w1) { Mem.Set(cursor, w0); Mem.Set(cursor + 4, w1); cursor += 8; }
    private static IReadOnlyList<GfxCommand> Run(uint dl = Dl)
    {
        Mem.Set(Task + 0x30, dl); Mem.Set(Task + 0x34, 0x100u);
        return new DisplayListInterpreter().Run(Task);
    }

    private static void Vertex(uint at, short x, short y, short z, short s, short t)
    {
        Mem.Set(at, x); Mem.Set(at + 2, y); Mem.Set(at + 4, z); Mem.Set(at + 6, (short)0); Mem.Set(at + 8, s); Mem.Set(at + 10, t);
        Mem.Set(at + 12, (byte)255); Mem.Set(at + 13, (byte)255); Mem.Set(at + 14, (byte)255); Mem.Set(at + 15, (byte)255);
    }

    [Fact]
    public void PartialDepthFillBecomesAFarPlaneQuadAndFullFillClearsTheBuffer()
    {
        Begin(Dl);
        Op(0xFF000000 | 479, 0x80000800u);                 // colour image == depth image: fills write depth
        Op(0xFE000000, 0x80000800u);
        Op(0xF7000000, 0xFFFCFFFCu);
        Op(0xF6000000 | ((239u * 4) << 12) | (177u * 4), 0);            // top-left 240x178 quadrant (a 4-player view)
        Op(0xF6000000 | ((479u * 4) << 12) | (359u * 4), 0);            // the whole screen
        Op(0xDF000000, 0);
        var cmds = Run();
        Assert.Equal(2, cmds.Count);
        var d = Assert.IsType<DrawCommand>(cmds[0]);
        Assert.True(d.State.ZClear);
        Assert.Equal(6, d.Vertices.Length);
        Assert.All(d.Vertices, v => { Assert.Equal(1f, v.Z); Assert.Equal(1f, v.W); });
        Assert.Equal(-1f, d.Vertices.Min(v => v.X), 3); Assert.Equal(240f / 480 * 2 - 1, d.Vertices.Max(v => v.X), 3);
        Assert.IsType<ClearDepthCommand>(cmds[1]);
    }

    [Fact]
    public void ColourFillStillClearsTheColourBuffer()
    {
        Begin(Dl);
        Op(0xFF000000 | 479, 0x80000800u);
        Op(0xFE000000, 0x80000900u);
        Op(0xF7000000, 0xF800F800u);
        Op(0xE3000A01, 0x00300000u);                                    // G_SETOTHERMODE_H cycle type = FILL (only FILL mode writes the fill colour)
        Op(0xF6000000 | ((479u * 4) << 12) | (359u * 4), 0);
        Op(0xDF000000, 0);
        var c = Assert.IsType<ClearColorCommand>(Assert.Single(Run()));
        Assert.Equal(1f, c.R); Assert.Equal(0f, c.G);
    }

    private static DrawCommand TexturedTriangle(int shiftS, int shiftT)
    {
        Vertex(Verts, -1, -1, 0, 1024, 1024); Vertex(Verts + 16, 1, -1, 0, 1024, 1024); Vertex(Verts + 32, 0, 1, 0, 1024, 1024);
        Begin(Dl);
        Op(0xD7000002, 0x80008000u);                                                        // G_TEXTURE on, scale 0.5
        Op(0xF5000000 | (2u << 19) | (8u << 9), (5u << 14) | ((uint)shiftT << 10) | (5u << 4) | (uint)shiftS);   // 16-bit RGBA, 32x32, masks 5, shifts
        Op(0xF2000000, (124u << 12) | 124u);                                                // tile size 32x32
        Op(0x01000000 | (3u << 12) | 6u, Verts);                                            // load 3 vertices
        Op(0xD7000002, 0xFFFFFFFFu);                                                        // the scale changes AFTER the vertices were loaded
        Op(0x05000000 | (0u << 16) | (2u << 8) | 4u, 0);
        Op(0xDF000000, 0);
        return Assert.IsType<DrawCommand>(Run().Last());
    }

    [Fact]
    public void TextureScaleIsAppliedWhenTheVertexIsLoaded()
    {
        var d = TexturedTriangle(0, 0);
        Assert.Equal(0.5f, d.Vertices[0].U, 4);          // 1024 * 0.5 = 512 = 16 texels of 32
        Assert.Equal(0.5f, d.Vertices[0].V, 4);
    }

    [Fact]
    public void TileShiftScalesTheCoordinates()
    {
        var d = TexturedTriangle(1, 2);
        Assert.Equal(0.25f, d.Vertices[0].U, 4);         // s >> 1
        Assert.Equal(0.125f, d.Vertices[0].V, 4);        // t >> 2
    }

    [Fact]
    public void DisplayListBranchDoesNotReturnToTheCaller()
    {
        Begin(Dl + 0x100);
        Op(0xFE000000, 0x80000900u);
        Op(0xFF000000 | 479, 0x80000800u);
        Op(0xF7000000, 0xFFFFFFFFu);
        Op(0xE3000A01, 0x00300000u);                                    // cycle type = FILL
        Op(0xF6000000 | ((479u * 4) << 12) | (359u * 4), 0);
        Op(0xDF000000, 0);
        Begin(Dl);
        Op(0xDE010000, Dl + 0x100);                                  // G_DL branch
        Op(0xF6000000 | ((479u * 4) << 12) | (359u * 4), 0);          // must never run
        Op(0xDF000000, 0);
        Assert.IsType<ClearColorCommand>(Assert.Single(Run()));
    }

    [Fact]
    public void OneToOneTextureRectangleSamplesWithPointFilter()
    {
        Begin(Dl);
        Op(0xD7000002, 0xFFFFFFFFu);
        Op(0xF5000000 | (2u << 19) | (8u << 9), 0);
        Op(0xF2000000, (124u << 12) | 124u);
        Op(0xE2000000 | (12u << 8) | 1u, 2u << 12);                  // SETOTHERMODE_H: bilinear filter on
        Op(0xE4000000 | (40u * 4 << 12) | (40u * 4), (0u << 24) | (8u * 4 << 12) | (8u * 4));
        Op(0xE1000000, 0);
        Op(0xF1000000 | 1024u << 16 | 1024u, 1024u << 16 | 1024u);
        Op(0xDF000000, 0);
        var cmds = Run();
        var d = Assert.IsType<DrawCommand>(cmds.Last());
        Assert.False(d.State.Linear);
    }
}

public class AudioInterfaceTests
{
    [Fact]
    public void AiFifoReportsFullAfterTwoFramesPlusHundredMilliseconds()
    {
        var f = new AiFifo(); f.SetFrequency(32000);
        Assert.Equal(0u, f.Status(0));
        f.Queue(0, 32000 * 4 / 10);               // 100 ms
        Assert.Equal(0u, f.Status(0));
        f.Queue(0, 4300);                         // + 33.6 ms (two frames of 16.7 ms)
        Assert.Equal(0x80000000u, f.Status(0));
        Assert.Equal(0u, f.Status(AiFifo.CountHz / 10));     // 100 ms of playing later
    }

    [Fact]
    public void AiFifoQueuesBehindWhatIsStillPlaying()
    {
        var f = new AiFifo(); f.SetFrequency(22050);
        f.Queue(100, 22050 * 4);                  // 1 s
        f.Queue(100 + AiFifo.CountHz / 2, 22050 * 4);          // half a second later: ends at t+2 s, not t+1.5 s
        Assert.Equal(0x80000000u, f.Status(100 + AiFifo.CountHz / 2));
        Assert.True(f.DmaDoneDue(100 + AiFifo.CountHz * 2));
        Assert.False(f.DmaDoneDue(100 + AiFifo.CountHz * 2 + AiFifo.CountHz / 30));
    }

    [Fact]
    public void RingWaitsForAPrebufferAndFallsBackAfterAnUnderrun()
    {
        var r = new AudioRing { Frequency = 8000 };          // prebuffer 100 ms = 800 frames = 3200 bytes
        var block = new byte[512 * 4];
        var be = new byte[1600];
        for (int i = 0; i < be.Length; i += 2) { be[i] = 0x12; be[i + 1] = 0x34; }
        r.Push(be);
        Assert.False(r.Read(block));                           // not primed: silence
        Assert.All(block, b => Assert.Equal(0, b));
        r.Push(be); r.Push(be);                                // 4800 bytes queued
        Assert.True(r.Read(block));                            // primed now
        Assert.Equal(0x34, block[0]); Assert.Equal(0x12, block[1]);       // little-endian on the way out
        Assert.True(r.Read(block));
        Assert.False(r.Read(block));                           // 4800 - 4096 < 2048: underrun
        Assert.Equal(1, r.Underruns);
        Assert.False(r.Primed);
    }

    // The audio thread's loop as the game runs it: on every tick, while osAiGetStatus is not FULL (and tasks complete promptly), produce one 1472-byte block.
    private static (int blocks, ulong bytes) SimulateAudioThread(int ticks, int maxBlocksPerTick, uint freq = 22050)
    {
        var f = new AiFifo(); f.SetFrequency(freq);
        int blocks = 0; ulong bytes = 0;
        for (int t = 1; t <= ticks; t++)
        {
            ulong now = (ulong)t * 781250;
            for (int k = 0; k < maxBlocksPerTick && f.Status(now) == 0; k++) { f.Queue(now, 1472); blocks++; bytes += 1472; }
        }
        return (blocks, bytes);
    }

    [Fact]
    public void PromptTaskCompletionProducesExactlyRealTimeAudio()
    {
        // one task per tick (the old tick-shared completion gave about 0.5): the FIFO is kept 2 frames + 100 ms ahead, the average is one block per tick
        var (blocks, bytes) = SimulateAudioThread(3000, 1);
        double seconds = 3000 / 60.0, produced = bytes / 4.0 / 22050;
        Assert.InRange(produced - seconds, 0.0, 0.2);          // never behind the clock, ahead by at most the FIFO lead
        Assert.InRange(blocks, 2995, 3015);
    }

    [Fact]
    public void FullStatusBoundsProductionEvenIfTasksWereUnlimited()
    {
        var (_, bytes) = SimulateAudioThread(3000, 100);        // headless/lockstep with no audio clock must not run away
        double seconds = 3000 / 60.0, produced = bytes / 4.0 / 22050;
        Assert.InRange(produced - seconds, 0.0, 0.2);
    }

    [Fact]
    public void RingDropsBackToOneHundredMillisecondsWhenTheGameRunsAhead()
    {
        var r = new AudioRing { Frequency = 8000 };
        var be = new byte[3200];
        for (int i = 0; i < 4; i++) r.Push(be);                // 12800 > 3 * 3200: trimmed
        Assert.True(r.Queued <= 3200 * 3);
        Assert.True(r.Queued >= 3200);
    }
}

[Collection("GuestMemoryHigh")]
public class AudioHleGoldenTests
{
    static AudioHleGoldenTests() { if (!Mem.IsMapped(0x80000000)) Mem.Map(0x80000000, 0x1000); }

    [Fact]
    public void MatchesTheCPortsAudioHleBitForBit()
    {
        Mem.Write(0x80000110, AudioGolden.Init.AsSpan(0x110));
        var hle = new AudioHle();
        hle.Run(0x110, AudioGolden.CommandBytes);
        var got = new byte[0x1000 - 0x110];
        Mem.Read(0x80000110, got);
        var want = AudioGolden.Final.AsSpan(0x110).ToArray();
        for (int i = 0; i < got.Length; i++)
            if (got[i] != want[i]) Assert.Fail($"guest byte {0x110 + i:X} is {got[i]:X2}, the C port's HLE gives {want[i]:X2}");
        Assert.Equal(1u, hle.Unknown);
    }
}

public class InputMapperTests
{
    [Fact]
    public void MouseLookMatchesTheCPortsStickConversion()
    {
        float[] cnt = { 0, 10, 100, 1000, -500, 3, 3, 3, 2000, -2000, 0, 0, 0, 0, 1, -1, 40, 40, 40, 40, 40, 0, 0, 0 };
        int[] yaw = { 0, 56, 80, 80, 80, 80, 65, 39, 80, -80, -80, -80, -80, -80, -80, -80, -80, -80, -80, -80, -80, -80, -54, 12 };
        int[] pitch = { 0, 49, 80, 80, 80, 47, 33, 34, 80, -80, -80, -80, -80, -80, -80, -80, -80, -80, -80, 70, 76, 20, -10, 0 };
        float py = 0, pp = 0;
        for (int i = 0; i < cnt.Length; i++)
        {
            Assert.Equal(yaw[i], InputMapper.MouseAxis(ref py, cnt[i], 1f, 1f / 60, InputMapper.YawRatePerE3, 30f));
            Assert.Equal(pitch[i], InputMapper.MouseAxis(ref pp, cnt[i], 1f, 1f / 60, InputMapper.PitchRatePerE3, 30f));
        }
    }

    private static InputSample Keys(int gameplay, params string[] down)
    {
        var set = new HashSet<string>(down);
        return new InputSample { KeyDown = set.Contains, Gameplay = gameplay };
    }

    [Fact]
    public void WasdIsTheCButtonsInGameAndTheDpadInMenus()
    {
        var m = new InputMapper();
        Assert.Equal(N64.CUp | N64.CLeft, m.Evaluate(Keys(1, "W", "A")).buttons);
        Assert.Equal(N64.DUp | N64.DLeft, m.Evaluate(Keys(0, "W", "A")).buttons);
        Assert.Equal(N64.CUp | N64.DUp, m.Evaluate(Keys(-1, "W")).buttons);       // unknown context: both layers are live
    }

    [Fact]
    public void KeyboardButtonsAndArrowStick()
    {
        var m = new InputMapper();
        var r = m.Evaluate(Keys(1, "Space", "Q", "Enter", "Z", "X", "E", "Left", "Up"));
        Assert.Equal(N64.R | N64.L | N64.Start | N64.Z | N64.B | N64.A, r.buttons);
        Assert.Equal((-80, 80), (r.x, r.y));
    }

    [Fact]
    public void MouseButtonsFireInGameAndConfirmInMenus()
    {
        var m = new InputMapper();
        var s = Keys(1); s.MouseButtons[0] = true; s.MouseButtons[1] = true;
        Assert.Equal(N64.Z | N64.B, m.Evaluate(s).buttons);
        s.Gameplay = 0;
        Assert.Equal(N64.A | N64.B, m.Evaluate(s).buttons);
    }

    [Fact]
    public void WheelNotchesBecomeSeparatePresses()
    {
        var m = new InputMapper();
        var s = Keys(1); s.WheelUp = 2;
        Assert.Equal(N64.A, m.Evaluate(s).buttons);
        s.WheelUp = 0;
        Assert.Equal(0, m.Evaluate(s).buttons);          // released between the two notches
        Assert.Equal(N64.A, m.Evaluate(s).buttons);
        Assert.Equal(0, m.Evaluate(s).buttons);
    }

    [Fact]
    public void MouseMotionDrivesTheStickIncludingTheGamesDeadZoneOffset()
    {
        var m = new InputMapper();
        var s = Keys(1); s.MouseValid = true; s.MouseDx = 10;
        var r = m.Evaluate(s);
        Assert.Equal(56, r.x); Assert.Equal(0, r.y);
        s.MouseDx = 0; s.MouseDy = -10;
        Assert.Equal(49, m.Evaluate(s).y);                 // mouse up = stick up
    }

    [Fact]
    public void GamepadDualStickLayout()
    {
        var m = new InputMapper();
        var s = Keys(1);
        s.Pad = new PadSnapshot { Connected = true, LY = 1f, RX = 0.6f, RT = 1f, A = true };
        var r = m.Evaluate(s);
        Assert.Equal(N64.CUp | N64.Z | N64.R, r.buttons);       // left stick up = walk, RT = fire, A = jump (R) in game
        Assert.Equal((int)MathF.Floor((0.6f - 0.2f) / 0.8f * 80 + 0.5f), r.x);
        s.Gameplay = 0; s.Pad.A = true;
        Assert.Equal(N64.DUp | N64.Z | N64.A, m.Evaluate(s).buttons);
    }

    [Fact]
    public void RadialDeadZoneIsRescaled()
    {
        Assert.Equal((0f, 0f), InputMapper.RadialStick(0.1f, 0.1f, 0.2f));
        var (x, y) = InputMapper.RadialStick(0.6f, 0f, 0.2f);
        Assert.Equal(0.5f, x, 4); Assert.Equal(0f, y, 4);
    }
}
