Shader "Hidden/MaxSRP/DDGIBaker/ProbeBakeSurface"
{
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

            float3 NormalizeProbeNormal(float3 normal)
            {
                float lengthSquared = dot(normal, normal);
                return lengthSquared > 1e-30f ? normal * rsqrt(lengthSquared) : 0.0f;
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

                // Match NativeGI's winding-derived front face and signed distance encoding.
                float distance = RayTCurrent();
                payload.signedDistance = dot(WorldRayDirection(), geometricNormal) > 0.0f ? -0.2f * distance : distance;
                payload.normal = normal;
                payload.geometricNormal = geometricNormal;
            }
            ENDHLSL
        }
    }
}
