#ifndef MAX_SIX_WAY_CLOUD_COMMON
#define MAX_SIX_WAY_CLOUD_COMMON
Texture2D<float4> _MaxSixWayPositive;
Texture2D<float4> _MaxSixWayNegative;
SamplerState sampler_MaxSixWayPositive;
float4 _MaxSixWayParams; // enabled, direct, ambient, opacity
float4 _MaxSixWayCentres[32]; // xyz world, w atlas shape index
float4 _MaxSixWayRight[32]; // xyz unit right, w half width
float4 _MaxSixWayUp[32]; // xyz unit up, w recycling fade
float3 _MaxSixWayObserver;
float _MaxSixWaySeaLevel;
int _MaxSixWayCount;

float2 MaxSixWayAtlasUV(float2 uv, uint shape)
{
    // Official atlas: two columns, four rows, each cell has a 2:1 aspect.
    uv = clamp(uv, 1.0 / float2(2048, 1024), 1 - 1.0 / float2(2048, 1024));
    return (float2(shape % 2, 3 - shape / 2) + uv) / float2(2, 4);
}
float MaxSixWayResponse(float3 positive, float3 negative, float3 light)
{
    // Unity channels: P = right, top, back; N = left, bottom, front.
    float3 response = float3(light.x >= 0 ? positive.x : negative.x,
        light.y >= 0 ? positive.y : negative.y, light.z >= 0 ? positive.z : negative.z);
    return dot(response, light * light);
}
float4 MaxSixWayShade(float2 uv, uint index, float3 sun, float3 solar, float3 ambient)
{
    float2 atlasUV = MaxSixWayAtlasUV(uv, (uint)_MaxSixWayCentres[index].w);
#if defined(MAX_SIX_WAY_FRAGMENT)
    float4 positive = _MaxSixWayPositive.Sample(sampler_MaxSixWayPositive, atlasUV);
    float3 negative = _MaxSixWayNegative.Sample(sampler_MaxSixWayPositive, atlasUV).rgb;
#else
    float4 positive = _MaxSixWayPositive.SampleLevel(sampler_MaxSixWayPositive, atlasUV, 1);
    float3 negative = _MaxSixWayNegative.SampleLevel(sampler_MaxSixWayPositive, atlasUV, 1).rgb;
#endif
    float3 right = _MaxSixWayRight[index].xyz, up = _MaxSixWayUp[index].xyz;
    float3 front = cross(right, up);
    float3 light = float3(dot(sun, right), dot(sun, up), -dot(sun, front));
    float direct = MaxSixWayResponse(positive.rgb, negative, light);
    float fill = dot(positive.rgb + negative, float3(1, 1, 1)) / 6;
    float alpha = saturate(positive.a * _MaxSixWayParams.w * _MaxSixWayUp[index].w);
    // Maps are uncoloured straight lighting responses. The cloud output is
    // premultiplied exactly once, with no extra HG phase on the baked response.
    float3 radiance = solar * (direct * _MaxSixWayParams.y / 3.141592654) +
        ambient * (fill * _MaxSixWayParams.z);
    return float4(radiance * alpha, alpha);
}
#endif
