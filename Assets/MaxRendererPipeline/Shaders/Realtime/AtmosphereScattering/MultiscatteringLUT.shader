Shader "MaxSRP/Hidden/MultiscatteringLUT"
{
    Properties
    {
        
    }
    SubShader
    {
        HLSLINCLUDE
        #pragma enable_d3d11_debug_symbols
        #include "MultiscatteringLUTPass.hlsl"
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
