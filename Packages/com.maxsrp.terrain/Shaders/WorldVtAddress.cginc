#ifndef DIRECT_DELTA_WORLD_VT_ADDRESS_INCLUDED
#define DIRECT_DELTA_WORLD_VT_ADDRESS_INCLUDED

// 世界页面的身份不随 AVT 分配改变：10km 世界每轴有 32768 张 mip0 页，
// 每页 128 个有效 texel，即全图 4194304 texel、约 419.43 texel/m。
// global mip 只表示材质的世界精度：0..7 为 RVT，8..15 为 SVT。
#define DD_VT_WORLD_SIZE 10000.0
#define DD_VT_INTERIOR 128
#define DD_VT_BORDER 8
#define DD_VT_PHYSICAL 144
#define DD_VT_TILES 32768u
#define DD_VT_MAX_MIP 15u
#define DD_VT_FIRST_SVT_MIP 8u
#define DD_VT_SLOT_COUNT 1543u

// UE 5.8 的 AVT 采用“全域粗表 + 目录指向局部精细表”。本工程将两种表
// 合并到一个 StructuredBuffer，减少 SRV；查表职责与两次依赖读取仍然保留。
// [0,16384) 是 128² 目录，之后依次是 1024²、512²、...、1² 页表 mip。
// 所有坐标均指向逻辑页表空间，和物理纹理数组的 slice 无关。
#define DD_VT_GRID_SIDE 128u
#define DD_VT_DIRECTORY_WORDS 16384u
#define DD_VT_TABLE_SIDE 1024u
#define DD_VT_LOW_GLOBAL_MIP 8
#define DD_VT_LOW_MAX_LOCAL_MIP 7u
#define DD_VT_MAX_ADAPTIVE_LEVEL 8u
#define DD_VT_FEEDBACK_BIAS 3
StructuredBuffer<uint> _DDVtTable;

float _DDVtMipBias;

#if defined(DD_WORLD_VT_ANISOTROPIC)
float _DDVtMaxAnisotropy; // 本轮为 2 或 4；AF1 不编译下面的足迹路径。

struct DDVtScreenFootprint
{
    float2 majorVector; // 世界 XZ 中完整长轴向量，单位为米/屏幕像素。
    float majorMetres;
    float minorMetres;
};

float DDVtAnisotropyLimit()
{
    return clamp(_DDVtMaxAnisotropy, 1.0, 4.0);
}

DDVtScreenFootprint DDVtComputeScreenFootprint(float2 screenDxXZ, float2 screenDyXZ)
{
    // J 的两列是 ddx(XZ)、ddy(XZ)。J*transpose(J) 的特征向量直接位于
    // 世界 XZ 中，不能把屏幕方向的特征向量直接当成纹理采样方向。
    float a = screenDxXZ.x * screenDxXZ.x + screenDyXZ.x * screenDyXZ.x;
    float b = screenDxXZ.x * screenDxXZ.y + screenDyXZ.x * screenDyXZ.y;
    float c = screenDxXZ.y * screenDxXZ.y + screenDyXZ.y * screenDyXZ.y;
    float difference = a - c;
    float discriminant = sqrt(max(difference * difference + 4.0 * b * b, 0.0));
    float majorSquared = max(0.5 * (a + c + discriminant), 0.0);

    // det(J)^2 = 两个特征值的乘积。用它求短轴，避免细长足迹中
    // trace-discriminant 的相减把小特征值直接抹成零。
    float determinant = screenDxXZ.x * screenDyXZ.y - screenDxXZ.y * screenDyXZ.x;
    float minorSquared = min(majorSquared,
        max(determinant * determinant / max(majorSquared, 1e-30), 0.0));

    // 两种等价的长轴特征向量中选长度较大者，兼容水平/竖直长轴。
    // 圆形或零足迹没有唯一方向，此时固定 X 轴，避免 normalize(0)。
    float2 candidate0 = float2(b, majorSquared - a);
    float2 candidate1 = float2(majorSquared - c, b);
    float2 axis = dot(candidate0, candidate0) > dot(candidate1, candidate1) ? candidate0 : candidate1;
    float axisSquared = dot(axis, axis);
    axis = axisSquared > 1e-20 ? axis * rsqrt(max(axisSquared, 1e-30)) : float2(1, 0);

    DDVtScreenFootprint result;
    result.majorMetres = sqrt(majorSquared);
    result.minorMetres = sqrt(minorSquared);
    result.majorVector = axis * result.majorMetres;
    return result;
}
#endif

bool DDVtInsideWorld(float2 worldXZ)
{
    return all(worldXZ >= 0.0) && all(worldXZ <= DD_VT_WORLD_SIZE);
}

