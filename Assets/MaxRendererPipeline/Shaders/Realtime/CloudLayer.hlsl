#ifndef MAX_CLOUD_LAYER_INCLUDED
#define MAX_CLOUD_LAYER_INCLUDED
Texture2D<float4> _MaxCloudMap;
Texture2D<float2> _MaxCloudLighting;
SamplerState sampler_MaxCloudMap, sampler_MaxCloudLighting;
float4 _MaxCloudShape;    // enabled, coverage, density, world tile size in metres
float4 _MaxCloudGeometry; // altitude above sea level, thickness, planet radius, reserved
float4 _MaxCloudWind;     // wrapped UV offset.xy, physical wind / tile size.zw
float4 _MaxCloudObserver; // world X, altitude above sea level, world Z, reserved
float4 _MaxCloudAmbient;

float MaxCloudShapeAtUV(float2 uv)
{
    if (_MaxCloudShape.y <= 0) return 0;
#if defined(MAX_CLOUD_FRAGMENT_SAMPLING)
    float value = _MaxCloudMap.Sample(sampler_MaxCloudMap, uv).r;
#else
    float value = _MaxCloudMap.SampleLevel(sampler_MaxCloudMap, uv, 0).r;
#endif
    value *= value;
    value *= smoothstep(0.004, 0.025, value);
    float threshold = max(0.52 - _MaxCloudShape.y, 0) * 1.5;
    float amplification = 1 + max(_MaxCloudShape.y - 0.52, 0) * 4;
    return saturate(max(0, value - threshold) * amplification);
}

float2 MaxCloudPlanePosition(float3 direction)
{
    float height = max(_MaxCloudGeometry.x - _MaxCloudObserver.y, 0);
    return _MaxCloudObserver.xz + direction.xz * (height / max(direction.y, 0.02));
}
float2 MaxCloudUV(float3 direction)
{
    return MaxCloudPlanePosition(direction) / max(_MaxCloudShape.w, 1) + 0.5;
}
float2 MaxCloudAdvectedUV(float3 direction, float2 wind)
{
    // F(worldXZ - windMetresPerSecond * time). This is translation on a fixed
    // world plane, not a rotation of the camera-centred sky direction.
    return MaxCloudUV(direction) + wind;
}
float MaxCloudOpacityAtUV(float2 uv)
{
    return 1 - exp(-MaxCloudShapeAtUV(uv) * _MaxCloudShape.z * 2);
}
float MaxCloudVisibility(float3 direction)
{
    float height = max(_MaxCloudGeometry.x - _MaxCloudObserver.y, 0);
    float distance = length(direction.xz) * height / max(direction.y, 0.02);
    // A finite-detail planar layer fades before its far-field repetitions become
    // horizontal bands. This has no influence on its world-space advection.
    return smoothstep(0.005, 0.06, direction.y) *
        (1 - smoothstep(_MaxCloudShape.w * 0.4, _MaxCloudShape.w * 0.85, distance));
}
float MaxCloudOpacity(float3 direction, float2 wind)
{
    if (_MaxCloudShape.x < 0.5 || direction.y <= 0 || _MaxCloudShape.y <= 0 ||
        _MaxCloudObserver.y >= _MaxCloudGeometry.x) return 0;
    return MaxCloudOpacityAtUV(MaxCloudAdvectedUV(direction, wind)) * MaxCloudVisibility(direction);
}
// Premultiplied radiance.rgb + transmittance.a, shared by visible sky and IBL.
float4 EvaluateMaxCloudLayer(float3 direction, float3 sunDirection, float3 solarIlluminance, float3 ambientRadiance)
{
    if (_MaxCloudShape.x < 0.5 || direction.y <= 0 || _MaxCloudObserver.y >= _MaxCloudGeometry.x)
        return float4(0, 0, 0, 1);
    float2 uv = MaxCloudAdvectedUV(direction, _MaxCloudWind.xy);
    float opacity = MaxCloudOpacityAtUV(uv) * MaxCloudVisibility(direction);
    float2 lighting = _MaxCloudLighting.SampleLevel(sampler_MaxCloudLighting, uv, 0);
    const float g = 0.65;
    float cosine = clamp(dot(direction, sunDirection), -1, 1);
    float phase = (1 - g * g) / (12.566370614 * pow(max(1 + g * g - 2 * g * cosine, 0.01), 1.5));
    float3 direct = solarIlluminance * (0.12 + phase) * (0.18 + 0.82 * lighting.x);
    float3 ambient = max(ambientRadiance, 0) * _MaxCloudAmbient.x * lighting.y;
    return float4((direct + ambient) * opacity, 1 - opacity);
}
#endif
