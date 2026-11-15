Shader "Hidden/MaxSRP/CloudLayerLighting"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex CloudBakeVertex
            #pragma fragment CloudBakeFragment
            #include "CloudLayerLighting.hlsl"
            ENDHLSL
        }
    }
}
