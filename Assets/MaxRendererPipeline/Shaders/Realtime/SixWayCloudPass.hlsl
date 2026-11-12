#ifndef MAX_SIX_WAY_CLOUD_PASS
#define MAX_SIX_WAY_CLOUD_PASS
#include "../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../ShaderLibrary/Realtime/AtomsphereScattering/Raymarching.hlsl"
#define MAX_SIX_WAY_FRAGMENT
#include "SixWayCloudCommon.hlsl"
Texture2D<float4> _MaxSixWaySky;
SamplerState sampler_MaxSixWaySky;
struct SixWayVaryings
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
    nointerpolation uint index : TEXCOORD1;
};
SixWayVaryings SixWayVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
{
    const float2 corners[6] = {float2(0,0),float2(1,0),float2(1,1),float2(0,0),float2(1,1),float2(0,1)};
    SixWayVaryings o;
    o.uv = corners[vertex]; o.index = instance;
    float halfWidth = _MaxSixWayRight[instance].w;
    float3 world = _MaxSixWayCentres[instance].xyz +
        _MaxSixWayRight[instance].xyz * ((o.uv.x * 2 - 1) * halfWidth) +
        _MaxSixWayUp[instance].xyz * ((o.uv.y * 2 - 1) * halfWidth * 0.5);
    o.position = mul(unity_MatrixVP, float4(world, 1));
    // Sky-only cards: preserve scene depth, draw after the clear sky, and allow
    // foreground opaque silhouettes to occlude clouds even beyond camera far.
#if defined(UNITY_REVERSED_Z)
    o.position.z = 0;
#else
    o.position.z = o.position.w;
#endif
    return o;
}
float4 SixWayFragment(SixWayVaryings input) : SV_Target
{
    AtmosphereParameter atmosphere = GetAtmosphereParameter();
    float altitude = max(1, _MaxSixWayCentres[input.index].y - _SeaLevel);
    float3 position = float3(0, atmosphere.PlanetRadius + altitude, 0);
    float3 cloudLightDirection = _AtmosphereSunDirection;
    float3 solar = atmosphere.SolarIrradiance * TransmittanceToSun(atmosphere, position, cloudLightDirection);
    float3 ambient = _MaxSixWaySky.SampleLevel(sampler_MaxSixWaySky, float2(0.5, 1), 0).rgb;
    return MaxSixWayShade(input.uv, input.index, cloudLightDirection, solar, ambient);
}
#endif
