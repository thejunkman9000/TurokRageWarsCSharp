using System.Buffers.Binary;
using Trw.Tools.Models;

namespace Trw.Tools.LevelGeo;

/// <summary>
/// Which parts / triangles of a placed static level object the game really draws, and how (see docs/formats/level_visibility.md).
/// Pure functions, no state. Source of every rule: func_00289054 (sector draw), func_002500A4 (distance LOD), func_0025021C (static object
/// draw), func_0026DA4C (part loop) and func_00269A80 (per-part material setup, which also returns "draw this part?").
///
/// Summary of the verified result: the game applies NO per-part or per-placement selection to the static level objects in the ROM
/// (every placement of every one of the 37 levels and every part of every model passes). The only run-time culling is view-dependent
/// (frustum / sector rectangle). What makes a level look "walled in on all sides" is that every wall/floor is a ONE-SIDED quad that the
/// game draws with back-face culling; the exporter must therefore cull back faces with the correct winding (<see cref="FrontFaceIsClockwise"/>).
/// </summary>
public static class Visibility
{
    /// <summary>
    /// Winding of FRONT faces in the exported world (right-handed, +Y up, the vertex order of LevelGeoData / world.obj): CLOCKWISE when viewed
    /// from the front (cross(v1-v0, v2-v0) points AWAY from the viewer). [V: of 3,900 axis-aligned horizontal first triangles in all 37 levels
    /// the front normal -cross(v1-v0,v2-v0) is +Y in every one, none is -Y, i.e. floors are CW seen from above.]
    /// Consumers using a CCW-front API (OpenGL default, OBJ viewers) must swap two indices of every triangle (see <see cref="FrontFaceOrder"/>).
    /// </summary>
    public const bool FrontFaceIsClockwise = true;

    /// <summary>Game detail/mode mask D_801462D0 (0x8, 0x10 or 0x20; 0x20 = default full). Object definitions carry a mask at +4.</summary>
    public const uint ModeMaskDefault = 0x20;

    /// <summary>Triangle vertex order to emit for a CCW-front renderer / OBJ with back-face culling: (a, c, b).</summary>
    public static (int A, int B, int C) FrontFaceOrder(int a, int b, int c) => (a, c, b);

    /// <summary>
    /// Is the placement drawn at all (view independent part of func_00289054 / func_0025021C)? Rules, all of which pass for every shipped placement:
    /// actor flag 0x40 = hidden (func_0025021C first test; never set in the ROM); object-definition mask (def +4, u32) AND the current
    /// mode mask must be non-zero (func_00289054: <c>def->unk4 &amp; D_801462D0.mask</c>; the mask is 0x139 for definitions 0..52 (0x39 for def 51), which contains all of 0x8, 0x10 and 0x20).
    /// <paramref name="def"/> may be null (then only the actor flag is tested).
    /// </summary>
    public static bool PlacementDrawn(Placement p, ObjectDef? def, uint modeMask = ModeMaskDefault)
    {
        if ((p.Flags & 0x40) != 0) return false;
        if (def != null && def.Raw.Length >= 8)
        {
            uint m = BinaryPrimitives.ReadUInt32BigEndian(def.Raw.AsSpan(4));
            if ((m & modeMask) == 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Is this model part drawn? func_00269A80 returns 0 (part skipped, nothing emitted) only when material flag 0x100 (blend/translucent)
    /// is set AND the material alpha (colour1 A, material +0xF) is 0; every other path returns 1. Also false when the placement is hidden.
    /// No placement-flag bit (0..6 in the data) and no material bit other than 0x100 influences drawing.
    /// </summary>
    public static bool PartDrawn(Placement p, ModelMaterial m, ObjectDef? def = null, uint modeMask = ModeMaskDefault)
    {
        if (!PlacementDrawn(p, def, modeMask)) return false;
        if ((m.Flags & 0x100) != 0 && m.Color1[3] == 0) return false;
        return true;
    }

    /// <summary>
    /// Does the part use back-face culling? func_00269A80 builds the geometry mode: lighting + G_CULL_BACK (0x20400) is set when
    /// (flags &amp; 0x42) == 0 or (flags &amp; 4) == 0. So culling is OFF (two-sided, unlit) only when flag 0x4 AND (0x2 or 0x40) are set.
    /// No material of any static object in any level has flag 0x4 (flags seen 0x48,0x49,0x4A,0x148,0x548,0x848,0x30000048): all parts are culled.
    /// </summary>
    public static bool BackFaceCulled(ModelMaterial m) => !((m.Flags & 0x4) != 0 && (m.Flags & 0x42) != 0);

    /// <summary>Material flag 0x100: alpha-blended part (drawn in the sorted/translucent pass; flags 0x700 select that pass in func_0026BC60).</summary>
    public static bool IsBlended(ModelMaterial m) => (m.Flags & 0x100) != 0;

    /// <summary>Texture addressing from material flags: 0x20 = clamp, 0x800 = mirror (func_00269A80 -> func_00295FB4, applied to both S and T). [V code, H naming]</summary>
    public static (bool Clamp, bool Mirror) TextureAddressing(ModelMaterial m) => ((m.Flags & 0x20) != 0, (m.Flags & 0x800) != 0);
}
