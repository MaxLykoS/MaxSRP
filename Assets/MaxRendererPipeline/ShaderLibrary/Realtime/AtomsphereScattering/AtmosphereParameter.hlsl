#ifndef MAX_ATMOSPHERE_PARAMETER
#define MAX_ATMOSPHERE_PARAMETER

float _SeaLevel;
float _PlanetRadius;
float _AtmosphereHeight;
float _SunLightIntensity;
float3 _SunLightColor;
float _SunDiskAngle;
float _RayleighScatteringScale;
float _RayleighScatteringScalarHeight;
float _MieScatteringScale;
float _MieAnisotropy;
float _MieScatteringScalarHeight;
float _OzoneAbsorptionScale;
float _OzoneLevelCenterHeight;
float _OzoneLevelWidth;

UNITY_DECLARE_TEX2D(_TransmittanceLUT);
UNITY_DECLARE_TEX2D(_MultiscatteringLUT);
UNITY_DECLARE_TEX2D(_SkyviewLUT);

#include "Scattering.hlsl"

AtmosphereParameter GetAtmosphereParameter()
{
    AtmosphereParameter param;

    param.SeaLevel = _SeaLevel;
    param.PlanetRadius = _PlanetRadius;
    param.AtmosphereHeight = _AtmosphereHeight;
    param.SunLightIntensity = _SunLightIntensity;
    param.SunLightColor = _SunLightColor;
    param.SunDiskAngle = _SunDiskAngle;
    param.RayleighScatteringScale = _RayleighScatteringScale;
    param.RayleighScatteringScalarHeight = _RayleighScatteringScalarHeight;
    param.MieScatteringScale = _MieScatteringScale;
    param.MieAnisotropy = _MieAnisotropy;
    param.MieScatteringScalarHeight = _MieScatteringScalarHeight;
    param.OzoneAbsorptionScale = _OzoneAbsorptionScale;
    param.OzoneLevelCenterHeight = _OzoneLevelCenterHeight;
    param.OzoneLevelWidth = _OzoneLevelWidth;

    return param;
}
#endif