Shader "Hidden/MaxSRP/PcssReuseFixture"
{
    SubShader
    {
        Pass
        {
            Cull Off ZTest Always ZWrite Off Blend Off
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex PBRVertex
            #pragma fragment ReuseFixtureFragment
            #include "MaxPcssReuseFixture.hlsl"
            ENDHLSL
        }
    }
}
