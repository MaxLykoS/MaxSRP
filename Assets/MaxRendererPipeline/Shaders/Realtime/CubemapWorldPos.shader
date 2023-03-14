Shader "MaxSRP/CubemapWorldPos"
{
    Properties
    {

    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "LightMode"="MaxForwardBase"}

        HLSLINCLUDE
        #pragma enable_cbuffer
        #include "../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
        ENDHLSL 

        Pass
        {
            Name "DEFAULT"
            Cull Back

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex vert
            #pragma fragment frag      

            struct appdata
            {
                float3 vertex : POSITION;
                float3 posW : TEXCOORD0;
            };

            struct v2f
            {
                float3 posW : TEXCOORD0;
                float4 posH : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.posH = ObjectToHClipPosition(v.vertex);
                o.posW = TransformObjectToWorld(v.vertex.xyz);
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                return float4(i.posW, 1.0);
            }

            ENDHLSL
        }
    }
}
