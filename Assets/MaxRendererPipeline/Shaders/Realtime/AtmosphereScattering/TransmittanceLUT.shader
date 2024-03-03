Shader "MaxSRP/Hidden/TransmittanceLUT"
{
    Properties
    {
        
    }
    SubShader
    {
        HLSLINCLUDE
        #pragma enable_d3d11_debug_symbols
        #include "TransmittanceLUTPass.hlsl"
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
