#ifndef BRDF_INCLUDED
#define BRDF_INCLUDED

#define PI 3.14159265359
#define MIN_REFLECTIVITY 0.04

float DistributionGGX(float3 N, float3 H, float roughness)
{
    float a      = roughness*roughness;
    float a2     = a*a;
    float NdotH  = max(dot(N, H), 0.0);
    float NdotH2 = NdotH*NdotH;

    float nom   = a2;
    float denom = (NdotH2 * (a2 - 1.0) + 1.0);
    denom = PI * denom * denom;

    return nom / denom;
}

float GeometrySchlickGGX(float NdotV, float roughness)
{
    float r = (roughness + 1.0);
    float k = (r*r) / 8.0;

    float nom   = NdotV;
    float denom = NdotV * (1.0 - k) + k;

    return nom / denom;
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
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}  

struct Surface
{
    float3 P;
    float3 L;
    float3 V;
    float3 N;
    float3 albedo;
    float metalness;
    float roughness;
};

float3 BRDF(Surface surface)
{
    float3 F0 = float3(MIN_REFLECTIVITY, MIN_REFLECTIVITY, MIN_REFLECTIVITY);
    F0 = lerp(F0, surface.albedo, surface.metalness);
    
    float3 H = normalize(surface.L + surface.V);     

    // cook-torrance brdf
    float D = DistributionGGX(surface.N, H, surface.roughness);        
    float G   = GeometrySmith(surface.N, surface.V, surface.L, surface.roughness);      
    float3 F    = fresnelSchlick(max(dot(H, surface.V), 0.0), F0);       

    float3 kS = F;
    float3 kD = (float3(1.0, 1.0, 1.0) - kS) * (1.0 - surface.metalness);   

    float3 nominator    = D * F * G;
    float denominator = 4.0 * max(dot(surface.N, surface.V), 0.0) * max(dot(surface.N, surface.L), 0.0) + 0.001; 
    float3 specular     = nominator / denominator;
                
    return kD * surface.albedo / PI + specular; 
}

float3 IndirectBRDF(Surface surface, float3 indirectDiffuse, float3 indirectSpec)
{
    float kiD = 1.0 - MIN_REFLECTIVITY;
    kiD = kiD - surface.metalness * kiD;

    return kiD * surface.albedo * indirectDiffuse + indirectSpec;
}
#endif