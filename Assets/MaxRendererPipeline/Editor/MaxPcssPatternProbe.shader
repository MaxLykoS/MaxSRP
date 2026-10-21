Shader "Hidden/MaxSRP/PcssPatternProbe"
{
    SubShader
    {
        HLSLINCLUDE
        #include "MaxPcssPatternProbe.hlsl"
        ENDHLSL
        Pass
        {
            Name "Raw"
            Cull Off ZTest Always ZWrite Off Blend Off
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex PBRVertex
            #pragma fragment PatternRawFragment
            #pragma multi_compile_local _ MAX_PCSS_LEGACY_VOGEL
            ENDHLSL
        }
        Pass
        {
            Name "Quad"
            Cull Off ZTest Always ZWrite Off Blend Off
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex PBRVertex
            #pragma fragment PatternFragment
            ENDHLSL
        }
    }
}
