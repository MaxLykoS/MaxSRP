#ifndef DIRECT_DELTA_HEX_TILING_INCLUDED
#define DIRECT_DELTA_HEX_TILING_INCLUDED

/* Hex Tiling，改编自Morten S. Mikkelsen的Practical Real-Time Hex-Tiling。
   Copyright (c) 2022 mmikk. MIT；完整许可：../ThirdParty/HexTiling/LICENSE.txt。
   https://github.com/mmikk/hextile-demo commit 43c3ed7e18e1e4539fa9323f72d5e2a65147ebb3
   对应hextiling.h / hextiling_rws.h：TriangleGridRWS、MakeCenST、LoadRot2x2、
   hash、hex2colTexRWS及bumphex2derivNMapRWS。以下DD前缀与数组/PBR接线为工程适配。

   一次材质/投影采3个随机UV副本，两张压缩源共6次SampleGrad；不是六个副本。
   每屏幕位置仍只读3个控制点；平地最多18、三面最多54次材质源采样。
   不接VT，不复制整个evaluator做软件AF，不改全局16 ID或Height blend公式。
   随机旋转要求源法线均值接近正Z，否则整块受光方向会随tile旋转，露出蜂窝。
   Gravel(ID3)的整体偏斜在Editor/MeshNormalPreparation离线校正并重建压缩mip；
   这里不增加运行时校正、纹理读取或材质特判。其他源法线仍需各自检查。
*/

void DDHexTriangleGrid(float2 st, float2 offset, out float3 weights,
    out int2 v0, out int2 v1, out int2 v2)
{
    // 原作者RWS网格：把较大的整数偏移与局部分数坐标分开计算。
    const float2x2 skew = float2x2(1,-.57735027,0,1.15470054);
    float2 local = mul(skew, st * (2 * sqrt(3.0)));
    float2 absoluteOffset = mul(skew, offset * (2 * sqrt(3.0)));
    int2 baseOffset = (int2)floor(absoluteOffset);
    float2 combined = local + frac(absoluteOffset);
    int2 baseId = (int2)floor(combined) + baseOffset;
    float3 temp = float3(frac(combined),0);
    temp.z = 1 - temp.x - temp.y;
    float s = step(0,-temp.z), signTriangle = 2*s - 1;
    weights = float3(-temp.z * signTriangle, s-temp.y*signTriangle, s-temp.x*signTriangle);
    v0 = baseId + int2(s,s); v1 = baseId + int2(s,1-s); v2 = baseId + int2(1-s,s);
}

float2 DDHexHash(float2 p)
{
    // 原作者散列；只依赖世界网格/材质种子，没有相机位置、帧数或Clipmap环形地址。
    return frac(sin(mul(float2x2(127.1,311.7,269.5,183.3),p)) * 43758.5453);
}

float2 DDHexCenter(int2 vertex)
{
    return mul(float2x2(1,.5,0,1/1.15470054),(float2)vertex) / (2*sqrt(3.0));
}

float2x2 DDHexRotation(int2 seed)
{
    float angle = abs(seed.x*seed.y) + abs(seed.x+seed.y) + UNITY_PI;
    angle = fmod(angle,2*UNITY_PI);
    if (angle < 0) angle += 2*UNITY_PI;
    if (angle > UNITY_PI) angle -= 2*UNITY_PI;
    angle *= saturate(_DDHexParams.x); // 工程默认0.25，即最多约±45度；0仍保留随机平移。
    float cs=cos(angle), sn=sin(angle);
    return float2x2(cs,-sn,sn,cs);
}

