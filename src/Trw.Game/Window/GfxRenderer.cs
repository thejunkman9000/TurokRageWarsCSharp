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
    /// <summary>Offscreen render size = 480x360 (the console's picture) times TRW_GFX_SCALE (default 2 = 960x720; 4 = 1920x1440).</summary>
    private static readonly int Scale = int.TryParse(Environment.GetEnvironmentVariable("TRW_GFX_SCALE"), out var gs) && gs >= 1 && gs <= 8 ? gs : 2;
    public static readonly int InternalWidth = 480 * Scale, InternalHeight = 360 * Scale;

    private readonly GraphicsDevice device;
    private readonly Effect combiner;
    private readonly RenderTarget2D target;
    private readonly SpriteBatch sprites;
    private readonly Dictionary<ulong, Texture2D> textures = new();
    private readonly Texture2D white;
    private readonly DepthStencilState[] depthStates = new DepthStencilState[4];      // [test * 2 + write]
    private readonly DepthStencilState depthClear = new() { DepthBufferEnable = true, DepthBufferWriteEnable = true, DepthBufferFunction = CompareFunction.Always };   // partial z-buffer clears: a far-plane quad that always passes
    private readonly BlendState noColor = new() { ColorWriteChannels = ColorWriteChannels.None };                                                              // ... and leaves the colour buffer alone
    private readonly RasterizerState raster = new() { CullMode = CullMode.None, ScissorTestEnable = true };
    private readonly SamplerState[,,] samplers = new SamplerState[2, 3, 3];            // [linear, u, v]
    private bool anyFrame;
    // local view (ViewLayout.cs): the local player's region of the N64 screen is drawn into its own target, sized for the window
    private RenderTarget2D? localTarget;
    private bool lastLocal;                                          // the frame in the targets is a local view
    private ViewRegion region;
    private int targetW = InternalWidth, targetH = InternalHeight;    // size of the target the current frame renders into
    private int windowW = 960, windowH = 720;                        // window client size at the last Present
    private GpuVertex[] scratch = new GpuVertex[3 * 6000];
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
    /// <param name="local">the local player's region (<see cref="ViewLayout.LocalRegion"/>): only that part of the screen is drawn, scaled to a target of its own; null = the whole screen</param>
    public void Execute(IReadOnlyList<GfxCommand> commands, ViewRegion? local = null)
    {
        lastLocal = local != null;
        if (local is { } r)
        {
            region = r;
            var (w, h) = ViewLayout.TargetSize(r, windowW, windowH, Scale, LocalView.Stretch);
            if (localTarget == null || localTarget.Width != w || localTarget.Height != h)
            {
                localTarget?.Dispose();
                localTarget = new RenderTarget2D(device, w, h, false, SurfaceFormat.Color, DepthFormat.Depth24);
                device.SetRenderTarget(localTarget);
                device.Clear(Color.Black);
            }
            device.SetRenderTarget(localTarget);
            targetW = w; targetH = h;
        }
        else
        {
            device.SetRenderTarget(target);
            targetW = InternalWidth; targetH = InternalHeight;
        }
        device.Viewport = new Viewport(0, 0, targetW, targetH);
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
        int scx, scy, scw, sch;
        if (lastLocal)
        {   // the scissor moved into the region (region pixels -> target); a draw scissored to another player's view ends up empty
            (scx, scy, scw, sch) = RdpMath.ScissorRect(st.ScissorX0 - region.X0 * 4, st.ScissorY0 - region.Y0 * 4, st.ScissorX1 - region.X0 * 4, st.ScissorY1 - region.Y0 * 4,
                                                       region.Width, region.Height, targetW, targetH);
            if (scw <= 0 || sch <= 0) return;
            // a draw not scissored to the view (full-screen scissor: the match clock, the borders between the views) that crosses the region's edge
            // would show up cut in half at the edge of the local view: left out (the clock is drawn by the port instead, see Scoreboard)
            if (st.ScissorX0 < region.X0 * 4 - 4 || st.ScissorY0 < region.Y0 * 4 - 4 || st.ScissorX1 > region.X1 * 4 + 4 || st.ScissorY1 > region.Y1 * 4 + 4)
                if (CrossesRegion(d.Vertices, st.ScreenWidth, st.ScreenHeight)) return;
        }
        else (scx, scy, scw, sch) = RdpMath.ScissorRect(st.ScissorX0, st.ScissorY0, st.ScissorX1, st.ScissorY1, st.ScreenWidth, st.ScreenHeight, targetW, targetH);
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
        combiner.Parameters["FogC"].SetValue(new Vector4(st.FogColor[0], st.FogColor[1], st.FogColor[2], st.FogColor[3]));
        combiner.Parameters["FogP"].SetValue(new Vector4(st.FogMix ? 1f : 0f, st.FogAlphaFromColor ? 1f : 0f, 0f, 0f));
        int cycle = (int)((st.OtherHigh >> 20) & 3);
        float thr = RdpMath.AlphaThreshold(st.OtherHigh, st.OtherLow, st.ZClear);
        combiner.Parameters["Flags"].SetValue(new Vector4(forceMode >= 0 ? forceMode : (float)st.Mode, cycle == 1 ? 1f : 0f, forceMode >= 0 ? -1f : thr, st.TextureOn ? 1f : 0f));

        bool blend = RdpMath.BlendEnabled(st.OtherHigh, st.OtherLow);
        device.DepthStencilState = st.ZClear ? depthClear : depthStates[RdpMath.DepthStateIndex(st.Geom, st.OtherLow)];
        device.BlendState = st.ZClear ? noColor : blend ? BlendState.NonPremultiplied : BlendState.Opaque;
        device.RasterizerState = raster;
        var tex0 = st.TextureOn && st.Tex0 != null ? GetTexture(st.Tex0) : white;
        var tex1 = st.TextureOn && st.Tex1 != null ? GetTexture(st.Tex1) : tex0;
        // EffectPass.Apply rebinds the sampler slots from the effect's texture parameters, so they have to be set there
        combiner.Parameters["Tex0"].SetValue(tex0);
        combiner.Parameters["Tex1"].SetValue(tex1);
        var smp = samplers[st.Linear ? 1 : 0, st.AddressU, st.AddressV];
        device.SamplerStates[0] = smp;
        device.SamplerStates[1] = smp;
        device.ScissorRectangle = new Rectangle(scx, scy, scw, sch);

        var src = MemoryMarshal.Cast<GfxVertex, GpuVertex>(d.Vertices.AsSpan());
        if (scratch.Length < src.Length) scratch = new GpuVertex[Math.Max(src.Length, scratch.Length * 2)];      // reused: no allocation per draw
        src.CopyTo(scratch);
        if (lastLocal)
        {   // clip-space remap of the region onto the whole target: ndc' = k * (ndc - region's low edge) - 1, i.e. x' = kx * x - bx * w (z, w unchanged)
            float sw = st.ScreenWidth, sh = st.ScreenHeight;
            float nx0 = region.X0 / sw * 2f - 1f, nx1 = region.X1 / sw * 2f - 1f, nyTop = 1f - region.Y0 / sh * 2f, nyBot = 1f - region.Y1 / sh * 2f;
            float kx = 2f / (nx1 - nx0), bx = kx * nx0 + 1f, ky = 2f / (nyTop - nyBot), by = ky * nyBot + 1f;
            for (int i = 0; i < src.Length; i++)
            {
                ref var p = ref scratch[i].Position;
                p.X = kx * p.X - bx * p.W; p.Y = ky * p.Y - by * p.W;
            }
        }
        var verts = scratch;
        foreach (var pass in combiner.CurrentTechnique.Passes)
        {
            pass.Apply();
            device.DrawUserPrimitives(PrimitiveType.TriangleList, verts, 0, src.Length / 3);
        }
    }

    /// <summary>True when the draw's screen bounding box is partly inside and partly outside the local region without covering it (vertices in clip space, w &gt; 0 only).</summary>
    private bool CrossesRegion(GfxVertex[] v, float sw, float sh)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (ref readonly var p in v.AsSpan())
        {
            if (!(p.W > 1e-6f)) continue;
            float sx = (p.X / p.W + 1f) * 0.5f * sw, sy = (1f - p.Y / p.W) * 0.5f * sh;      // N64 screen pixels
            if (sx < x0) x0 = sx; if (sx > x1) x1 = sx; if (sy < y0) y0 = sy; if (sy > y1) y1 = sy;
        }
        if (x0 > x1) return false;
        bool inside = x1 > region.X0 && x0 < region.X1 && y1 > region.Y0 && y0 < region.Y1;
        bool contained = x0 >= region.X0 - 0.5f && x1 <= region.X1 + 1.5f && y0 >= region.Y0 - 0.5f && y1 <= region.Y1 + 1.5f;
        bool covers = x0 <= region.X0 + 0.5f && x1 >= region.X1 - 0.5f && y0 <= region.Y0 + 0.5f && y1 >= region.Y1 - 0.5f;      // full-screen fades / flashes stay
        return inside && !contained && !covers;
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
        var frame = lastLocal && localTarget != null ? localTarget : target;          // a local view is saved at its own aspect
        int height = Math.Max(1, (int)((long)width * frame.Height / frame.Width));
        using var small = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color, DepthFormat.None);
        device.SetRenderTarget(small);
        device.Clear(Color.Black);
        sprites.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        sprites.Draw(frame, new Rectangle(0, 0, width, height), Color.White);
        sprites.End();
        Overlay?.Invoke(new Rectangle(0, 0, width, height), width, height);            // the screenshot shows what the window shows
        device.SetRenderTarget(null);
        using var fs = File.Create(path);
        small.SaveAsPng(fs, width, height);
    }

    /// <summary>Draws the offscreen target letterboxed (4:3) into the window.</summary>
    public void Present(int windowWidth, int windowHeight)
    {
        device.SetRenderTarget(null);
        device.Clear(Color.Black);
        if (windowWidth > 0 && windowHeight > 0) { windowW = windowWidth; windowH = windowHeight; }
        if (!anyFrame) return;
        var frame = lastLocal && localTarget != null ? localTarget : target;
        Rectangle dest;
        if (frame == localTarget && LocalView.Stretch) dest = new Rectangle(0, 0, windowWidth, windowHeight);
        else
        {   // letterbox at the frame's aspect (4:3, or the local view's own: 8:3 for a 2-player half)
            float scale = Math.Min(windowWidth / (float)frame.Width, windowHeight / (float)frame.Height);
            int w = (int)(frame.Width * scale), h = (int)(frame.Height * scale);
            dest = new Rectangle((windowWidth - w) / 2, (windowHeight - h) / 2, w, h);
        }
        LastDest = dest;
        sprites.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        sprites.Draw(frame, dest, Color.White);
        sprites.End();
        Overlay?.Invoke(dest, windowWidth, windowHeight);
        // test aid: TRW_SCORE_SHOT_DIR=<dir> saves screenshots when a score record changed (MatchInfo.ScoreChanges), right away and 90 presents later, and of the results screen (screen 17)
        if (scoreShotDir != null)
        {
            int ch = MatchInfo.ScoreChanges;
            if (ch != scoreShotSeen) { scoreShotSeen = ch; scoreShotAt = new[] { presents + 2, presents + 90 }; }
            if (Game.ScreenId == 17 && lastScreen != 17) scoreShotAt = new[] { presents + 1, presents + 4 };     // and the results screen after a match
            lastScreen = Game.ScreenId;
            presents++;
            foreach (var at in scoreShotAt)
                if (at == presents) { Directory.CreateDirectory(scoreShotDir); SavePng(Path.Combine(scoreShotDir, $"score_{ch:D3}_{presents:D6}.png")); }
        }
    }

    private static readonly string? scoreShotDir = Environment.GetEnvironmentVariable("TRW_SCORE_SHOT_DIR");
    private int scoreShotSeen, presents, lastScreen;
    private int[] scoreShotAt = Array.Empty<int>();

    /// <summary>Port-drawn overlays (the scoreboard), drawn over the presented picture and into screenshots: (where the picture is, target width, target height).</summary>
    public Action<Rectangle, int, int>? Overlay { get; set; }

    /// <summary>Where the last Present put the picture in the window (overlays align to it).</summary>
    public Rectangle LastDest { get; private set; }

    public void Dispose()
    {
        target.Dispose(); localTarget?.Dispose(); sprites.Dispose(); white.Dispose();
        foreach (var t in textures.Values) t.Dispose();
    }
}
