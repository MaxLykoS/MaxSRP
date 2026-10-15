Shader "MaxSRP/ScreenSpaceShadowMap"
{
    Properties
    {
        
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #pragma enable_cbuffer
        #pragma enable_d3d11_debug_symbols
        #include "./ScreenSpaceShadowMapPass.hlsl"
        ENDHLSL
        Pass //ScreenSpaceShadowMap
        {
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex vert_v
            #pragma fragment fragBlur
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex vert_h
            #pragma fragment fragBlur
            ENDHLSL
        }
    }
}
