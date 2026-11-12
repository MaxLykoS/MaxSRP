#ifndef MAX_Light_INCLUDED
#define MAX_Light_INCLUDED

#include "BRDF.hlsl"
#include "LightInput.hlsl"
#include "GlobalIllumination.hlsl"
#include "AtomsphereScattering/RayMarching.hlsl"
#include "Shadow.hlsl"
#include "MaxViewDirection.hlsl"
#include "MaxLightingDiagnostics.hlsl"

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

    uint lightListStart, lightListCount;
    bool useCluster = GetPointLightList(surface.P, lightListStart, lightListCount);
    uint pointLightCount = useCluster ? lightListCount : (uint)GetPointLightCount();
    for (uint i1 = 0; i1 < pointLightCount; ++i1)
    {
        uint lightIndex = useCluster ? GetPointLightListIndex(lightListStart, i1) : i1;
        PointLight pointLight = GetPointLight(lightIndex);
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

float3 PBR_IndirectLit(Surface surface, float skyVisibility)
{
    // indirect lighting
    // diffuse (from light probe SH)
    float3 Lo = float3(0.0, 0.0, 0.0);

    float3 reflectDir = normalize(reflect(-surface.V, surface.N));
    float NdotV = saturate(dot(surface.N, surface.V));

    float3 kS = fresnelSchlickRoughness(NdotV, surface.F0, surface.roughness);
    float3 kD = MaxIndirectDiffuseWeight(NdotV, surface.F0, surface.roughness, surface.metalness);

    float3 indirectDiffuse = GetDiffuseIBL(surface.N, NdotV, surface.F0, surface.roughness, surface.albedo, kD)
        * saturate(skyVisibility);
    float3 indirectSpec = GetSpecularIBL(reflectDir, surface.roughness, NdotV, kS);
    if (_MaxLightingDiagnosticMode == 3.0) return indirectDiffuse;
    if (_MaxLightingDiagnosticMode == 4.0) return indirectSpec;
    Lo = Lo + indirectDiffuse + indirectSpec;
    return Lo;
}

float3 PBR_IndirectLit(Surface surface)
{
    return PBR_IndirectLit(surface, 1.0);
}

// 总光照函数
float3 PBR_Shading(float3 Pw, float3 N, float3 albedo, float metalness, float roughness, float2 uv, float visibility,
    float skyVisibility)
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
    float3 c_indirLit = PBR_IndirectLit(surface, skyVisibility);

    if (_MaxLightingDiagnosticMode == 5.0) return c_dirDirLight + c_dirPointLight;
    if (_MaxLightingDiagnosticMode >= 2.0 && _MaxLightingDiagnosticMode <= 4.0) return c_indirLit;

    if (_MaxLightingDiagnosticMode == 1.0) return c_dirDirLight * visibility + c_dirPointLight;
    return c_dirDirLight * visibility + c_dirPointLight + c_indirLit;
}

// Existing numerical fixtures and non-lightmapped callers retain unoccluded sky.
float3 PBR_Shading(float3 Pw, float3 N, float3 albedo, float metalness, float roughness, float2 uv, float visibility)
{
    return PBR_Shading(Pw, N, albedo, metalness, roughness, uv, visibility, 1.0);
}
#endif
