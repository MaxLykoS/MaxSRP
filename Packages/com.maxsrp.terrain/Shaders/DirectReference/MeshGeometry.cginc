#ifndef DIRECT_DELTA_GEOMETRY_INCLUDED
#define DIRECT_DELTA_GEOMETRY_INCLUDED

// 几何阅读入口：Vert → SampleHeight → Frag。源高度法线见MeshHeightNormals.cginc。
// 三个pass复用同一高度重建和morph；只有Game实体pass调用材质shading。

// Keep in sync with MeshHeightLayout.ClipSize; real GPU ring
// readback and A/B rendering validate this shared layout contract.
#define MESH_HEIGHT_CLIP_SIZE 1295
// [工程几何密度] 与MeshCdlod.GridQuads同步。32m叶块 / 32格 = 最近1m；2048m根块 / 32格 = 最粗64m。
// 高度母版仍1m、原DEM名义2m；源高度法线独立重建，减面不恢复面法线折痕。
// 按用户要求128→32格，每块32768→2048三角；保留XZ/Y父级morph和quarter退化。
#define MESH_PATCH_QUADS 32
// 几何LOD0..6覆盖32..2048m；高度Clipmap仍只有LOD0..4，禁止直接用几何LOD索引高度层。
#define MESH_GEOMETRY_MAX_LOD 6
#define MESH_HEIGHT_MAX_LOD 4

Texture2DArray<float> _MeshHeightClip;
SamplerState sampler_MeshHeightClip; // Bilinear / Repeat / aniso1; both height rings share this contract.
float4 _MeshClipOrigins[5]; // origin world X/Z, sample stride, valid
float4 _MeshObserver; // observer XYZ, vertical scale for the LOD distance
int _MeshDebug;
float _MeshHeightScale;
float _MeshSourceNormals;
float4 _MeshWireColor;
UNITY_INSTANCING_BUFFER_START(MeshInstances)
    UNITY_DEFINE_INSTANCED_PROP(float4, _MeshPatch)
    UNITY_DEFINE_INSTANCED_PROP(float4, _MeshParams)
    // Previous selection is matched by patch identity on CPU; changed topology rejects history.
    UNITY_DEFINE_INSTANCED_PROP(float4, _MeshPreviousParams)
    UNITY_DEFINE_INSTANCED_PROP(float, _MeshMotionValidity)
UNITY_INSTANCING_BUFFER_END(MeshInstances)

// [硬件高度过滤] DirectDelta与Delta现在都使用独立高度Clipmap。
// 每个array slice是一层1/2/4/8/16m高度格；纹理本身没有mip链。
float SampleHeight(float2 worldXZ, int level, out float invalid)
{
    worldXZ = clamp(worldXZ, 0, 10000);
    int stride = 1 << level;
    float2 gridFloat = worldXZ / stride;
    float2 lowWorld = floor(gridFloat) * stride;
    float2 highWorld = lowWorld + stride;
    float4 descriptor = _MeshClipOrigins[level];
    // 硬件过滤仍须检查完整四点足迹。Repeat只解决物理环回，不代表窗口外数据有效。
    invalid = descriptor.w < .5 || descriptor.z != stride ||
        any(lowWorld < descriptor.xy) || any(highWorld > descriptor.xy + (MESH_HEIGHT_CLIP_SIZE - 1) * stride) ? 1 : 0;
    // 全局采样格按物理边长环回；+0.5对齐texel中心，避免半格平移。
    // 显式frac把坐标限制到一圈，Repeat负责跨最后/第一个texel的双线性足迹。
    float2 uv = frac((gridFloat + .5) / MESH_HEIGHT_CLIP_SIZE);
    // level放在数组层，mip固定0；这是一次硬件双线性采样，不再四Load+lerp。
    return _MeshHeightClip.SampleLevel(sampler_MeshHeightClip, float3(uv, level), 0) * _MeshHeightScale;
}

#include "MeshHeightNormals.cginc"

#if defined(DD_LIVE_OVERLAYS)
float3 DDLiveSourceWorld(float3 displayWorld)
{
    // 高保真参考不使用 CDLOD morph 的 Y 定位覆盖；取当前能覆盖位置的最细源高度。
    // 近处 1m 与页面源一致；远处可用源层精度仍受既有 Clipmap 范围限制。
    int selected = 4;
    [unroll] for (int level = 4; level >= 0; level--)
        if (MeshNormalLevelMargin(displayWorld.xz, level) >= 0) selected = level;
    float invalid;
    displayWorld.y = SampleHeight(displayWorld.xz, selected, invalid);
    return displayWorld;
}
#endif

struct Attributes
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
    float2 quarter : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};
struct Varyings
{
    float4 position : SV_POSITION;
    float3 world : TEXCOORD0;
    float2 grid : TEXCOORD1;
    float4 diagnostic : TEXCOORD2; // LOD, morph, invalid source, active quarter
};

