#ifndef MAX_GRASS_WORLD_RECORD_INCLUDED
#define MAX_GRASS_WORLD_RECORD_INCLUDED

#include "GotGrassData.hlsl"

int _Seed;
float2 _MaxLatticeOrigin;
float _MaxSpacing;

#define MAX_GRASS_WORLD_PATCH_SIDE 82u
#define MAX_GRASS_WORLD_PATCH_CAPACITY 6724u
#define MAX_GRASS_WORLD_PATCHES_PER_TILE 64u

// 32 bytes. Position offsets are relative to the deterministic pre-clump
// wind root so their precision is independent of the distance from world zero.
struct WorldGrassRecord
{
    uint2 WorldId;
    uint RootOffsetXZ;
    float RootY;
    uint HeightWidth;
    uint TiltBend;
    uint CurveClump;
    uint FacingTypeFlags;
};

uint2 MaxGrassWorldSeedKey(int2 worldGrid)
{
    return uint2(worldGrid+int2((uint)_Seed&65535u,((uint)_Seed>>16)&65535u))&65535u;
}

float4 MaxGrassSeedHash(uint2 key)
{
    float2 k=(float2)key,p=k*0.6180340051651001;
    float2 q=p-floor(k*0.008704704232513905)*71+float2(26,161);
    float2 r=p+float2(27,162)-floor(k*0.008704704232513905+0.014084506779909134)*71;
    q*=q;r*=r;
    float s=q.x*0.0010513747110962868;
    return frac(float4(s*q.y,q.y*0.0010513747110962868*r.x,s*r.y,r.x*0.0010513747110962868*r.y));
}

uint MaxGrassJitterLayer(uint2 key)
{
    uint lanes=key.x|key.y|64u;
    return lanes&(~lanes+1u);
}

float2 MaxGrassJitter(float2 hash,float scale)
{
    float2 jitter=hash*(scale*1.350000023841858);
    return float2(jitter.x>scale?jitter.x-scale:jitter.x,
                  jitter.y>scale?jitter.y-scale:jitter.y);
}

float2 MaxGrassWorldWindRoot(int2 worldGrid,uint2 key,float2 hash)
{
    float scale=(float)MaxGrassJitterLayer(key)*_GoTSpawn.x*_MaxSpacing;
    return _MaxLatticeOrigin+float2(worldGrid)*_MaxSpacing+MaxGrassJitter(hash,scale);
}

void MaxGrassPrepareWorldSeed(int2 worldGrid,out float4 hash,out float2 windRoot)
{
    uint2 key=MaxGrassWorldSeedKey(worldGrid);
    hash=MaxGrassSeedHash(key);
    windRoot=MaxGrassWorldWindRoot(worldGrid,key,hash.xy);
}

int MaxGrassWorldCeilDiv(int value,int divisor)
{
    // HLSL signed integer division truncates toward zero.
    return value/divisor+(value%divisor>0?1:0);
}

int MaxGrassWorldPatchSeedStart(int patch)
{
    // Divide first so large signed patch coordinates do not overflow when
    // converting every 25 patch intervals to 2048 lattice intervals.
    return (patch/25)*2048+MaxGrassWorldCeilDiv((patch%25)*2048,25);
}

bool MaxGrassWorldPatchCandidate(int2 worldPatch,uint localIndex,uint stride,out int2 worldGrid)
{
    // 8 m / (25/256 m) = 2048/25 seeds. Neighboring signed patches
    // share an integer half-open boundary even where the counts differ by one.
    int2 first=int2(MaxGrassWorldPatchSeedStart(worldPatch.x),MaxGrassWorldPatchSeedStart(worldPatch.y));
    int2 end=int2(MaxGrassWorldPatchSeedStart(worldPatch.x+1),MaxGrassWorldPatchSeedStart(worldPatch.y+1));
    int step=max(1,(int)stride);
    first=int2(MaxGrassWorldCeilDiv(first.x,step),MaxGrassWorldCeilDiv(first.y,step))*step;
    int2 count=max(0,(end-first+(step-1))/step);
    worldGrid=0;
    if(localIndex>=(uint)(count.x*count.y))return false;
    worldGrid=first+int2(localIndex%(uint)count.x,localIndex/(uint)count.x)*step;
    return true;
}

