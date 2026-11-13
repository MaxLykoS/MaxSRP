#ifndef MAX_ATMOSPHERE_SAMPLING_INCLUDED
#define MAX_ATMOSPHERE_SAMPLING_INCLUDED

// Numerical quality only; scattering phase and feedback follow the same platform variant.
// Distances are kilometres. UE default non-jittered integration samples at 0.3 of each segment.
static const float ATMOSPHERE_SAMPLE_OFFSET = 0.3;
static const float ATMOSPHERE_SKY_DISTANCE_TO_MAX_KM = 150.0;
#if defined(MAX_ATMOSPHERE_MOBILE)
static const int ATMOSPHERE_TRANSMITTANCE_SAMPLE_COUNT = 10;
static const int ATMOSPHERE_MULTI_SAMPLE_COUNT = 15;
static const int ATMOSPHERE_MULTI_DIRECTION_COUNT = 2;
static const float ATMOSPHERE_SKY_SAMPLE_COUNT_MIN = 1.0;
static const float ATMOSPHERE_SKY_SAMPLE_COUNT_MAX = 8.0;
#else
static const int ATMOSPHERE_TRANSMITTANCE_SAMPLE_COUNT = 30;
static const int ATMOSPHERE_MULTI_SAMPLE_COUNT = 20;
static const int ATMOSPHERE_MULTI_DIRECTION_COUNT = 32;
// Cine effective default sky budget; the user explicitly retained 32 steps.
static const float ATMOSPHERE_SKY_SAMPLE_COUNT_MIN = 8.0;
static const float ATMOSPHERE_SKY_SAMPLE_COUNT_MAX = 32.0;
#endif

// UE-style stratified sphere generator, using the user-selected 32 directions.
// Seed FRandomStream(0xDE4DC0DE); a 4x8 grid covers the entire sphere:
// (u0,u1)=((i+random)/4,(j+random)/8), a=1-2*u0, b=sqrt(1-a*a), phi=2*pi*u1.
// Index=j*4+i. Rotate UE Z-up into this renderer's Y-up as (UE.y,UE.z,UE.x).
// This is a custom direction budget, not the first half of a 64-direction table.
#if !defined(MAX_ATMOSPHERE_MOBILE)
static const float3 AtmosphereUniformSphereSamples[32] = {
    float3(0.200354606, 0.638925314, 0.74271971),
    float3(0.621213317, 0.0898737907, 0.778470695),
    float3(0.12445379, -0.474097371, 0.871632338),
    float3(0.299312115, -0.661028028, 0.688080132),
    float3(0.438548237, 0.898357749, 0.0250759143),
    float3(0.743026853, 0.403265476, 0.534123659),
    float3(0.971603751, -0.00866007805, 0.236455485),
    float3(0.417329162, -0.899499178, 0.129373759),
    float3(0.71978581, 0.598837018, -0.351144761),
    float3(0.752890825, 0.29681164, -0.587416649),
    float3(0.965779901, -0.149423003, -0.211995184),
    float3(0.814284265, -0.576788664, -0.0652375966),
    float3(0.0351322182, 0.85317713, -0.520436823),
    float3(0.595512271, 0.205188394, -0.77669996),
    float3(0.541701317, -0.0224773884, -0.8402704),
    float3(0.493548512, -0.570977569, -0.656044662),
    float3(-0.142613873, 0.966411293, -0.213800132),
    float3(-0.0451244153, 0.444797754, -0.89449358),
    float3(-0.661812127, -0.231021404, -0.713185668),
    float3(-0.447429091, -0.675678015, -0.585889459),
    float3(-0.824634492, 0.564998746, -0.0274664741),
    float3(-0.961033583, 0.274187624, -0.035152927),
    float3(-0.994536161, -0.102009177, -0.0221820623),
    float3(-0.700506747, -0.642029405, -0.311590344),
    float3(-0.70032829, 0.708247364, 0.089027524),
    float3(-0.817837715, 0.408855796, 0.404942274),
    float3(-0.926568508, -0.0999953747, 0.362590194),
    float3(-0.797032237, -0.603225946, 0.0292925928),
    float3(-0.35991171, 0.684771776, 0.633680701),
    float3(-0.099000521, 0.0409137011, 0.994245887),
    float3(-0.163548589, -0.26004827, 0.951644242),
    float3(-0.0958643034, -0.885785341, 0.454086274),
};
#endif

float3 AtmosphereMultiSampleDirection(int sampleIndex)
{
#if defined(MAX_ATMOSPHERE_MOBILE)
    // UE low-quality approximation traces one ray along local up and one down.
    return sampleIndex == 0 ? float3(0.0, 1.0, 0.0) : float3(0.0, -1.0, 0.0);
#else
    return AtmosphereUniformSphereSamples[sampleIndex];
#endif
}

#endif
