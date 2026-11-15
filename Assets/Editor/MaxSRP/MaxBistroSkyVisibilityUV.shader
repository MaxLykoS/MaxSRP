Shader "Hidden/MaxSRP/Bistro Sky Visibility UV"
{
    Properties
    {
        _MainTex("Alpha Extraction Source", 2D) = "white" {}
    }
    SubShader
    {
        Pass
        {
            Cull Off ZTest Always ZWrite Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            float4x4 _BakeObjectToWorld;
            float4x4 _BakeNormalToWorld;
            float4 _BakeLightmapST;
            float4 _BakeBaseST;
            sampler2D _BakeBaseMap;
            float _BakeCutoff;
            struct Attributes { float4 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float2 uv2 : TEXCOORD1; };
            struct Varyings { float4 position : SV_POSITION; float3 world : TEXCOORD0; float2 uv : TEXCOORD1; float3 normal : TEXCOORD2; };
            Varyings Vert(Attributes v)
            {
                Varyings o;
                float2 atlasUV = v.uv2 * _BakeLightmapST.xy + _BakeLightmapST.zw;
                o.position = float4(atlasUV * 2 - 1, 0, 1);
                #if UNITY_UV_STARTS_AT_TOP
                    o.position.y = -o.position.y;
                #endif
                o.world = mul(_BakeObjectToWorld, v.position).xyz;
                o.normal = mul((float3x3)_BakeNormalToWorld, v.normal);
                o.uv = v.uv * _BakeBaseST.xy + _BakeBaseST.zw;
                return o;
            }
            struct Output { float4 position : SV_Target0; float4 normal : SV_Target1; };
            Output Frag(Varyings i)
            {
                if (_BakeCutoff > 0) clip(tex2D(_BakeBaseMap, i.uv).a - _BakeCutoff);
                Output o;
                // UV-space winding differs between charts; vertex normals only orient the
                // flat derivative normal. Normal maps never affect sky-visibility rays.
                o.position = float4(i.world, 1);
                float3 geometric = normalize(cross(ddy(i.world), ddx(i.world)));
                if (dot(geometric, i.normal) < 0) geometric = -geometric;
                o.normal = float4(geometric, 1);
                return o;
            }
            ENDHLSL
        }
        Pass
        {
            Cull Off ZTest Always ZWrite Off
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragAlpha
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float FragAlpha(v2f_img i) : SV_Target { return tex2D(_MainTex, i.uv).a; }
            ENDHLSL
        }
    }
}
