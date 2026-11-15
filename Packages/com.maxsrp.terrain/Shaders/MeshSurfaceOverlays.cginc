#ifndef DIRECT_DELTA_MESH_SURFACE_OVERLAYS_INCLUDED
#define DIRECT_DELTA_MESH_SURFACE_OVERLAYS_INCLUDED

// 静态道路/贴花的直接表面合成，与页生产完全独立。Direct baseline 在浮点属性上
// 实时预览静态覆盖；Delta 只在直接模式或缺页回退时使用它，缓存命中直接消费成品页。
// 位置来自源高度场，颜色/法线/粗糙度先合成，然后只做一次地形光照。
// 此文件只在 DD_LIVE_OVERLAYS 屏幕变体编译，不能加入 producer。
#if defined(DD_LIVE_OVERLAYS)
struct DDLiveOverlay
{
    // 与 C# GpuEntry 顺序逐项一致：17 个 float4，共 272 字节。
    // Box: worldToBox 的前三行。Road: 两根 barycentric 梯度 / 三角形世界 XZ 原点。
    float4 row0, row1, row2;
    float4 shape;             // Box: 完整盒 XYZ 尺寸、羽化米数；Road: 半宽、0、0、羽化。
    float4 uv;                // 材质每米重复次数 XY、材质 UV 偏移 ZW。
    float4 maskUv;            // 遮罩 UV 缩放 XY、偏移 ZW。
    float4 tintOpacity;       // 线性底色乘数 RGB、整体透明度 A。
    float4 info;              // 0=Box/1=Road、稳定材质 ID、mask 通道、同一道路的 group。
    float4 flags;             // Full 开关、WrapU、WrapV、预留。
    float4 uv0, uv1, uv2;      // Road 三个顶点的横向米数/累计纵向米数。
    float4 tangent0, tangent1, tangent2; // Road 三个世界 +U 切线，W 为手性。
    float4 bounds;            // 世界 XZ 包围范围 minX/minZ/maxX/maxZ，先做廉价剔除。
    float4 maskInfo;          // 原 mask 宽、高、mip 数、像素 Buffer 的起始元素；宽为 0 表示无 mask。
};
StructuredBuffer<DDLiveOverlay> _DDLiveOverlays;
StructuredBuffer<float4> _DDLiveMaskPixels;
int _DDLiveOverlayCount;

// 定义位于两条路径各自的 MeshGeometry.cginc（其高度/Clipmap 声明晚于表面文件）。
// 重建源高度，而不是用随 CDLOD morph 变化的显示网格 Y 定位静态投影盒。
float3 DDLiveSourceWorld(float3 displayWorld);

float DDLiveFeather(float distanceToEdge, float featherMetres)
{
    float weight = featherMetres > 0 ? saturate(distanceToEdge / featherMetres) : 1;
    return weight * weight * (3 - 2 * weight);
}

int DDLiveWrapIndex(int index, int size, int mode)
{
    // Unity TextureWrapMode: Repeat=0, Clamp=1, Mirror=2, MirrorOnce=3。
    // 在整数 texel 层处理边界，因此 UV=0/1 的双线性邻点也与原纹理一致。
    if (mode == 0) return (index % size + size) % size;
    if (mode == 2)
    {
        int period = size * 2;
        int wrapped = (index % period + period) % period;
        return min(wrapped, period - 1 - wrapped);
    }
    if (mode == 3) return clamp(index < 0 ? -index - 1 : index, 0, size - 1);
    return clamp(index, 0, size - 1);
}

float4 DDLiveMaskPixel(int2 texel, int2 size, uint offset, int2 wrapMode)
{
    int x = DDLiveWrapIndex(texel.x, size.x, wrapMode.x);
    int y = DDLiveWrapIndex(texel.y, size.y, wrapMode.y);
    return _DDLiveMaskPixels[offset + (uint)(y * size.x + x)];
}

