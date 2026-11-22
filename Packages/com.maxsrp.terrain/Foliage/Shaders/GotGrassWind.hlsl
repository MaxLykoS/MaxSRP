// Readable arithmetic reconstruction from 0039_cs.dxil.ll / 0040_cs.dxil.ll.
// Source arithmetic retained; Unity bindings and time/strength inputs are adapters.
// Physical source lines 1431-2020; source tables are physical lines 71-72.
// Returned components are current-frame and previous-frame wind magnitude.
#ifndef SP_GRASS_GOT_WIND_INCLUDED
#define SP_GRASS_GOT_WIND_INCLUDED

static const uint GOTWindPerm[256] = {
    151, 160, 137, 91, 90, 15, 131, 13, 201, 95, 96, 53, 194, 233, 7, 225,
    140, 36, 103, 30, 69, 142, 8, 99, 37, 240, 21, 10, 23, 190, 6, 148,
    247, 120, 234, 75, 0, 26, 197, 62, 94, 252, 219, 203, 117, 35, 11, 32,
    57, 177, 33, 88, 237, 149, 56, 87, 174, 20, 125, 136, 171, 168, 68, 175,
    74, 165, 71, 134, 139, 48, 27, 166, 77, 146, 158, 231, 83, 111, 229, 122,
    60, 211, 133, 230, 220, 105, 92, 41, 55, 46, 245, 40, 244, 102, 143, 54,
    65, 25, 63, 161, 1, 216, 80, 73, 209, 76, 132, 187, 208, 89, 18, 169,
    200, 196, 135, 130, 116, 188, 159, 86, 164, 100, 109, 198, 173, 186, 3, 64,
    52, 217, 226, 250, 124, 123, 5, 202, 38, 147, 118, 126, 255, 82, 85, 212,
    207, 206, 59, 227, 47, 16, 58, 17, 182, 189, 28, 42, 223, 183, 170, 213,
    119, 248, 152, 2, 44, 154, 163, 70, 221, 153, 101, 155, 167, 43, 172, 9,
    129, 22, 39, 253, 19, 98, 108, 110, 79, 113, 224, 232, 178, 185, 112, 104,
    218, 246, 97, 228, 251, 34, 242, 193, 238, 210, 144, 12, 191, 179, 162, 241,
    81, 51, 145, 235, 249, 14, 239, 107, 49, 192, 214, 31, 181, 199, 106, 157,
    184, 84, 204, 176, 115, 121, 50, 45, 127, 4, 150, 254, 138, 236, 205, 93,
    222, 114, 67, 29, 24, 72, 243, 141, 128, 195, 78, 66, 215, 61, 156, 180
};

static const float4 GOTWindGrad[32] = {
    float4(0, -1, -1, -1),
    float4(0, -1, -1, 1),
    float4(0, -1, 1, -1),
    float4(0, -1, 1, 1),
    float4(0, 1, -1, -1),
    float4(0, 1, -1, 1),
    float4(0, 1, 1, -1),
    float4(0, 1, 1, 1),
    float4(-1, 0, -1, -1),
    float4(-1, 0, -1, 1),
    float4(-1, 0, 1, -1),
    float4(-1, 0, 1, 1),
    float4(1, 0, -1, -1),
    float4(1, 0, -1, 1),
    float4(1, 0, 1, -1),
    float4(1, 0, 1, 1),
    float4(-1, -1, 0, -1),
    float4(-1, -1, 0, 1),
    float4(-1, 1, 0, -1),
    float4(-1, 1, 0, 1),
    float4(1, -1, 0, -1),
    float4(1, -1, 0, 1),
    float4(1, 1, 0, -1),
    float4(1, 1, 0, 1),
    float4(-1, -1, -1, 0),
    float4(-1, -1, 1, 0),
    float4(-1, 1, -1, 0),
    float4(-1, 1, 1, 0),
    float4(1, -1, -1, 0),
    float4(1, -1, 1, 0),
    float4(1, 1, -1, 0),
    float4(1, 1, 1, 0)
};

// Every original permutation lookup masks its index to 255. The second copy of
// the original 512-entry table is therefore redundant in this shader.
uint GOTWindPermutation(int x)
{
    return GOTWindPerm[x & 255];
}

float GOTWindGradient(int3 lattice, float3 delta)
{
    uint h = GOTWindPermutation(lattice.x);
    h = GOTWindPermutation(h + lattice.y);
    h = GOTWindPermutation(h + lattice.z);
    // The compiled code performs a float4 dot with delta.w = 0.
    return dot(GOTWindGrad[h & 31], float4(delta, 0));
}

float GOTWindPerlin3(float3 p)
{
    float3 latticeFloat = floor(p);
    int3 lattice = (int3)latticeFloat;
    float3 f = p - latticeFloat;
    float3 f2 = f * f;
    float3 f3 = f2 * f;
    float3 fade = f3 * (10 - 15 * f + 6 * f2);

    float n000 = GOTWindGradient(lattice + int3(0,0,0), f);
    float n100 = GOTWindGradient(lattice + int3(1,0,0), f - float3(1,0,0));
    float n010 = GOTWindGradient(lattice + int3(0,1,0), f - float3(0,1,0));
    float n110 = GOTWindGradient(lattice + int3(1,1,0), f - float3(1,1,0));
    float n001 = GOTWindGradient(lattice + int3(0,0,1), f - float3(0,0,1));
    float n101 = GOTWindGradient(lattice + int3(1,0,1), f - float3(1,0,1));
    float n011 = GOTWindGradient(lattice + int3(0,1,1), f - float3(0,1,1));
    float n111 = GOTWindGradient(lattice + int3(1,1,1), f - float3(1,1,1));

    // Keep the original arithmetic grouping of lines 1688-1708. This equals
    // trilinear interpolation with a quintic fade along each axis.
    float x00 = (n100 - n000) * fade.x + n000;
    float x10Delta = (n110 - n010) * fade.x;
    float x01 = (n101 - n001) * fade.x + n001;
    float x11Delta = (n111 - n011) * fade.x;
    float y0Delta = (n010 - x00) + x10Delta;
    float y1Delta = (n011 - x01) + x11Delta;
    float xy0 = y0Delta * fade.y + x00;
    float xy1Delta = (x01 - xy0) + y1Delta * fade.y;
    return fade.z * xy1Delta + xy0;
}

// Runtime adapters for the original resource-table wind fields. Noise scale
// is measured per source centimetre. These values are not recovered game art.
float4 _GoTWindCurrent;    // offsetXY, zTime, baseAmplitude
float4 _GoTWindNoise;      // xyScale, gustAmount, thresholdScale
float _GoTWindThresholdBias;
int _WindEnabled;
float4 _Wind;

float SPGoTStoredWind(float2 absoluteMetres, float2 bladeHash, float amplitude)
{
    if(amplitude==0 || _WindEnabled==0 || _Wind.z<=0 || _GoTWindCurrent.w<=0)return 0;
    float2 p=absoluteMetres*100.0+bladeHash*100.0;
    float2 xy=p*_GoTWindNoise.xy+_GoTWindCurrent.xy;
    float noise01=GOTWindPerlin3(float3(xy,_GoTWindCurrent.z))*.5+.5;
    float gust=saturate(noise01*_GoTWindNoise.w+_GoTWindThresholdBias);
    float wind=(gust*_GoTWindCurrent.w)*_GoTWindNoise.z+_GoTWindCurrent.w;
    return amplitude*asfloat(0x391ee00a)*wind;
}
#endif
