// Persistent v4 archive identity includes this hidden producer shader name.
// Keep the original name while the implementation and page contents are unchanged.
Shader "Hidden/DirectDeltaMesh/WorldVtOverlay"
{
    Properties
    {
        [HideInInspector] _DDRvtHeight ("Fixed source heights", 2D) = "black" {}
        [HideInInspector] _DDAlbedoHeight ("Surface16 albedo height", 2DArray) = "" {}
        [HideInInspector] _DDNormalRoughAo ("Surface16 normal roughness AO", 2DArray) = "" {}
        [HideInInspector] _DDOverlayMask ("Linear opacity mask", 2D) = "white" {}
        _DDOverlayMaterialId ("Stable material ID", Int) = 0
        _DDOverlayTint ("Linear albedo tint", Color) = (1,1,1,1)
        _DDOverlayOpacity ("Coverage opacity", Range(0,1)) = 1
        _DDOverlayUv ("Material repeats per metre and offset", Vector) = (1,1,0,0)
        _DDOverlayMaskUv ("Mask scale and offset", Vector) = (1,1,0,0)
        _DDOverlayMaskChannel ("Mask component 0 to 3", Int) = 0
        _DDOverlayBoxSize ("Full box size in metres", Vector) = (1,1,1,0)
        _DDOverlayBoxFeather ("Box XZ feather in metres", Float) = 0
    }
    SubShader
    {
        // 写进页面三附件，不参与屏幕深度、CDLOD morph 或当前相机变换。
        Cull Off ZWrite Off ZTest Always
        // 输出已预乘 opacity；alpha 也按 source-over 合成。
        // 各 pass 有 indexed ColorMask，必须同时为各 MRT 显式声明混合。
        // 只写未编号 Blend 时，Unity 的独立附件状态可能让 RT1/2 保持 Blend Off。
        Blend 0 One OneMinusSrcAlpha, One OneMinusSrcAlpha
        Blend 1 One OneMinusSrcAlpha, One OneMinusSrcAlpha
        Blend 2 One OneMinusSrcAlpha, One OneMinusSrcAlpha
        CGINCLUDE
        #define DD_WORLD_VT_RGBA8 1
        #include "UnityCG.cginc"
        #include "../VirtualTexturing/SinglePage/SinglePageRvtSurface.cginc"
        #include "../VirtualTexturing/SinglePage/SinglePageRvtCommon.cginc"
        #include "../VirtualTexturing/SinglePage/SinglePageRvtSource.cginc"
        #include "WorldVtOverlayCommon.cginc"

        Texture2D<float4> _DDOverlayMask;
        SamplerState sampler_DDOverlayMask;
        int _DDOverlayMaterialId;
        float4 _DDOverlayTint;
        float _DDOverlayOpacity;
        float4 _DDOverlayUv;
        float4 _DDOverlayMaskUv;
        int _DDOverlayMaskChannel;
        float4x4 _DDOverlayWorldToBox;
        float4 _DDOverlayBoxSize;
        float _DDOverlayBoxFeather;

        struct OverlayVertex
        {
            // 已是世界坐标；Y 不用于定位页面，高度在片元中读固定源窗口。
            float4 position : POSITION;
            float2 uvMetres : TEXCOORD0; // Road: 横向米 / 中心线累计纵向米。
            float2 widthFeather : TEXCOORD1; // Road: halfWidth / inward feather，单位米。
            float4 tangent : TANGENT; // world +U；cross(T,up)*w 指向 road +V。
        };
        struct OverlayVaryings
        {
            float4 position : SV_POSITION;
            float2 uvMetres : TEXCOORD0;
            float2 widthFeather : TEXCOORD1;
            float4 tangent : TEXCOORD2;
        };

        OverlayVaryings OverlayVert(OverlayVertex input)
        {
            OverlayVaryings output;
            // 以全局纹素原点确定该页正交范围。先减世界页原点，避免先产生
            // 数百万量级虚拟纹素坐标再相减；所有页使用同一个全局纹素格。
            float2 originMetres = _DDRvtTexelGrid.xy * _DDRvtTexelGrid.zw;
            float2 pageTexel = (input.position.xz - originMetres) / _DDRvtTexelGrid.zw;
            float2 clipXY = (pageTexel + _DDRvtInfo.y) / _DDRvtInfo.z * 2 - 1;
            // 与 DDRvtPageWorldXZ(SV_POSITION) 的行方向配对：D3D 的物理第0行
            // 在页面顶部，仍对应较小世界Z。不使用相机投影矩阵的翻转状态。
            #if UNITY_UV_STARTS_AT_TOP
            clipXY.y = -clipXY.y;
            #endif
            output.position = float4(clipXY, 0, 1);
            output.uvMetres = input.uvMetres;
            output.widthFeather = input.widthFeather;
            output.tangent = input.tangent;
            return output;
        }

        float DDOverlayFeather(float distanceToEdge, float featherMetres)
        {
            // width/box 范围已经包含羽化；只向内渐变，不偷偷扩大对象覆盖范围。
            float weight = featherMetres > 0 ? saturate(distanceToEdge / featherMetres) : 1;
            return weight * weight * (3 - 2 * weight);
        }

        float DDOverlayReadOpacity(float2 maskUv, float2 maskDx, float2 maskDy)
        {
            float4 mask = _DDOverlayMask.SampleGrad(sampler_DDOverlayMask, maskUv, maskDx, maskDy);
            int channel = clamp(_DDOverlayMaskChannel, 0, 3);
            return saturate(mask[channel]) * saturate(_DDOverlayOpacity);
        }

        float3 DDOverlayTangent(float3 direction, float3 sourceNormal)
        {
            float3 tangent = direction - sourceNormal * dot(direction, sourceNormal);
            if (dot(tangent, tangent) < 1e-10)
            {
                // 旋转投影盒的某根轴可能接近坡面法线，换一根稳定世界轴。
                float3 axis = abs(sourceNormal.x) < .8 ? float3(1,0,0) : float3(0,0,1);
                tangent = axis - sourceNormal * dot(axis, sourceNormal);
            }
            return normalize(tangent);
        }

        DDMaterial DDOverlayReadMaterial(float2 uv, float2 uvDx, float2 uvDy,
            float3 tangent, float3 bitangent, float3 sourceNormal)
        {
            // CPU 快照先验证稳定ID及完整16层源。钳制只保证 GPU 索引有界，
            // 不替代输入校验。覆盖UV自成米制坐标，不叠加基底投影的 _DDMaterialUv。
            int id = clamp(_DDOverlayMaterialId, 0, 15);
            float4 ah = _DDAlbedoHeight.SampleGrad(sampler_DDAlbedoHeight, float3(uv,id), uvDx, uvDy);
            float4 nrao = _DDNormalRoughAo.SampleGrad(sampler_DDAlbedoHeight, float3(uv,id), uvDx, uvDy);
            float2 xy = nrao.rg * 2 - 1;
            float3 tangentNormal = normalize(float3(xy * max(_DDMaterialInfo[id].x, 0),
                sqrt(max(1 - dot(xy,xy), 1e-6))));
            DDMaterial material;
            // 与Direct静态覆盖一致：先限制计算后的底色，再进行MRT预乘/透明混合。
            // 只限制BaseColor输出，不限制Tint乘数，也不截断光照后的颜色。
            material.albedo = saturate(ah.rgb * _DDOverlayTint.rgb);
            // flat normal=(0,0,1) 返回源基础法线。完整材质覆盖会抹掉底层微法线；
            // 颜色贴花必须选 ColorOnly pass，不能用 flat normal 表示“不修改法线”。
            material.normal = normalize(tangentNormal.x * tangent + tangentNormal.y * bitangent +
                tangentNormal.z * sourceNormal);
            material.height = ah.a;
            material.roughness = nrao.b;
            material.ao = nrao.a;
            return material;
        }

        DDOverlayMrtOutputs RoadFrag(OverlayVaryings input)
        {
            // UV 来自完整道路三角带；跨页只裁剪几何，不重置累计长度或纹理相位。
            float2 uv = input.uvMetres * _DDOverlayUv.xy + _DDOverlayUv.zw;
            float halfWidth = max(input.widthFeather.x, 1e-6);
            float2 maskBaseUv = float2(input.uvMetres.x / (2 * halfWidth) + .5, input.uvMetres.y);
            float2 maskUv = maskBaseUv * _DDOverlayMaskUv.xy + _DDOverlayMaskUv.zw;
            // 导数必须在 opacity/discard 分支前求出，采材质与透明度均用显式梯度。
            float2 uvDx = ddx(uv), uvDy = ddy(uv);
            float2 maskDx = ddx(maskUv), maskDy = ddy(maskUv);
            float distanceToEdge = halfWidth - abs(input.uvMetres.x);
            float opacity = DDOverlayReadOpacity(maskUv, maskDx, maskDy) *
                DDOverlayFeather(distanceToEdge, max(input.widthFeather.y, 0));
            clip(distanceToEdge);
            clip(opacity - 1e-6);
            float2 worldXZ = DDRvtPageWorldXZ(input.position.xy);
            float3 sourceNormal = DDRvtSourceNormal(worldXZ);
            float3 tangent = DDOverlayTangent(input.tangent.xyz, sourceNormal);
            float handedness = input.tangent.w < 0 ? -1 : 1;
            float3 bitangent = cross(tangent, sourceNormal) * handedness;
            DDMaterial material = DDOverlayReadMaterial(uv, uvDx, uvDy, tangent, bitangent, sourceNormal);
            return DDOverlayPackTargets(material.albedo, material.normal, material.ao,
                material.roughness, DDOverlayHeightCode(material.height), opacity);
        }

        DDOverlayMrtOutputs BoxFrag(OverlayVaryings input)
        {
            // CPU 仅画投影盒世界XZ包围盒的一个quad，不画盒子六面，避免重复混合。
            // 每片元从固定高度面重建Y，再做真正的三维盒内/深度测试。
            float2 worldXZ = DDRvtPageWorldXZ(input.position.xy);
            float3 world = float3(worldXZ.x, DDRvtSourceHeight(worldXZ), worldXZ.y);
            float3 local = mul(_DDOverlayWorldToBox, float4(world,1)).xyz;
            float2 localMetres = local.xz * _DDOverlayBoxSize.xz;
            float2 uv = localMetres * _DDOverlayUv.xy + _DDOverlayUv.zw;
            float2 maskUv = (local.xz + .5) * _DDOverlayMaskUv.xy + _DDOverlayMaskUv.zw;
            float2 uvDx = ddx(uv), uvDy = ddy(uv);
            float2 maskDx = ddx(maskUv), maskDy = ddy(maskUv);
            float3 inside = .5 - abs(local);
            float distanceToEdge = min(inside.x * _DDOverlayBoxSize.x, inside.z * _DDOverlayBoxSize.z);
            float opacity = DDOverlayReadOpacity(maskUv, maskDx, maskDy) *
                DDOverlayFeather(distanceToEdge, max(_DDOverlayBoxFeather, 0));
            clip(min(inside.x, min(inside.y, inside.z)));
            clip(opacity - 1e-6);
            float3 sourceNormal = DDRvtSourceNormal(worldXZ);
            // worldToBox 的行是相应UV轴的世界梯度。把+U投到源坡面，
            // 再按+V决定手性；平坦normal在任意盒旋转下都保持 sourceNormal。
            float3 uDirection = _DDOverlayWorldToBox[0].xyz;
            float3 vDirection = _DDOverlayWorldToBox[2].xyz;
            float3 tangent = DDOverlayTangent(uDirection, sourceNormal);
            float3 bitangent = cross(tangent, sourceNormal);
            bitangent *= dot(bitangent, vDirection) < 0 ? -1 : 1;
            DDMaterial material = DDOverlayReadMaterial(uv, uvDx, uvDy, tangent, bitangent, sourceNormal);
            return DDOverlayPackTargets(material.albedo, material.normal, material.ao,
                material.roughness, DDOverlayHeightCode(material.height), opacity);
        }
        ENDCG

        // 固定 ColorMask 由 pass 决定，不能依赖每次 Draw 的 MPB 修改管线状态。
        // 首版只支持完整PBR覆盖或仅底色；不伪装成AO/roughness独立逐像素权重。
        Pass
        {
            Name "RoadFull"
            ColorMask RGBA 0
            ColorMask RGBA 1
            ColorMask RGBA 2
            CGPROGRAM
            #pragma editor_sync_compilation
            #pragma target 4.5
            #pragma vertex OverlayVert
            #pragma fragment RoadFrag
            ENDCG
        }
        Pass
        {
            Name "BoxFull"
            ColorMask RGBA 0
            ColorMask RGBA 1
            ColorMask RGBA 2
            CGPROGRAM
            #pragma editor_sync_compilation
            #pragma target 4.5
            #pragma vertex OverlayVert
            #pragma fragment BoxFrag
            ENDCG
        }
        Pass
        {
            Name "RoadColorOnly"
            ColorMask RGBA 0
            ColorMask 0 1
            ColorMask 0 2
            CGPROGRAM
            #pragma editor_sync_compilation
            #pragma target 4.5
            #pragma vertex OverlayVert
            #pragma fragment RoadFrag
            ENDCG
        }
        Pass
        {
            Name "BoxColorOnly"
            ColorMask RGBA 0
            ColorMask 0 1
            ColorMask 0 2
            CGPROGRAM
            #pragma editor_sync_compilation
            #pragma target 4.5
            #pragma vertex OverlayVert
            #pragma fragment BoxFrag
            ENDCG
        }
    }
    Fallback Off
}
