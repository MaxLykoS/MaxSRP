#ifndef MAX_SCREENSPACESHADOWMAP_PASS_INCLUDED
#define MAX_SCREENSPACESHADOWMAP_PASS_INCLUDED

#include "../../ShaderLibrary/Realtime/Lighting.hlsl"
#include "../../ShaderLibrary/Realtime/Shadow.hlsl"
#include "../../ShaderLibrary/Realtime/SpaceTransform.hlsl"
#include "../../ShaderLibrary/Realtime/Packing.hlsl"
#include "../../ShaderLibrary/Realtime/Random.hlsl"

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
UNITY_DECLARE_TEX2D(_GBuffer1);

float4x4 _ShadowCameraInverseVP;
float4x4 _ShadowCameraView;
float4 _ShadowScreenSize;

v2f vert(uint vertexID : SV_VertexID)
{
    v2f o;
    o.uv = float2((vertexID << 1) & 2, vertexID & 2);
    o.pH = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
#if UNITY_UV_STARTS_AT_TOP
    o.uv.y = 1.0 - o.uv.y;
#endif
    return o;
}

float4 frag(v2f o) : SV_Target
{
	float4 GBuffer1 = UNITY_SAMPLE_TEX2D(_GBuffer1, o.uv);
	float GDepth = UNITY_SAMPLE_DEPTH(UNITY_SAMPLE_TEX2D(_GDepth, o.uv));

	// gb1
	float3 nW = normalize(GBuffer1.rgb * 2 - 1);

	// depth buffer
	float depth = GDepth;
#if defined(UNITY_REVERSED_Z)
    if (depth <= 0.0) return 1.0;
#else
    if (depth >= 1.0) return 1.0;
#endif
	// reprojection 计算世界坐标
	float4 pNDC = float4(o.uv * 2 - 1, DeviceDepthToClipDepth(depth), 1);
#if UNITY_UV_STARTS_AT_TOP
    pNDC.y = -pNDC.y;
#endif
	float4 pW = mul(_ShadowCameraInverseVP, pNDC);
	pW = pW / pW.w;
    float eyeDepth = -mul(_ShadowCameraView, pW).z;
    if (_CascadeDepthRange.z <= 0.0 ||
        eyeDepth < _CascadeDepthRange.x || eyeDepth > _CascadeDepthRange.y) return 1.0;
    float depthLinear = saturate((eyeDepth - _CascadeDepthRange.x) * _CascadeDepthRange.z);
	
	float3 lightDir = normalize(_DirectionalLightDirection.xyz);
	float biasScale = 2.0 - saturate(dot(nW, lightDir));

	// 防止低掠角导致大片阴影
	if (dot(lightDir, nW) < 0.005) return 0;
	
	// 随机旋转角度
	uint seed = RandomSeed(o.uv, _ShadowScreenSize.xy);
	float2 uv_noi = o.uv * _ShadowScreenSize.xy / _NoiseTextureResolution;
	float rotateAngle = rand(seed) * 2.0 * 3.1415926;
	rotateAngle = tex2D(_NoiseTexture, uv_noi * 0.5).r * 2.0 * 3.1415926;   // blue noise
	
	// 给法线做bias，向着法线偏移采样点
	float4 worldPosOffset = pW;
	float shadow = 1.0;
	if (depthLinear <= _Split0)
	{
		worldPosOffset.xyz += nW * _ShadingPointNormalBias0;
		shadow *= PCSS(worldPosOffset, _MainShadowMap0, _ShadowVPMatrix0, _OrthoWidth0, _ShadowDepthHalfRange0, _ShadowMapResolution, rotateAngle, _PcssSearchRadius0, _PcssFilterRadius0, _DepthNormalBias0 * biasScale);
	}
	else if (depthLinear <= _Split1)
	{
		worldPosOffset.xyz += nW * _ShadingPointNormalBias1;
		shadow *= PCSS(worldPosOffset, _MainShadowMap1, _ShadowVPMatrix1, _OrthoWidth1, _ShadowDepthHalfRange1, _ShadowMapResolution, rotateAngle, _PcssSearchRadius1, _PcssFilterRadius1, _DepthNormalBias1 * biasScale);
	}
	else if (depthLinear <= _Split2)
	{
		worldPosOffset.xyz += nW * _ShadingPointNormalBias2;
		shadow *= HardShadow(worldPosOffset.xyz, _MainShadowMap2, _ShadowVPMatrix2, _DepthNormalBias2 * biasScale, _ShadowDepthHalfRange2);
	}
	else if (depthLinear <= _Split3)
	{
		worldPosOffset.xyz += nW * _ShadingPointNormalBias3;
		shadow *= HardShadow(worldPosOffset.xyz, _MainShadowMap3, _ShadowVPMatrix3, _DepthNormalBias3 * biasScale, _ShadowDepthHalfRange3);
	}

	return shadow;
}

