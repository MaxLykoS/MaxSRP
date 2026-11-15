#ifndef MAX_LIGHTING_DIAGNOSTICS_INCLUDED
#define MAX_LIGHTING_DIAGNOSTICS_INCLUDED

// Editor comparison only. Zero preserves normal production rendering.
// 1..5 retain the existing direct / sky IBL baseline modes.
// 6 = baked artificial diffuse; 7 = emission; 8 = scalar sky visibility.
// 9 = decoded native irradiance E; 10 = directional E with the unmapped mesh normal;
// 11 = directional E with the mapped normal; 12 = baked material response
// with the unmapped mesh normal. 9..11 omit receiver albedo and BRDF weight.
// 9..11 keep E * 0.01; 6/12 add the final Lambert 1/pi and receiver material.
float _MaxLightingDiagnosticMode;

bool MaxIsBakedLightingDiagnosticMode()
{
    return _MaxLightingDiagnosticMode == 6.0 ||
        (_MaxLightingDiagnosticMode >= 9.0 && _MaxLightingDiagnosticMode <= 12.0);
}

bool MaxIsGBufferLightingDiagnosticMode()
{
    return MaxIsBakedLightingDiagnosticMode() || _MaxLightingDiagnosticMode == 7.0;
}

float3 MaxDiagnosticGBufferLighting(float3 bakedDiffuse, float3 emission)
{
    if (MaxIsBakedLightingDiagnosticMode()) return bakedDiffuse;
    if (_MaxLightingDiagnosticMode == 7.0) return emission;
    return bakedDiffuse + emission;
}

#endif
