Shader "MaxSRP/SHCubemapVisual"
{
    Properties
    {

    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "LightMode"="MaxForwardBase"}

        Pass
        {
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex VS
            #pragma fragment PS
            #include "UnityCG.cginc"
            #include "../../ShaderLibrary/Realtime/GlobalIllumination.hlsl"

            struct a2v
            {
                float3 pO : POSITION;
            };

            struct v2f
            {
                float4 pH : SV_POSITION;
                float3 pO : TEXCOORD0;
            };

            v2f VS(a2v v)
            {
                v2f o;
			    o.pH = UnityObjectToClipPos(v.pO);
			    o.pO = v.pO;
                return o;
            }
            float4 PS(v2f o) : SV_Target
            {
                float3 SH = CubemapApprox(o.pO);
                return float4(SH, 1.0);
            }
            ENDHLSL
        }
    }
}