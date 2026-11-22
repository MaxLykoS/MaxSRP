#ifndef GOT_GRASS_PLANT_INCLUDED
#define GOT_GRASS_PLANT_INCLUDED

#include "GotGrassWorldRecord.hlsl"
#include "GotGrassWind.hlsl"
#include "GotGrassTerrain.hlsl"

float4 _Dimensions;
float2 _GoTGlobalFacingBias;
float _MaxWindInfluence;

float2 GotGrassNormalize(float2 x)
{
    float inverse=rsqrt(dot(x,x));
    return isfinite(inverse)?x*inverse:0;
}

float4 GotGrassClumpHash(float2 p)
{
    const float inv71=.014084506779909134;
    float2 p0=p-floor(p*inv71)*71;
    float2 p1=p+1-floor((p+1)*inv71)*71;
    float4 q=float4(p0.x+26,p0.y+161,p1.x+26,p1.y+161);
    q*=q;
    float s=q.x*.0010513747110962868;
    return frac(float4(s*q.y,q.y*.0010513747110962868*q.z,
        s*q.w,q.z*.0010513747110962868*q.w));
}

float2 GotGrassVoronoiHash(float2 cell)
{
    return frac(sin(float2(dot(cell,float2(12.989800453186035,78.23300170898438)),
        dot(cell,float2(-67.23300170898438,10.989800453186035))))*float2(43758.546875,73756.546875));
}

struct GotGrassClump { float weight; float2 center; float4 hash; float average; };

GotGrassClump GotGrassFindClump(float2 centimetres,float frequency)
{
    float2 p=centimetres*frequency, cell=floor(p), f=frac(p);
    float best=3.4028234663852886e38;
    GotGrassClump result=(GotGrassClump)0;
    [unroll]for(int y=-1;y<=1;y++)
    [unroll]for(int x=-1;x<=1;x++)
    {
        float2 offset=float2(x,y)+GotGrassVoronoiHash(cell+float2(x,y));
        float2 delta=offset-f;
        float distance=dot(delta,delta)+.000001;
        if(distance<best) { best=distance; result.center=cell+offset; }
    }
    result.weight=saturate(1-sqrt(best));
    result.hash=GotGrassClumpHash(result.center);
    result.average=dot(result.hash.xyz,float3(.3333333432674408,.3333333432674408,.3333333432674408));
    return result;
}

bool GotGrassPlant(int2 worldGrid,float4 hash,float2 windRoot,out WorldGrassRecord record,out float morphFade)
{
    record=(WorldGrassRecord)0;
    morphFade=0;
    if(!GotGrassInTerrain(windRoot))return false;
    float2 coverage=GotGrassMaterialWeights(windRoot);
    if(hash.z>=coverage.x+coverage.y)return false;
    bool useA=hash.z<coverage.x;
    uint typeId=(uint)round(useA?_GotTerrainGrassMaterials.z:_GotTerrainGrassMaterials.w);
    if(typeId==0 || typeId>=(uint)_GoTTypeCount)return false;
    SPGoTType type=_GoTTypes[typeId];
    float density=type.v[224/4];
    if(density<=0 || hash.w>density || type.v[116/4]<=.0001 || type.v[120/4]+type.v[124/4]<=.0001)return false;
    float heightRandom=hash.w/max(density,.00001);
    float grassHeight=useA?_GotTerrainGrassHeight.x:_GotTerrainGrassHeight.y;
    float height=saturate(type.v[120/4]*grassHeight+type.v[124/4])*type.v[116/4];
    if(height<=.1)return false;
    float tilt=type.v[100/4]*(hash.z*1.399999976158142+.30000001192092896)*(height*.25+1);

    float2 centimetres=windRoot*100;
    float frequency=max(type.v[132/4],.0000001);
    GotGrassClump clump=GotGrassFindClump(centimetres,frequency);
    float2 toCenter=clump.center-centimetres*frequency;
    height+=(clump.weight*.30000001192092896*clump.average-.10000000149011612)*type.v[228/4];
    float width=_Dimensions.z*.5*(type.v[96/4]+hash.z*.5);
    width*=1+(clump.weight*.5*clump.average-.10000000149011612)*type.v[228/4];
    float2 root=windRoot+(clump.weight*type.v[136/4]*clump.hash.w*clump.average)*(toCenter/frequency)*.01;
    float rootY;
    float2 terrainGradient;
    if(!GotGrassTerrainFace(root,rootY,terrainGradient))return false;
    morphFade=GotGrassBeforeMorph(root);
    if(morphFade<=0)return false;
    if(any(root!=windRoot))
    {
        float2 rootCoverage=GotGrassMaterialWeights(root);
        if((useA?rootCoverage.x:rootCoverage.y)<=0)return false;
    }

    // Feed the original facing term its normalized central-difference slope.
    float2 slope=-2*terrainGradient/max(_MeshHeightScale,1e-6);
    float invSlopeLength=rsqrt(dot(slope,slope)+_GoTSpawn.y*_GoTSpawn.y);
    float averageHash=dot(hash.xyz,float3(.3333333432674408,.3333333432674408,.3333333432674408));
    float2 facing=hash.xy*2-1-clump.average*type.v[148/4]*GotGrassNormalize(toCenter);
    facing+=clump.weight*type.v[144/4]*(clump.hash.xy*2-1)+
        _GoTGlobalFacingBias*(_MaxWindInfluence*type.v[152/4])+slope*(invSlopeLength*type.v[156/4]);
    height*=1-type.v[232/4]+type.v[232/4]*heightRandom;
    if(height<=0)return false;

    SPGrassBlade blade=(SPGrassBlade)0;
    blade.Position=float3(root.x,rootY,root.y);
    blade.Facing=GotGrassNormalize(facing);
    blade.Hash=hash.z;
    blade.GrassType=(float)typeId;
    blade.ClumpColor=clump.hash.x>type.v[140/4]?.25:.75;
    blade.Height=height*1.7;
    blade.Width=2*width;
    blade.Tilt=atan(tilt);
    blade.Bend=type.v[104/4]*(heightRandom+.5)*(averageHash<type.v[112/4]?-1:1);
    blade.SideCurve=type.v[128/4]*(averageHash*2-1);
    record=EncodeWorldGrass(blade,float4(clump.weight,0,hash.xy),asuint(worldGrid),windRoot);
    return true;
}

#endif
