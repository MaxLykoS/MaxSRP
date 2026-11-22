#ifndef DIRECT_DELTA_CONTROLS_INCLUDED
#define DIRECT_DELTA_CONTROLS_INCLUDED

// 阅读第2步：控制图。包含PPT p73/p74原文与本工程等价实现。
// 引用源：用户提供的GDC2025 Delta Force截图。
// https://media.gdcvault.com/gdc2025/Slides/Jiao_Hang_Delta%2BForce%2BPerformant.pdf
// 本文件由DeltaSurface.cginc包含，所需结构/纹理由其声明。

void DDDecodeControl(uint packed, out int bottom, out int top, out float opacity)
{
    // [工程约定] RGBA8 = Bottom8 + Top8 + TopOpacity8 + Reserved8；PPT p67为5/5/3。
    // Bottom<=Top；ID已在制作阶段按覆层顺序排列（岩/砂/泥土在前，草在后）。
    // 例如泥9在Bottom、草14在Top，opacity表示草的覆盖率，不是泥的覆盖率。
    // 单材质Bottom=Top，覆盖率规范为0。不能按Height灰度重排，也不把ID当颜色过滤。
    bottom = (int)(packed & 255u);
    top = (int)((packed >> 8) & 255u);
    opacity = bottom == top ? 0 : ((packed >> 16) & 255u) / 255.0;
}

// [工程实现] 只做一次整数Load。RGBA8_UNorm逐通道恢复字节，保持8bit覆盖率。
uint DDLoadControl(int2 globalGrid, int level, int side)
{
    #if defined(DD_CONTROL_RESIDENT)
    uint4 bytes = (uint4)round(saturate(_DDControlResident.Load(int3(globalGrid, 0))) * 255.0);
    return bytes.x | (bytes.y << 8) | (bytes.z << 16);
    #else
    // World X/Z is clamped before reconstruction, so these grid coordinates
    // are non-negative even when the window's world origin is negative.
    int2 texel = globalGrid % side;
    uint4 bytes = (uint4)round(saturate(_DDControlClip.Load(int4(texel, level, 0))) * 255.0);
    return bytes.x | (bytes.y << 8) | (bytes.z << 16);
    #endif
}

// [工程实现] 根据控制纹理的有效窗口选择密度。不是材质贴图mip。
// DirectDelta固定1m；Delta按XZ窗口选一级，不进行跨级混合。只检查描述符，不采纹理。
int DDSelectControlCell(float2 worldXZ, int side, out int2 cell, out float2 fraction)
{
    int selectedLevel = -1;
    float worldSize = max(_DDControlInfo.x, 1);
    worldXZ = clamp(worldXZ, 0, worldSize);
    cell = 0;
    fraction = 0;
    #if defined(DD_CONTROL_RESIDENT)
    // DirectDelta always reads the complete repaired 1m lattice. No window,
    // ring addressing, control-source LOD selection or averaged ID mips.
    float2 grid = worldXZ / max(_DDControlInfo.w, .001);
    cell = (int2)min(floor(grid), (float)(side - 2));
    fraction = grid - cell;
    selectedLevel = 0;
    #else
    // Select ONE repaired control source. Coverage decisions use descriptors
    // only, not extra texture reads. Keep a 32-sample interior on fine levels;
    // the pinned coarsest level needs no margin and covers the whole world.
    [unroll] for (int level = 0; level < 6; ++level)
    {
        float4 descriptor = _DDControlOrigins[level];
        float stride = max(descriptor.z, .001);
        int sourceLast = max((int)ceil(worldSize / stride), 1);
        float2 candidateGrid = worldXZ / stride;
        int2 candidateCell = (int2)min(floor(candidateGrid), (float)(sourceLast - 1));
        float2 lowWorld = candidateCell * stride;
        float2 highWorld = (candidateCell + 1) * stride;
        float margin = level == 5 ? 0 : 32;
        float2 minimum = descriptor.xy + margin * stride;
        float2 maximum = descriptor.xy + (side - 1 - margin) * stride;
        bool covered = descriptor.w > .5 && descriptor.z >= _DDControlInfo.w &&
            all(lowWorld >= minimum) && all(highWorld <= maximum);
        if (selectedLevel < 0 && covered)
        {
            selectedLevel = level;
            cell = candidateCell;
            fraction = candidateGrid - candidateCell;
        }
    }
    // Each level is repaired independently. A hard control-source LOD change
    // is an approximation in this first version, not the unpublished original
    // game's exact filtering/transition. No cross-level blend: only 3 reads.
    #endif
    return selectedLevel;
}

