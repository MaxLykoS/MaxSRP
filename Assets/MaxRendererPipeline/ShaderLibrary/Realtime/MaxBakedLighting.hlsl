#ifndef MAX_BAKED_LIGHTING_INCLUDED
#define MAX_BAKED_LIGHTING_INCLUDED

#include "CommonInput.hlsl"
#include "MaxDiffuseResponse.hlsl"
#include "MaxViewDirection.hlsl"
#include "MaxLightingDiagnostics.hlsl"

// The verified native Progressive output is irradiance E, not E/pi.
// LightDataGI uses physical intensity/1000; diagnostics 9..11 keep raw E
// in the pipeline's physical/100000 scale. Apply Lambert 1/pi exactly once
// when converting E into the final receiver's diffuse outgoing radiance.
// Native transport already accounts for the BRDFs of preceding bounces.
// Keep these constants in sync with MaxLightmapping.
static const float MAX_BAKED_IRRADIANCE_TO_SCENE_HDR = 0.01;
static const float MAX_BAKED_DIFFUSE_TO_SCENE_HDR = 0.01 * 0.3183098861837907;

#if defined(LIGHTMAP_ON)
UNITY_DECLARE_TEX2D(unity_Lightmap);
UNITY_DECLARE_TEX2D_NOSAMPLER(unity_LightmapInd);
#endif
float4 unity_Lightmap_HDR;

UNITY_DECLARE_TEX2DARRAY(_MaxSkyVisibilityArray);
float _MaxSkyVisibilityReady;

float2 MaxLightmapUV(float2 uv)
{
    return uv * unity_LightmapST.xy + unity_LightmapST.zw;
}

float MaxHasBakedLightmap()
{
#if defined(LIGHTMAP_ON)
    return 1.0;
#else
    return 0.0;
#endif
}

float3 MaxDecodeLightmap(float4 encoded)
{
    // Match UnityCG.cginc DecodeLightmap for native HDR, RGBM and double LDR.
    // Do not include UnityCG here: it would redeclare our SRP UnityPerDraw.
#if defined(UNITY_LIGHTMAP_DLDR_ENCODING)
    return unity_Lightmap_HDR.x * encoded.rgb;
#elif defined(UNITY_LIGHTMAP_RGBM_ENCODING)
    #if defined(UNITY_COLORSPACE_GAMMA)
        #if defined(UNITY_FORCE_LINEAR_READ_FOR_RGBM)
            return (unity_Lightmap_HDR.x * encoded.a) * sqrt(encoded.rgb);
        #else
            return (unity_Lightmap_HDR.x * encoded.a) * encoded.rgb;
        #endif
    #else
        return (unity_Lightmap_HDR.x * pow(encoded.a, unity_Lightmap_HDR.y)) * encoded.rgb;
    #endif
#else
    return encoded.rgb;
#endif
}

float3 MaxSampleBakedDiffuse(float2 lightmapUV, float3 positionWS, float3 normalWS,
    float3 albedo, float metalness, float roughness, float3 unmappedNormalWS)
{
#if defined(LIGHTMAP_ON)
    float3 irradiance = MaxDecodeLightmap(UNITY_SAMPLE_TEX2D(unity_Lightmap, lightmapUV));
    if (_MaxLightingDiagnosticMode == 9.0)
        return irradiance * MAX_BAKED_IRRADIANCE_TO_SCENE_HDR;
    // Only offline diagnostic modes substitute the unmapped mesh normal. The
    // production path keeps its existing mapped-normal direction and BRDF math.
    if (_MaxLightingDiagnosticMode == 10.0 || _MaxLightingDiagnosticMode == 12.0)
        normalWS = unmappedNormalWS;
    #if defined(DIRLIGHTMAP_COMBINED)
        float4 direction = UNITY_SAMPLE_TEX2D_SAMPLER(unity_LightmapInd, unity_Lightmap, lightmapUV);
        // Its length encodes directionality; normalizing destroys that information.
        float halfLambert = dot(normalWS, direction.xyz - 0.5) + 0.5;
        irradiance *= max(halfLambert, 0.0) / max(direction.w, 1e-4);
    #endif
    if (_MaxLightingDiagnosticMode == 10.0 || _MaxLightingDiagnosticMode == 11.0)
        return max(irradiance, 0.0) * MAX_BAKED_IRRADIANCE_TO_SCENE_HDR;
    metalness = saturate(metalness);
    float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo, metalness);
    float NdotV = saturate(dot(normalWS, GetSurfaceViewDirection(positionWS)));
    float3 diffuseReflectance = albedo * MaxIndirectDiffuseWeight(NdotV, F0, max(roughness, 0.05), metalness);
    return max(irradiance, 0.0) * diffuseReflectance * MAX_BAKED_DIFFUSE_TO_SCENE_HDR;
#else
    return 0.0;
#endif
}

// Compatibility for materials that do not provide a separate unmapped normal.
float3 MaxSampleBakedDiffuse(float2 lightmapUV, float3 positionWS, float3 normalWS,
    float3 albedo, float metalness, float roughness)
{
    return MaxSampleBakedDiffuse(lightmapUV, positionWS, normalWS,
        albedo, metalness, roughness, normalWS);
}

float MaxSampleSkyVisibility(float2 lightmapUV, float slice)
{
#if defined(LIGHTMAP_ON)
    if (_MaxSkyVisibilityReady > 0.5 && slice >= 0.0)
        return saturate(UNITY_SAMPLE_TEX2DARRAY(_MaxSkyVisibilityArray, float3(lightmapUV, slice)).r);
#endif
    return 1.0;
}

#endif
