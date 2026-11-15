#ifndef DIRECT_DELTA_PROJECTION_INCLUDED
#define DIRECT_DELTA_PROJECTION_INCLUDED

/* 阅读陡壁改动的第1步：构造投影权重、连续UV梯度和各投影的法线基。

   [公开依据] 三角洲讲稿p76–81：按表面方向选投影、SampleGrad显式梯度，
   随机选面处理接缝，结果进入VT。公开资料没有给出完整投影shader。
   [本工程选择] 用户要求先不做VT，因此这里是每屏幕位置直接计算的、
   确定性三平面混合，不是三角洲的随机单平面算法，也没有屏幕噪声/时间抖动。

   Unity为Y-up：X投影用YZ，Y投影用XZ（地面），Z投影用XY。
   普通平铺：每材质/每有效投影读取AH、NRAO各一次，平地最多6次、三面最多18次。
   Hex开启时每面取3个副本，以上分别变为18/54次；代码见DeltaHexTiling.cginc。
   控制图另计，仍只读取三个控制点；源高度场法线的4–8次高度采样也另计。
*/

struct DDProjection
{
    float3 position, dx, dy; // 连续世界坐标及其屏幕导数；不是normalmap的导数。
    float3 normal, weights, signs;
};

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
    // 仅用于旧XZ画面的同机位验证；正常运行默认开启三平面。
    if (_DDCliffProjection < .5)
    {
        projection.weights = float3(0,1,0);
        projection.signs.y = 1;
    }
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
    // 混合投影不会凭空把斜坡/崖壁的基础法线拉回世界轴。
    float3 rotationAxis = cross(planeNormal, geometryNormal);
    return normalize(value + cross(rotationAxis, value) +
        cross(rotationAxis, cross(rotationAxis, value)) / max(1 + dot(planeNormal, geometryNormal), 1e-5));
}

#endif
