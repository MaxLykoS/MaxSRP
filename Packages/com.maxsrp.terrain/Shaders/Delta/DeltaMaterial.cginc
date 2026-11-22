#ifndef DIRECT_DELTA_MATERIAL_INCLUDED
#define DIRECT_DELTA_MATERIAL_INCLUDED

// 阅读第3步：采样最多三种材质 → 逐控制点Height blend → 重心插值最终表面。
// 本文件由DeltaSurface.cginc包含。

// [PPT p65的数据布局 + 工程采样实现]
// 每个稳定ID直接对应一个数组slice；没有DTA槽映射。
// AH.rgb为sRGB（GPU解码为线性光），AH.a为线性Height；NRAO全部线性。
// Height只参与材质融合，不改变地形几何。
// 阅读陡壁改动的第2步：一次投影的两源采样及normalmap空间转换。
DDMaterial DDSampleMaterialPlane(int id, DDProjection projection, int axis)
{
    DDMaterial material;
    float2 worldUv, worldDx, worldDy;
    float3 tangent, bitangent, planeNormal;
    DDProjectionFrame(axis, projection, worldUv, worldDx, worldDy, tangent, bitangent, planeNormal);
    if (_DDHexTiling > .5)
        return DDSampleHexMaterialPlane(id,worldUv,worldDx,worldDy,tangent,bitangent,planeNormal,projection.normal);
    float4 transform = _DDMaterialUv[id], info = _DDMaterialInfo[id];
    // U/V继续使用原材质的每米重复率；陡壁的V改用世界高度Y，避免压成竖向条纹。
    float2 uv = worldUv * transform.xy + transform.zw;
    float3 location = float3(uv, id);
    // worldDx/worldDy在材质ID分支前由连续世界坐标求导。
    // SampleGrad让GPU正常选mip及三线性过滤；没有手动LOD、mip0锁定或偏置。
    // 两源共享Repeat/Trilinear/aniso1采样器；同一材质用相同UV/梯度。
    float2 dx = worldDx * transform.xy, dy = worldDy * transform.xy;
    float4 ah = _DDAlbedoHeight.SampleGrad(sampler_DDAlbedoHeight, location, dx, dy);
    float4 nrao = _DDNormalRoughAo.SampleGrad(sampler_DDAlbedoHeight, location, dx, dy);
    material.albedo = ah.rgb;
    material.height = ah.a;
    // RG解码法线XY，重建正Z；先由原XY算Z，再施加法线强度并归一化。
    float2 xy = nrao.rg * 2 - 1;
    float3 tangentNormal = normalize(float3(xy * max(info.x, 0), sqrt(max(1 - dot(xy,xy), 1e-6))));
    material.normal = DDReorientProjectionNormal(tangentNormal, tangent, bitangent, planeNormal, projection.normal);
    material.roughness = saturate(nrao.b); material.ao = saturate(nrao.a);
    return material;
}

DDMaterial DDSampleMaterial(int id, DDProjection projection)
{
    // 正式路径：每个世界位置只采一个投影。构造上下文时已经选好方向，
    // 三种材质/全部通道共用该面；AH/NRAO及Hex的三个副本也不会分别随机。
    // 连续世界梯度在选面前生成，SampleGrad不依赖随机切面后的UV差值。
    return DDSampleMaterialPlane(id, projection, projection.selectedAxis);
}

/* 历史三平面混合参考（不编译，不是运行时选项）。
   2026-11-14用户接受随机单投影后，正式Delta已改为上方单次投影采样。
   这里按要求保留原混合函数完整代码，方便研究加权过渡的成本和画质取舍。
   如研究时重新启用，必须同步恢复投影契约并重新确定归档配方，不能把三面
   混合结果与现有随机单投影SVT当作同一内容。DirectReference私有副本仍保留
   并实际执行自己的三平面混合，作为高保真参照；不要通过这里改变Direct。

DDMaterial DDSampleMaterial(int id, DDProjection projection)
{
    // 阅读陡壁改动的第3步：同一材质按三个投影权重混合所有通道。
    // 先得到该材质的统一Height，再进入下面原有的有序高度混合，层序/权重语义不变。
    DDMaterial material;
    material.albedo = 0; material.normal = 0;
    material.height = 0; material.roughness = 0; material.ao = 0;
    [unroll] for (int axis = 0; axis < 3; ++axis)
    {
        float weight = projection.weights[axis];
        if (weight > 0)
        {
            DDMaterial plane = DDSampleMaterialPlane(id, projection, axis);
            material.albedo += plane.albedo * weight;
            material.normal += plane.normal * weight;
            material.height += plane.height * weight;
            material.roughness += plane.roughness * weight;
            material.ao += plane.ao * weight;
        }
    }
    // 法线先在世界空间合成，不能把三张不同切线空间的RG直接当颜色混合。
    material.normal = normalize(material.normal);
    return material;
}

*/

