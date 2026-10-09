#include "b20_types.h"
void *func_0028FD94(void *tree, s32 idx);
/* port: the matching C calls func_0028FD94(lv->unk78) without the index (it only worked because a1 still held idx);
   pass the index explicitly */
void *func_0028CF48(Level *lv, s32 idx) {
    if (idx == -1) {
        return 0;
    }
    return func_0028FD94(lv->unk78, idx);
}