float4 DDLiveReadMaskMip(DDLiveOverlay item, float2 uv, int mip)
{
    int2 size = (int2)item.maskInfo.xy;
    uint offset = (uint)item.maskInfo.w;
    [loop] for (int level = 0; level < mip; level++)
    {
        offset += (uint)(size.x * size.y);
        size = max(size / 2, int2(1, 1));
    }
    int2 wrapMode = (int2)item.flags.yz;
    float2 texel = uv * size - .5;
    int2 low = (int2)floor(texel);
    float2 weight = frac(texel);
    float4 lower = lerp(DDLiveMaskPixel(low, size, offset, wrapMode),
        DDLiveMaskPixel(low + int2(1, 0), size, offset, wrapMode), weight.x);
    float4 upper = lerp(DDLiveMaskPixel(low + int2(0, 1), size, offset, wrapMode),
        DDLiveMaskPixel(low + int2(1, 1), size, offset, wrapMode), weight.x);
    return lerp(lower, upper, weight.y);
}

float DDLiveReadMask(DDLiveOverlay item, float2 uv, float2 uvDx, float2 uvDy)
{
    // Buffer 保存原始 mask 各级 mip 的精确 RGBA 像素。不统一尺寸、不重采样。
    // 与静态 writer 一样，使用 Bilinear（最近 mip 内双线性）。
    if (item.maskInfo.x == 0) return 1;
    float2 dx = uvDx * item.maskInfo.xy, dy = uvDy * item.maskInfo.xy;
    float rho = max(dot(dx, dx), dot(dy, dy));
    float lod = clamp(.5 * log2(max(rho, 1e-12)), 0, item.maskInfo.z - 1);
    float4 sample = DDLiveReadMaskMip(item, uv, (int)floor(lod + .5));
    return saturate(sample[clamp((int)item.info.z, 0, 3)]);
}

float3 DDLiveTangent(float3 direction, float3 sourceNormal)
{
    float3 tangent = direction - sourceNormal * dot(direction, sourceNormal);
    if (dot(tangent, tangent) < 1e-10)
    {
        float3 axis = abs(sourceNormal.x) < .8 ? float3(1, 0, 0) : float3(0, 0, 1);
        tangent = axis - sourceNormal * dot(axis, sourceNormal);
    }
    return normalize(tangent);
}

