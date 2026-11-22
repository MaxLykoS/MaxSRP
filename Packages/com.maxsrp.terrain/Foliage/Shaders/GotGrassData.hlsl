#ifndef SP_GRASS_GOT_DATA_INCLUDED
#define SP_GRASS_GOT_DATA_INCLUDED
struct SPGrassBlade
{
    float3 Position;
    float2 Facing;
    float WindStrength;
    float Hash;
    float GrassType;
    float2 ClumpFacing;
    float ClumpColor;
    float Height;
    float Width;
    float Tilt;
    float Bend;
    float SideCurve;
};

// Recovered type record is 236 B. Resource descriptors at 0/32/64 are
// replaced by named Unity textures; the remaining numeric offsets are kept.
struct SPGoTType { float v[59]; };
StructuredBuffer<SPGoTType> _GoTTypes;
int _GoTTypeCount;
float4 _GoTGeneration; // density, distance thinning start/end (m), clump shape
float4 _GoTSpawn; // jitter in cells, slope up component, coordinate mode, reserved
float4 _GoTTerrainParams; // height scale/offset (m), displacement amplitude, enabled
float4 _GoTMapFlags; // type/facing/displacement/depth-hierarchy texture enabled
#endif
