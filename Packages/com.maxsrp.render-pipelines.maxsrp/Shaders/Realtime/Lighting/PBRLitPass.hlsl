#ifndef MAX_PBRLIT_PASS_INCLUDED
#define MAX_PBRLIT_PASS_INCLUDED

#include "../../../ShaderLibrary/Realtime/Lighting/Lighting.hlsl"
#include "../../../ShaderLibrary/Realtime/Shadows/Shadow.hlsl"
#include "../../../ShaderLibrary/Realtime/Core/SpaceTransform.hlsl"
#include "../../../ShaderLibrary/Realtime/Core/Packing.hlsl"
#include "../../../ShaderLibrary/Realtime/AtmosphereScattering/AerialPerspective.hlsl"
#include "../GI/BakedSolarProbeGI.hlsl"

struct a2v
{
	float4 pO : POSITION;
	float2 uv : TEXCOORD0;
};

struct v2f
{
	float2 uv : TEXCOORD0;
	float4 pH : SV_POSITION;
};

UNITY_DECLARE_TEX2D(_GDepth);
UNITY_DECLARE_TEX2D(_GBuffer0);
UNITY_DECLARE_TEX2D(_GBuffer1);
UNITY_DECLARE_TEX2D(_GBuffer2);
UNITY_DECLARE_TEX2D(_GBuffer3);

#if defined(MAX_GTAO_UINT)
Texture2D<uint> _MaxGTAOTexture;
#else
Texture2D<float> _MaxGTAOTexture;
#endif
float4 _MaxGTAOParams; // strength, AO-only display, buffer width, buffer height
float4 _MaxTaaJitter;
float4 _CameraBackgroundColor;
int _CameraClearFlags;

v2f PBRVertex(uint vertexID : SV_VertexID)
{
	v2f o;
	o.uv = float2((vertexID << 1) & 2, vertexID & 2);
    o.pH = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
#if UNITY_UV_STARTS_AT_TOP
    o.uv.y = 1.0 - o.uv.y;
#endif

	return o;
}

