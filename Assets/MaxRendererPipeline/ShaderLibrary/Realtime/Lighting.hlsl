#ifndef MAX_Light_INCLUDED
#define MAX_Light_INCLUDED

#include "BRDF.hlsl"
#include "LightInput.hlsl"
#include "GlobalIllumination.hlsl"
#include "Shadow.hlsl"

float3 PBR_DirectLitDirectionalLight(Surface surface)
{
    //      direct lighting
    // directional light shading
    surface.L = normalize(_MaxDirectionalLightDirection.xyz);

    // 太阳降到地平线下不再产生radiance
    if (-surface.L.y >= 0)
        return float3(0.0, 0.0, 0.0);

    float3 Lo = float3(0.0, 0.0, 0.0);
    float3 radiance = _MaxDirectionalLightColor.rgb;
    float NdotL = max(dot(surface.N, surface.L), 0);

    Lo = Lo + CookTorranceBRDF(surface) * radiance * NdotL;

    return Lo;
}

float3 PBR_DirectLitPointLight(Surface surface)
{
    // point light shading
    float3 Lo = float3(0.0, 0.0, 0.0);
    int lightCount = clamp(OTHER_LIGHT_COUNT, 0, MAX_OTHER_LIGHT_PER_OBJECT);

    for (int i1 = 0; i1 < lightCount; ++i1)
    {
        MaxOtherLight otherLight = GetOtherLight(i1);
        float3 otherLightPos = otherLight.positionRange.xyz;
        float distance = length(otherLightPos.xyz - surface.P);
        float lightRange = otherLight.positionRange.w;
        float attenuation = DistanceAtten(distance * distance, lightRange * lightRange);
        float3 radiance = otherLight.color.rgb * attenuation;

        surface.L = normalize(otherLightPos - surface.P);
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

    float3 indirectDiffuse = CubemapApprox(surface.N) * surface.albedo;

    float3 indirectSpec = GetSpec(reflectDir, surface.roughness, NdotV, kS);
    //float3 ambient = (kD * indirectDiffuse + indirectSpec) * surface.ao; // AO图
    Lo = Lo + indirectDiffuse + indirectSpec;
    return Lo;
}

// 总光照函数
float3 PBR_Shading(float3 Pw, float3 N, float3 albedo, float metalness, float roughness)
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

    float visibility = max(0.0, (1 - GetMainLightShadowAtten(surface.P, surface.N)));
    return c_dirDirLight * visibility + c_dirPointLight + c_indirLit;
}
#endif