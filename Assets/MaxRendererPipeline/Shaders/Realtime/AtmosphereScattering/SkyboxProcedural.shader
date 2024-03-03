Shader "MaxSRP/SkyboxProcedural"
{
    Properties
    {

    }
    SubShader
    {
        Cull Off 
        ZWrite Off 
        ZTest LEqual

        HLSLINCLUDE
        #pragma enable_d3d11_debug_symbols
        #include "SkyboxProceduralPass.hlsl"
        ENDHLSL
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }
    }
}
