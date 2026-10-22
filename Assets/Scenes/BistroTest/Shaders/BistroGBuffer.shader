Shader "MaxSRP/Bistro GBuffer"
{
    Properties
    {
        _BaseColorMap("Base Color / Opacity", 2D) = "white" {}
        _MaskMap("AO (R), Roughness (G), Metalness (B)", 2D) = "white" {}
        [Normal] _NormalMap("Normal Map", 2D) = "bump" {}
        _EmissionMap("Emission Color", 2D) = "black" {}
        _EmissionNits("Emission Luminance Scale (cd/m2)", Float) = 0
        _Cutoff("Alpha Cutoff (0 = Opaque)", Range(0, 1)) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "LightMode" = "MaxDeferred" }

        HLSLINCLUDE
        #pragma enable_cbuffer
        #include "BistroGBuffer.hlsl"
        ENDHLSL

        Pass
        {
            Name "DEFAULT"
            Cull [_Cull]
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex BistroGBufferVertex
            #pragma fragment BistroGBufferFragment
            ENDHLSL
        }

        Pass
        {
            Name "MotionVectors"
            Tags { "LightMode" = "MotionVectors" }
            Cull [_Cull]
            ZWrite Off
            ZTest Equal
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex BistroMotionVertex
            #pragma fragment BistroMotionFragment
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            ColorMask 0
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex BistroShadowVertex
            #pragma fragment BistroShadowFragment
            ENDHLSL
        }
    }
}
