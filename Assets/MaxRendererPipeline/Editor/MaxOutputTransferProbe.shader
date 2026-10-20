Shader "Hidden/MaxSRP/OutputTransferProbe"
{
    SubShader
    {
        Pass
        {
            Cull Off ZTest Always ZWrite Off Blend Off
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex ColorOutputVertex
            #pragma fragment ProbeFragment
            #include "../Shaders/Realtime/ColorOutputPass.hlsl"
            float4 ProbeFragment(ColorOutputVaryings input, out float depth : SV_Depth) : SV_Target
            {
                // Frozen pre-fix SDR transfer, reusing the same tone curve/noise.
                float4 source = UNITY_SAMPLE_TEX2D(_SceneColorHDR, input.uv);
                depth = UNITY_SAMPLE_DEPTH(UNITY_SAMPLE_TEX2D(_SceneDepthHDR, input.uv));
                float3 color = max(source.rgb, 0.0) * _ColorOutputParameters.x;
                if (_ColorOutputParameters.y > .5) color = ToneMapACES(color);
                if (_ColorOutputParameters.w > 1.5)
                    color = ColorOutputDecodeSRGB(saturate(ColorOutputEncodeSRGB(saturate(color)) + ColorOutputDitherNoise(input.position.xy) / 255.0));
                return float4(color, source.a);
            }
            ENDHLSL
        }
    }
}
