Shader "Hidden/MaxSRP/PostProcessBloom"
{
    SubShader
    {
        Cull Off
        ZTest Always
        ZWrite Off
        Blend Off
        HLSLINCLUDE
        #pragma target 5.0
        #include "PostProcessBloom.hlsl"
        ENDHLSL
        Pass
        {
            Name "SetupPC"
            HLSLPROGRAM
            #pragma vertex BloomVertex
            #pragma fragment BloomSetupPC
            ENDHLSL
        }
        Pass
        {
            Name "DownsamplePC"
            HLSLPROGRAM
            #pragma vertex BloomVertex
            #pragma fragment BloomDownsamplePC
            ENDHLSL
        }
        Pass
        {
            Name "GaussianX"
            HLSLPROGRAM
            #pragma vertex BloomVertex
            #pragma fragment BloomGaussianX
            ENDHLSL
        }
        Pass
        {
            Name "GaussianYAdd"
            HLSLPROGRAM
            #pragma vertex BloomVertex
            #pragma fragment BloomGaussianYAdd
            ENDHLSL
        }
        Pass
        {
            Name "SetupMobile"
            HLSLPROGRAM
            #pragma vertex BloomVertex
            #pragma fragment BloomSetupMobile
            ENDHLSL
        }
        Pass
        {
            Name "DownsampleMobile"
            HLSLPROGRAM
            #pragma vertex BloomVertex
            #pragma fragment BloomDownsampleMobile
            ENDHLSL
        }
        Pass
        {
            Name "UpsampleMobile"
            HLSLPROGRAM
            #pragma vertex BloomVertex
            #pragma fragment BloomUpsampleMobile
            ENDHLSL
        }
        Pass
        {
            Name "MergeMobile"
            HLSLPROGRAM
            #pragma vertex BloomVertex
            #pragma fragment BloomMergeMobile
            ENDHLSL
        }
    }
}
