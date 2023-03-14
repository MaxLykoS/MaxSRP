Shader "MaxSRP/Decal"
{
    Properties
    {
        _AlbedoMap ("Albedo", 2D) = "white" {}
        _DecalScale("Decal Scale", Range(0,1)) = 0
    }
    SubShader
    {
        Tags {"RenderType" = "Transparent" "LightMode" = "MaxForwardBase" "Queue" = "Transparent"}

        HLSLINCLUDE
        #pragma enable_cbuffer
        #include "./DecalLitPass.hlsl"
        ENDHLSL

        Pass
        {
            Name "DEFAULT"

            //Blend SrcAlpha OneMinusSrcAlpha
            //Cull Off

            HLSLPROGRAM
            #pragma vertex DecalVertex
            #pragma fragment DecalFragment

            #pragma shader_feature _RECEIVE_SHADOWS   
            ENDHLSL
        }
    }
}
