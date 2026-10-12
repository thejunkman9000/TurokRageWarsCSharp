namespace Trw.Platform.Gfx;

/// <summary>
/// The pure decisions of the RDP emulation (which blender / alpha-test / addressing / fog behaviour a render state asks for), kept free of any graphics API so they
/// can be unit tested. Each mirrors the corresponding line of the C port's renderer.cpp (setupState / flush); the verdict table is in docs of the fidelity patch.
/// </summary>
public static class RdpMath
{
    /// <summary>Cycle type of othermode-high (bits 20-21): 0 one cycle, 1 two cycle, 2 copy, 3 fill.</summary>
    public static int CycleType(uint omH) => (int)((omH >> 20) & 3);

    /// <summary>Tile shift field as a coordinate scale: the RDP computes s &gt;&gt; shift for 0..10 and s &lt;&lt; (16 - shift) for 11..15.</summary>
    public static float ShiftFactor(int sh) => sh == 0 ? 1f : sh <= 10 ? 1f / (1 << sh) : (float)(1 << (16 - sh));

    /// <summary>Sampler addressing for one axis: 0 wrap, 1 mirror, 2 clamp. A tile without a mask clamps (the RDP does not wrap without a mask).</summary>
    public static int AddressMode(int mask, int cm) => mask == 0 ? 2 : (cm & 2) != 0 ? 2 : (cm & 1) != 0 ? 1 : 0;

    /// <summary>Bilinear filter requested (othermode-high bits 12-13 non-zero). The C port uses plain hardware bilinear, not the RDP's 3-point filter.</summary>
    public static bool LinearFilter(uint omH) => ((omH >> 12) & 3) != 0;

    /// <summary>
    /// Blender cycle 1 = (fog colour * factor + colour * (1 - factor)): P = CLR_FOG (3), A = A_SHADE (2) or A_FOG (1), M = CLR_IN (0), B = 1-A (0).
    /// <paramref name="alphaFromFog"/> is set for A_FOG (the factor is the fog colour's alpha instead of the vertex alpha).
    /// </summary>
    public static bool FogMix(uint omH, uint omL, bool fogEnabled, out bool alphaFromFog)
    {
        uint m1a = (omL >> 30) & 3, m1b = (omL >> 26) & 3, m2a = (omL >> 22) & 3, m2b = (omL >> 18) & 3;
        alphaFromFog = m1b == 1;
        return CycleType(omH) < 2 && m1a == 3 && (m1b == 2 || m1b == 1) && m2a == 0 && m2b == 0 && fogEnabled;
    }

    /// <summary>The RSP's per-vertex fog factor G_FOG writes into the vertex alpha: clamp(z_ndc * fm + fo, 0, 255) / 255.</summary>
    public static float FogAlpha(float zNdc, int fogMul, int fogOff)
    {
        float f = zNdc * fogMul + fogOff;
        return f <= 0f ? 0f : f >= 255f ? 1f : f / 255f;
    }

    /// <summary>Alpha-test threshold (negative = no test): copy cycle 0.01, alpha compare / coverage-from-alpha modes 0.5 / 0.3, lowered to 0.02 when the blender is on.</summary>
    public static float AlphaThreshold(uint omH, uint omL, bool depthClearQuad = false)
    {
        float thr = -1f;
        if (CycleType(omH) == 2) thr = 0.01f;
        else if ((omL & 3) == 1) thr = 0.5f;
        else if ((omL & 0x3000) != 0) thr = 0.3f;
        if ((omL & 0x4000) != 0) thr = thr > 0 ? 0.02f : -1f;
        if (depthClearQuad) thr = -1f;
        return thr;
    }

    /// <summary>FORCE_BL (othermode-low bit 14) in a one/two-cycle mode selects alpha blending; everything else is opaque (the C port has no other blender modes).</summary>
    public static bool BlendEnabled(uint omH, uint omL) => (omL & 0x4000) != 0 && CycleType(omH) < 2;

    /// <summary>Depth buffer index [test * 2 + write] from the geometry mode (G_ZBUFFER, bit 0) and othermode-low (Z_COMPARE 0x10, Z_UPDATE 0x20).</summary>
    public static int DepthStateIndex(uint geom, uint omL)
    {
        bool zb = (geom & 1) != 0, zcmp = (omL & 0x10) != 0, zupd = (omL & 0x20) != 0;
        return ((zb && zcmp) ? 2 : 0) + ((zb && zupd) ? 1 : 0);
    }

    /// <summary>Scissor rectangle (10.2 N64 units) to render-target pixels, clamped like the C port's flush() plus the ordering MonoGame requires.</summary>
    public static (int x, int y, int w, int h) ScissorRect(int sx0, int sy0, int sx1, int sy1, float screenW, float screenH, int targetW, int targetH)
    {
        int x0 = (int)(sx0 * targetW / (screenW * 4)), y0 = (int)(sy0 * targetH / (screenH * 4));
        int x1 = (int)(sx1 * targetW / (screenW * 4)), y1 = (int)(sy1 * targetH / (screenH * 4));
        x0 = Math.Clamp(x0, 0, targetW); x1 = Math.Clamp(x1, x0, targetW);
        y0 = Math.Clamp(y0, 0, targetH); y1 = Math.Clamp(y1, y0, targetH);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// A rectangle copied 1:1 (dsdx = dtdy = 1 texel per pixel) is sampled with point filtering: the RDP fetches the texel centre, and bilinear on the upscaled
    /// target smeared a line around the cached background patch of the main menu.
    /// </summary>
    public static bool ForcePointForOneToOneRect(float dsdx, float dtdy) => dsdx == 1024 && dtdy == 1024;
}
