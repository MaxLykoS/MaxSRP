Shader "Hidden/MaxSRP/MobileSSAO"
{
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off
        HLSLINCLUDE
        // Raster-only ES3 / SM4 baseline. Fine derivatives are an optional
        // higher-feature variant, not an implicit compute/UAV requirement.
        #pragma target 3.5
        #pragma target 4.5 MAX_SSAO_INLINE_QUAD
        #pragma multi_compile_local _ MAX_SSAO_INLINE_QUAD
        #include "MaxSSAO.hlsl"
        ENDHLSL
        Pass
        {
            Name "FurthestDepthSetup"
            HLSLPROGRAM
            #pragma vertex SSAOVertex
            #pragma fragment SSAOSetup
            ENDHLSL
        }
        Pass
        {
            Name "UEAmbientOcclusion"
            HLSLPROGRAM
            #pragma vertex SSAOVertex
            #pragma fragment SSAOEvaluate
            ENDHLSL
        }
        Pass
        {
            Name "UEAmbientOcclusionFilter"
            HLSLPROGRAM
            #pragma vertex SSAOVertex
            #pragma fragment SSAOFilter
            ENDHLSL
        }
        Pass
        {
            Name "UEBilateralUpsample"
            HLSLPROGRAM
            #pragma vertex SSAOVertex
            #pragma fragment SSAOUpscale
            ENDHLSL
        }
    }
}
