Shader "MaxSRP/PBRLit"
{
    Properties
    {
        _AlbedoMap ("Albedo", 2D) = "white" {}
        _MetalnessMap("Metallic", 2D) = "black" {}
        _RoughnessMap("Roughness",2D) = "black" {}
        _NormalMap("Normal Map",2D) = "black" {}
        _Roughness("Roughness",Range(0,1)) = 0.2
        _Metalness("Metalness",Range(0,1)) = 0.2
        [Toggle(_RECEIVE_SHADOWS)] _RECEIVE_SHADOWS ("Receive Shadows", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "LightMode"="MaxForwardBase"}

        HLSLINCLUDE
        #pragma enable_cbuffer
        #include "./PBRLitPass.hlsl"
        ENDHLSL 

        Pass
        {
            Name "DEFAULT"
            Cull Back

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex PBRVertex
            #pragma fragment PBRFragment

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
