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
            ENDHLSL
        }
        Pass
        {
            Name "Resolved Color"
            HLSLPROGRAM
            #pragma vertex TaaVertex
            #pragma fragment TaaCopy
            ENDHLSL
        }
    }
}
