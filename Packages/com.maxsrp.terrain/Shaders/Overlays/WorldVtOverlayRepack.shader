// Persistent v4 archive identity includes this hidden producer shader name.
// Keep the original name while the implementation and page contents are unchanged.
Shader "Hidden/DirectDeltaMesh/WorldVtOverlayRepack"
{
    Properties
    {
        [HideInInspector] _DDOverlayTempColor ("Composited linear albedo in sRGB RT", 2D) = "black" {}
        [HideInInspector] _DDOverlayTempNormal ("Composited affine XYZ normal", 2D) = "gray" {}
        [HideInInspector] _DDOverlayTempData ("AO roughness height code", 2D) = "black" {}
        [HideInInspector] _DDRvtHeight ("Fixed source heights for normal fallback", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        Pass
        {
            Name "Repack"
            CGPROGRAM
            #pragma editor_sync_compilation
            #pragma target 4.5
            #pragma vertex RepackVert
            #pragma fragment RepackFrag
            #define DD_WORLD_VT_RGBA8 1
            #include "UnityCG.cginc"
            #include "../VirtualTexturing/SinglePage/SinglePageRvtSurface.cginc"
            #include "../VirtualTexturing/SinglePage/SinglePageRvtCommon.cginc"
            #include "../VirtualTexturing/SinglePage/SinglePageRvtSource.cginc"

            Texture2D<float4> _DDOverlayTempColor;
            Texture2D<float4> _DDOverlayTempNormal;
            Texture2D<float4> _DDOverlayTempData;

            float4 RepackVert(uint vertexId : SV_VertexID) : SV_POSITION
            {
                float2 corner = float2((vertexId << 1) & 2u, vertexId & 2u);
                return float4(corner * 2 - 1, 0, 1);
            }
            struct RepackOutputs
            {
                float4 albedoAo : SV_Target0;
                float4 normalRoughnessHeight : SV_Target1;
            };
            RepackOutputs RepackFrag(float4 position : SV_POSITION)
            {
                // 读三张独立临时图，写回原两张stage；禁止把输入RT本身作为输出。
                // Load 后颜色已由 sRGB SRV 解码，输出至原 sRGB stage 时再由硬件编码。
                int3 texel = int3((int2)position.xy, 0);
                float3 albedo = _DDOverlayTempColor.Load(texel).rgb;
                float3 normal = _DDOverlayTempNormal.Load(texel).rgb * 2 - 1;
                float3 attributes = _DDOverlayTempData.Load(texel).rgb;
                // XYZ8 的零点不能精确表示，近相消向量可能只剩量化噪声。
                // 长度不足0.01时回到固定源基础法线，不对任意微小向量归一化。
                float lengthSquared = dot(normal, normal);
                if (lengthSquared > 1e-4)
                    normal *= rsqrt(lengthSquared);
                else
                    normal = DDRvtSourceNormal(DDRvtPageWorldXZ(position.xy));
                RepackOutputs output;
                output.albedoAo = float4(albedo, attributes.r);
                // HeightCode 已经在1..255范围内混合，此处直接搬运，不重复量化。
                // 若基底意外输出零诊断码也原样保留，不能掩盖输入错误。
                output.normalRoughnessHeight = float4(DDRvtEncodeNormal(normal), attributes.g, attributes.b);
                return output;
            }
            ENDCG
        }
    }
    Fallback Off
}
