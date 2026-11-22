#ifndef GOT_GRASS_TERRAIN_INCLUDED
#define GOT_GRASS_TERRAIN_INCLUDED

// Match MeshHeightLayout and MeshGeometry.cginc. LOD0 vertices use 1 m
// cells, and the observer metric reads the fixed 16 m height layer.
#define GOT_TERRAIN_WORLD_SIZE 10240.0
#define GOT_TERRAIN_HEIGHT_SIDE 1295

Texture2DArray<float> _MeshHeightClip;
SamplerState sampler_MeshHeightClip;
float4 _MeshClipOrigins[5];
float _MeshHeightScale;
float _GotTerrainMinHeight, _GotTerrainMinNormalY;
float _GotTerrainMorphStart, _GotTerrainVerticalScale;
float4 _GotTerrainObserver;

Texture2DArray<float4> _DDControlClip;
Texture2D<float4> _DDControlResident;
float4 _DDControlOrigins[6], _DDControlInfo;
struct DDControls
{
    int3 ids;
    float3 opacity;
    int3 bottom, top;
    float3 coverage, bary;
    int count, level;
    float invalid;
};
#include "../../Shaders/Delta/DeltaControls.cginc"

float4 _GotTerrainGrassMaterials; // terrain material A/B, grass species A/B
float4 _GotTerrainGrassDensity; // relative densities A/B
float4 _GotTerrainGrassHeight; // normalized source grass height A/B

bool GotGrassInTerrain(float2 xz)
{
    return all(xz>=0) && all(xz<GOT_TERRAIN_WORLD_SIZE);
}

bool GotGrassClipCovered(float2 lowWorld,float2 highWorld,int level)
{
    float4 ring=_MeshClipOrigins[level];
    int stride=1<<level;
    return ring.w>.5 && ring.z==stride && all(lowWorld>=ring.xy) &&
        all(highWorld<=ring.xy+(GOT_TERRAIN_HEIGHT_SIDE-1)*stride);
}

// Sample the exact source vertex height at the centre of a toroidal texel.
float GotGrassLoadHeight(int2 vertex)
{
    return _MeshHeightClip.Load(int4(vertex%GOT_TERRAIN_HEIGHT_SIDE,0,0))*_MeshHeightScale;
}

bool GotGrassTerrainFace(float2 xz,out float height,out float2 gradient)
{
    height=0;
    gradient=0;
    int2 cell=(int2)floor(xz);
    if(!GotGrassInTerrain(xz) || !GotGrassClipCovered(cell,cell+1,0))return false;

    float2 fraction=xz-cell;
    float b=GotGrassLoadHeight(cell+int2(0,1));
    float c=GotGrassLoadHeight(cell+int2(1,0));
    // MeshCdlod.BuildPatchMesh uses a,b,c then c,b,d. The diagonal is x+z=1.
    if(fraction.x+fraction.y<=1)
    {
        float a=GotGrassLoadHeight(cell);
        gradient=float2(c-a,b-a);
        height=a+dot(fraction,gradient);
    }
    else
    {
        float d=GotGrassLoadHeight(cell+1);
        gradient=float2(d-b,d-c);
        height=d-dot(1-fraction,gradient);
    }
    return height>=_GotTerrainMinHeight && rsqrt(1+dot(gradient,gradient))>=_GotTerrainMinNormalY;
}

float GotGrassBeforeMorph(float2 xz)
{
    float2 grid=xz/16;
    float2 lowWorld=floor(grid)*16;
    if(!GotGrassClipCovered(lowWorld,lowWorld+16,4))return 0;
    float2 uv=frac((grid+.5)/GOT_TERRAIN_HEIGHT_SIDE);
    float height=_MeshHeightClip.SampleLevel(sampler_MeshHeightClip,float3(uv,4),0)*_MeshHeightScale;
    float3 delta=float3(xz.x,height,xz.y)-_GotTerrainObserver.xyz;
    delta.y*=_GotTerrainVerticalScale;
    // Fade out before terrain vertices morph out of the 1 m source grid.
    return saturate((_GotTerrainMorphStart-length(delta))*.125);
}

float2 GotGrassMaterialWeights(float2 xz)
{
    DDControls control=DDReadControls(xz);
    if(control.invalid>.01)return 0;
    float2 weight=0;
    int2 material=(int2)round(_GotTerrainGrassMaterials.xy);
    [unroll]for(int i=0;i<3;i++)
    {
        float2 bottom=float2(control.bottom[i]==material.x,control.bottom[i]==material.y);
        float2 top=float2(control.top[i]==material.x,control.top[i]==material.y);
        weight+=lerp(bottom,top,control.coverage[i])*control.bary[i];
    }
    return saturate(weight)*saturate(_GotTerrainGrassDensity.xy);
}

#endif
