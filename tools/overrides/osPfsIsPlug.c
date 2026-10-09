#include "common.h"

/* No controller pak is ever inserted: report an empty pattern and success.
   (The original polls the PIF over the SI, which the C# build does not emulate.) */
s32 osPfsIsPlug(OSMesgQueue *queue, u8 *pattern) {
    *pattern = 0;
    return 0;
}