void DDApplyLiveOverlays(float3 world, float3 sourceNormal, float3 positionDx, float3 positionDy,
    inout DDSurface surface)
{
    sourceNormal = normalize(sourceNormal);
    // 梯度在片元入口获得；后面盒内/道路内的分支不调用隐式导数。
    // Y 梯度由源坡度重建，防止显示网格 morph 的高度梯度改变投影纹理精度。
    float2 slope = -sourceNormal.xz / max(sourceNormal.y, 1e-5);
    positionDx.y = dot(slope, positionDx.xz);
    positionDy.y = dot(slope, positionDy.xz);
    int previousRoad = -1;
    bool normalChanged = false;
    [loop] for (int index = 0; index < _DDLiveOverlayCount; index++)
    {
        DDLiveOverlay item = _DDLiveOverlays[index];
        if (item.tintOpacity.a <= 0 || any(world.xz < item.bounds.xy) || any(world.xz > item.bounds.zw)) continue;
        bool road = item.info.x > .5;
        if (road && previousRoad == (int)item.info.w) continue;
        float2 metres, metresDx, metresDy, maskBase, maskBaseDx, maskBaseDy;
        float3 tangent, bitangent;
        float distanceToEdge;
        bool inside;
        if (road)
        {
            // row0/row1 为两个 barycentric 坐标对世界 X/Z 的梯度，row2 是局部原点。
            // 先减原点，避免在大世界直接做巨大坐标和常数项的抵消。
            float2 relative = world.xz - item.row2.xy;
            float b = dot(item.row0.xy, relative), c = dot(item.row1.xy, relative);
            float3 bary = float3(1 - b - c, b, c);
            float2 uvB = item.uv1.xy - item.uv0.xy, uvC = item.uv2.xy - item.uv0.xy;
            metres = item.uv0.xy + uvB * b + uvC * c;
            metresDx = uvB * dot(item.row0.xy, positionDx.xz) + uvC * dot(item.row1.xy, positionDx.xz);
            metresDy = uvB * dot(item.row0.xy, positionDy.xz) + uvC * dot(item.row1.xy, positionDy.xz);
            float inverseWidth = rcp(2 * max(item.shape.x, 1e-6));
            maskBase = float2(metres.x * inverseWidth + .5, metres.y);
            maskBaseDx = float2(metresDx.x * inverseWidth, metresDx.y);
            maskBaseDy = float2(metresDy.x * inverseWidth, metresDy.y);
            float4 roadTangent = item.tangent0 * bary.x + item.tangent1 * bary.y + item.tangent2 * bary.z;
            tangent = DDLiveTangent(roadTangent.xyz, sourceNormal);
            bitangent = cross(tangent, sourceNormal) * (roadTangent.w < 0 ? -1 : 1);
            distanceToEdge = item.shape.x - abs(metres.x);
            // 包含共边，但同一 road group 一旦命中便跳过其余三角片，确保每路每点只混一次。
            inside = min(bary.x, min(bary.y, bary.z)) >= -1e-6 && distanceToEdge >= 0;
        }
        else
        {
            float4 worldPoint = float4(world, 1);
            float3 local = float3(dot(item.row0, worldPoint), dot(item.row1, worldPoint), dot(item.row2, worldPoint));
            float2 localDx = float2(dot(item.row0.xyz, positionDx), dot(item.row2.xyz, positionDx));
            float2 localDy = float2(dot(item.row0.xyz, positionDy), dot(item.row2.xyz, positionDy));
            metres = local.xz * item.shape.xz;
            metresDx = localDx * item.shape.xz;
            metresDy = localDy * item.shape.xz;
            maskBase = local.xz + .5;
            maskBaseDx = localDx; maskBaseDy = localDy;
            float3 edge = .5 - abs(local);
            distanceToEdge = min(edge.x * item.shape.x, edge.z * item.shape.z);
            inside = min(edge.x, min(edge.y, edge.z)) >= 0;
            tangent = DDLiveTangent(item.row0.xyz, sourceNormal);
            bitangent = cross(tangent, sourceNormal);
            bitangent *= dot(bitangent, item.row2.xyz) < 0 ? -1 : 1;
        }
        float2 uv = metres * item.uv.xy + item.uv.zw;
        float2 uvDx = metresDx * item.uv.xy, uvDy = metresDy * item.uv.xy;
        float2 maskUv = maskBase * item.maskUv.xy + item.maskUv.zw;
        float2 maskDx = maskBaseDx * item.maskUv.xy, maskDy = maskBaseDy * item.maskUv.xy;
        if (!inside) continue;
        if (road) previousRoad = (int)item.info.w;
        float alpha = item.tintOpacity.a * DDLiveFeather(distanceToEdge, item.shape.w) * DDLiveReadMask(item, maskUv, maskDx, maskDy);
        if (alpha <= 0) continue;
        int id = clamp((int)item.info.y, 0, 15);
        float4 ah = _DDAlbedoHeight.SampleGrad(sampler_DDAlbedoHeight, float3(uv, id), uvDx, uvDy);
        // 底色先完成Tint乘法，再按0..1范围参与透明混合；Tint参数本身仍可大于1。
        // 与RVT/SVT写页保持同一规则，不能等最终光照或8位RT写入时才截断。
        float3 overlayAlbedo = saturate(ah.rgb * item.tintOpacity.rgb);
        surface.albedo = lerp(surface.albedo, overlayAlbedo, alpha);
        if (item.flags.x > .5)
        {
            float4 nrao = _DDNormalRoughAo.SampleGrad(sampler_DDAlbedoHeight, float3(uv, id), uvDx, uvDy);
            float2 xy = nrao.rg * 2 - 1;
            float3 tangentNormal = normalize(float3(xy * max(_DDMaterialInfo[id].x, 0), sqrt(max(1 - dot(xy, xy), 1e-6))));
            float3 normal = normalize(tangentNormal.x * tangent + tangentNormal.y * bitangent + tangentNormal.z * sourceNormal);
            // 仿照三 MRT 的 XYZ 向量 source-over，不在每次叠加后提前 normalize。
            surface.normal = lerp(surface.normal, normal, alpha);
            normalChanged = true;
            surface.ao = lerp(surface.ao, nrao.a, alpha);
            surface.roughness = lerp(surface.roughness, nrao.b, alpha);
            surface.height = lerp(surface.height, ah.a, alpha);
        }
    }
    // ColorOnly 完全保留原 normal，不做一次多余 normalize 造成基线位差。
    if (normalChanged)
        surface.normal = dot(surface.normal, surface.normal) > 1e-10 ? normalize(surface.normal) : sourceNormal;
}
#endif
#endif
