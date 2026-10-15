// Functions that tools/regen.ps1 does not produce any more but the game reaches (hand-kept outside Generated/ so a regeneration cannot drop them).
// The six func_ files next to this one were recompiled from the MIPS by an earlier mips2cs run: they are only reached through data tables (function
// pointers), which the current recompile pass does not discover. func_0041D694 is called but defined nowhere (the C port stubs it as well).
// If a later regeneration produces any of them again, the build fails with a duplicate definition: delete the copy here.
using Trw.Platform;

namespace Trw.Game;

public static partial class Game
{
    public static void RegisterPreserved()
    {
        Funcs.Add(0x00211014U, "func_00211014", (a0, a1, a2, a3) => Ai_UpdateRushAttackTail_00211014(a0, a1, a2, a3));
        Funcs.Add(0x002627B4U, "func_002627B4", (a0, a1, a2, a3) => func_002627B4(a0, a1, a2, a3));
        Funcs.Add(0x00403E88U, "func_00403E88", (a0, a1, a2, a3) => func_00403E88(a0, a1, a2, a3));
        Funcs.Add(0x0041C67CU, "func_0041C67C", (a0, a1, a2, a3) => func_0041C67C(a0, a1, a2, a3));
        Funcs.Add(0x002C2040U, "func_002C2040", (a0, a1, a2, a3) => func_002C2040(a0, a1, a2, a3));
        Funcs.Add(0x002C205CU, "func_002C205C", (a0, a1, a2, a3) => func_002C205C(a0, a1, a2, a3));
    }

    public static uint func_0041D694(uint a0 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0) { Stub("func_0041D694"); return 0; }
}
