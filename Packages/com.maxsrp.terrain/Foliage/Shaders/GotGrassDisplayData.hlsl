#ifndef MAX_GRASS_WORLD_DISPLAY_DATA_INCLUDED
#define MAX_GRASS_WORLD_DISPLAY_DATA_INCLUDED

#include "GotGrassWorldRecord.hlsl"

// 48 bytes. Keep view-dependent inputs out of the resident grass record.
struct WorldGrassDisplayRecord
{
    WorldGrassRecord Grass;
    float Wind;
    float WidthFactor;
    uint Reserved;
    uint Bin;
};

#endif