// [PPT p89–90只公开CDLOD/局部父quarter方向，没有给出这里的完整shader]
// 下方全部是工程实现：距离度量 → 奇数顶点向父格morph → quarter退化 → 高度重建。
// Current, previous-morph and shadow passes call this exact same reconstruction.
float3 MeshReconstructWorld(Attributes input, float4 patch, float4 parameters,
    float4 observer, out float4 diagnostic)
{
    float2 index = round(input.uv * MESH_PATCH_QUADS);
    float2 worldXZ = clamp(patch.xy + index / MESH_PATCH_QUADS * patch.zw, 0, 10000);
    // A fixed global 16m source is used only for the distance metric.
    // Both paths reconstruct the identical samples. This is an
    // explicit adapter, not Delta Force's unpublished morph formula.
    float metricInvalid;
    float metricHeight = SampleHeight(worldXZ, 4, metricInvalid);
    float3 observerDelta = float3(worldXZ.x, metricHeight, worldXZ.y) - observer.xyz;
    observerDelta.y *= observer.w;
    float observerDistance = length(observerDelta);
    float morph = parameters.y > parameters.x ?
        saturate((observerDistance - parameters.x) / (parameters.y - parameters.x)) : 0;
    float2 odd = index - floor(index * .5) * 2;
    float2 morphed = index - odd * morph;
    worldXZ = patch.xy + morphed / MESH_PATCH_QUADS * patch.zw;
    uint mask = (uint)round(parameters.w), quarter = (uint)round(input.quarter.x);
    float active = (mask & (1u << quarter)) != 0u ? 1 : 0;
    // Independent quarter topology makes this a true zero-area
    // collapse, retaining the other parts of a partial parent patch.
    if (active < .5) worldXZ = patch.xy + patch.zw * .5;
    worldXZ = clamp(worldXZ, 0, 10000);
    int geometryLod = clamp((int)round(parameters.z), 0, MESH_GEOMETRY_MAX_LOD);
    int level = min(geometryLod, MESH_HEIGHT_MAX_LOD);
    float finalInvalid;
    float height = SampleHeight(worldXZ, level, finalInvalid);
    // [工程实现] A denser parent has vertices between its source-height
    // samples. Morph Y to that parent's reconstructed surface as well as XZ,
    // so fully collapsed child edges exactly meet the actual parent mesh.
    if (geometryLod < MESH_GEOMETRY_MAX_LOD && morph > 0)
    {
        float parentInvalid;
        // LOD4→5→6继续折叠XZ网格；高度均读取固定覆盖全世界的16m源，不增高度纹理。
        int parentLevel = min(geometryLod + 1, MESH_HEIGHT_MAX_LOD);
        float parentHeight = SampleHeight(worldXZ, parentLevel, parentInvalid);
        height = lerp(height, parentHeight, morph);
        finalInvalid = max(finalInvalid, parentInvalid);
    }
    diagnostic = float4(geometryLod, morph, max(metricInvalid,finalInvalid), active);
    return float3(worldXZ.x, height, worldXZ.y);
}

Varyings Vert(Attributes input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    float4 patch = UNITY_ACCESS_INSTANCED_PROP(MeshInstances, _MeshPatch);
    float4 parameters = UNITY_ACCESS_INSTANCED_PROP(MeshInstances, _MeshParams);
    Varyings output;
    output.world = MeshReconstructWorld(input, patch, parameters, _MeshObserver, output.diagnostic);
    // MaxSRP binds its camera-local jittered VP before GBuffer/Motion rasterization.
    output.position = mul(UNITY_MATRIX_VP, float4(output.world,1));
    output.grid = input.uv * MESH_PATCH_QUADS;
    return output;
}

float3 LevelColor(float level)
{
    if (level < .5) return float3(.25,.7,.38);
    if (level < 1.5) return float3(.35,.62,.92);
    if (level < 2.5) return float3(.95,.72,.28);
    if (level < 3.5) return float3(.85,.4,.27);
    if (level < 4.5) return float3(.67,.42,.8);
    if (level < 5.5) return float3(.18,.75,.78);
    return float3(.9,.25,.35);
}

// Game和Scene实体使用同一入口；DD_SURFACE_ON启用真正材质，其余为几何诊断。
float4 Frag(Varyings input) : SV_Target
{
    if (input.diagnostic.z > .01) return float4(1,0,1,1);
    float3 normal;
    if (_MeshSourceNormals > .5)
    {
        float normalInvalid, normalLevel;
        normal = MeshSourceHeightNormal(input.world.xz,normalInvalid,normalLevel);
        if (normalInvalid > .01) return float4(1,0,1,1);
    }
    else
    {
        // 旧三角面法线，只保留作可见的同机位对照。
        normal = normalize(cross(ddy(input.world),ddx(input.world)));
        if (normal.y < 0) normal = -normal;
    }
    #if defined(DD_SURFACE_ON)
        if (_MeshDebug == 0) return DDShade(input.world, normal);
    #endif
    float illumination = .3 + .7 * saturate(dot(normal,normalize(float3(-.45,.8,.3))));
    float3 color = float3(.62,.65,.68) * illumination;
    if (_MeshDebug == 1) color = LevelColor(input.diagnostic.x) * illumination;
    if (_MeshDebug == 3) color = LevelColor(min(input.diagnostic.x,MESH_HEIGHT_MAX_LOD)) * illumination;
    if (_MeshDebug == 2) color = lerp(float3(.15,.35,.8),float3(.95,.4,.12),input.diagnostic.y);
    if (_MeshDebug == 4)
    {
        // UV grid diagnostic, including the shared diagonal. It is
        // not a geometry-shader wireframe or a material texture.
        float3 coordinates = float3(input.grid,input.grid.x + input.grid.y);
        float3 edge = abs(frac(coordinates + .5) - .5) / max(fwidth(coordinates),1e-4);
        float gridLine = 1 - saturate(min(edge.x,min(edge.y,edge.z)));
        color = lerp(color,float3(.035,.045,.055),gridLine);
    }
    return float4(color,1);
}
float4 FragWire(Varyings input) : SV_Target
{
    // An inactive quarter collapses to a point. Also discard its
    // zero-length line primitives so they cannot leave a dot.
    clip(input.diagnostic.w - .5);
    if (input.diagnostic.z > .01) return float4(1,0,1,1);
    return _MeshWireColor;
}
float4 FragDepth(Varyings input) : SV_Target
{ return 0; }

#endif
