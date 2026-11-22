#ifndef MAX_SKYVIEW_PASS_INCLUDED
#define MAX_SKYVIEW_PASS_INCLUDED


#include "../../../ShaderLibrary/Realtime/Core/SpaceTransform.hlsl"
#include "../../../ShaderLibrary/Realtime/Lighting/LightInput.hlsl"
#include "../../../ShaderLibrary/Realtime/AtmosphereScattering/Helper.hlsl"
#include "../../../ShaderLibrary/Realtime/AtmosphereScattering/Scattering.hlsl"
#include "../../../ShaderLibrary/Realtime/AtmosphereScattering/AtmosphereParameter.hlsl"
#include "../../../ShaderLibrary/Realtime/AtmosphereScattering/RayMarching.hlsl"

float _AtmosphereCameraY;
float4 _AtmosphereSkyViewRight;
float4 _AtmosphereSkyViewForward;

struct appdata
{
    float4 vertex : POSITION;
    float2 uv : TEXCOORD0;
};

struct v2f
{
    float2 uv : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

v2f vert(appdata v)
{
    v2f o;
    o.vertex = UnityObjectToClipPos(v.vertex);
    o.uv = v.uv;
    return o;
}

float4 frag(v2f i) : SV_Target
{
	AtmosphereParameter param = GetAtmosphereParameter();

	float4 color = float4(0, 0, 0, 1);
	float2 uv = i.uv;
	// Legacy generator: float3 viewDir = UVToViewDir(uv);
	float viewHeight = AtmosphereCameraPosition(_AtmosphereCameraY, param.SeaLevel, param.PlanetRadius).y;
	float3 localViewDir = SkyViewLutUVToLocalDir(uv, viewHeight, param.PlanetRadius, _AtmosphereSkyViewSizeAndInvSize);
    float3 viewDir = _AtmosphereSkyViewRight.xyz * localViewDir.x + float3(0, 1, 0) * localViewDir.y +
        _AtmosphereSkyViewForward.xyz * localViewDir.z;

	float3 lightDir = _AtmosphereSunDirection;

	float3 eyePos = AtmosphereCameraPosition(_AtmosphereCameraY, param.SeaLevel, param.PlanetRadius);

	// Both sources are integrated together; the environment uses this same
    // scattering-only path and deliberately excludes the visible light disks.
	color.rgb = GetSkyView(
		param, eyePos, viewDir, lightDir, -1.0f);
	return color;
}

#endif
