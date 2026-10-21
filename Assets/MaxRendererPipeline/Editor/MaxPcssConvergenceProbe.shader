Shader "Hidden/MaxSRP/PcssConvergenceProbe"
{
    SubShader
    {
        Cull Off ZTest Always ZWrite Off
        HLSLINCLUDE
        #include "MaxPcssConvergenceProbe.hlsl"
        ENDHLSL
        Pass
        {
            Blend Off
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex PBRVertex
            #pragma fragment ConvergenceFragment
            ENDHLSL
        }
        Pass
        {
            Blend One One
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex PBRVertex
            #pragma fragment ConvergenceFragment
            ENDHLSL
        }
        Pass
        {
            Blend Off
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex PBRVertex
            #pragma fragment PlaneFixtureFragment
            ENDHLSL
        }    }
}
