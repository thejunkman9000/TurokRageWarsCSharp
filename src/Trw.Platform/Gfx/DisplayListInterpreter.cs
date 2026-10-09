namespace Trw.Platform.Gfx;

/// <summary>
/// Walks an F3DEX2 display list in guest memory and turns it into GPU-agnostic draw commands (port of the C port's renderer.cpp, CPU half).
/// RDP state (tiles, TMEM, combiner, other modes) is tracked here; textures are decoded from an emulated TMEM into RGBA8; vertices are
/// transformed, clipped against the near plane, culled and batched. Nothing here touches a graphics API.
/// </summary>
public sealed class DisplayListInterpreter
{
    // ---------------------------------------------------------------------------------------------- state
    private struct Tile { public int Fmt, Siz, Line, Tmem, Pal, CmT, MaskT, ShiftT, CmS, MaskS, ShiftS, Uls, Ult, Lrs, Lrt; }
    private struct Light { public float R, G, B, Dx, Dy, Dz; }
    private struct V { public float Cx, Cy, Cz, Cw, R, G, B, A, S, T; }

    private sealed class RdpState
    {
        public uint Geom, OmL, OmH, CombW0, CombW1;
        public uint Cimg, Zimg, Timg, TimgW; public int TimgFmt, TimgSiz;
        public uint Fill; public float[] Prim = new float[4], Env = new float[4];
        public Tile[] Tile = new Tile[8]; public byte[] Tmem = new byte[4096]; public int Tluts;
        public float[][] Mv = Enumerable.Range(0, 10).Select(_ => new float[16]).ToArray(); public int MvTop;
        public float[] Proj = new float[16], Mvp = new float[16]; public bool MvpDirty;
        public int[] Sc = new int[4]; public float[] Vsc = new float[4], Vtr = new float[4];
        public int NumLights; public Light[] Lights = new Light[10];
        public uint TexScaleS, TexScaleT; public int TexTile, TexOn;
        public V[] Vtx = new V[64];
    }

    private RdpState S = new();
    private readonly uint[] segbase = new uint[16];
    private float screenW = 480f, screenH = 360f;      // the game switches the colour image width between screens (480 in matches, 284 in menus): follows SETCIMG

    private readonly List<GfxVertex> batch = new();
    private readonly List<GfxCommand> commands = new();
    private DrawState cur = new(); private bool curValid; private int curTexW = 1, curTexH = 1;
    private readonly Dictionary<ulong, DecodedTexture> texCache = new();

    // ---------------------------------------------------------------------------------------------- guest memory access (zeros for garbage pointers)

    private static byte G8(uint a, int i) => a == 0 ? (byte)0 : Mem.IsMapped(a + (uint)i) ? Mem.U8(a + (uint)i) : (byte)0;
    private static ushort G16(uint a, int i) => (ushort)((G8(a, i) << 8) | G8(a, i + 1));
    private static uint G32(uint a, int i) => ((uint)G16(a, i) << 16) | G16(a, i + 2);

    /// <summary>Resolves a segmented address; returns 0 for addresses outside RAM (reads of 0 give zeros).</summary>
    private uint Res(uint a)
    {
        if ((a & 0x80000000u) == 0) a = 0x80000000u | (segbase[(a >> 24) & 0xF] + (a & 0xFFFFFF));
        return a >= 0x80000000u && a < 0x80800000u ? a : 0;
    }

    // ---------------------------------------------------------------------------------------------- matrices

    private static void MatIdent(float[] m) { Array.Clear(m); m[0] = m[5] = m[10] = m[15] = 1; }

