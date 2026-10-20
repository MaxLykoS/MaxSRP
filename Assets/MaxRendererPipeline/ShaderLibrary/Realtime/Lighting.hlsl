#ifndef MAX_Light_INCLUDED
#define MAX_Light_INCLUDED

#include "BRDF.hlsl"
#include "LightInput.hlsl"
#include "GlobalIllumination.hlsl"
#include "AtomsphereScattering/RayMarching.hlsl"

// Editor comparison camera only. Zero preserves normal rendering.
float _MaxLightingDiagnosticMode;

float3 GetSurfaceViewDirection(float3 positionWS)
{
    if (unity_OrthoParams.w > 0.5)
        return normalize(mul((float3x3)unity_MatrixInvV, float3(0.0, 0.0, 1.0)));
    return normalize(_WorldSpaceCameraPos - positionWS);
}

float3 PBR_DirectLitDirectionalLight(Surface surface)
{
    //      direct lighting
    // directional light shading
    if (!any(_DirectionalLightColor.rgb > 0.0)) return float3(0.0, 0.0, 0.0);
    surface.L = normalize(_DirectionalLightDirection.xyz);

    float3 Lo = float3(0.0, 0.0, 0.0);
    float3 radiance = _DirectionalLightColor.rgb * SurfaceSunTransmittance(surface.P, surface.L);
    float NdotL = max(dot(surface.N, surface.L), 0);

    Lo = Lo + CookTorranceBRDF(surface) * radiance * NdotL;

    return Lo;
}

float3 PBR_DirectLitPointLight(Surface surface)
{
    // point light shading
    float3 Lo = float3(0.0, 0.0, 0.0);

    for (int i1 = 0; i1 < _PointLightCount; ++i1)
    {
        PointLight pointLight = GetPointLight(i1);
        float3 pointLightPos = pointLight.positionRange.xyz;
        float3 lightOffset = pointLightPos - surface.P;
        float distanceSqr = dot(lightOffset, lightOffset);
        float lightRange = pointLight.positionRange.w;
        float attenuation = DistanceAtten(distanceSqr, lightRange * lightRange);
        float3 radiance = pointLight.color.rgb * attenuation;

        surface.L = lightOffset * rsqrt(max(distanceSqr, 1e-6));

        float NdotL = max(dot(surface.N, surface.L), 0);

        Lo = Lo + CookTorranceBRDF(surface) * radiance * NdotL;
    }

    return Lo;
}

float3 PBR_IndirectLit(Surface surface)
{
    // indirect lighting
    // diffuse (from light probe SH)
    float3 Lo = float3(0.0, 0.0, 0.0);

    float3 reflectDir = normalize(reflect(-surface.V, surface.N));
    float NdotV = saturate(dot(surface.N, surface.V));

    float3 kS = fresnelSchlickRoughness(NdotV, surface.F0, surface.roughness);
    float3 kD = 1.0 - kS;
    kD = kD * (1.0 - surface.metalness);

    float3 indirectDiffuse = GetDiffuseIBL(surface.N, NdotV, surface.F0, surface.roughness, surface.albedo, kD);
    float3 indirectSpec = GetSpecularIBL(reflectDir, surface.roughness, NdotV, kS);
    if (_MaxLightingDiagnosticMode == 3.0) return indirectDiffuse;
    if (_MaxLightingDiagnosticMode == 4.0) return indirectSpec;
    Lo = Lo + indirectDiffuse + indirectSpec;
    return Lo;
}

// 总光照函数
float3 PBR_Shading(float3 Pw, float3 N, float3 albedo, float metalness, float roughness, float2 uv)
{
    // surface的L赋值放到各个函数里
    Surface surface;
    surface.P = Pw;
    surface.V = GetSurfaceViewDirection(Pw);
    surface.N = normalize(N);
    surface.albedo = albedo;
    surface.metalness = metalness;
    surface.roughness = roughness;
    float3 F0 = float3(MIN_REFLECTIVITY, MIN_REFLECTIVITY, MIN_REFLECTIVITY);
    F0 = lerp(F0, albedo, metalness);
    surface.F0 = F0;

    float3 c_dirDirLight = PBR_DirectLitDirectionalLight(surface);
    float3 c_dirPointLight = PBR_DirectLitPointLight(surface);
    float3 c_indirLit = PBR_IndirectLit(surface);

    float visibility = UNITY_SAMPLE_TEX2D(_ScreenSpaceShadowMapBlur, uv);
    if (_MaxLightingDiagnosticMode == 1.0) return c_dirDirLight * visibility + c_dirPointLight;
    if (_MaxLightingDiagnosticMode == 5.0) return c_dirDirLight + c_dirPointLight;
    if (_MaxLightingDiagnosticMode >= 2.0 && _MaxLightingDiagnosticMode <= 4.0) return c_indirLit;
    return c_dirDirLight * visibility + c_dirPointLight + c_indirLit;
}
#endif