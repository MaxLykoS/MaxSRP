Shader "Hidden/MaxSRP/Shadow Tile Clear"
{
    SubShader
    {
        Pass
        {
            Cull Off ZTest Always ZWrite On ColorMask 0
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            float4 Vert(uint id : SV_VertexID) : SV_POSITION
            {
                float2 uv = float2((id << 1) & 2, id & 2);
                return float4(uv * 2.0 - 1.0, 0.0, 1.0);
            }
            float Frag() : SV_Depth
            {
            #if defined(UNITY_REVERSED_Z)
                return 0.0;
            #else
                return 1.0;
            #endif
            }
            ENDHLSL
        }
    }
}
