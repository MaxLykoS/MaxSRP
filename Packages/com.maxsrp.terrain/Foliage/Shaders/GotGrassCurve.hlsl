#ifndef GOT_GRASS_CURVE_INCLUDED
#define GOT_GRASS_CURVE_INCLUDED

#include "GotGrassData.hlsl"

float _AnimationTime,_MaxWindInfluence;
float4 _GoTWind;
float2 _GoTGlobalFacingBias;
Texture2D<float2> _GotGrassDisplacementMap;
SamplerState sampler_GotGrassDisplacementMap;
float4 _GotGrassDisplacementWindow; // centre X/Z, inverse world width when active
float4 _GotGrassDisplacementBounds; // min and max world X/Z

float2 GotGrassFacing(float2 facing)
{
    float inverse=rsqrt(dot(facing,facing));
    return isfinite(inverse)?facing*inverse:0;
}

float3 GotGrassFilterWeights(float f)
{
    float blend=((.5-f*.3333333432674408)*f+.5)*f+.1666666716337204;
    float negative=(.23999999463558197*f+.5600000023841858)*f+.20000000298023224;
    float positive=(.23999999463558197*f-1.0399999618530273)*f+1;
    return float3(blend,negative,positive);
}

float GotGrassBorderWeight(float uv)
{
    return saturate(uv*256+.5)*saturate((1-uv)*256+.5);
}

float2 GotGrassDisplacement(float2 xz)
{
    if(_GotGrassDisplacementWindow.z<=0 || any(xz<_GotGrassDisplacementBounds.xy) ||
        any(xz>_GotGrassDisplacementBounds.zw))return 0;
    float2 relative=xz-_GotGrassDisplacementWindow.xy;
    if(any(abs(relative)>=50+150.0/256))return 0;
    float2 cm=relative*100;
    float2 uv=float2(cm.x*.00009999999747378752+.5,.5-cm.y*.00009999999747378752);
    float2 phase=frac(float2(cm.x*.025599999353289604+127.5,127.5-cm.y*.025599999353289604));
    float3 x=GotGrassFilterWeights(phase.x), y=GotGrassFilterWeights(phase.y);
    float left=uv.x-x.y/256, right=uv.x+x.z/256;
    float bottom=uv.y-y.y/256, top=uv.y+y.z/256;
    float2 xInside=float2(GotGrassBorderWeight(left),GotGrassBorderWeight(right));
    float2 yInside=float2(GotGrassBorderWeight(bottom),GotGrassBorderWeight(top));
    float2 a=_GotGrassDisplacementMap.SampleLevel(sampler_GotGrassDisplacementMap,float2(left,bottom),0)*xInside.x*yInside.x;
    float2 b=_GotGrassDisplacementMap.SampleLevel(sampler_GotGrassDisplacementMap,float2(right,bottom),0)*xInside.y*yInside.x;
    float2 c=_GotGrassDisplacementMap.SampleLevel(sampler_GotGrassDisplacementMap,float2(left,top),0)*xInside.x*yInside.y;
    float2 d=_GotGrassDisplacementMap.SampleLevel(sampler_GotGrassDisplacementMap,float2(right,top),0)*xInside.y*yInside.y;
    return lerp(lerp(a,c,y.x),lerp(b,d,y.x),x.x);
}

void GotGrassCurveFrame(SPGrassBlade blade,SPGoTType type,float t,float orientation,
    out float3 position,out float3 normal,out float3 across)
{
    float2 restFacing=blade.Facing;
    if(blade.Height<.6)
        restFacing=GotGrassFacing(restFacing+.600000023841858*orientation*float2(restFacing.y,-restFacing.x));
    float2 upper=GotGrassDisplacement(blade.Position.xz+restFacing*(blade.Height*.65));
    float2 lower=GotGrassDisplacement(blade.Position.xz+restFacing*(blade.Height*.125));
    float h01=blade.Height/1.7;
    float2 facing=GotGrassFacing(restFacing+type.v[220/4]*h01*upper+
        type.v[204/4]*.000151515152538195*blade.WindStrength*_GoTGlobalFacingBias);
    float3 facing3=float3(facing.x,0,facing.y);
    across=float3(facing.y,0,-facing.x);
    float tilt=tan(blade.Tilt)+type.v[216/4]*h01*length(lower);
    float3 direction=normalize(facing3*tilt+float3(0,1,0));
    float3 bendNormal=normalize(cross(across,direction));
    float3 baseTip=direction*blade.Height+across*(blade.SideCurve*blade.Height);
    float3 a=type.v[108/4]*baseTip-bendNormal*(blade.Bend*blade.Height);
    float phase=_AnimationTime*22*type.v[208/4]+blade.Hash*62.8318557739258-1.5*t;
    float amplitude=(_GoTWind.w*.000045454547635978+.100000001490116)*_MaxWindInfluence;
    float branchBias=(orientation*.100000001490116)*_MaxWindInfluence;
    float flutter=saturate(sin(phase)*amplitude+branchBias+.100000001490116*_MaxWindInfluence+blade.WindStrength);
    a+=facing3*(.34*h01*flutter);
    float3 b=baseTip+facing3*(1.02000007629395*h01*flutter)+float3(0,-1.32599990844727*h01*flutter,0);
    position=blade.Position+t*(2*(1-t)*a+t*b);
    float3 tangent=normalize(2*(1-t)*a+2*t*(b-a));
    normal=normalize(cross(across,tangent));
}

#endif
