#ifndef MAX_GRASS_VISIBILITY_INCLUDED
#define MAX_GRASS_VISIBILITY_INCLUDED

// Every animated control lies inside this bound regardless of wind. Flutter
// is saturated, and the angular frames have unit length.
float MaxGrassBaseRadius(SPGrassBlade blade,SPGoTType type)
{
    float height=abs(blade.Height);
    float tip=height*sqrt(1+blade.SideCurve*blade.SideCurve);
    float controlA=abs(type.v[108/4])*tip+abs(blade.Bend)*height+.2*height;
    float controlB=tip+sqrt(.6*.6+.78*.78)*height;
    float radius=max(controlA,controlB);
    if(blade.Height>=.6)
    {
        // The type can remap intermediate vertices outside the Bezier range.
        // Bound the absolute basis weights in that case.
        float lo=min(0,min(type.v[168/4],type.v[172/4]));
        float hi=max(1,max(type.v[168/4],type.v[172/4]));
        if(lo<0 || hi>1)
        {
            float a=max(.5,max(abs(2*lo*(1-lo)),abs(2*hi*(1-hi))));
            float b=max(lo*lo,hi*hi);
            radius=a*controlA+b*controlB;
        }
    }
    return radius+max(.001,radius*.00001);
}

// Recovered 0039/0040 display fade, hierarchical thinning and compensating
// width. Stable world candidates replace the source CPU region's local grid;
// the hash key is identical to MaxGrassGenerate's 16-bit world-lattice adapter.
// This affects presentation only: the physical blade width remains unchanged.
float MaxGrassDisplayWidthFactor(uint2 worldId,float2 windRoot,float2 cameraXZ,
    int seed,float start,float end,bool enabled)
{
    if(!enabled)return 1;
    uint2 key=uint2(asint(worldId)+int2((uint)seed&65535u,((uint)seed>>16)&65535u))&65535u;
    float2 k=(float2)key;
    float randomValue=frac(sin(dot(k,float2(12.989800453186035,78.23300170898438)))*43758.546875);
    float distance=length(windRoot-cameraXZ);
    float fade=saturate((end-distance)/max(.001,end-start)-randomValue*.5);
    if(fade<=0)return 0;
    float scaledDistance=distance/max(.001,start);
    float widthScale=1;
    if(scaledDistance>.5)
    {
        uint mask=0;
        [loop]while(scaledDistance>1)
        {
            scaledDistance*=.5;
            widthScale*=2;
            mask=(mask<<1)|1;
        }
        uint parity=worldId.x|worldId.y;
        if((mask&parity)!=0)return 0;
        if((((mask<<1)|1)&parity)!=0)
        {
            fade=min(fade,saturate(randomValue+3-scaledDistance*4));
            if(fade<=0)return 0;
        }
        else if(fade==1)widthScale*=2*scaledDistance;
    }
    return widthScale*fade;
}

#endif
