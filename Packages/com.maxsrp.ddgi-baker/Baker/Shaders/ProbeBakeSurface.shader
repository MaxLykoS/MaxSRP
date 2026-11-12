Shader "Hidden/MaxSRP/DDGIBaker/ProbeBakeSurface"
{
    Properties
    {
        _BakeAlbedo("Albedo", 2D) = "white" {}
        _BakeMetalness("Metalness / Bistro Mask", 2D) = "black" {}
        _BakeRoughness("Roughness", 2D) = "black" {}
        _BakeUvScaleOffset("UV scale and offset", Vector) = (1, 1, 0, 0)
        _BakeMaterialParams("Cutoff / Bistro mask / Two-sided / Scene material", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Pass
        {
            Name "DDGIProbeBake"
            Tags { "LightMode" = "RayTracing" }

            HLSLPROGRAM
            #pragma raytracing ProbeBakeSurface

            #include "UnityRaytracingMeshUtils.cginc"
            #include "ProbeTracePayload.hlsl"

            Texture2D<float4> _BakeAlbedo, _BakeMetalness, _BakeRoughness;
            SamplerState sampler_BakeAlbedo, sampler_BakeMetalness, sampler_BakeRoughness;
            float4 _BakeUvScaleOffset;
            float4 _BakeMaterialParams;

            float3 NormalizeProbeNormal(float3 normal)
            {
                float lengthSquared = dot(normal, normal);
                return lengthSquared > 1e-30f ? normal * rsqrt(lengthSquared) : 0.0f;
            }

            float2 BakeSurfaceUV(uint3 indices, float2 barycentrics)
            {
                float2 uv = (1.0f - barycentrics.x - barycentrics.y)
                    * UnityRayTracingFetchVertexAttribute2(indices.x, kVertexAttributeTexCoord0)
                    + barycentrics.x * UnityRayTracingFetchVertexAttribute2(indices.y, kVertexAttributeTexCoord0)
                    + barycentrics.y * UnityRayTracingFetchVertexAttribute2(indices.z, kVertexAttributeTexCoord0);
                return uv * _BakeUvScaleOffset.xy + _BakeUvScaleOffset.zw;
            }

            [shader("anyhit")]
            void ProbeAlphaTest(inout ProbeTracePayload payload : SV_RayPayload,
                BuiltInTriangleIntersectionAttributes attributes : SV_IntersectionAttributes)
            {
                uint3 indices = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
                float2 uv = BakeSurfaceUV(indices, attributes.barycentrics);
                if (_BakeAlbedo.SampleLevel(sampler_BakeAlbedo, uv, 0.0f).a < _BakeMaterialParams.x)
                    IgnoreHit();
            }

            [shader("closesthit")]
            void ProbeClosestHit(inout ProbeTracePayload payload : SV_RayPayload,
                BuiltInTriangleIntersectionAttributes attributes : SV_IntersectionAttributes)
            {
                uint3 indices = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());
                float3 a = UnityRayTracingFetchVertexAttribute3(indices.x, kVertexAttributePosition);
                float3 b = UnityRayTracingFetchVertexAttribute3(indices.y, kVertexAttributePosition);
                float3 c = UnityRayTracingFetchVertexAttribute3(indices.z, kVertexAttributePosition);
                float3x3 objectToWorld = (float3x3)ObjectToWorld();
                float3 geometricNormal = NormalizeProbeNormal(cross(
                    mul(objectToWorld, b - a), mul(objectToWorld, c - a)));

                float2 uv = attributes.barycentrics;
                float3 normal = (1.0f - uv.x - uv.y) * UnityRayTracingFetchVertexAttribute3(indices.x, kVertexAttributeNormal)
                    + uv.x * UnityRayTracingFetchVertexAttribute3(indices.y, kVertexAttributeNormal)
                    + uv.y * UnityRayTracingFetchVertexAttribute3(indices.z, kVertexAttributeNormal);
                normal = NormalizeProbeNormal(mul(normal, (float3x3)WorldToObject()));
                if (dot(normal, normal) < 0.5f) normal = geometricNormal;
                if (dot(normal, geometricNormal) < 0.0f) normal = -normal;

                float distance = RayTCurrent();
                bool backface = dot(WorldRayDirection(), geometricNormal) > 0.0f;
                if (backface && _BakeMaterialParams.z > 0.5f)
                {
                    normal = -normal;
                    geometricNormal = -geometricNormal;
                    backface = false;
                }
                // Keep the SDK's signed-distance encoding for one-sided backfaces.
                payload.signedDistance = backface ? -0.2f * distance : distance;
                payload.normal = normal;
                payload.geometricNormal = geometricNormal;
                if (_BakeMaterialParams.w > 0.5f && !backface)
                {
                    float2 materialUV = BakeSurfaceUV(indices, uv);
                    // The original texture's sRGB view decodes base color to linear.
                    float3 albedo = saturate(_BakeAlbedo.SampleLevel(sampler_BakeAlbedo, materialUV, 0.0f).rgb);
                    float3 mask = _BakeMetalness.SampleLevel(sampler_BakeMetalness, materialUV, 0.0f).rgb;
                    bool bistroMask = _BakeMaterialParams.y > 0.5f;
                    float metalness = saturate(bistroMask ? mask.b : mask.r);
                    float roughness = max(0.05f, saturate(bistroMask ? mask.g
                        : _BakeRoughness.SampleLevel(sampler_BakeRoughness, materialUV, 0.0f).r));

                    // Match MaxSRP's roughness-aware diffuse response for the outgoing ray.
                    float3 f0 = lerp(0.04f.xxx, albedo, metalness);
                    float f = 1.0f - saturate(dot(normal, -WorldRayDirection()));
                    float f2 = f * f;
                    float3 fresnel = f0 + (max(1.0f - roughness, f0) - f0) * (f2 * f2 * f);
                    payload.diffuseReflectance = albedo * (1.0f - fresnel) * (1.0f - metalness);
                }
            }
            ENDHLSL
        }
    }
}
