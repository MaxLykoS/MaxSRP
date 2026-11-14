#ifndef MAX_ATMOSPHERE_SCATTERING
#define MAX_ATMOSPHERE_SCATTERING

#ifndef PI
#define PI 3.14159265359
#endif

struct AtmosphereParameter
{
    // All lengths are km; scattering / absorption coefficients are per km.
    float SeaLevel;
    float3 GroundAlbedo;
    float PlanetRadius;
    float AtmosphereHeight;
    float3 SolarIrradiance;
    float SunDiskAngle;
    float RayleighScatteringScale;
    float RayleighScatteringScalarHeight;
    float MieScatteringScale;
    float MieAnisotropy;
    float MieScatteringScalarHeight;
    float OzoneAbsorptionScale;
    float OzoneLevelCenterHeight;
    float OzoneLevelWidth;
};

// ------------------------------------------------------------------------- //

// Match UE's illuminance / disk solid angle. The visible disk's outer edge is
// softened separately, without renormalizing it. Keep the fixed HDR unit scale.
float3 SolarDiskRadiance(in AtmosphereParameter param)
{
    float radius = max(param.SunDiskAngle, 0.001) * (PI / 180.0);
    // 2*pi*(1-cos(radius)) = 4*pi*sin(radius/2)^2. The latter avoids
    // subtracting nearly equal floats at the Sun's small angular radius.
    float halfSine = sin(0.5 * radius);
    return param.SolarIrradiance / max(4.0 * PI * halfSine * halfSine, 1e-10);
}
float3 RayleighCoefficient(in AtmosphereParameter param, float h)
{
    const float3 sigma = float3(5.802, 13.558, 33.1) * 1e-3;
    float H_R = param.RayleighScatteringScalarHeight;
    float rho_h = exp(-(h / H_R));
    return sigma * rho_h * max(param.RayleighScatteringScale, 0.0);
}

float RayleiPhase(in AtmosphereParameter param, float cos_theta)
{
    return (3.0 / (16.0 * PI)) * (1.0 + cos_theta * cos_theta);
}

float3 MieCoefficient(in AtmosphereParameter param, float h)
{
    const float3 sigma = (3.996 * 1e-3).xxx;
    float H_M = param.MieScatteringScalarHeight;
    float rho_h = exp(-(h / H_M));
    return sigma * rho_h * max(param.MieScatteringScale, 0.0);
}

float MiePhase(in AtmosphereParameter param, float cos_theta)
{
    float g = param.MieAnisotropy;

#if defined(MAX_ATMOSPHERE_MOBILE)
    // Henyey-Greenstein, using cos_theta between the view direction and direction
    // toward the light. This equals UE's HG(G, -cos_theta) direction convention.
    float denominator = 1.0 + g * g - 2.0 * g * cos_theta;
    return (1.0 - g * g) / (4.0 * PI * denominator * sqrt(denominator));
#else
    // PC preserves the existing Cornette-Shanks phase function.
    float a = 3.0 / (8.0 * PI);
    float b = (1.0 - g * g) / (2.0 + g * g);
    float c = 1.0 + cos_theta * cos_theta;
    float d = pow(1.0 + g * g - 2 * g * cos_theta, 1.5);

    return a * b * (c / d);
#endif
}

// Mie scale changes aerosol density: scattering and absorption scale together.
float3 MieAbsorption(in AtmosphereParameter param, float h)
{
    // Absorption = extinction (4.440) - scattering (3.996), in 1/kilometre.
    const float3 sigma = ((4.440 - 3.996) * 1e-3).xxx;
    float H_M = param.MieScatteringScalarHeight;
    float rho_h = exp(-(h / H_M));
    return sigma * rho_h * max(param.MieScatteringScale, 0.0);
}

float3 OzoneAbsorption(in AtmosphereParameter param, float h)
{
#define sigma_lambda (float3(0.650f, 1.881f, 0.085f)) * 1e-3
    float center = param.OzoneLevelCenterHeight;
    float width = param.OzoneLevelWidth;
    float rho = max(0, 1.0 - (abs(h - center) / width));
    return sigma_lambda * rho * max(param.OzoneAbsorptionScale, 0.0);
}

// ------------------------------------------------------------------------- //

float3 Scattering(in AtmosphereParameter param, float3 p, float3 lightDir, float3 viewDir)
{
    float cos_theta = dot(lightDir, viewDir);

    float h = max(0.0, length(p) - param.PlanetRadius);
    float3 rayleigh = RayleighCoefficient(param, h) * RayleiPhase(param, cos_theta);
    float3 mie = MieCoefficient(param, h) * MiePhase(param, cos_theta);

    return rayleigh + mie;
}

#endif
