Shader "MaxSRP/PBRLitTransparent"
{
    Properties
    {
        _AlbedoMap("Albedo", 2D) = "white" {}
        _MetalnessMap("Metallic", 2D) = "black" {}
        _RoughnessMap("Roughness",2D) = "black" {}
        _NormalMap("Normal Map",2D) = "black" {}
        _Transparency("Transparency", Range(0,1)) = 0.7
        [Toggle(_RECEIVE_SHADOWS)] _RECEIVE_SHADOWS ("Receive Shadows", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "LightMode"="MaxForwardBase" "Queue" = "Transparent"}
        ZWrite Off

        HLSLINCLUDE
        #pragma enable_cbuffer
        #include "./PBRLitPass.hlsl"
        ENDHLSL    

        Pass
        {
            Name "DEFAULT"

            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM   
            #pragma vertex PBRVertex
            #pragma fragment PBRFragmentTransparent

            #pragma shader_feature _RECEIVE_SHADOWS  
            ENDHLSL
        }

        Pass
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
        }
    }
}