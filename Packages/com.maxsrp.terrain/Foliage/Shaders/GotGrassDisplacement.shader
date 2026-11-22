Shader "Hidden/MaxTerrain/GotGrassDisplacement"
{
    SubShader
    {
        Pass
        {
            Name "ParticleDisplacement"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One One

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex StampVertex
            #pragma fragment StampFragment
            #include "UnityCG.cginc"

            StructuredBuffer<float4> _GotGrassStampCenterSize;
            // w is negative for a moving particle.
            StructuredBuffer<float4> _GotGrassStampBasisAmplitude;
            float4 _GotGrassDisplacementWindow; // center XZ, inverse window width, unused
            Texture2D<float2> _GotGrassTrailBrush;
            SamplerState sampler_GotGrassTrailBrush;
            float4 _GotGrassTrailBrushParams; // procedural forward, procedural side, use texture, unused

            struct StampVaryings
            {
                float4 position : SV_Position;
                float2 uv : TEXCOORD0;
                nointerpolation float2 axis : TEXCOORD1;
                nointerpolation float2 amplitudeAndMode : TEXCOORD2;
            };

            static const float2 StampCorners[6] = {
                float2(-1, -1), float2(1, -1), float2(-1, 1),
                float2(-1, 1), float2(1, -1), float2(1, 1)
            };

            float2 Perpendicular(float2 axis)
            {
                return float2(-axis.y, axis.x);
            }

            StampVaryings StampVertex(uint vertex : SV_VertexID, uint instance : SV_InstanceID)
            {
                float4 centerSize = _GotGrassStampCenterSize[instance];
                float4 basis = _GotGrassStampBasisAmplitude[instance];
                float2 corner = StampCorners[vertex];
                float2 axis = basis.xy;
                float2 across = Perpendicular(axis);
                float2 position = centerSize.xy - _GotGrassDisplacementWindow.xy;
                position += corner.x * centerSize.w * axis;
                position += corner.y * centerSize.z * across;
                float2 clip = position * (2 * _GotGrassDisplacementWindow.z);
                #if !UNITY_UV_STARTS_AT_TOP
                    clip.y = -clip.y;
                #endif

                StampVaryings output;
                output.position = float4(clip, 0, 1);
                output.uv = corner * 0.5 + 0.5;
                output.axis = axis;
                output.amplitudeAndMode = basis.zw;
                return output;
            }

            float2 StampFragment(StampVaryings input) : SV_Target
            {
                float2 local = input.uv * 2 - 1;
                float radiusSquared = dot(local, local);
                float envelope = 1 - smoothstep(.2, 1, radiusSquared);
                float2 across = Perpendicular(input.axis);
                float amplitude = input.amplitudeAndMode.x;
                [branch] if(input.amplitudeAndMode.y < 0)
                {
                    [branch] if(_GotGrassTrailBrushParams.z > 0)
                    {
                        // Native quad U falls along axis A (across); V rises along axis B (forward).
                        float2 uv = float2(1 - input.uv.y, input.uv.x);
                        float2 q = _GotGrassTrailBrush.SampleLevel(sampler_GotGrassTrailBrush, uv, 0) * 2 - 1;
                        q *= abs(q);
                        return amplitude * ((q.x + q.y) * .5 * across + (q.x - q.y) * .5 * input.axis);
                    }
                    float side = _GotGrassTrailBrushParams.y * clamp(3 * local.y, -1, 1);
                    return amplitude * envelope * (_GotGrassTrailBrushParams.x * input.axis + side * across);
                }
                float2 radial = local * rsqrt(max(radiusSquared, .16)) * .70710678;
                return amplitude * envelope * (radial.x * input.axis + radial.y * across);
            }
            ENDHLSL
        }
    }
}
