Shader "MaxSRP/SixWayCloud"
{
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend One OneMinusSrcAlpha, Zero One
        Pass
        {
            Name "SixWayMainCloud"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex SixWayVertex
            #pragma fragment SixWayFragment
            #include "SixWayCloudPass.hlsl"
            ENDHLSL
        }
    }
}