    /// <summary>Row-vector convention: o = a * b.</summary>
    private static void MatMul(float[] a, float[] b, float[] o)
    {
        var t = new float[16];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                t[i * 4 + j] = a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] + a[i * 4 + 2] * b[8 + j] + a[i * 4 + 3] * b[12 + j];
        Array.Copy(t, o, 16);
    }

    private static void LoadMtx(uint p, float[] m)
    {
        for (int i = 0; i < 16; i++)
        {
            short ip = (short)G16(p, i * 2);
            ushort fp = G16(p, 32 + i * 2);
            m[i] = ip + fp / 65536f;
        }
    }

    // ---------------------------------------------------------------------------------------------- texture decode from TMEM

    private void TlutEntry(int idx, byte[] o, int oi)
    {
        int a = (0x800 + idx * 8) & 0xFFE;
        ushort v = (ushort)((S.Tmem[a] << 8) | S.Tmem[a + 1]);
        if (S.Tluts == 3) { o[oi] = o[oi + 1] = o[oi + 2] = (byte)(v >> 8); o[oi + 3] = (byte)(v & 0xFF); }
        else { o[oi] = (byte)(((v >> 11) & 31) * 255 / 31); o[oi + 1] = (byte)(((v >> 6) & 31) * 255 / 31); o[oi + 2] = (byte)(((v >> 1) & 31) * 255 / 31); o[oi + 3] = (byte)((v & 1) != 0 ? 255 : 0); }
    }

    private void DecodeTexel(in Tile t, int x, int y, byte[] o, int oi)
    {
        int lineBytes = t.Line * 8, row = t.Tmem * 8 + y * lineBytes, swap = (y & 1) != 0 ? 4 : 0;
        int B(int a) => S.Tmem[(a ^ swap) & 0xFFF];
        if (t.Siz == 2)
        {
            int v = (B(row + x * 2) << 8) | B(row + x * 2 + 1);
            if (t.Fmt == 0) { o[oi] = (byte)(((v >> 11) & 31) * 255 / 31); o[oi + 1] = (byte)(((v >> 6) & 31) * 255 / 31); o[oi + 2] = (byte)(((v >> 1) & 31) * 255 / 31); o[oi + 3] = (byte)((v & 1) != 0 ? 255 : 0); }
            else if (t.Fmt == 3) { o[oi] = o[oi + 1] = o[oi + 2] = (byte)(v >> 8); o[oi + 3] = (byte)(v & 0xFF); }
            else if (t.Fmt == 2) TlutEntry(v & 0xFF, o, oi);
            else { o[oi] = o[oi + 1] = o[oi + 2] = (byte)(v >> 8); o[oi + 3] = 255; }
        }
        else if (t.Siz == 3)
        {
            int a = (row + x * 2) ^ swap;
            o[oi] = S.Tmem[a & 0x7FF]; o[oi + 1] = S.Tmem[(a + 1) & 0x7FF]; o[oi + 2] = S.Tmem[(a + 0x800) & 0xFFF]; o[oi + 3] = S.Tmem[(a + 0x801) & 0xFFF];
        }
        else if (t.Siz == 1)
        {
            int v = B(row + x);
            if (t.Fmt == 2) TlutEntry(v, o, oi);
            else if (t.Fmt == 3) { o[oi] = o[oi + 1] = o[oi + 2] = (byte)((v >> 4) * 17); o[oi + 3] = (byte)((v & 15) * 17); }
            else o[oi] = o[oi + 1] = o[oi + 2] = o[oi + 3] = (byte)v;
        }
        else
        {
            int b = B(row + (x >> 1)); int n = (x & 1) != 0 ? (b & 15) : (b >> 4);
            if (t.Fmt == 2) TlutEntry(t.Pal * 16 + n, o, oi);
            else if (t.Fmt == 3) { o[oi] = o[oi + 1] = o[oi + 2] = (byte)((n >> 1) * 255 / 7); o[oi + 3] = (byte)((n & 1) != 0 ? 255 : 0); }
            else o[oi] = o[oi + 1] = o[oi + 2] = o[oi + 3] = (byte)(n * 17);
        }
    }

    private static void TileDims(in Tile t, out int w, out int h)
    {
        w = ((t.Lrs - t.Uls) >> 2) + 1; h = ((t.Lrt - t.Ult) >> 2) + 1;
        if (t.MaskS != 0 && (1 << t.MaskS) > w) w = 1 << t.MaskS;
        if (t.MaskT != 0 && (1 << t.MaskT) > h) h = 1 << t.MaskT;
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        if (w > 1024) w = 1024;
        if (h > 1024) h = 1024;
    }

    private DecodedTexture GetTexture(in Tile t, int w, int h)
    {
        ulong key = 1469598103934665603UL;
        void Mix(ulong v) => key = (key ^ v) * 1099511628211UL;
        Mix((ulong)t.Fmt); Mix((ulong)t.Siz); Mix((ulong)t.Line); Mix((ulong)w); Mix((ulong)h); Mix((ulong)t.Pal); Mix((ulong)S.Tluts);
        int bytes = t.Line * 8 * h + 8;
        if (t.Tmem * 8 + bytes > 4096) bytes = 4096 - t.Tmem * 8;
        for (int i = 0; i < bytes; i++) Mix(S.Tmem[t.Tmem * 8 + i]);
        if (t.Fmt == 2)
        {
            int n = t.Siz == 0 ? 16 : 256;
            for (int i = 0; i < n * 8; i++) Mix(S.Tmem[(0x800 + (t.Siz == 0 ? t.Pal * 128 : 0) + i) & 0xFFF]);
        }
        if (texCache.TryGetValue(key, out var hit)) return hit;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                DecodeTexel(t, x, y, px, (y * w + x) * 4);
        if (texCache.Count > 4000) texCache.Clear();
        var tex = new DecodedTexture { Width = w, Height = h, Rgba = px, Key = key };
        texCache[key] = tex;
        return tex;
    }

    // ---------------------------------------------------------------------------------------------- batching

    private void Flush()
    {
        if (batch.Count == 0) { return; }
        var st = cur;
        // the scissor in force at draw time
        st.ScissorX0 = S.Sc[0]; st.ScissorY0 = S.Sc[1]; st.ScissorX1 = S.Sc[2]; st.ScissorY1 = S.Sc[3];
        st.ScreenWidth = screenW; st.ScreenHeight = screenH;
        commands.Add(new DrawCommand(st, batch.ToArray()));
        batch.Clear();
        cur = CloneState(st);          // later draws must not alias the state captured in the command
    }

    private static DrawState CloneState(DrawState s) => new()
    {
        Geom = s.Geom, OtherLow = s.OtherLow, OtherHigh = s.OtherHigh, CombineW0 = s.CombineW0, CombineW1 = s.CombineW1,
        Prim = (float[])s.Prim.Clone(), Env = (float[])s.Env.Clone(), Mode = s.Mode, TextureOn = s.TextureOn, Tex0 = s.Tex0, Tex1 = s.Tex1,
        Linear = s.Linear, AddressU = s.AddressU, AddressV = s.AddressV,
    };

    private void SetupState(DrawMode mode)
    {
        var n = new DrawState { Geom = S.Geom, OtherLow = S.OmL, OtherHigh = S.OmH, CombineW0 = S.CombW0, CombineW1 = S.CombW1, Mode = mode };
        Array.Copy(S.Prim, n.Prim, 4); Array.Copy(S.Env, n.Env, 4);
        int cycle = (int)((S.OmH >> 20) & 3);
        if (cycle == 2 && mode == DrawMode.Combiner) n.Mode = DrawMode.Copy;
        var t = S.Tile[S.TexTile & 7];
        n.TextureOn = S.TexOn != 0 && mode != DrawMode.VertexColor;
        if (n.TextureOn)
        {
            TileDims(t, out int w, out int h); curTexW = w; curTexH = h;
            n.Tex0 = GetTexture(t, w, h);
            uint cw0 = S.CombW0, cw1 = S.CombW1;
            uint[] f =
            {
                (cw0 >> 20) & 15, (cw1 >> 28) & 15, (cw0 >> 15) & 31, (cw1 >> 15) & 7, (cw0 >> 12) & 7, (cw1 >> 12) & 7, (cw0 >> 9) & 7, (cw1 >> 9) & 7,
                (cw0 >> 5) & 15, (cw1 >> 24) & 15, cw0 & 31, (cw1 >> 6) & 7, (cw1 >> 21) & 7, (cw1 >> 3) & 7, (cw1 >> 18) & 7, cw1 & 7,
            };
            bool uses1 = false;
            for (int k = 0; k < 16; k++) if (f[k] == 2 || (k == 2 && f[k] == 9) || (k == 10 && f[k] == 9)) uses1 = true;
            if (uses1)
            {
                var t1 = S.Tile[(S.TexTile + 1) & 7];
                TileDims(t1, out int w1, out int h1);
                n.Tex1 = GetTexture(t1, w1, h1);
            }
            n.Linear = ((S.OmH >> 12) & 3) != 0;
            n.AddressU = t.MaskS == 0 ? 2 : (t.CmS & 2) != 0 ? 2 : (t.CmS & 1) != 0 ? 1 : 0;
            n.AddressV = t.MaskT == 0 ? 2 : (t.CmT & 2) != 0 ? 2 : (t.CmT & 1) != 0 ? 1 : 0;
        }
        else { n.AddressU = n.AddressV = 2; }
        bool same = curValid && cur.Geom == n.Geom && cur.OtherLow == n.OtherLow && cur.OtherHigh == n.OtherHigh && cur.CombineW0 == n.CombineW0 &&
                    cur.CombineW1 == n.CombineW1 && cur.Mode == n.Mode && cur.Prim.AsSpan().SequenceEqual(n.Prim) && cur.Env.AsSpan().SequenceEqual(n.Env) &&
                    cur.Tex0 == n.Tex0 && cur.Tex1 == n.Tex1 && cur.Linear == n.Linear && cur.AddressU == n.AddressU && cur.AddressV == n.AddressV &&
                    cur.TextureOn == n.TextureOn;
        if (!same) { Flush(); cur = n; curValid = true; }
    }

    private void PushVtx(in V v, float tw, float th, float uls, float ult)
    {
        float invw = 1f / v.Cw;
        float nx = v.Cx * invw, ny = v.Cy * invw, nz = v.Cz * invw;
        float px = (nx * S.Vsc[0] + S.Vtr[0]) * 0.25f;                                  // 10.2 fixed point viewport
        float py = (-ny * S.Vsc[1] + S.Vtr[1]) * 0.25f;                                 // clip y is up, screen y is down
        float zs = S.Vsc[2] != 0 ? (nz * S.Vsc[2] + S.Vtr[2]) / 1023f : (nz * 0.5f + 0.5f);
        if (zs < 0) zs = 0;
        if (zs > 1) zs = 1;
        var o = new GfxVertex
        {
            X = (px / screenW * 2f - 1f) * v.Cw, Y = (1f - py / screenH * 2f) * v.Cw, Z = zs * v.Cw, W = v.Cw,
            R = v.R, G = v.G, B = v.B, A = v.A,
        };
        float sScale = S.TexScaleS / 65536f, tScale = S.TexScaleT / 65536f;
        o.U = (v.S / 32f * sScale - uls / 4f) / tw;
        o.V = (v.T / 32f * tScale - ult / 4f) / th;
        batch.Add(o);
    }

    private void EmitTri(int i0, int i1, int i2)
    {
        var vs = new[] { S.Vtx[i0], S.Vtx[i1], S.Vtx[i2] };
        // near-plane clip (z >= -w, w > 0)
        var poly = new V[6]; int n = 0;
        static bool Inside(in V a) => a.Cw > 1e-5f && a.Cz >= -a.Cw;
        for (int i = 0; i < 3; i++)
        {
            V a = vs[i], b = vs[(i + 1) % 3];
            bool ia = Inside(a), ib = Inside(b);
            if (ia) poly[n++] = a;
            if (ia != ib)
            {
                float da = a.Cz + a.Cw, db = b.Cz + b.Cw, t = da / (da - db);
                poly[n++] = new V
                {
                    Cx = a.Cx + (b.Cx - a.Cx) * t, Cy = a.Cy + (b.Cy - a.Cy) * t, Cz = a.Cz + (b.Cz - a.Cz) * t, Cw = a.Cw + (b.Cw - a.Cw) * t,
                    R = a.R + (b.R - a.R) * t, G = a.G + (b.G - a.G) * t, B = a.B + (b.B - a.B) * t, A = a.A + (b.A - a.A) * t,
                    S = a.S + (b.S - a.S) * t, T = a.T + (b.T - a.T) * t,
                };
            }
        }
        if (n < 3) return;
        if ((S.Geom & 0x600) != 0)     // culling in screen space
        {
            var sx = new float[3]; var sy = new float[3];
            for (int i = 0; i < 3; i++) { sx[i] = poly[i].Cx / poly[i].Cw * S.Vsc[0] * 0.25f; sy[i] = -poly[i].Cy / poly[i].Cw * S.Vsc[1] * 0.25f; }
            float cross = (sx[1] - sx[0]) * (sy[2] - sy[0]) - (sx[2] - sx[0]) * (sy[1] - sy[0]);
            bool front = cross < 0;
            if ((S.Geom & 0x400) != 0 && !front) return;
            if ((S.Geom & 0x200) != 0 && front) return;
        }
        SetupState(DrawMode.Combiner);
        var tile = S.Tile[S.TexTile & 7];
        if (batch.Count + 12 > 3 * 6000) Flush();
        for (int k = 1; k + 1 < n; k++)
        {
            PushVtx(poly[0], curTexW, curTexH, tile.Uls, tile.Ult);
            PushVtx(poly[k], curTexW, curTexH, tile.Uls, tile.Ult);
            PushVtx(poly[k + 1], curTexW, curTexH, tile.Uls, tile.Ult);
        }
    }

    private void LoadVtx(uint w0, uint w1)
    {
        int n = (int)((w0 >> 12) & 0xFF), v0 = (int)((w0 >> 1) & 0x7F) - n;
        if (v0 < 0 || v0 + n > 64) return;
        uint p = Res(w1);
        if (S.MvpDirty) { MatMul(S.Mv[S.MvTop], S.Proj, S.Mvp); S.MvpDirty = false; }
        for (int i = 0; i < n; i++, p = p == 0 ? 0 : p + 16)
        {
            ref V o = ref S.Vtx[v0 + i];
            float x = (short)G16(p, 0), y = (short)G16(p, 2), z = (short)G16(p, 4);
            var m = S.Mvp;
            o.Cx = x * m[0] + y * m[4] + z * m[8] + m[12]; o.Cy = x * m[1] + y * m[5] + z * m[9] + m[13];
            o.Cz = x * m[2] + y * m[6] + z * m[10] + m[14]; o.Cw = x * m[3] + y * m[7] + z * m[11] + m[15];
            o.S = (short)G16(p, 8); o.T = (short)G16(p, 10);
            if ((S.Geom & 0x20000) != 0)       // lighting: the colour bytes are a normal
            {
                var mv = S.Mv[S.MvTop];
                float nx = (sbyte)G8(p, 12), ny = (sbyte)G8(p, 13), nz = (sbyte)G8(p, 14);
                float tx = nx * mv[0] + ny * mv[4] + nz * mv[8], ty = nx * mv[1] + ny * mv[5] + nz * mv[9], tz = nx * mv[2] + ny * mv[6] + nz * mv[10];
                float l = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
                if (l > 0) { tx /= l; ty /= l; tz /= l; }
                float r = S.Lights[S.NumLights].R, g = S.Lights[S.NumLights].G, b = S.Lights[S.NumLights].B;
                for (int k = 0; k < S.NumLights; k++)
                {
                    float d = tx * S.Lights[k].Dx + ty * S.Lights[k].Dy + tz * S.Lights[k].Dz;
                    if (d < 0) d = 0;
                    r += S.Lights[k].R * d; g += S.Lights[k].G * d; b += S.Lights[k].B * d;
                }
                o.R = r > 1 ? 1 : r; o.G = g > 1 ? 1 : g; o.B = b > 1 ? 1 : b;
            }
            else { o.R = G8(p, 12) / 255f; o.G = G8(p, 13) / 255f; o.B = G8(p, 14) / 255f; }
            o.A = G8(p, 15) / 255f;
        }
    }

    private void FillRect(int x0, int y0, int x1, int y1)       // screen pixels (inclusive, in N64 screen space)
    {
        uint col = S.Fill;
        ushort c16 = (ushort)(col >> 16);
        float r = ((c16 >> 11) & 31) / 31f, g = ((c16 >> 6) & 31) / 31f, b = ((c16 >> 1) & 31) / 31f;
        bool full = x0 <= 0 && y0 <= 0 && x1 >= screenW - 1 && y1 >= screenH - 1;
        if (S.Cimg == S.Zimg)
        {
            if (full) { Flush(); commands.Add(new ClearDepthCommand()); }
            return;
        }
        if (full) { Flush(); commands.Add(new ClearColorCommand(r, g, b)); return; }
        uint sg = S.Geom, sl = S.OmL; S.Geom &= ~1u; S.OmL &= ~0x4000u;
        SetupState(DrawMode.VertexColor);
        float fx0 = x0 / screenW * 2 - 1, fx1 = (x1 + 1) / screenW * 2 - 1, fy0 = 1 - y0 / screenH * 2, fy1 = 1 - (y1 + 1) / screenH * 2;
        GfxVertex Q(float x, float y) => new() { X = x, Y = y, Z = 0, W = 1, R = r, G = g, B = b, A = 1 };
        var q0 = Q(fx0, fy0); var q1 = Q(fx1, fy0); var q2 = Q(fx1, fy1); var q3 = Q(fx0, fy1);
        batch.Add(q0); batch.Add(q1); batch.Add(q2); batch.Add(q0); batch.Add(q2); batch.Add(q3);
        S.Geom = sg; S.OmL = sl;
    }

    private void TexRect(uint w0, uint w1, uint h1w1, uint h2w1)
    {
        int xh = (int)((w0 >> 12) & 0xFFF), yh = (int)(w0 & 0xFFF), tile = (int)((w1 >> 24) & 7), xl = (int)((w1 >> 12) & 0xFFF), yl = (int)(w1 & 0xFFF);
        float s0 = (short)(h1w1 >> 16), t0 = (short)(h1w1 & 0xFFFF), dsdx = (short)(h2w1 >> 16), dtdy = (short)(h2w1 & 0xFFFF);
        int cycle = (int)((S.OmH >> 20) & 3); float sp = cycle == 2 ? 0.25f : 1f;
        int save = S.TexTile; S.TexTile = tile; uint sg = S.Geom; S.Geom &= ~1u;
        SetupState(cycle == 2 ? DrawMode.Copy : DrawMode.Combiner);
        var t = S.Tile[tile & 7];
        float x0 = xl / 4f, y0 = yl / 4f, x1 = xh / 4f, y1 = yh / 4f;
        if (cycle >= 2) { x1 += 1; y1 += 1; }
        float s1 = s0 + (x1 - x0) * dsdx * sp / 1024f * 32f, t1 = t0 + (y1 - y0) * dtdy / 1024f * 32f;
        float Uvx(float s) => (s / 32f - t.Uls / 4f) / curTexW;
        float Uvy(float tt) => (tt / 32f - t.Ult / 4f) / curTexH;
        float X0 = x0 / screenW * 2 - 1, X1 = x1 / screenW * 2 - 1, Y0 = 1 - y0 / screenH * 2, Y1 = 1 - y1 / screenH * 2;
        GfxVertex Q(float x, float y, float u, float v) => new() { X = x, Y = y, Z = 0, W = 1, R = 1, G = 1, B = 1, A = 1, U = u, V = v };
        var q0 = Q(X0, Y0, Uvx(s0), Uvy(t0)); var q1 = Q(X1, Y0, Uvx(s1), Uvy(t0)); var q2 = Q(X1, Y1, Uvx(s1), Uvy(t1)); var q3 = Q(X0, Y1, Uvx(s0), Uvy(t1));
        batch.Add(q0); batch.Add(q1); batch.Add(q2); batch.Add(q0); batch.Add(q2); batch.Add(q3);
        S.TexTile = save; S.Geom = sg;
    }

    // ---------------------------------------------------------------------------------------------- texture loads

    private void LoadBlock(uint w0, uint w1)
    {
        int uls = (int)((w0 >> 12) & 0xFFF), ult = (int)(w0 & 0xFFF), tile = (int)((w1 >> 24) & 7), lrs = (int)((w1 >> 12) & 0xFFF), dxt = (int)(w1 & 0xFFF);
        int bpt = S.TimgSiz == 0 ? 1 : S.TimgSiz == 1 ? 1 : S.TimgSiz == 2 ? 2 : 4;
        int texels = lrs + 1;
        int bytes = S.TimgSiz == 0 ? (texels + 1) / 2 : texels * bpt;
        uint addr = S.Timg + (uint)(S.TimgSiz == 0 ? (ult * (int)S.TimgW + uls) / 2 : (ult * (int)S.TimgW + uls) * bpt);
        uint src = Res(addr);
        int words = (bytes + 7) / 8;
        for (int j = 0; j < words; j++)
        {
            int off = S.Tile[tile].Tmem * 8 + j * 8;
            if (off + 8 > 4096) break;
            int row = dxt != 0 ? (int)(((uint)j * (uint)dxt) >> 11) : 0;
            for (int k = 0; k < 8; k++)
            {
                int from = (row & 1) != 0 ? (k < 4 ? k + 4 : k - 4) : k;
                S.Tmem[off + k] = G8(src, j * 8 + from);
            }
        }
    }

    private void LoadTile(uint w0, uint w1)
    {
        int uls = (int)(((w0 >> 12) & 0xFFF) >> 2), ult = (int)((w0 & 0xFFF) >> 2), tile = (int)((w1 >> 24) & 7), lrs = (int)(((w1 >> 12) & 0xFFF) >> 2), lrt = (int)((w1 & 0xFFF) >> 2);
        int bpt = S.TimgSiz == 2 ? 2 : S.TimgSiz == 3 ? 4 : 1; int w = lrs - uls + 1, h = lrt - ult + 1;
        int rowBytes = S.TimgSiz == 0 ? (w + 1) / 2 : w * bpt; int line = S.Tile[tile].Line * 8;
        int srcStride = S.TimgSiz == 0 ? (int)S.TimgW / 2 : (int)S.TimgW * bpt;
        uint baseA = S.Timg + (uint)((S.TimgSiz == 0 ? uls / 2 : uls * bpt) + ult * srcStride);
        for (int r = 0; r < h; r++)
        {
            uint src = Res(baseA + (uint)(r * srcStride));
            int off = S.Tile[tile].Tmem * 8 + r * line;
            if (off + rowBytes > 4096) break;
            for (int k = 0; k < rowBytes; k++) S.Tmem[off + k] = G8(src, k);
            if ((r & 1) != 0)
                for (int k = 0; k + 8 <= ((rowBytes + 7) & ~7) && off + k + 8 <= 4096; k += 8)
                    for (int q = 0; q < 4; q++) (S.Tmem[off + k + q], S.Tmem[off + k + 4 + q]) = (S.Tmem[off + k + 4 + q], S.Tmem[off + k + q]);
        }
    }

    private void LoadTlut(uint w0, uint w1)
    {
        int tile = (int)((w1 >> 24) & 7), count = (int)(((w1 >> 14) & 0x3FF) + 1);
        uint src = Res(S.Timg); int baseT = S.Tile[tile].Tmem * 8;
        for (int i = 0; i < count && baseT + i * 8 + 8 <= 4096; i++)
            for (int k = 0; k < 4; k++)
            {
                S.Tmem[baseT + i * 8 + k * 2] = G8(src, i * 2);
                S.Tmem[baseT + i * 8 + k * 2 + 1] = G8(src, i * 2 + 1);
            }
    }

    // ---------------------------------------------------------------------------------------------- the display list

    private void RunDL(uint addr, int depth)
    {
        if (depth > 12) return;
        uint p = Res(addr);
        if (p == 0) return;
        for (int guard = 0; guard < 200000; guard++, p += 8)
        {
            if (p + 8 > 0x80800000u) return;      // a display list without an end command ran off the end of RAM
            uint w0 = Mem.U32(p), w1 = Mem.U32(p + 4);
            int op = (int)(w0 >> 24);
            if (LogEnabled) opHist[op]++;
            if (traceDl) Console.Error.WriteLine($"[dl] {new string(' ', depth * 2)}{p:X8}: {w0:X8} {w1:X8}");
            switch (op)
            {
                case 0xDF: return;
                case 0xDE:
                    RunDL(w1, depth + 1);
                    if (((w0 >> 16) & 1) != 0) return;       // branch (no return)
                    break;
                case 0x01: LoadVtx(w0, w1); break;
                case 0x02:       // G_MODIFYVTX: overwrite colour / texture coordinates / screen position of a loaded vertex
                {
                    int off = (int)((w0 >> 16) & 0xFF), idx = (int)((w0 & 0xFFFF) / 2);
                    if (idx >= 0 && idx < 64)
                    {
                        ref V v = ref S.Vtx[idx];
                        if (off == 0x10) { v.R = ((w1 >> 24) & 255) / 255f; v.G = ((w1 >> 16) & 255) / 255f; v.B = ((w1 >> 8) & 255) / 255f; v.A = (w1 & 255) / 255f; }
                        else if (off == 0x14) { v.S = (short)(w1 >> 16); v.T = (short)(w1 & 0xFFFF); }
                        else if (off == 0x18)
                        {
                            float x = (short)(w1 >> 16), y = (short)(w1 & 0xFFFF);
                            float vs0 = S.Vsc[0] != 0 ? S.Vsc[0] : 1f, vs1 = S.Vsc[1] != 0 ? S.Vsc[1] : 1f;
                            v.Cw = 1f; v.Cx = (x - S.Vtr[0]) / vs0; v.Cy = (S.Vtr[1] - y) / vs1;
                        }
                        else if (off == 0x1C) v.Cz = 0f;
                    }
                    break;
                }
                case 0x05: EmitTri((int)((w0 >> 16) & 0xFF) / 2, (int)((w0 >> 8) & 0xFF) / 2, (int)(w0 & 0xFF) / 2); break;
                case 0x06: case 0x07:
                    EmitTri((int)((w0 >> 16) & 0xFF) / 2, (int)((w0 >> 8) & 0xFF) / 2, (int)(w0 & 0xFF) / 2);
                    EmitTri((int)((w1 >> 16) & 0xFF) / 2, (int)((w1 >> 8) & 0xFF) / 2, (int)(w1 & 0xFF) / 2);
                    break;
                case 0xDA:       // MTX
                {
                    int f = (int)(w0 & 0xFF) ^ 1; var m = new float[16];
                    LoadMtx(Res(w1), m);
                    if ((f & 4) != 0) { if ((f & 2) != 0) Array.Copy(m, S.Proj, 16); else MatMul(m, S.Proj, S.Proj); }
                    else
                    {
                        if ((f & 1) != 0 && S.MvTop < 9) { Array.Copy(S.Mv[S.MvTop], S.Mv[S.MvTop + 1], 16); S.MvTop++; }
                        if ((f & 2) != 0) Array.Copy(m, S.Mv[S.MvTop], 16); else MatMul(m, S.Mv[S.MvTop], S.Mv[S.MvTop]);
                    }
                    S.MvpDirty = true;
                    break;
                }
                case 0xD8: { int n = (int)((w1 >> 6) & 0x3FF); if (n < 1) n = 1; S.MvTop -= n; if (S.MvTop < 0) S.MvTop = 0; S.MvpDirty = true; break; }
                case 0xD9: S.Geom = (S.Geom & (w0 & 0xFFFFFF)) | w1; break;
                case 0xD7: S.TexOn = (int)((w0 >> 1) & 0x7F); S.TexTile = (int)((w0 >> 8) & 7); S.TexScaleS = w1 >> 16; S.TexScaleT = w1 & 0xFFFF; break;
                case 0xDB:
                {
                    int idx = (int)((w0 >> 16) & 0xFF), off = (int)(w0 & 0xFFFF);
                    if (idx == 6) segbase[(off >> 2) & 15] = w1 & 0xFFFFFF;
                    else if (idx == 2) S.NumLights = (int)(w1 / 24) > 7 ? 7 : (int)(w1 / 24);
                    break;
                }
                case 0xDC:
                {
                    int idx = (int)(w0 & 0xFF), off = (int)((w0 >> 8) & 0xFF) * 8;
                    uint d = Res(w1);
                    if (idx == 8)
                    {
                        for (int i = 0; i < 4; i++) { S.Vsc[i] = (short)G16(d, i * 2); S.Vtr[i] = (short)G16(d, 8 + i * 2); }
                    }
                    else if (idx == 10)
                    {
                        int slot = off / 24;
                        if (slot >= 2 && slot <= 9)       // LIGHT_n sits at n*24+24 (n = 1..): lights[n-1]; the ambient light follows the last one
                        {
                            ref Light l = ref S.Lights[slot - 2];
                            l.R = G8(d, 0) / 255f; l.G = G8(d, 1) / 255f; l.B = G8(d, 2) / 255f;
                            l.Dx = (sbyte)G8(d, 8) / 127f; l.Dy = (sbyte)G8(d, 9) / 127f; l.Dz = (sbyte)G8(d, 10) / 127f;
                        }
                    }
                    break;
                }
                case 0xED: Flush(); S.Sc[0] = (int)((w0 >> 12) & 0xFFF); S.Sc[1] = (int)(w0 & 0xFFF); S.Sc[2] = (int)((w1 >> 12) & 0xFFF); S.Sc[3] = (int)(w1 & 0xFFF); break;    // SETSCISSOR (10.2)
                case 0xE2: case 0xE3:
                {
                    int len = (int)(w0 & 0xFF) + 1, sft = 32 - (int)((w0 >> 8) & 0xFF) - len;
                    uint mask = (len >= 32 ? 0xFFFFFFFFu : ((1u << len) - 1)) << sft;
                    if (op == 0xE2) S.OmL = (S.OmL & ~mask) | (w1 & mask); else S.OmH = (S.OmH & ~mask) | (w1 & mask);
                    if (op == 0xE3) S.Tluts = (int)((S.OmH >> 14) & 3);
                    break;
                }
                case 0xFC: S.CombW0 = w0 & 0xFFFFFF; S.CombW1 = w1; break;
                case 0xFA: S.Prim[0] = (w1 >> 24) / 255f; S.Prim[1] = ((w1 >> 16) & 255) / 255f; S.Prim[2] = ((w1 >> 8) & 255) / 255f; S.Prim[3] = (w1 & 255) / 255f; break;
                case 0xFB: S.Env[0] = (w1 >> 24) / 255f; S.Env[1] = ((w1 >> 16) & 255) / 255f; S.Env[2] = ((w1 >> 8) & 255) / 255f; S.Env[3] = (w1 & 255) / 255f; break;
                case 0xF7: S.Fill = w1; break;
                case 0xFF:
                {
                    float nw = (w0 & 0xFFF) + 1;
                    if (nw != screenW && nw >= 64) { Flush(); screenW = nw; screenH = MathF.Floor(nw * 0.75f + 0.5f); }
                    S.Cimg = (w1 & 0x80000000u) != 0 ? w1 : (0x80000000u | (segbase[(w1 >> 24) & 15] + (w1 & 0xFFFFFF)));
                    break;
                }
                case 0xFE: S.Zimg = (w1 & 0x80000000u) != 0 ? w1 : (0x80000000u | (segbase[(w1 >> 24) & 15] + (w1 & 0xFFFFFF))); break;
                case 0xFD: S.TimgFmt = (int)((w0 >> 21) & 7); S.TimgSiz = (int)((w0 >> 19) & 3); S.TimgW = (w0 & 0xFFF) + 1; S.Timg = w1; break;
                case 0xF5:
                {
                    ref Tile t = ref S.Tile[(w1 >> 24) & 7];
                    t.Fmt = (int)((w0 >> 21) & 7); t.Siz = (int)((w0 >> 19) & 3); t.Line = (int)((w0 >> 9) & 0x1FF); t.Tmem = (int)(w0 & 0x1FF);
                    t.Pal = (int)((w1 >> 20) & 15); t.CmT = (int)((w1 >> 18) & 3); t.MaskT = (int)((w1 >> 14) & 15); t.ShiftT = (int)((w1 >> 10) & 15);
                    t.CmS = (int)((w1 >> 8) & 3); t.MaskS = (int)((w1 >> 4) & 15); t.ShiftS = (int)(w1 & 15);
                    break;
                }
                case 0xF2:
                {
                    ref Tile t = ref S.Tile[(w1 >> 24) & 7];
                    t.Uls = (int)((w0 >> 12) & 0xFFF); t.Ult = (int)(w0 & 0xFFF); t.Lrs = (int)((w1 >> 12) & 0xFFF); t.Lrt = (int)(w1 & 0xFFF);
                    break;
                }
                case 0xF3: LoadBlock(w0, w1); break;
                case 0xF4: LoadTile(w0, w1); break;
                case 0xF0: LoadTlut(w0, w1); break;
                case 0xF6: FillRect((int)((w1 >> 14) & 0x3FF), (int)((w1 >> 2) & 0x3FF), (int)(((w0 >> 12) & 0xFFF) >> 2), (int)((w0 & 0xFFF) >> 2)); break;
                case 0xE4:
                {
                    uint q = p + 8;
                    if (Mem.IsMapped(q + 8) && (Mem.U32(q) >> 24) == 0xE1) { TexRect(w0, w1, Mem.U32(q + 4), Mem.U32(q + 12)); p += 16; }   // gsSPTextureRectangle: E4 + E1 (s,t) + F1 (dsdx,dtdy)
                    else TexRect(w0, w1, 0, 0x04000400u);                                                                                    // this game emits a bare E4: texture origin 0, one texel per pixel
                    break;
                }
                default: break;     // NOOP, SYNCs, RDPHALF, SETCONVERT, SETPRIMDEPTH, SETFOGCOLOR, SETBLENDCOLOR ...
            }
        }
    }

    /// <summary>Interprets the display list of a graphics OSTask (guest address). Returns the draw commands in order.</summary>
    public IReadOnlyList<GfxCommand> Run(uint taskAddr)
    {
        uint dl = Mem.U32(taskAddr + 0x30);       // OSTask.data_ptr
        S = new RdpState();
        S.Sc[2] = (int)(screenW * 4); S.Sc[3] = (int)(screenH * 4);
        for (int i = 0; i < 4; i++) S.Vsc[i] = i == 0 ? 640f : i == 1 ? 480f : i == 2 ? 511f : 0f;
        S.Vtr[0] = 640f; S.Vtr[1] = 480f; S.Vtr[2] = 511f;
        MatIdent(S.Proj); foreach (var m in S.Mv) MatIdent(m); S.MvpDirty = true;
        curValid = false; batch.Clear(); commands.Clear(); cur = new DrawState();
        if (LogEnabled) Array.Clear(opHist);
        bool dumpNow = DumpFlag != null && File.Exists(DumpFlag);
        if (dumpNow) { try { File.Delete(DumpFlag!); } catch (IOException) { } }
        traceDl = LogEnabled && (dumpNow || runs + 1 == DumpRun) && Environment.GetEnvironmentVariable("TRW_GFX_DL") != null;      // TRW_GFX_DL=1: print every command of the dumped list
        RunDL(dl, 0);
        Flush();
        if (LogEnabled && (++runs <= 5 || runs % 60 == 0 || runs == DumpRun || dumpNow))
        {
            try
            {
            int draws = commands.Count(c => c is DrawCommand), verts = commands.OfType<DrawCommand>().Sum(c => c.Vertices.Length);
                var ops = string.Join(" ", Enumerable.Range(0, 256).Where(i => opHist[i] > 0).Select(i => $"{i:X2}x{opHist[i]}"));
                Console.Error.WriteLine($"[gfx] run {runs}: task {taskAddr:X8} dl {dl:X8} size {Mem.U32(taskAddr + 0x34):X} -> {commands.Count} commands, {draws} draws, {verts} vertices; ops {ops}");
                Console.Error.WriteLine($"[gfx]   first words: " + string.Join(" ", Enumerable.Range(0, 8).Select(i => $"{Mem.U32(Res(dl) + (uint)i * 4):X8}")));
                if (runs == DumpRun || dumpNow)
                {
                    var seen = new HashSet<ulong>();
                    foreach (var d in commands.OfType<DrawCommand>().Where(d => d.State.Tex0 != null))
                    {
                        var t = d.State.Tex0!;
                        if (!seen.Add(t.Key)) continue;
                        int lit = 0, opaque = 0;
                        for (int i = 0; i < t.Width * t.Height; i++) { if ((t.Rgba[i * 4] | t.Rgba[i * 4 + 1] | t.Rgba[i * 4 + 2]) != 0) lit++; if (t.Rgba[i * 4 + 3] != 0) opaque++; }
                        Console.Error.WriteLine($"[gfx]   texture {t.Width}x{t.Height} key={t.Key:X16} non-black {lit} non-transparent {opaque} of {t.Width * t.Height}");
                    }
                }
                if (runs == DumpRun || dumpNow)
                    foreach (var c in commands)
                    {
                        if (c is not DrawCommand d) { Console.Error.WriteLine($"[gfx]   {c}"); continue; }
                        var st = d.State; var v = d.Vertices;
                        Console.Error.WriteLine($"[gfx]   draw mode={st.Mode} tex={st.TextureOn}({(st.Tex0 == null ? "-" : st.Tex0.Width + "x" + st.Tex0.Height)}) cc={st.CombineW0:X8}/{st.CombineW1:X8} other={st.OtherHigh:X8}/{st.OtherLow:X8} prim=({string.Join(",", st.Prim)}) env=({string.Join(",", st.Env)}) tex1={(st.Tex1 == null ? "-" : st.Tex1.Width + "x" + st.Tex1.Height + " key " + st.Tex1.Key.ToString("X") + " a>0:" + Enumerable.Range(0, st.Tex1.Width * st.Tex1.Height).Count(q => st.Tex1.Rgba[q * 4 + 3] != 0))} same={(st.Tex0 == st.Tex1)} bbox=({v.Min(q => q.X):F2},{v.Min(q => q.Y):F2})-({v.Max(q => q.X):F2},{v.Max(q => q.Y):F2}) col0=({v[0].R:F2},{v[0].G:F2},{v[0].B:F2},{v[0].A:F2}) n={v.Length}");
                    }
                if (runs <= 2)
                    foreach (var c in commands.Take(12))
                    {
                        if (c is not DrawCommand d) { Console.Error.WriteLine($"[gfx]   {c}"); continue; }
                        var st = d.State; var v = d.Vertices;
                        Console.Error.WriteLine($"[gfx]   draw mode={st.Mode} tex={st.TextureOn} geom={st.Geom:X} cc={st.CombineW0:X8}/{st.CombineW1:X8} other={st.OtherHigh:X8}/{st.OtherLow:X8} prim=({string.Join(",", st.Prim)}) env=({string.Join(",", st.Env)}) tex0={(st.Tex0 == null ? "-" : st.Tex0.Width + "x" + st.Tex0.Height)} sc={st.ScissorX0},{st.ScissorY0}-{st.ScissorX1},{st.ScissorY1} screen={st.ScreenWidth}x{st.ScreenHeight} verts={v.Length}");
                        for (int i = 0; i < Math.Min(6, v.Length); i++)
                            Console.Error.WriteLine($"[gfx]     v{i}: pos=({v[i].X:F3},{v[i].Y:F3},{v[i].Z:F3},{v[i].W:F3}) col=({v[i].R:F2},{v[i].G:F2},{v[i].B:F2},{v[i].A:F2}) uv=({v[i].U:F3},{v[i].V:F3})");
                    }
            }
            catch (Exception e) { Console.Error.WriteLine("[gfx] dump failed: " + e); }
        }
        return commands.ToArray();
    }

    private static readonly bool LogEnabled = Environment.GetEnvironmentVariable("TRW_GFX_LOG") != null;
    private static readonly string? DumpFlag = Environment.GetEnvironmentVariable("TRW_GFX_DUMP_FLAG");   // while this file exists the next display list is listed in full (and the file removed)
    private static readonly int DumpRun = int.TryParse(Environment.GetEnvironmentVariable("TRW_GFX_DUMP_RUN"), out var dr) ? dr : 60;   // display list number whose draws are listed in full
    private readonly int[] opHist = new int[256];
    private int runs;
    private bool traceDl;
}
