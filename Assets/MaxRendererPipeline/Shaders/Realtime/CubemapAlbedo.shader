Shader "MaxSRP/CubemapAlbedo"
{
    Properties
    {
        _AlbedoMap ("Main Tex", 2D) = "white" {}
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
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 posH : SV_POSITION;
            };

            UNITY_DECLARE_TEX2D(_AlbedoMap);

            CBUFFER_START(UnityPerMaterial)
            float4 _AlbedoMap_ST;  
            CBUFFER_END

            v2f vert (appdata v)
            {
                v2f o;
                o.posH = ObjectToHClipPosition(v.vertex);
                o.uv = v.uv * _AlbedoMap_ST.xy + _AlbedoMap_ST.zw;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float4 col = UNITY_SAMPLE_TEX2D(_AlbedoMap, i.uv);
                return col;
            }
            ENDHLSL
        }
    }
}