// 从三个控制点提取升序材质ID、有效数量及非法邻域标记。
// 依赖Bottom<=Top且三角邻域最多三种ID，不是任意六个ID的通用排序。
int3 DDExtractMaterialIds(int3 bottom, int3 top, out int count, out float invalid)
{
    /* [PPT原文 p73，按用户截图转录；仅注释]
       uint minIdx = min(min(MatID00,MatID10),MatID20);
       uint maxIdx = max(max(MatID01,MatID11),MatID21);
       uint midIdx = minIdx;
       midIdx = MatID00 > minIdx && MatID00 < maxIdx ? MatID00 : midIdx;
       midIdx = MatID01 > minIdx && MatID01 < maxIdx ? MatID01 : midIdx;
       midIdx = MatID10 > minIdx && MatID10 < maxIdx ? MatID10 : midIdx;
       midIdx = MatID11 > minIdx && MatID11 < maxIdx ? MatID11 : midIdx;
       midIdx = MatID20 > minIdx && MatID20 < maxIdx ? MatID20 : midIdx;
       midIdx = MatID21 > minIdx && MatID21 < maxIdx ? MatID21 : midIdx;
       [等价改写] MatID00/10/20 = bottom.xyz；MatID01/11/21 = top.xyz。
       离线修复保证并集<=3，所以min/max之间至多一种ID；下方用向量比较提取它。
       校验超三材质、逆序以及合并单/双材质分支属于工程补充。
    */
    int minId = min(bottom.x, min(bottom.y, bottom.z));
    int maxId = max(top.x, max(top.y, top.z));
    int3 middleBottom = ((bottom > minId) && (bottom < maxId)) ? bottom : minId;
    int3 middleTop = ((top > minId) && (top < maxId)) ? top : minId;
    int3 candidates = max(middleBottom, middleTop);
    int midId = max(candidates.x, max(candidates.y, candidates.z));

    // Preserve the invalid-neighborhood diagnostic if the source violates
    // either contract. An interior ID different from midId is a fourth ID.
    invalid = 0;
    if (any(top < bottom) ||
        any((middleBottom != minId) && (middleBottom != midId)) ||
        any((middleTop != minId) && (middleTop != midId)))
        invalid = 1;

    count = minId == maxId ? 1 : (midId == minId ? 2 : 3);
    // The compositor reads only the first count slots. Duplicate the final
    // ID for the unused slots, including (minId, maxId, maxId) for two layers.
    return int3(minId, midId == minId ? maxId : midId, maxId);
}

