#ifndef DIRECT_DELTA_PROJECTION_INCLUDED
#define DIRECT_DELTA_PROJECTION_INCLUDED

/* 阅读陡壁改动的第1步：构造投影权重、连续UV梯度和各投影的法线基。

   [公开依据] 三角洲讲稿p76–81：按表面方向选投影、SampleGrad显式梯度，
   随机选面处理接缝，结果进入VT。公开资料没有给出完整投影shader。
   [本工程正式路径] 每个位置按法线权重随机选一个投影，把结果写入VT。
   用户已接受近看可能出现的颗粒，并结束A/B试验；此处不再提供运行时混合切换。
   PPT没有公开概率权重、噪声或完整shader，下面的权重和哈希是工程实现。
   随机数只依赖固定世界XZ格点，不依赖帧、相机、物理槽或写页批次。
   原三平面混合函数完整保留在DeltaMaterial.cginc的历史注释中；
   DirectReference仍使用自己的私有三平面参照副本，不受此处收敛影响。

   Unity为Y-up：X投影用YZ，Y投影用XZ（地面），Z投影用XY。
   每种材质只采一个投影：AH、NRAO各一次。最多三种材质时，普通平铺最多6次。
   Hex开启时每个投影取3个副本，最多18次源纹理读取；见DeltaHexTiling.cginc。
   “一个投影”不是全部材质通道合计只读一张纹理。
   控制图另计，仍只读取三个控制点；源高度场法线的4–8次高度采样也另计。
*/

struct DDProjection
{
    float3 position, dx, dy; // 世界位置及屏幕/写页纹素的连续世界梯度；不是normalmap导数。
    float3 normal, weights, signs;
    int selectedAxis; // 0/1/2：本位置唯一选择的投影，后续所有材质和通道共同使用。
};

float DDProjectionRandom(float2 worldXZ)
{
    // 用正式VT mip0的世界格作为固定随机域：4194304纹素覆盖10000米。
    // 较粗页只是对这个相同世界域取更稀疏的样本；不以每页局部像素作种子，
    // 因而相邻页border的同一个世界位置会选择同一投影，缓存重画也不会闪烁。
    // 缺页直接回退同样能用世界位置重现该随机数，不需要页地址或物理slot。
    int2 cell = (int2)floor(worldXZ * (4194304.0 / 10000.0));
    uint hash = (uint)cell.x * 0x8da6b343u ^ (uint)cell.y * 0xd8163841u ^ 0xa511e9b3u;
    hash ^= hash >> 16;
    hash *= 0x7feb352du;
    hash ^= hash >> 15;
    hash *= 0x846ca68bu;
    hash ^= hash >> 16;
    // 恰为[0,1)，避免把1落到本来权重为0的最后一个投影。
    return (float)(hash & 0x00ffffffu) * (1.0 / 16777216.0);
}

DDProjection DDBuildProjection(float3 position, float3 geometryNormal)
{
    DDProjection projection;
    projection.position = position;
    // 必须在材质ID/投影面分支之前求导；后面仅swizzle并缩放这些连续梯度。
    // 不对切换后的UV做隐式ddx/ddy，避免跨面跳变让GPU误选很粗的mip。
    projection.dx = ddx(position);
    projection.dy = ddy(position);
    projection.normal = normalize(geometryNormal);
    projection.signs = float3(geometryNormal.x < 0 ? -1 : 1,
        geometryNormal.y < 0 ? -1 : 1, geometryNormal.z < 0 ? -1 : 1);

    // [工程参数] 法线分量的四次方让最正对表面的投影占主导。
    // 去掉小于0.01的尾部后归一化，让缓坡保持纯XZ、无贡献的投影不采样。
    // 这两个常数不是PPT公开参数。权重只由几何法线决定，避免normalmap反馈选面。
    float3 weights = abs(projection.normal);
    weights *= weights;
    weights *= weights;
    weights = max(weights - .01, 0);
    projection.weights = weights / dot(weights, float3(1,1,1));
    // 仅用于旧XZ画面的同机位验证；正式运行按法线权重随机选择投影。
    if (_DDCliffProjection < .5)
    {
        projection.weights = float3(0,1,0);
        projection.signs.y = 1;
    }
    // 只在构造上下文时选一次。随后最多三种材质和它们的全部通道共享此面，
    // 防止同一位置的颜色/法线/Height分别随机，破坏原有有序高度混合。
    // 与已验收的随机投影版本保持完全相同的哈希、权重和累计选择公式。
    float choice = DDProjectionRandom(position.xz);
    // 浮点归一化后权重和可能略小于1，剩余尾部归给最后一个有效面，
    // 不让零权重的方向偶然被选中（例如纯XY混合时Z权重恰为0）。
    int lastAxis = projection.weights.z > 0 ? 2 : (projection.weights.y > 0 ? 1 : 0);
    projection.selectedAxis = choice < projection.weights.x ? 0 :
        (choice < projection.weights.x + projection.weights.y && projection.weights.y > 0 ? 1 : lastAxis);
    return projection;
}

void DDProjectionFrame(int axis, DDProjection projection,
    out float2 uv, out float2 dx, out float2 dy,
    out float3 tangent, out float3 bitangent, out float3 planeNormal)
{
    // 同时翻转UV的U和对应切线，保证正/负方向崖壁的纹理法线方向一致。
    // 三个基都沿用旧地面投影的T/B手性，不能只换UV却继续使用Y-up法线基。
    if (axis == 0)
    {
        float signX = projection.signs.x;
        uv = projection.position.zy * float2(signX,1);
        dx = projection.dx.zy * float2(signX,1);
        dy = projection.dy.zy * float2(signX,1);
        tangent = float3(0,0,signX); bitangent = float3(0,1,0); planeNormal = float3(signX,0,0);
    }
    else if (axis == 1)
    {
        float signY = projection.signs.y;
        uv = projection.position.xz * float2(signY,1);
        dx = projection.dx.xz * float2(signY,1);
        dy = projection.dy.xz * float2(signY,1);
        tangent = float3(signY,0,0); bitangent = float3(0,0,1); planeNormal = float3(0,signY,0);
    }
    else
    {
        float signZ = projection.signs.z;
        uv = projection.position.xy * float2(-signZ,1);
        dx = projection.dx.xy * float2(-signZ,1);
        dy = projection.dy.xy * float2(-signZ,1);
        tangent = float3(-signZ,0,0); bitangent = float3(0,1,0); planeNormal = float3(0,0,signZ);
    }
}

float3 DDReorientProjectionNormal(float3 tangentNormal, float3 tangent, float3 bitangent,
    float3 planeNormal, float3 geometryNormal)
{
    // 第一步：将normalmap切线空间法线转到这个投影面的世界基。
    float3 value = tangentNormal.x * tangent + tangentNormal.y * bitangent + tangentNormal.z * planeNormal;
    // 第二步：最短弧旋转，将投影面基法线对齐实际几何法线。
    // 因选面符号与几何法线一致，有效面的dot>=0，没有180度反向奇点。
    // 所以平坦normalmap(0,0,1)在所有面上都返回geometryNormal；
    // 各投影都会返回同一geometryNormal，不会凭空把坡面/崖壁拉回世界轴。
    float3 rotationAxis = cross(planeNormal, geometryNormal);
    return normalize(value + cross(rotationAxis, value) +
        cross(rotationAxis, cross(rotationAxis, value)) / max(1 + dot(planeNormal, geometryNormal), 1e-5));
}

#endif
