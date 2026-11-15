Shader "MaxSRP/Hidden/SkyviewLUT"
{
    Properties
    {
        
    }
    SubShader
    {
        HLSLINCLUDE
        #pragma enable_d3d11_debug_symbols
        #include "SkyviewLUTPass.hlsl"
        ENDHLSL

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 5.0
            #pragma multi_compile_local_fragment _ MAX_ATMOSPHERE_MOBILE
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }
    }
}