uint DDVtDesiredMip(float2 screenDxXZ, float2 screenDyXZ)
{
    #if defined(DD_WORLD_VT_ANISOTROPIC)
    DDVtScreenFootprint footprint = DDVtComputeScreenFootprint(screenDxXZ, screenDyXZ);
    // 先选一个虚拟 mip，再在这一层沿长轴过滤；不采相邻虚拟 mip。
    // 最多 AF 个点时，短轴之外还要满足长轴/AF 的精度下限。
    // MainPass 和独立反馈共用本函数，不能只让消费端请求更细的页。
    float footprintMetres = max(footprint.minorMetres, footprint.majorMetres / DDVtAnisotropyLimit());
    float texelsPerMetre = (float)(DD_VT_TILES * DD_VT_INTERIOR) / DD_VT_WORLD_SIZE;
    float footprintTexels = max(footprintMetres * texelsPerMetre, 1.0);
    return (uint)clamp(floor(log2(footprintTexels) + _DDVtMipBias + 0.5), 0.0, (float)DD_VT_MAX_MIP);
    #else
    // 梯度单位是米/屏幕像素。用较长的轴，避免以短轴估计而放大掠视混叠。
    // 这里选最近整数 global mip。物理页数组只有 mip0，不能把它与虚拟 mip 混淆。
    float footprintMetres = max(length(screenDxXZ), length(screenDyXZ));
    float texelsPerMetre = (float)(DD_VT_TILES * DD_VT_INTERIOR) / DD_VT_WORLD_SIZE;
    float footprintTexels = max(footprintMetres * texelsPerMetre, 1.0);
    return (uint)clamp(floor(log2(footprintTexels) + _DDVtMipBias + 0.5), 0.0, (float)DD_VT_MAX_MIP);
    #endif
}

float DDVtPageWidthMetres(uint mip)
{
    return DD_VT_WORLD_SIZE / (float)(DD_VT_TILES >> mip);
}

float2 DDVtAddressUV(float2 worldXZ)
{
    // 最大值是小于 1 的相邻 float，确保正世界边界落在最后一个地块/页内。
    // 仅查地址时向内钳制，材质页内采样仍使用原始世界位置。
    return clamp(worldXZ / DD_VT_WORLD_SIZE, 0.0, 0.9999999403953552);
}

uint2 DDVtPageCoordinate(float2 worldXZ, uint mip)
{
    return (uint2)floor(DDVtAddressUV(worldXZ) * (float)(DD_VT_TILES >> mip));
}

struct DDVtTableAddress
{
    uint word;          // 在合并 Buffer 中的实际下标。
    uint2 page;         // 当前 local mip 上的页表 XY，用于反馈。
    int localMip;       // 允许为负：当前 allocation 不够精细，需要 CPU 扩张。
    uint maxLocalMip;
};

uint DDVtTableMipOffset(uint mip)
{
    uint side = DD_VT_TABLE_SIDE >> mip;
    // 等比数列：跳过前面所有页表 mip，目录占用的前缀也计入。
    return DD_VT_DIRECTORY_WORDS + ((DD_VT_TABLE_SIDE * DD_VT_TABLE_SIDE - side * side) * 4u) / 3u;
}

DDVtTableAddress DDVtResolveAddress(float2 worldXZ, uint globalMip)
{
    DDVtTableAddress result;
    float2 uv = DDVtAddressUV(worldXZ);
    uint2 offset = 0u;
    uint regionSide = DD_VT_GRID_SIDE;
    result.maxLocalMip = DD_VT_LOW_MAX_LOCAL_MIP;
    result.localMip = (int)globalMip - DD_VT_LOW_GLOBAL_MIP;

    // 对应 UE ApplyAdaptivePageTableUniform：粗需求直接使用全域粗表，
    // 更细的需求才读取目录。目录尚未发布时继续采粗表，同时反馈负 local mip。
    [branch] if (result.localMip < 0)
    {
        float2 gridPosition = uv * (float)DD_VT_GRID_SIDE;
        uint2 grid = (uint2)floor(gridPosition);
        uint directory = _DDVtTable[grid.y * DD_VT_GRID_SIDE + grid.x];
        uint2 localOffset = uint2(directory & 4095u, (directory >> 12u) & 4095u);
        uint localLevel = (directory >> 24u) & 15u;
        uint localSide = 1u << localLevel;
        bool valid = (directory & (1u << 28u)) != 0u && localLevel <= DD_VT_MAX_ADAPTIVE_LEVEL &&
            all(localOffset + localSide <= DD_VT_TABLE_SIDE);
        [branch] if (valid)
        {
            offset = localOffset;
            regionSide = localSide;
            result.maxLocalMip = localLevel;
            result.localMip += (int)localLevel;
            uv = frac(gridPosition);
        }
    }

    // 查表只能使用现有的 mip；负数只保留给反馈，以请求更细的逻辑分配。
    uint sampledMip = (uint)clamp(result.localMip, 0, (int)result.maxLocalMip);
    uint2 cell = min((uint2)floor(uv * (float)regionSide), regionSide - 1u);
    result.page = (offset + cell) >> sampledMip;
    uint tableSide = DD_VT_TABLE_SIDE >> sampledMip;
    result.word = DDVtTableMipOffset(sampledMip) + result.page.y * tableSide + result.page.x;
    return result;
}

uint DDVtFeedbackKey(DDVtTableAddress address)
{
    // UE 5.8 协议的 X12/Y12/偏置 mip4；本工程只有一个 space（高4位为0）。
    // +3 可表达 -3/-2/-1，CPU 据此知道“当前局部表不够细”。这不是全局材质页键。
    // 最后 +1 是本工程 R32_UINT 反馈图的运输约定，给清屏/天空保留 0。
    uint level = (uint)clamp(address.localMip + DD_VT_FEEDBACK_BIAS, 0, 15);
    return (address.page.x | (address.page.y << 12u) | (level << 24u)) + 1u;
}

#endif
