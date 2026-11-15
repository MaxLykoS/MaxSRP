Shader "Hidden/MaxSRP/ColorOutput"
{
    SubShader
    {
        Pass
        {
            Name "ColorOutput"
            Cull Off
            ZTest Always
            ZWrite On
            Blend Off

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex ColorOutputVertex
            #pragma fragment ColorOutputFragment
            #include "ColorOutputPass.hlsl"
            ENDHLSL
        }
    }
}
