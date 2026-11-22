Shader "Hidden/MaxSRP/TemporalAA"
{
    SubShader
    {
        Cull Off ZTest Always ZWrite Off Blend Off
        HLSLINCLUDE
        #pragma target 5.0
        #include "TemporalAAPass.hlsl"
        ENDHLSL
        Pass
        {
            Name "Temporal Resolve"
            HLSLPROGRAM
            #pragma vertex TaaVertex
            #pragma fragment TaaResolve
            #pragma multi_compile_local_fragment _ MAX_TAA_GEN4_LOW
            #pragma multi_compile_fragment _ MAX_TAA_VELOCITY_UINT
            #pragma multi_compile_fragment _ MAX_AUTO_EXPOSURE_BUFFER
            ENDHLSL
        }
        Pass
        {
            Name "Resolved Color"
            HLSLPROGRAM
            #pragma vertex TaaVertex
            #pragma fragment TaaCopy
            #pragma multi_compile_local_fragment _ MAX_TAA_GEN4_LOW
            ENDHLSL
        }
        Pass
        {
            Name "Temporal Resolve Color Only"
            HLSLPROGRAM
            #pragma vertex TaaVertex
            #pragma fragment TaaResolveColorOnly
            #pragma multi_compile_local_fragment _ MAX_TAA_GEN4_LOW
            #pragma multi_compile_fragment _ MAX_TAA_VELOCITY_UINT
            #pragma multi_compile_fragment _ MAX_AUTO_EXPOSURE_BUFFER
            ENDHLSL
        }
    }
}
