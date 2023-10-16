Shader "MaxSRP/ScreenSpaceShadowMap"
{
    Properties
    {
        
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }

        HLSLINCLUDE
        #pragma enable_cbuffer
        #pragma enable_d3d11_debug_symbols
        #include "./ScreenSpaceShadowMapPass.hlsl"
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
