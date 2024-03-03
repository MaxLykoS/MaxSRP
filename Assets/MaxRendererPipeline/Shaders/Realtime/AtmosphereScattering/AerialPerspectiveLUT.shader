Shader "MaxSRP/Hidden/AerialPerspectiveLUT"
{
    Properties
    {
        
    }
    SubShader
    {
        HLSLINCLUDE
        #pragma enable_d3d11_debug_symbols
        #include "AerialPerspectiveLUTPass.hlsl"
        ENDHLSL

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }
    }
}
