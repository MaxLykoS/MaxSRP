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
        Blend Off

        HLSLINCLUDE
        #pragma enable_d3d11_debug_symbols
        #include "SkyboxProceduralPass.hlsl"
        ENDHLSL
        Pass
        {
            Name "ProceduralSkybox"
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }
    }
}
