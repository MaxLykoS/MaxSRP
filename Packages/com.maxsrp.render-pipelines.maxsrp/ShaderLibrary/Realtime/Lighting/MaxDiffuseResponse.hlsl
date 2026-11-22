#ifndef MAX_DIFFUSE_RESPONSE_INCLUDED
#define MAX_DIFFUSE_RESPONSE_INCLUDED

// Pure material response: usable by both UnityCG-based debug shaders and the
// custom SRP without introducing a second camera / UnityPerDraw declaration.
float3 fresnelSchlickRoughness(float NdotV, float3 F0, float roughness)
{
    float f = 1.0 - saturate(NdotV);
    float f2 = f * f;
    return F0 + (max(float3(1.0 - roughness, 1.0 - roughness, 1.0 - roughness), F0) - F0)
        * (f2 * f2 * f);
}

// Baked and sky diffuse share the same receiving-material response. No extra
// Lambert pi belongs here: sky supplies E/pi and MaxBakedLighting converts
// native irradiance E to E/pi at its outgoing-radiance boundary.
float3 MaxIndirectDiffuseWeight(float NdotV, float3 F0, float roughness, float metalness)
{
    return (1.0 - fresnelSchlickRoughness(NdotV, F0, roughness)) * (1.0 - metalness);
}

#endif
