using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Trw.Platform.Gfx;

namespace Trw.Game;

/// <summary>
/// Executes the draw commands the display-list interpreter produced: renders into an offscreen target of INTERNAL_W x INTERNAL_H
/// (independent of the N64's 480x360, 4:3) and blits it letterboxed to the window. The RDP colour combiner runs in Combiner.fx.
/// </summary>
public sealed class GfxRenderer : IDisposable
{
    public const int InternalWidth = 1920, InternalHeight = 1440;

    private readonly GraphicsDevice device;
    private readonly Effect combiner;
    private readonly RenderTarget2D target;
    private readonly SpriteBatch sprites;
    private readonly Dictionary<ulong, Texture2D> textures = new();
    private readonly Texture2D white;
    private readonly DepthStencilState[] depthStates = new DepthStencilState[4];      // [test * 2 + write]
    private readonly RasterizerState raster = new() { CullMode = CullMode.None, ScissorTestEnable = true };
    private readonly SamplerState[,,] samplers = new SamplerState[2, 3, 3];            // [linear, u, v]
    private bool anyFrame;
    private readonly int forceMode = int.TryParse(Environment.GetEnvironmentVariable("TRW_GFX_FORCE_MODE"), out var fm) ? fm : -1;   // debug: draw everything as 1 = vertex colour, 2 = texel

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuVertex : IVertexType
    {
        public Vector4 Position, Color;
        public Vector2 TexCoord;
        private static readonly VertexDeclaration Decl = new(
            new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.Position, 0),
            new VertexElement(16, VertexElementFormat.Vector4, VertexElementUsage.Color, 0),
            new VertexElement(32, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0));
        public VertexDeclaration VertexDeclaration => Decl;
    }

    public GfxRenderer(GraphicsDevice device, Effect combiner)
    {
        this.device = device;
        this.combiner = combiner;
        target = new RenderTarget2D(device, InternalWidth, InternalHeight, false, SurfaceFormat.Color, DepthFormat.Depth24);
        sprites = new SpriteBatch(device);
        white = new Texture2D(device, 1, 1);
        white.SetData(new[] { Color.White });
        for (int t = 0; t < 2; t++)
            for (int w = 0; w < 2; w++)
                depthStates[t * 2 + w] = new DepthStencilState { DepthBufferEnable = t != 0, DepthBufferWriteEnable = w != 0, DepthBufferFunction = CompareFunction.LessEqual };
        var modes = new[] { TextureAddressMode.Wrap, TextureAddressMode.Mirror, TextureAddressMode.Clamp };
        for (int f = 0; f < 2; f++)
            for (int u = 0; u < 3; u++)
                for (int v = 0; v < 3; v++)
                    samplers[f, u, v] = new SamplerState { Filter = f != 0 ? TextureFilter.Linear : TextureFilter.Point, AddressU = modes[u], AddressV = modes[v] };
    }

    private Texture2D GetTexture(DecodedTexture t)
    {
        if (textures.TryGetValue(t.Key, out var tex)) return tex;
        if (textures.Count > 4000) { foreach (var x in textures.Values) x.Dispose(); textures.Clear(); }
        tex = new Texture2D(device, t.Width, t.Height, false, SurfaceFormat.Color);
        tex.SetData(t.Rgba);
        textures[t.Key] = tex;
        return tex;
    }

    /// <summary>Renders one frame's worth of commands into the offscreen target.</summary>
    public void Execute(IReadOnlyList<GfxCommand> commands)
    {
        device.SetRenderTarget(target);
        device.Viewport = new Viewport(0, 0, InternalWidth, InternalHeight);
        if (!anyFrame) { device.Clear(Environment.GetEnvironmentVariable("TRW_GFX_BG") != null ? new Color(0, 0, 90) : Color.Black); anyFrame = true; }
        foreach (var c in commands)
        {
            switch (c)
            {
                case ClearColorCommand cc: device.Clear(ClearOptions.Target, new Color(cc.R, cc.G, cc.B, 1f), 1f, 0); break;
                case ClearDepthCommand: device.Clear(ClearOptions.DepthBuffer, Color.Black, 1f, 0); break;
                case DrawCommand d: Draw(d); break;
            }
        }
        device.SetRenderTarget(null);
    }

    private void Draw(DrawCommand d)
    {
        var st = d.State;
        var cw0 = st.CombineW0; var cw1 = st.CombineW1;
        // combiner fields (see gbi.h GCCc0w0 / GCCc0w1 / GCCc1w0 / GCCc1w1)
        uint a0 = (cw0 >> 20) & 15, c0 = (cw0 >> 15) & 31, Aa0 = (cw0 >> 12) & 7, Ac0 = (cw0 >> 9) & 7, a1 = (cw0 >> 5) & 15, c1 = cw0 & 31;
        uint b0 = (cw1 >> 28) & 15, d0 = (cw1 >> 15) & 7, Ab0 = (cw1 >> 12) & 7, Ad0 = (cw1 >> 9) & 7;
        uint b1 = (cw1 >> 24) & 15, Aa1 = (cw1 >> 21) & 7, Ac1 = (cw1 >> 18) & 7, d1 = (cw1 >> 6) & 7, Ab1 = (cw1 >> 3) & 7, Ad1 = cw1 & 7;
        combiner.Parameters["C0"].SetValue(new Vector4(a0, b0, c0, d0));
        combiner.Parameters["A0"].SetValue(new Vector4(Aa0, Ab0, Ac0, Ad0));
        combiner.Parameters["C1"].SetValue(new Vector4(a1, b1, c1, d1));
        combiner.Parameters["A1"].SetValue(new Vector4(Aa1, Ab1, Ac1, Ad1));
        combiner.Parameters["Prim"].SetValue(new Vector4(st.Prim[0], st.Prim[1], st.Prim[2], st.Prim[3]));
        combiner.Parameters["Env"].SetValue(new Vector4(st.Env[0], st.Env[1], st.Env[2], st.Env[3]));
        int cycle = (int)((st.OtherHigh >> 20) & 3);
        float thr = -1f;
        if (cycle == 2) thr = 0.01f;
        else if ((st.OtherLow & 3) == 1) thr = 0.5f;
        else if ((st.OtherLow & 0x3000) != 0) thr = 0.3f;
        if ((st.OtherLow & 0x4000) != 0) thr = thr > 0 ? 0.02f : -1f;
        combiner.Parameters["Flags"].SetValue(new Vector4(forceMode >= 0 ? forceMode : (float)st.Mode, cycle == 1 ? 1f : 0f, forceMode >= 0 ? -1f : thr, st.TextureOn ? 1f : 0f));

        bool zb = (st.Geom & 1) != 0, zcmp = (st.OtherLow & 0x10) != 0, zupd = (st.OtherLow & 0x20) != 0;
        bool blend = (st.OtherLow & 0x4000) != 0 && cycle < 2;
        device.DepthStencilState = depthStates[((zb && zcmp) ? 2 : 0) + ((zb && zupd) ? 1 : 0)];
        device.BlendState = blend ? BlendState.NonPremultiplied : BlendState.Opaque;
        device.RasterizerState = raster;
        var tex0 = st.TextureOn && st.Tex0 != null ? GetTexture(st.Tex0) : white;
        var tex1 = st.TextureOn && st.Tex1 != null ? GetTexture(st.Tex1) : tex0;
        // EffectPass.Apply rebinds the sampler slots from the effect's texture parameters, so they have to be set there
        combiner.Parameters["Tex0"].SetValue(tex0);
        combiner.Parameters["Tex1"].SetValue(tex1);
        var smp = samplers[st.Linear ? 1 : 0, st.AddressU, st.AddressV];
        device.SamplerStates[0] = smp;
        device.SamplerStates[1] = smp;
        int sx0 = (int)(st.ScissorX0 * InternalWidth / (st.ScreenWidth * 4)), sy0 = (int)(st.ScissorY0 * InternalHeight / (st.ScreenHeight * 4));
        int sx1 = (int)(st.ScissorX1 * InternalWidth / (st.ScreenWidth * 4)), sy1 = (int)(st.ScissorY1 * InternalHeight / (st.ScreenHeight * 4));
        sx0 = Math.Clamp(sx0, 0, InternalWidth); sx1 = Math.Clamp(sx1, sx0, InternalWidth);
        sy0 = Math.Clamp(sy0, 0, InternalHeight); sy1 = Math.Clamp(sy1, sy0, InternalHeight);
        device.ScissorRectangle = new Rectangle(sx0, sy0, sx1 - sx0, sy1 - sy0);

        var verts = MemoryMarshal.Cast<GfxVertex, GpuVertex>(d.Vertices.AsSpan()).ToArray();
        foreach (var pass in combiner.CurrentTechnique.Passes)
        {
            pass.Apply();
            device.DrawUserPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length / 3);
        }
    }

    /// <summary>Saves the offscreen target as a PNG (downscaled to <paramref name="width"/> x 3/4 of that, to keep previews small).</summary>
    public void SavePng(string path, int width = 960)
    {
        if (!anyFrame) return;
        if (Environment.GetEnvironmentVariable("TRW_GFX_BG") != null)
        {
            var px = new Color[InternalWidth * InternalHeight];
            target.GetData(px);
            var hist = new Dictionary<uint, int>();
            foreach (var c in px) { hist.TryGetValue(c.PackedValue, out var n); hist[c.PackedValue] = n + 1; }
            Console.Error.WriteLine("[gfx] target colours: " + string.Join(", ", hist.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key:X8}x{k.Value}")));
        }
        int height = width * 3 / 4;
        using var small = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None);
        device.SetRenderTarget(small);
        device.Clear(Color.Black);
        sprites.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        sprites.Draw(target, new Rectangle(0, 0, width, height), Color.White);
        sprites.End();
        device.SetRenderTarget(null);
        using var fs = File.Create(path);
        small.SaveAsPng(fs, width, height);
    }

    /// <summary>Draws the offscreen target letterboxed (4:3) into the window.</summary>
    public void Present(int windowWidth, int windowHeight)
    {
        device.SetRenderTarget(null);
        device.Clear(Color.Black);
        if (!anyFrame) return;
        float scale = Math.Min(windowWidth / 4f, windowHeight / 3f);
        int w = (int)(scale * 4), h = (int)(scale * 3);
        var dest = new Rectangle((windowWidth - w) / 2, (windowHeight - h) / 2, w, h);
        sprites.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        sprites.Draw(target, dest, Color.White);
        sprites.End();
    }

    public void Dispose()
    {
        target.Dispose(); sprites.Dispose(); white.Dispose();
        foreach (var t in textures.Values) t.Dispose();
    }
}
