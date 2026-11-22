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
            #pragma multi_compile_fragment _ MAX_AUTO_EXPOSURE_BUFFER
            #include "ColorOutputPass.hlsl"
            ENDHLSL
        }
    }
}
