// The RDP's colour combiner: out = (A - B) * C + D, one or two cycles, for colour and alpha separately.
// Selector values arrive as floats (the C# side converts the combiner words): see DisplayListInterpreter / GfxRenderer.
//
// The selectors are written as one-hot sums (K(s, k) is 1 when the selector equals k) instead of chains of ?: operators: the chained
// conditional form compiled to wrong alpha results in MonoGame's OpenGL translation of this shader.
#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4 C0;   // cycle 0 colour selectors (A, B, C, D)
float4 A0;   // cycle 0 alpha selectors (A, B, C, D)
float4 C1;
float4 A1;
float4 Prim;
float4 Env;
float4 Flags;   // x: mode (0 combiner, 1 vertex colour, 2 copy; debug: 3 = second texture)  y: two cycles  z: alpha-test threshold  w: texture on

sampler2D Tex0 : register(s0);
sampler2D Tex1 : register(s1);

struct VI { float4 pos : POSITION0; float4 col : COLOR0; float2 uv : TEXCOORD0; };
struct PI { float4 pos : SV_POSITION; float4 col : COLOR0; float2 uv : TEXCOORD0; };

PI VS(VI i) { PI o; o.pos = i.pos; o.col = i.col; o.uv = i.uv; return o; }

float K(float s, float k) { return step(abs(s - k), 0.1); }

// colour inputs. A: 0 combined, 1 texel0, 2 texel1, 3 prim, 4 shade, 5 env, 6 one, 7 noise (0.5)
float3 selA(float s, float3 cb, float3 t0, float3 t1, float3 pr, float3 sh, float3 en)
{
    return cb * K(s, 0) + t0 * K(s, 1) + t1 * K(s, 2) + pr * K(s, 3) + sh * K(s, 4) + en * K(s, 5) + float3(1, 1, 1) * K(s, 6) + float3(0.5, 0.5, 0.5) * K(s, 7);
}
// B: 0..5 as A, everything else 0
float3 selB(float s, float3 cb, float3 t0, float3 t1, float3 pr, float3 sh, float3 en)
{
    return cb * K(s, 0) + t0 * K(s, 1) + t1 * K(s, 2) + pr * K(s, 3) + sh * K(s, 4) + en * K(s, 5);
}
// C: 0..5 as A, 7 combined alpha, 8 texel0 alpha, 9 texel1 alpha, 10 prim alpha, 11 shade alpha, 12 env alpha, everything else 0
float3 selC(float s, float3 cb, float ca, float3 t0, float t0a, float3 t1, float t1a, float3 pr, float pra, float3 sh, float sha, float3 en, float ena)
{
    return cb * K(s, 0) + t0 * K(s, 1) + t1 * K(s, 2) + pr * K(s, 3) + sh * K(s, 4) + en * K(s, 5)
         + float3(ca, ca, ca) * K(s, 7) + float3(t0a, t0a, t0a) * K(s, 8) + float3(t1a, t1a, t1a) * K(s, 9)
         + float3(pra, pra, pra) * K(s, 10) + float3(sha, sha, sha) * K(s, 11) + float3(ena, ena, ena) * K(s, 12);
}
// D: 0..5 as A, 6 one, 7 zero
float3 selD(float s, float3 cb, float3 t0, float3 t1, float3 pr, float3 sh, float3 en)
{
    return cb * K(s, 0) + t0 * K(s, 1) + t1 * K(s, 2) + pr * K(s, 3) + sh * K(s, 4) + en * K(s, 5) + float3(1, 1, 1) * K(s, 6);
}
// alpha A/B/D: 0 combined, 1 texel0, 2 texel1, 3 prim, 4 shade, 5 env, 6 one, 7 zero
float selAa(float s, float ca, float t0a, float t1a, float pra, float sha, float ena)
{
    return ca * K(s, 0) + t0a * K(s, 1) + t1a * K(s, 2) + pra * K(s, 3) + sha * K(s, 4) + ena * K(s, 5) + K(s, 6);
}
// alpha C: 0 lod fraction (0), 1 texel0, 2 texel1, 3 prim, 4 shade, 5 env, 6 prim lod fraction (0), 7 zero
float selAc(float s, float t0a, float t1a, float pra, float sha, float ena)
{
    return t0a * K(s, 1) + t1a * K(s, 2) + pra * K(s, 3) + sha * K(s, 4) + ena * K(s, 5);
}

float4 cycle(float4 c, float4 a, float4 comb, float4 t0, float4 t1, float4 sh)
{
    float3 A = selA(c.x, comb.rgb, t0.rgb, t1.rgb, Prim.rgb, sh.rgb, Env.rgb);
    float3 B = selB(c.y, comb.rgb, t0.rgb, t1.rgb, Prim.rgb, sh.rgb, Env.rgb);
    float3 C = selC(c.z, comb.rgb, comb.a, t0.rgb, t0.a, t1.rgb, t1.a, Prim.rgb, Prim.a, sh.rgb, sh.a, Env.rgb, Env.a);
    float3 D = selD(c.w, comb.rgb, t0.rgb, t1.rgb, Prim.rgb, sh.rgb, Env.rgb);
    float aA = selAa(a.x, comb.a, t0.a, t1.a, Prim.a, sh.a, Env.a);
    float aB = selAa(a.y, comb.a, t0.a, t1.a, Prim.a, sh.a, Env.a);
    float aC = selAc(a.z, t0.a, t1.a, Prim.a, sh.a, Env.a);
    float aD = selAa(a.w, comb.a, t0.a, t1.a, Prim.a, sh.a, Env.a);
    return saturate(float4((A - B) * C + D, (aA - aB) * aC + aD));
}

float4 PS(PI i) : COLOR0
{
    float4 t0 = Flags.w > 0.5 ? tex2D(Tex0, i.uv) : float4(1, 1, 1, 1);
    float4 t1 = Flags.w > 0.5 ? tex2D(Tex1, i.uv) : float4(1, 1, 1, 1);
    float4 o;
    if (Flags.x > 2.5) o = float4(t1.rgb, 1);        // debug (TRW_GFX_FORCE_MODE=3): the second texture
    else if (Flags.x > 1.5) o = t0;                  // copy cycle: the texel is the pixel
    else if (Flags.x > 0.5) o = i.col;               // flat fill
    else
    {
        // separate variables: feeding o back into its own assignment (o = cycle(.., o, ..)) lost the alpha in the GL translation of this shader
        float4 o0 = cycle(C0, A0, float4(0, 0, 0, 0), t0, t1, i.col);
        float4 o1 = cycle(C1, A1, o0, t0, t1, i.col);
        o = Flags.y > 0.5 ? o1 : o0;
    }
    clip(o.a - Flags.z);
    return o;
}

technique Combiner
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL VS();
        PixelShader = compile PS_SHADERMODEL PS();
    }
}