DDMaterial DDSampleHexMaterialPlane(int id, float2 worldUv, float2 worldDx, float2 worldDy,
    float3 tangent, float3 bitangent, float3 planeNormal, float3 geometryNormal)
{
    float4 transform=_DDMaterialUv[id], info=_DDMaterialInfo[id];
    // 工程固定世界块拆分RWS，避免相机移动改变采样配方；世界块不是纹理拼接边。
    float2 anchor=floor(worldUv/256)*256;
    float2 local=(worldUv-anchor)*transform.xy;
    float2 offset=anchor*transform.xy+transform.zw;
    float2 dx=worldDx*transform.xy, dy=worldDy*transform.xy;
    float3 bary; int2 v0,v1,v2;
    DDHexTriangleGrid(local,offset,bary,v0,v1,v2);
    int2 vertices[3]; vertices[0]=v0; vertices[1]=v1; vertices[2]=v2;
    float4 ah[3], nrao[3]; float2 slopes[3];
    [unroll] for (int patch=0;patch<3;patch++)
    {
        int2 vertex=vertices[patch];
        // 材质ID只作为散列种子；不改变网格位置，不增加任何材质ID或DTA映射。
        int2 seed=vertex+int2(id*19,id*23);
        float2x2 rotation=DDHexRotation(seed);
        float2 center=DDHexCenter(vertex);
        float2 uv=mul(local,rotation)+frac(mul(offset-center,rotation)+center)+DDHexHash(seed);
        // 原作者RWS坐标与显式梯度。两源共享每个副本的UV、梯度及旋转。
        float2 rdx=mul(dx,rotation), rdy=mul(dy,rotation);
        ah[patch]=_DDAlbedoHeight.SampleGrad(sampler_DDAlbedoHeight,float3(uv,id),rdx,rdy);
        nrao[patch]=_DDNormalRoughAo.SampleGrad(sampler_DDAlbedoHeight,float3(uv,id),rdx,rdy);
        // RG重建正Z，再施加强度；斜率混合避免直接把不同旋转基中的RG当颜色相加。
        float2 xy=nrao[patch].rg*2-1;
        float z=sqrt(max(1-dot(xy,xy),1e-6));
        xy*=max(info.x,0);
        // 原作者将极端斜率限制在约128；这里用本工程UV手性，不沿用demo的V轴翻转。
        float2 slope=xy/max(z,max(abs(xy.x),abs(xy.y))/128);
        slopes[patch]=mul(rotation,slope); // 反向旋转采得的斜率，回到未随机化的投影基。
    }
    // 原作者用颜色亮度/法线斜率调制重心权重，再以高次幂缩窄混合带，减轻洗灰。
    // 默认falloff=.6、exponent=7来自参考代码；没有做直方图预处理或精确保直方图。
    float3 luma=float3(dot(ah[0].rgb,float3(.299,.587,.114)),
        dot(ah[1].rgb,float3(.299,.587,.114)),dot(ah[2].rgb,float3(.299,.587,.114)));
    float3 sharp=pow(max(bary,0),max(_DDHexParams.z,1));
    float3 colorWeights=sharp*lerp(1,luma,saturate(_DDHexParams.y));
    colorWeights/=max(dot(colorWeights,float3(1,1,1)),1e-12);
    float3 magnitudes=float3(dot(slopes[0],slopes[0]),dot(slopes[1],slopes[1]),dot(slopes[2],slopes[2]));
    float3 normalWeights=sharp*lerp(1,sqrt(magnitudes/(1+magnitudes)),saturate(_DDHexParams.y));
    normalWeights/=max(dot(normalWeights,float3(1,1,1)),1e-12);
    // 多通道工程约定：AH全部通道、Roughness/AO共享colorWeights；Normal按作者斜率权重。
    // 每个材质先合成自身Height，再进入原有有序Height blend，不能随机打乱Bottom/Top。
    float4 mixedAh=ah[0]*colorWeights.x+ah[1]*colorWeights.y+ah[2]*colorWeights.z;
    float2 slope=slopes[0]*normalWeights.x+slopes[1]*normalWeights.y+slopes[2]*normalWeights.z;
    float2 roughAo=nrao[0].ba*colorWeights.x+nrao[1].ba*colorWeights.y+nrao[2].ba*colorWeights.z;
    DDMaterial material;
    material.albedo=mixedAh.rgb; material.height=mixedAh.a;
    material.roughness=saturate(roughAo.x); material.ao=saturate(roughAo.y);
    material.normal=DDReorientProjectionNormal(normalize(float3(slope,1)),tangent,bitangent,planeNormal,geometryNormal);
    return material;
}
#endif
