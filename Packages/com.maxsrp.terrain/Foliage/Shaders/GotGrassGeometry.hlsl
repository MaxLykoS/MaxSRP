#ifndef GOT_GRASS_GEOMETRY_INCLUDED
#define GOT_GRASS_GEOMETRY_INCLUDED

#include "GotGrassCurve.hlsl"
#include "GotGrassDisplayData.hlsl"

StructuredBuffer<WorldGrassDisplayRecord> _WorldDisplayRecords;
StructuredBuffer<uint> _WorldDrawIndices,_WorldTypeOffsets;
int _WorldDrawBin,_Segments;
float3 _CameraPosition;
float4 _GoTLodMorph;
float _GoTWidthCompensation;

float GotGrassRemapT(float t,SPGoTType type)
{
    return t!=0 && t!=1?lerp(type.v[168/4],type.v[172/4],t):t;
}

float GotGrassFoldT(int id,int middle,float inverseSegments)
{
    int folded=(max(id-middle,middle-id)+(id>=middle?1:0))>>1;
    float value=1-float(folded)*inverseSegments;
    return 1-value*value;
}

// Source topology budgets: 15/7 vertices per record, including both short-grass branches.
void GotGrassCurveCoordinates(uint vertex,float height,float width,SPGoTType type,float lodBlend,
    out float t,out float side,out float orientation,out float halfWidth)
{
    int id=(int)vertex;
    bool shortBlade=height<.6;
    orientation=1;
    halfWidth=width*.5;
    if(_Segments==7)
    {
        int lowId=clamp(id-4,0,6);
        float highT,lowT;
        if(shortBlade)
        {
            orientation=id>=7?1:-1;
            halfWidth*=saturate((float)id);
            int folded=(max(id-7,7-id)+(id>=7?1:0))>>1;
            float value=1-(1-saturate((float)id))/7.0-float(folded)*.25;
            highT=1-value*value;
            lowT=GotGrassFoldT(lowId,3,.5);
        }
        else
        {
            highT=GotGrassRemapT(float(id>>1)/7.0,type);
            lowT=GotGrassRemapT(float(lowId>>1)/3.0,type);
        }
        t=lerp(highT,lowT,lodBlend);
    }
    else if(shortBlade)
    {
        orientation=id>=3?1:-1;
        t=GotGrassFoldT(id,3,.5);
    }
    else t=GotGrassRemapT(float(id>>1)/3.0,type);
    side=halfWidth<1e-8?0:float((id&1)*2-1);
}

void GotGrassVertex(uint vertex,uint instance,out float3 position,out float3 normal,
    out float2 uv,out float2 material,out uint species)
{
    uint address=_WorldDrawIndices[_WorldTypeOffsets[_WorldDrawBin]+instance];
    WorldGrassDisplayRecord draw=_WorldDisplayRecords[address];
    SPGrassBlade blade;
    float4 auxiliary;
    uint2 worldId;
    float2 windRoot;
    DecodeWorldGrass(draw.Grass,blade,auxiliary,worldId,windRoot);
    blade.WindStrength=draw.Wind;
    species=WorldGrassType(draw.Grass);
    SPGoTType type=_GoTTypes[species];
    float rootDistance=length(blade.Position-_CameraPosition);
    float lodBlend=_GoTLodMorph.w>.5?saturate((rootDistance+blade.Hash*_GoTLodMorph.z-_GoTLodMorph.x)/
        max(.00001,_GoTLodMorph.y-_GoTLodMorph.x)):0;
    float t,side,orientation,halfWidth;
    GotGrassCurveCoordinates(vertex,blade.Height,blade.Width*draw.WidthFactor,type,lodBlend,t,side,orientation,halfWidth);
    float3 across;
    GotGrassCurveFrame(blade,type,t,orientation,position,normal,across);
    float widthCompensation=dot(normalize(position-_CameraPosition),across)*(1+saturate(_GoTWidthCompensation));
    position+=side*halfWidth*across-side*min(halfWidth,.01)*normal*widthCompensation;
    uv=float2(side*.5+.5,t*orientation);
    material=float2(blade.ClumpColor,blade.Height*100);
}

#endif