/* [PPT公式 p82，用户提供的原文，不是完整shader源码]
   Blend Factor = saturate(( A * (Height1 - Height0) + (Weight - 0.5) * Scale) * Sharpness + 0.5)
   When A = 0, Scale = Sharpness = 1: Degenerates to Linear Blending
   [工程当前选择] A固定为1，Scale/Sharpness由_DDHeightBlend.xy绑定（4/0.5）。
   用户已取消相机/屏幕LOD渐退；这两个数值是工程参数，不是PPT公开数值。
*/
float DDHeightFactor(float bottomHeight, float topHeight, float opacity)
{
    // Bottom/Top身份由制作层序先确定：草土组合必须是土在下、草在上。
    // 这两张Height各自是材质内部相对灰度，不是同一物理高度基准；不负责排序ID。
    // opacity是Top覆盖率；topHeight-bottomHeight只调整这个既定覆盖关系的局部alpha。
    // Material maps use ordinary mip filtering. The blend parameters do not
    // depend on camera distance, screen derivatives or control-source LOD.
    return saturate(((topHeight - bottomHeight) + (opacity - .5) * _DDHeightBlend.x) * _DDHeightBlend.y + .5);
}

// [工程实现] 将全局ID映射到这次评估的min/mid/max槽，返回one-hot向量。
// 单/双材质可能有重复ids，按x→y→z只命中第一个；不能直接比较出多个1。
// 用向量加权也避免D3D11对动态float3分量写入的限制。
float3 DDMaterialSlotMask(int id, int3 ids)
{
    return id == ids.x ? float3(1,0,0) : (id == ids.y ? float3(0,1,0) : float3(0,0,1));
}

// [亮带修复 / 本工程实现，非PPT原文]
// 先在三个原始控制点各自的Bottom/Top上做Height blend，再插值最终贡献。
// 同一个shading point的三种材质Height供三个角点复用，不是额外采三套材质。
// 这样换Top ID时，不会因先拆分opacity而把两层都截为0。
float3 DDHeightBlendWeights(DDControls controls, float3 heights)
{
    float3 weights = 0;
    // A nonlinear blend must act on each control point's original two-layer
    // coverage before spatial interpolation. Applying it to split, aligned
    // mid/max opacities can erase both tops where their IDs change.
    [unroll] for (int corner = 0; corner < 3; ++corner)
    {
        float3 bottom = DDMaterialSlotMask(controls.bottom[corner], controls.ids);
        float3 top = DDMaterialSlotMask(controls.top[corner], controls.ids);
        float alpha = controls.bottom[corner] == controls.top[corner] ? 0 :
            DDHeightFactor(dot(heights,bottom), dot(heights,top), controls.coverage[corner]);
        weights += controls.bary[corner] * (bottom * (1 - alpha) + top * alpha);
    }
    return weights;
}

DDSurface DDEvaluateSurface(float3 worldPosition, float3 geometryNormal, DDControls controls)
{
    // 每个求值位置只评估一次（屏幕像素或写页纹素）。三个控制点已读取，
    // 随机投影上下文只构造一次，最多三种材质共享同一方向。
    DDProjection projection = DDBuildProjection(worldPosition, geometryNormal);
    DDMaterial first = DDSampleMaterial(controls.ids.x, projection);
    DDMaterial second = first, third = first;
    if (controls.count > 1) second = DDSampleMaterial(controls.ids.y, projection);
    if (controls.count > 2) third = DDSampleMaterial(controls.ids.z, projection);
    float3 weights = DDHeightBlendWeights(controls, float3(first.height, second.height, third.height));
    // 每个角点的Bottom/Top贡献和为1，bary和为1，因此weights和也为1；无需再归一化。
    // 所有通道使用同一组weights，法线只在合成末尾归一化。
    DDSurface surface;
    // BaseColor输出契约为0..1，之后才允许叠加道路/贴花；不限制最终光照亮度。
    surface.albedo = saturate(first.albedo * weights.x + second.albedo * weights.y + third.albedo * weights.z);
    surface.normal = normalize(first.normal * weights.x + second.normal * weights.y + third.normal * weights.z + geometryNormal * 1e-6);
    surface.height = dot(float3(first.height, second.height, third.height), weights);
    surface.roughness = dot(float3(first.roughness, second.roughness, third.roughness), weights);
    surface.ao = dot(float3(first.ao, second.ao, third.ao), weights);
    surface.invalid = max(controls.invalid, min(_DDMaterialInfo[controls.ids.x].w,
        min(_DDMaterialInfo[controls.ids.y].w, _DDMaterialInfo[controls.ids.z].w)) < .5 ? 1 : 0);
    return surface;
}


#endif
