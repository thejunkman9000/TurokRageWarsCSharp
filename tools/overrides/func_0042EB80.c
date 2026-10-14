/* PORT (C# port) override of src/ovl_a/func_0042EB80.c (arena / game-type setter): unchanged, plus the test switch TRW_ARENA=n[tT] (pins the arena and
   optionally the type, e.g. a trial map: the game picks arenas at random, so map-specific bugs need it; host code in src/Trw.Game/Os/TestHooks.cs). */
#include "o04_types.h"
extern s32 D_8015402C;
s32 trw_arena_index(s32 type, s32 index);
s32 trw_arena_type(s32 type);
void func_0042EB80(s32 a, s32 b) {
    s32 *p = &D_8015402C;
    b = trw_arena_index(a, b);
    a = trw_arena_type(a);
    *p = b;
    p[-1] = a;
}