bool MaxGrassWorldPatchCandidate(int2 worldPatch,uint localIndex,out int2 worldGrid)
{
    return MaxGrassWorldPatchCandidate(worldPatch,localIndex,1u,worldGrid);
}

uint MaxGrassWorldPackHalf2(float2 pair)
{
    return f32tof16(pair.x)|(f32tof16(pair.y)<<16);
}

float2 MaxGrassWorldUnpackHalf2(uint packed)
{
    return float2(f16tof32(packed&65535u),f16tof32(packed>>16));
}

uint WorldGrassType(WorldGrassRecord record) { return (record.FacingTypeFlags>>16)&255u; }

WorldGrassRecord EncodeWorldGrass(SPGrassBlade blade,float4 aux,uint2 worldId,float2 windRoot)
{
    WorldGrassRecord record;
    record.WorldId=worldId;
    record.RootOffsetXZ=MaxGrassWorldPackHalf2(blade.Position.xz-windRoot);
    record.RootY=blade.Position.y;
    record.HeightWidth=MaxGrassWorldPackHalf2(float2(blade.Height,blade.Width));
    record.TiltBend=MaxGrassWorldPackHalf2(float2(blade.Tilt,blade.Bend));
    record.CurveClump=MaxGrassWorldPackHalf2(float2(blade.SideCurve,aux.x));
    const float inverseTau=.15915494309189534;
    bool hasFacing=dot(blade.Facing,blade.Facing)>1e-10;
    float angle=hasFacing?atan2(blade.Facing.y,blade.Facing.x):0;
    uint facing=(uint)round(frac(angle*inverseTau)*65536.0)&65535u;
    uint flags=(blade.Height<.6?1u:0u)|(blade.ClumpColor>.5?2u:0u)|(hasFacing?4u:0u);
    record.FacingTypeFlags=facing|((uint)blade.GrassType<<16)|(flags<<24);
    return record;
}

void DecodeWorldGrassAtRoot(WorldGrassRecord record,float4 hash,float2 windRoot,
    out SPGrassBlade blade,out float4 aux)
{
    float2 rootXZ=windRoot+MaxGrassWorldUnpackHalf2(record.RootOffsetXZ);
    float2 heightWidth=MaxGrassWorldUnpackHalf2(record.HeightWidth);
    float2 tiltBend=MaxGrassWorldUnpackHalf2(record.TiltBend);
    float2 curveClump=MaxGrassWorldUnpackHalf2(record.CurveClump);
    uint flags=record.FacingTypeFlags>>24;
    float angle=(record.FacingTypeFlags&65535u)*(6.283185307179586/65536.0);
    blade=(SPGrassBlade)0;
    blade.Position=float3(rootXZ.x,record.RootY,rootXZ.y);
    if((flags&4u)!=0)sincos(angle,blade.Facing.y,blade.Facing.x);
    blade.Hash=hash.z;
    blade.GrassType=WorldGrassType(record);
    blade.ClumpColor=(flags&2u)!=0?.75:.25;
    // Preserve the original branch class even at the half-precision boundary.
    blade.Height=(flags&1u)!=0?min(heightWidth.x,asfloat(0x3f199999u)):max(heightWidth.x,.6);
    blade.Width=heightWidth.y;
    blade.Tilt=tiltBend.x;
    blade.Bend=tiltBend.y;
    blade.SideCurve=curveClump.x;
    aux=float4(curveClump.y,0,hash.xy);
}

void DecodeWorldGrass(WorldGrassRecord record,out SPGrassBlade blade,out float4 aux,
    out uint2 worldId,out float2 windRoot)
{
    worldId=record.WorldId;
    float4 hash;
    MaxGrassPrepareWorldSeed(asint(worldId),hash,windRoot);
    DecodeWorldGrassAtRoot(record,hash,windRoot,blade,aux);
}

#endif