struct v2fBlur
{
	float2 uv[5] : TEXCOORD0;
	float4 pH : SV_POSITION;
};

#define KERNEL_SIZE 2.0
float4 _ScreenSpaceShadowMapBeforeBlur_TexelSize;

//用于计算纵向模糊的纹理坐标元素
v2fBlur vert_v(uint vertexID : SV_VertexID)
{
	v2fBlur o;
    v2f fullscreen = vert(vertexID);
    o.pH = fullscreen.pH;
    float2 uv = fullscreen.uv;

	//以扩散的方式对数组进行排序，只偏移y轴，其中1和2,3和4分别位于原始点0的上下，且距离1个单位和2个像素单位
	//得到的最终偏移与模糊范围的控制参数进行乘积
	float texelSize = _ScreenSpaceShadowMapBeforeBlur_TexelSize.y;
	o.uv[0] = uv;
	o.uv[1] = uv + float2(0.0, texelSize) * KERNEL_SIZE;
	o.uv[2] = uv - float2(0.0, texelSize) * KERNEL_SIZE;
	o.uv[3] = uv + float2(0.0, texelSize * 2 * KERNEL_SIZE);
	o.uv[4] = uv - float2(0.0, texelSize * 2 * KERNEL_SIZE);

	return o;
}

//用于计算横向模糊的纹理坐标元素
v2fBlur vert_h(uint vertexID : SV_VertexID)
{
	v2fBlur o;
    v2f fullscreen = vert(vertexID);
    o.pH = fullscreen.pH;
    float2 uv = fullscreen.uv;

	//与上面同理，只不过是x轴向的模糊偏移
	float texelSize = _ScreenSpaceShadowMapBeforeBlur_TexelSize.x;
	o.uv[0] = uv;
	o.uv[1] = uv + float2(texelSize * 1.0, 0.0) * KERNEL_SIZE;
	o.uv[2] = uv - float2(texelSize * 1.0, 0.0) * KERNEL_SIZE;
	o.uv[3] = uv + float2(texelSize * 2.0, 0.0) * KERNEL_SIZE;
	o.uv[4] = uv - float2(texelSize * 2.0, 0.0) * KERNEL_SIZE;

	return o;
}

//在片元着色器中进行最终的模糊计算，此过程在每个Pass中都会进行一次计算，但计算方式是统一的
float4 fragBlur(v2fBlur o) : SV_Target
{
	float weights[3] = {0.4026,0.2442,0.0545};

	float4 col = UNITY_SAMPLE_TEX2D(_ScreenSpaceShadowMapBeforeBlur, o.uv[0]);

	float3 sum = col.rgb * weights[0];

	//对采样结果进行对应纹理偏移坐标的权重计算，以得到模糊的效果
	for (int it = 1; it < 3; it++)
	{
		sum += UNITY_SAMPLE_TEX2D(_ScreenSpaceShadowMapBeforeBlur, o.uv[2 * it - 1]).rgb * weights[it];//对应1和3，也就是原始像素的上方两像素
		sum += UNITY_SAMPLE_TEX2D(_ScreenSpaceShadowMapBeforeBlur, o.uv[2 * it]).rgb * weights[it];//对应2和4，下方两像素
	}
	float4 color = float4(sum, 1.0);
	return color;
}

#endif