float4 PBRFragment(v2f o, out float depthOut : SV_Depth) : SV_Target
{
	float4 GBuffer0 = UNITY_SAMPLE_TEX2D(_GBuffer0, o.uv);
	float4 GBuffer1 = UNITY_SAMPLE_TEX2D(_GBuffer1, o.uv);
	float4 GBuffer2 = UNITY_SAMPLE_TEX2D(_GBuffer2, o.uv);
	float4 GBuffer3 = UNITY_SAMPLE_TEX2D(_GBuffer3, o.uv);
	float GDepth = UNITY_SAMPLE_DEPTH(UNITY_SAMPLE_TEX2D(_GDepth, o.uv));
    depthOut = GDepth;
#if defined(UNITY_REVERSED_Z)
    bool isBackground = GDepth <= 0.0;
#else
    bool isBackground = GDepth >= 1.0;
#endif

	// gb0
	float4 albedo = GBuffer0;
	
	// bg1
	float3 normal = normalize(GBuffer1.rgb * 2 - 1);

	// gb2
	// R/G are baked sky visibility / lightmap validity. Object motion has its
	// own texture. Older and unlightmapped materials retain unoccluded sky.
	float skyVisibility = GBuffer2.g > 0.5 ? saturate(GBuffer2.r) : 1.0;
	float roughness = GBuffer2.b;
	roughness = max(roughness, 0.05);
	float metalness = GBuffer2.a;

	// gb3
    bool hasReceiverNormal = abs(GBuffer1.a - MaxGBufferReceiverNormalTag) < (1.0 / 6.0);
    float3 receiverNormal = hasReceiverNormal ? GBuffer3.rgb : 0.0;
    float3 emissionAndBakedDiffuse = hasReceiverNormal ? 0.0 : GBuffer3.rgb;
    // Unified visibility convention for standard, Bistro and terrain materials.
    // Baked diffuse was already modulated at its writer; emission is unaffected.
    float materialAO = saturate(GBuffer3.a);

	// depth buffer
	float depth = GDepth;
	depthOut = depth;

	// reprojection 计算世界坐标
	float4 pNDC = float4(o.uv * 2 - 1, DeviceDepthToClipDepth(depth), 1);
#if UNITY_UV_STARTS_AT_TOP
    pNDC.y = -pNDC.y;
#endif
	float4 pW = mul(unity_MatrixInvVP, pNDC);
	pW = pW / pW.w;

	float3 receiverDx, receiverDy;
    ShadowReceiverDerivatives(pW.xyz, receiverDx, receiverDy);
    float visibility = 1.0;
    if (isBackground)
    {
        if (_MaxIndirectLightingDebug.w > 0.5) return float4(0, 0, 0, 1);
        if (_CameraClearFlags == 4) discard; // Nothing: preserve previous color/depth.
        if (_CameraClearFlags == 3) return float4(0, 0, 0, 0); // Depth only.
        if (_MaxGTAOParams.y > 0.5f && _MaxLightingDiagnosticMode == 0.0f)
            return 1.0f;
        return _CameraBackgroundColor;
    }

    // These views precede SSAO and aerial perspective. The writer has already
    // separated baked diffuse from emission for this same camera.
    if (_MaxIndirectLightingDebug.w == 4.0)
        return float4(emissionAndBakedDiffuse, 1.0);
    if (_MaxIndirectLightingDebug.w == 5.0)
        return float4(skyVisibility.xxx, 1.0);

    // Terrain's material / VT diagnostics are linear scene colors, not lit
    // materials. A negative GBuffer3.a is their explicit unlit sentinel; normal
    // surfaces keep material AO in [0,1]. Shared TAA/exposure/output still follow.
    if (GBuffer3.a < 0.0)
        return float4(GBuffer3.rgb, 1.0);

    // Bake diagnostics expose the linear component before atmosphere, exposure
    // and output tone mapping. Their GBuffer draw uses the same global mode.
    if (MaxIsGBufferLightingDiagnosticMode())
        return float4(emissionAndBakedDiffuse, 1.0);
    if (_MaxLightingDiagnosticMode == 8.0)
        return float4(skyVisibility.xxx, 1.0);
    // Source probes used by the offline precision investigation.
    if (_MaxLightingDiagnosticMode == 13.0)
        return float4(SurfaceSunTransmittance(pW.xyz, normalize(_DirectionalLightDirection.xyz)), 1.0);
    if (_MaxLightingDiagnosticMode == 14.0)
        return float4(pW.xyz, 1.0);

    float probeVisibility = 1.0f;
    [branch]
    if (_MaxLightingDiagnosticMode == 0.0f && any(_MaxGTAOParams.xy > 0.0f))
    {
        float visibilityAO = 1.0f;
        [branch]
        if (_MaxGTAOParams.x > 0.0f)
        {
            int2 size = int2(_MaxGTAOParams.zw);
            int2 pixel = clamp(int2(o.uv * size), 0, size - 1);
#if defined(MAX_GTAO_UINT)
            visibilityAO = _MaxGTAOTexture.Load(int3(pixel, 0)) * (1.0f / 255.0f);
#else
            visibilityAO = saturate(_MaxGTAOTexture.Load(int3(pixel, 0)));
#endif
        }
        if (_MaxGTAOParams.y > 0.5f)
            return float4(visibilityAO.xxx, 1.0f);
        // The baked visibility and screen estimate may represent the same occluder.
        skyVisibility = lerp(skyVisibility, min(skyVisibility, visibilityAO), _MaxGTAOParams.x);
        probeVisibility = lerp(1.0f, visibilityAO, _MaxGTAOParams.x);
    }

    float3 probeIndirect = 0.0f;
    [branch]
    if (_MaxLightingDiagnosticMode == 0.0f && _MaxIndirectLightingDebug.y < 0.5f &&
        _MaxDDGISettings.x > 0.5f)
    {
        float3 irradiance = MaxSampleBakedDDGI(pW.xyz, normal) * _MaxDDGILightColor * _MaxDDGISettings.z;
        float3 F0 = lerp(float3(0.04f, 0.04f, 0.04f), albedo.rgb, saturate(metalness));
        float NdotV = saturate(dot(normal, GetSurfaceViewDirection(pW.xyz)));
        float3 diffuseReflectance = albedo.rgb * MaxIndirectDiffuseWeight(NdotV, F0, roughness, saturate(metalness));
        probeIndirect = irradiance * diffuseReflectance * (probeVisibility * materialAO / PI);
    }
    if (_MaxIndirectLightingDebug.w == 3.0 ||
        (_MaxLightingDiagnosticMode == 0.0f && _MaxDDGISettings.w > 0.5f))
        return float4(probeIndirect, 1.0f);

    if ((_MaxIndirectLightingDebug.w < 0.5 || _MaxIndirectLightingDebug.w == 1.0) &&
        (_MaxLightingDiagnosticMode < 2.0 || _MaxLightingDiagnosticMode > 5.0))
        visibility = MainLightShadow(pW.xyz, normal, receiverDx, receiverDy, receiverNormal);
    float3 c = PBR_Shading(pW.xyz, normal, albedo.rgb, metalness, roughness, o.uv, visibility, skyVisibility, materialAO);
    if (_MaxIndirectLightingDebug.w < 0.5) c += emissionAndBakedDiffuse;
    c += probeIndirect;
    if (_MaxIndirectLightingDebug.w > 0.5) return float4(c, 1.0);

	// Perspective LUT depth is ray length; orthographic rays start on the camera plane.
    float atmosphereDistance = unity_OrthoParams.w > 0.5
        ? max(0.0, -mul(unity_MatrixV, pW).z) : length(pW.xyz - _WorldSpaceCameraPos);
    c = ApplyAerialPerspective(c, o.uv - _MaxTaaJitter.xy, atmosphereDistance);
    return float4(c, 1.0);
}
#endif
