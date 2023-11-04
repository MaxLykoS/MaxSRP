#ifndef MAX_Light_INCLUDED
#define MAX_Light_INCLUDED

#include "BRDF.hlsl"
#include "LightInput.hlsl"
#include "GlobalIllumination.hlsl"

float3 PBR_DirectLitDirectionalLight(Surface surface)
{
    //      direct lighting
    // directional light shading
    surface.L = normalize(_DirectionalLightDirection.xyz);

    // 太阳降到地平线下不再产生radiance
    if (-surface.L.y >= 0)
        return float3(0.0, 0.0, 0.0);

    float3 Lo = float3(0.0, 0.0, 0.0);
    float3 radiance = _DirectionalLightColor.rgb;
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
        float distance = length(pointLightPos.xyz - surface.P);
        float lightRange = pointLight.positionRange.w;
        float attenuation = DistanceAtten(distance * distance, lightRange * lightRange);
        float3 radiance = pointLight.color.rgb * attenuation;

        surface.L = normalize(pointLightPos - surface.P);
        surface.V = normalize(_WorldSpaceCameraPos - surface.P);
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
    float NdotV = max(0, dot(surface.N, surface.V));

    float3 kS = fresnelSchlickRoughness(max(dot(surface.N, surface.V), 0.0), surface.F0, surface.roughness);
    float3 kD = 1.0 - kS;
    kD = kD * (1.0 - surface.metalness);

    float3 indirectDiffuse = GetDiffuseIBL(surface.N, NdotV, surface.F0, surface.roughness, surface.albedo, kD);
    float3 indirectSpec = GetSpecularIBL(reflectDir, surface.roughness, NdotV, kS);
    Lo = Lo + indirectDiffuse + indirectSpec;
    return Lo;
}

// 总光照函数
float3 PBR_Shading(float3 Pw, float3 N, float3 albedo, float metalness, float roughness, float2 uv)
{
    // surface的L赋值放到各个函数里
    Surface surface;
    surface.P = Pw;
    surface.V = normalize(_WorldSpaceCameraPos - Pw);
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
    return c_dirDirLight * visibility + c_dirPointLight + c_indirLit;
}
#endif