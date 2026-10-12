namespace Trw.Platform.Gfx;

/// <summary>A decoded texture: RGBA8, top row first. <see cref="Key"/> identifies identical TMEM contents so the GPU copy can be cached.</summary>
public sealed class DecodedTexture
{
    public int Width, Height;
    public byte[] Rgba = Array.Empty<byte>();
    public ulong Key;
    public uint Src;      // debugging: RDRAM address the texture was loaded from
}

/// <summary>A vertex ready for the GPU: clip-space position (already multiplied by w, so the GPU divides), colour, texture coordinates in 0..1.</summary>
public struct GfxVertex
{
    public float X, Y, Z, W;
    public float R, G, B, A;
    public float U, V;
}

public enum DrawMode
{
    Combiner = 0,       // textured/shaded triangle through the colour combiner
    VertexColor = 1,    // flat fill rectangle
    Copy = 2,           // copy-cycle texture rectangle: the texel is the pixel
}

/// <summary>Everything the GPU needs to draw a batch: RDP state at the time of the draw.</summary>
public sealed class DrawState
{
    public uint Geom, OtherLow, OtherHigh, CombineW0, CombineW1;
    public float[] Prim = new float[4], Env = new float[4];
    public float[] FogColor = new float[4] { 0, 0, 0, 1 };
    public bool FogMix, FogAlphaFromColor;   // blender cycle 1 mixes the fog colour in; the factor is the fog colour's alpha instead of the vertex alpha
    public DrawMode Mode;
    public bool TextureOn;
    public DecodedTexture? Tex0, Tex1;
    public bool Linear;                 // bilinear filtering
    public int AddressU, AddressV;      // 0 wrap, 1 mirror, 2 clamp
    public int ScissorX0, ScissorY0, ScissorX1, ScissorY1;   // 10.2 fixed point, N64 screen units
    public float ScreenWidth, ScreenHeight;
    public bool ZClear;                 // far-plane quad that clears part of the z-buffer: depth test always, depth write on, colour writes off
}

public abstract record GfxCommand;
/// <summary>Clear the colour buffer.</summary>
public sealed record ClearColorCommand(float R, float G, float B) : GfxCommand;
public sealed record ClearDepthCommand : GfxCommand;
/// <summary>Draw triangles (3 vertices each) with the given state.</summary>
public sealed record DrawCommand(DrawState State, GfxVertex[] Vertices) : GfxCommand;