// 用重心权重插值有序覆盖率，仅供F6/F7诊断；不是最终Height blend权重。
float3 DDInterpolateOrderedOpacity(int3 bottom, int3 top, float3 coverage, float3 bary,
    int3 ids, int count)
{
    float3 opacityByLayer = float3(1,0,0);
    /* [PPT原文 p74 / Sampling，按用户截图转录；仅注释]
       float midW = 0;
       float maxW = 0;
       midW += (MatID00 == midIdx) ? Ws.x : 0.0;
       maxW += (MatID00 == maxIdx) ? Ws.x : 0.0;
       midW += (MatID01 == midIdx) ? w0 * Ws.x : 0.0;
       maxW += (MatID01 == maxIdx) ? w0 * Ws.x : 0.0;
       midW += (MatID10 == midIdx) ? Ws.y : 0.0;
       maxW += (MatID10 == maxIdx) ? Ws.y : 0.0;
       midW += (MatID11 == midIdx) ? w1 * Ws.y : 0.0;
       maxW += (MatID11 == maxIdx) ? w1 * Ws.y : 0.0;
       midW += (MatID20 == midIdx) ? Ws.z : 0.0;
       maxW += (MatID20 == maxIdx) ? Ws.z : 0.0;
       midW += (MatID21 == midIdx) ? w2 * Ws.z : 0.0;
       maxW += (MatID21 == maxIdx) ? w2 * Ws.z : 0.0;
       float3 SortedMatIDWeight = float3(1.0f, midW, maxW);
       [等价改写] Ws=bary，w0/w1/w2=coverage.xyz；下方用layer/corner循环展开。
       Bottom命中加完整bary，Top命中加coverage*bary。opacity不是归一化混合权重。
       [当前用途] 保留为F6/F7诊断；亮带修复后，最终shading改用原始三点coverage，
       不再直接将这组三材质opacity分别送入非线性Height blend。
    */
    [unroll] for (int layer = 1; layer < 3; ++layer)
    {
        if (layer < count)
        {
            int id = ids[layer]; float opacity = 0;
            [unroll] for (int corner = 0; corner < 3; ++corner)
            {
                if (bottom[corner] == id) opacity += bary[corner];
                if (top[corner] == id) opacity += bary[corner] * coverage[corner];
            }
            opacityByLayer[layer] = opacity;
        }
    }
    return opacityByLayer;
}

// 入口：选控制源 → 取三角三个点 → 提取最多三种ID → 保留原始点和诊断opacity。
DDControls DDReadControls(float2 worldXZ)
{
    DDControls controls = (DDControls)0;
    controls.level = -1;
    controls.count = 1;
    controls.opacity = float3(1,0,0);
    int side = max((int)round(_DDControlInfo.y), 2);
    int2 cell;
    float2 fraction;
    controls.level = DDSelectControlCell(worldXZ, side, cell, fraction);
    if (controls.level < 0)
    {
        controls.invalid = 1;
        return controls;
    }
    int2 texel0, texel1, texel2;
    float3 bary;
    /* [PPT原文 p74 / Sampling，按用户截图转录；仅注释，不参与编译]
       // triangle weight
       float w10 = RightBottomFlag * (1.0f - FractionX) + (1.0f - RightBottomFlag) * FractionY;
       float w01 = RightBottomFlag * (1.0f - FractionY) + (1.0f - RightBottomFlag) * FractionX;
       float w00 = 1.0f - w10 - w01;
       float3 TriangleWeights = float3(w00, w10, w01);
       // matid weight cal
       float3 Ws = (float3)TriangleWeights;
       [等价思想 / 本工程角点编号]
       原稿w10/w01必须和原稿采样点顺序一起理解，不直接套到本工程坐标命名。
       下方显式列出texel0/1/2及对应bary，和离线控制图使用同一条反对角线。
       bary是空间插值系数，总和为1；不是材质的Top覆盖率。
    */
    if (fraction.x + fraction.y <= 1)
    {
        texel0 = cell; texel1 = cell + int2(1,0); texel2 = cell + int2(0,1);
        bary = float3(1 - fraction.x - fraction.y, fraction.x, fraction.y);
    }
    else
    {
        texel0 = cell + 1; texel1 = cell + int2(0,1); texel2 = cell + int2(1,0);
        bary = float3(fraction.x + fraction.y - 1, 1 - fraction.x, 1 - fraction.y);
    }
    int3 bottom, top; float3 coverage;
    DDDecodeControl(DDLoadControl(texel0, controls.level, side), bottom.x, top.x, coverage.x);
    DDDecodeControl(DDLoadControl(texel1, controls.level, side), bottom.y, top.y, coverage.y);
    DDDecodeControl(DDLoadControl(texel2, controls.level, side), bottom.z, top.z, coverage.z);
    controls.bottom = bottom; controls.top = top;
    controls.coverage = coverage; controls.bary = bary;
    controls.ids = DDExtractMaterialIds(bottom, top, controls.count, controls.invalid);
    controls.opacity = float3(1,0,0);
    controls.opacity = DDInterpolateOrderedOpacity(bottom, top, coverage, bary, controls.ids, controls.count);
    return controls;
}


#endif
