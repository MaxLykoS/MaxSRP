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

v2f vert(a2v v)
{
	v2f o;
	o.pH = UnityObjectToClipPos(v.pO);
	o.uv = v.uv;

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
	float depthLinear = Linear01Depth(depth);
	// reprojection ������������
	float4 pNDC = float4(o.uv * 2 - 1, depth, 1);
	float4 pW = mul(unity_MatrixInvVP, pNDC);
	pW = pW / pW.w;
	
	float3 lightDir = normalize(_DirectionalLightDirection.xyz);
	float bias = max(0.001 * (1.0 - dot(nW, lightDir)), 0.001);

	// ��ֹ���ӽǵ��´�Ƭ��Ӱ
	if (dot(lightDir, nW) < 0.005) return 0;
	
	// �����ת�Ƕ�
	uint seed = RandomSeed(o.uv, _ScreenParams.xy);
	float2 uv_noi = o.uv * _ScreenParams.xy / _NoiseTextureResolution;
	float rotateAngle = rand(seed) * 2.0 * 3.1415926;
	rotateAngle = tex2D(_NoiseTexture, uv_noi * 0.5).r * 2.0 * 3.1415926;   // blue noise
	
	// ��������bias�����ŷ���ƫ�Ʋ�����
	float4 worldPosOffset = pW;
	float shadow = 1.0;
	if (depthLinear < _Split0)
	{
		worldPosOffset.xyz += nW * _ShadingPointNormalBias0;
		shadow *= PCSS(worldPosOffset, _MainShadowMap0, _ShadowVPMatrix0, _OrthoWidth0, _OrthoDistance, _ShadowMapResolution, rotateAngle, _PcssSearchRadius0, _PcssFilterRadius0);
	}
	else if (depthLinear < _Split0 + _Split0)
	{
		worldPosOffset.xyz += nW * _ShadingPointNormalBias1;
		shadow *= PCSS(worldPosOffset, _MainShadowMap1, _ShadowVPMatrix1, _OrthoWidth1, _OrthoDistance, _ShadowMapResolution, rotateAngle, _PcssSearchRadius1, _PcssFilterRadius1);
	}
	else if (depthLinear < _Split0 + _Split1 + _Split2)
	{
		worldPosOffset.xyz += nW * _ShadingPointNormalBias2;
		shadow *= HardShadow(worldPosOffset, _MainShadowMap2, _ShadowVPMatrix2);
	}
	else if (depthLinear < _Split0 + _Split1 + _Split2 + _Split3)
	{
		worldPosOffset.xyz += nW * _ShadingPointNormalBias3;
		shadow *= HardShadow(worldPosOffset, _MainShadowMap3, _ShadowVPMatrix3);
	}

	return shadow;
}

struct v2fBlur
{
	float2 uv[5] : TEXCOORD0;
	float4 pH : SV_POSITION;
};

#define KERNEL_SIZE 2.0

//���ڼ�������ģ������������Ԫ��
v2fBlur vert_v(a2v v)
{
	v2fBlur o;
	o.pH = UnityObjectToClipPos(v.pO);
	float2 uv = v.uv;

	//����ɢ�ķ�ʽ�������������ֻƫ��y�ᣬ����1��2,3��4�ֱ�λ��ԭʼ��0�����£��Ҿ���1����λ��2�����ص�λ
	//�õ�������ƫ����ģ����Χ�Ŀ��Ʋ������г˻�
	float texelSize = _ShadowMapResolution / 1.0f;
	o.uv[0] = uv;
	o.uv[1] = uv + float2(0.0, texelSize) * KERNEL_SIZE;
	o.uv[2] = uv - float2(0.0, texelSize) * KERNEL_SIZE;
	o.uv[3] = uv + float2(0.0, texelSize * 2 * KERNEL_SIZE);
	o.uv[4] = uv - float2(0.0, texelSize * 2 * KERNEL_SIZE);

	return o;
}

//���ڼ������ģ������������Ԫ��
v2fBlur vert_h(a2v v)
{
	v2fBlur o;
	o.pH = UnityObjectToClipPos(v.pO);
	float2 uv = v.uv;

	//������ͬ��ֻ������x�����ģ��ƫ��
	float texelSize = 1.0f / _ShadowMapResolution;
	o.uv[0] = uv;
	o.uv[1] = uv + float2(texelSize * 1.0, 0.0) * KERNEL_SIZE;
	o.uv[2] = uv - float2(texelSize * 1.0, 0.0) * KERNEL_SIZE;
	o.uv[3] = uv + float2(texelSize * 2.0, 0.0) * KERNEL_SIZE;
	o.uv[4] = uv - float2(texelSize * 2.0, 0.0) * KERNEL_SIZE;

	return o;
}

//��ƬԪ��ɫ���н������յ�ģ�����㣬�˹�����ÿ��Pass�ж������һ�μ��㣬�����㷽ʽ��ͳһ��
float4 fragBlur(v2fBlur o) : SV_Target
{
	float weights[3] = {0.4026,0.2442,0.0545};

	float4 col = UNITY_SAMPLE_TEX2D(_ScreenSpaceShadowMapBeforeBlur, o.uv[0]);

	float3 sum = col.rgb * weights[0];

	//�Բ���������ж�Ӧ����ƫ�������Ȩ�ؼ��㣬�Եõ�ģ����Ч��
	for (int it = 1; it < 3; it++)
	{
		sum += UNITY_SAMPLE_TEX2D(_ScreenSpaceShadowMapBeforeBlur, o.uv[2 * it - 1]).rgb * weights[it];//��Ӧ1��3��Ҳ����ԭʼ���ص��Ϸ�������
		sum += UNITY_SAMPLE_TEX2D(_ScreenSpaceShadowMapBeforeBlur, o.uv[2 * it]).rgb * weights[it];//��Ӧ2��4���·�������
	}
	float4 color = float4(sum, 1.0);
	return color;
}

#endif