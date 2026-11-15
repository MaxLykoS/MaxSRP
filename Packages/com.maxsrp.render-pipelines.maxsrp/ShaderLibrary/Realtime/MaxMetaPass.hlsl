#ifndef MAX_META_PASS_INCLUDED
#define MAX_META_PASS_INCLUDED

#include "CommonInput.hlsl"

// UnityMetaPass.cginc's extraction protocol, with no UnityCG dependency and
// therefore the same SRP UnityPerDraw layout as the other material passes.
CBUFFER_START(UnityMetaPass)
bool4 unity_MetaVertexControl;
bool4 unity_MetaFragmentControl;
int unity_VisualizationMode;
CBUFFER_END
float unity_OneOverOutputBoost;
float unity_MaxOutputValue;

float4 MaxMetaRasterPosition(float4 positionOS, float2 lightmapUV, float2 dynamicUV)
{
    if (unity_MetaVertexControl.x)
    {
        positionOS.xy = lightmapUV * unity_LightmapST.xy + unity_LightmapST.zw;
        positionOS.z = positionOS.z > 0.0 ? 1e-4 : 0.0;
    }
    if (unity_MetaVertexControl.y)
    {
        positionOS.xy = dynamicUV * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
        positionOS.z = positionOS.z > 0.0 ? 1e-4 : 0.0;
    }
    return mul(unity_MatrixVP, float4(positionOS.xyz, 1.0));
}

float3 MaxMetaLightmappingAlbedo(float3 albedo, float metalness, float roughness)
{
    metalness = saturate(metalness);
    float3 diffuse = albedo * (0.96 * (1.0 - metalness));
    float3 specular = lerp(float3(0.04, 0.04, 0.04), albedo, metalness);
    // Match UnityStandardMeta: rough metals still return a broad reflected lobe
    // to the diffuse baker. Its roughness is squared perceptual roughness.
    return diffuse + specular * saturate(roughness) * saturate(roughness) * 0.5;
}

float4 MaxMetaFragment(float3 albedo)
{
    if (unity_MetaFragmentControl.x)
        return float4(clamp(pow(max(albedo, 0.0), saturate(unity_OneOverOutputBoost)), 0.0, unity_MaxOutputValue), 1.0);
    // Practical point lights represent luminous Bistro fixtures. Baking fixture
    // emission too would count their energy twice; emission extraction is black.
    return float4(0.0, 0.0, 0.0, 1.0);
}

#endif
