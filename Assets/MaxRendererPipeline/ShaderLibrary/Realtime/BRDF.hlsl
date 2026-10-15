#ifndef BRDF_INCLUDED
#define BRDF_INCLUDED

#define PI 3.14159265359
#define MIN_REFLECTIVITY 0.04

float DistributionGGX(float3 N, float3 H, float roughness)
{
    float a      = roughness * roughness;
    float a2     = a * a;
    float NdotH  = saturate(dot(N, H));
    float NdotH2 = NdotH * NdotH;

    float nom   = a2;
    float denom = (1.0 - NdotH2) + NdotH2 * a2;
    denom = PI * denom * denom;

    return nom / max(denom, 1e-10);
}

float GeometrySchlickGGX(float NdotV, float roughness)
{
    float r = (roughness + 1.0); 
    float k = (r * r) / 8.0;

    float nom   = NdotV;
    float denom = NdotV * (1.0 - k) + k;

    return nom / max(denom, 1e-10);
}
float GeometrySmith(float3 N, float3 V, float3 L, float roughness)
{
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    float ggx2  = GeometrySchlickGGX(NdotV, roughness);
    float ggx1  = GeometrySchlickGGX(NdotL, roughness);

    return ggx1 * ggx2;
}
float3 fresnelSchlick(float cosTheta, float3 F0)
{
    float f = 1.0 - saturate(cosTheta);
    float f2 = f * f;
    return F0 + (1.0 - F0) * (f2 * f2 * f);
}

struct Surface
{
    float3 P;
    float3 L;
    float3 V;
    float3 F0;
    float3 N;
    float3 albedo;
    float metalness;
    float roughness;
};

float3 CookTorranceBRDF(Surface surface)
{    
    float NdotL = saturate(dot(surface.N, surface.L));
    // Normal-map lobes may face away from V on otherwise visible geometry.
    // Smith suppresses their specular term; do not hard-cut the diffuse term.
    if (NdotL <= 0.0) return float3(0.0, 0.0, 0.0);
    float3 halfVector = surface.L + surface.V;
    float3 H = halfVector * rsqrt(max(dot(halfVector, halfVector), 1e-10));

    // cook-torrance brdf
    float NDF = DistributionGGX(surface.N, H, surface.roughness);        
    float G   = GeometrySmith(surface.N, surface.V, surface.L, surface.roughness);      
    float3 F    = fresnelSchlick(max(dot(H, surface.V), 0.0), surface.F0);       

    float3 kS = F;
    float3 kD = (float3(1.0, 1.0, 1.0) - kS) * (1.0 - surface.metalness);   

    float3 nominator    = NDF * G * F;
    float denominator = 4.0 * max(dot(surface.N, surface.V), 0.0) * max(dot(surface.N, surface.L), 0.0) + 0.001; 
    float3 specular     = nominator / denominator;
    return kD * surface.albedo / PI + specular;
}
#endif
