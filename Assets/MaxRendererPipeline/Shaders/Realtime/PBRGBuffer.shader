Shader "Unlit/PBRGBuffer"
{
    Properties
    {
        _AlbedoMap("Albedo", 2D) = "white" {}
        _MetalnessMap("Metallic", 2D) = "black" {}
        _RoughnessMap("Roughness",2D) = "black" {}
        [Normal] _NormalMap("Normal Map",2D) = "black" {}
        [Toggle(_RECEIVE_SHADOWS)] _RECEIVE_SHADOWS("Receive Shadows", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "LightMode" = "MaxDeferred"}

        HLSLINCLUDE
        #pragma enable_cbuffer
        #pragma enable_d3d11_debug_symbols
        #include "./PBRGBufferPass.hlsl"
        ENDHLSL

        Pass
        {
            Name "DEFAULT"
            Cull Back

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex PBRGBufferVertex
            #pragma fragment PBRGBufferFragment

            //#pragma shader_feature _RECEIVE_SHADOWS       
            ENDHLSL
        }

        /*Pass
        {
            Name "ShadowCaster"
            Tags{"LightMode" = "ShadowCaster"}

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM

            #pragma vertex ShadowCasterVertex
            #pragma fragment ShadowCasterFragment

            ENDHLSL
        }*/
    }
}
