// 正式世界页已在 CPU 完成整页输入校验，允许发布的页中 B 恒为 1；consumer 不读取 B。
// 旧 invalid/mixed 编码逻辑暂保留，以维持既有 GPU 压缩结果的逐字节兼容性。
// B 当前仅预留；今后真正复用该通道时，再统一修改打包和对应归档。
Shader "Hidden/MaxTerrain/WorldVtCompressBc3"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        Pass
        {
            Name "CompressBc3"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11
            #pragma vertex BlockVert
            #pragma fragment BlockFrag

            Texture2D<float4> _DDVtBc3AlbedoAo;
            Texture2D<float4> _DDVtBc3NormalData;

            float4 BlockVert(uint vertexId : SV_VertexID) : SV_POSITION
            {
                float2 corner = float2((vertexId << 1) & 2u, vertexId & 2u);
                return float4(corner * 2.0 - 1.0, 0.0, 1.0);
            }

            #include "../VirtualTexturing/World/WorldVtBc3Common.cginc"

            struct BlockOutputs
            {
                uint4 albedoNormal : SV_Target0;
                uint4 dataNormal : SV_Target1;
            };

            BlockOutputs BlockFrag(float4 position : SV_POSITION)
            {
                // D3D11 的整数 Load 和 SV_POSITION 都从目标的首行起算。
                // 一个输出纹素编码输入的 4×4 块；144×144 页因此写成 36×36 个块。
                int2 firstTexel = (int2)position.xy * 4;
                float4 colorBlock[16];
                float4 dataBlock[16];
                uint validMask = 0u;
                [unroll] for (uint i = 0u; i < 16u; ++i)
                {
                    int3 texel = int3(firstTexel + int2(i & 3u, i >> 2), 0);
                    float4 albedoAo = _DDVtBc3AlbedoAo.Load(texel);
                    float4 normalData = _DDVtBc3NormalData.Load(texel);
                    bool valid = normalData.a > 0.0;
                    if (valid) validMask |= 1u << i;
                    // stage 的 sRGB Load 返回线性 RGB，BC3 sRGB 的端点需要再编码。
                    // 两个 A 各存一个八面体分量，避免与 RGB 共用颜色索引。
                    colorBlock[i] = float4(EncodeSrgb(albedoAo.rgb), normalData.r);
                    dataBlock[i] = float4(albedoAo.a, normalData.b, valid ? 1.0 : 0.0, normalData.g);
                }

                BlockOutputs output;
                if (validMask == 0u)
                {
                    output.albedoNormal = 0u;
                    output.dataNormal = 0u;
                    return output;
                }
                output.albedoNormal = EncodeBc3(colorBlock, validMask, false);
                output.dataNormal = EncodeBc3(dataBlock, validMask, true);
                return output;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
