#ifndef MAX_ATMOSPHERE_SCATTERING
#define MAX_ATMOSPHERE_SCATTERING

#ifndef PI
#define PI 3.14159265359
#endif

struct AtmosphereParameter
{
    float SeaLevel;
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

// A uniform solar disk integrates to normal illuminance E = L*pi*sin(radius)^2.
// SolarIrradiance and this radiance share the fixed 100000 photometric unit scale.
float3 SolarDiskRadiance(in AtmosphereParameter param)
{
    float radius = max(param.SunDiskAngle, 0.001) * (PI / 180.0);
    float sine = sin(radius);
    return param.SolarIrradiance / max(PI * sine * sine, 1e-10);
}
float3 RayleighCoefficient(in AtmosphereParameter param, float h)
{
    const float3 sigma = float3(5.802, 13.558, 33.1) * 1e-6;
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
    const float3 sigma = (3.996 * 1e-6).xxx;
    float H_M = param.MieScatteringScalarHeight;
    float rho_h = exp(-(h / H_M));
    return sigma * rho_h * max(param.MieScatteringScale, 0.0);
}

float MiePhase(in AtmosphereParameter param, float cos_theta)
{
    float g = param.MieAnisotropy;

    float a = 3.0 / (8.0 * PI);
    float b = (1.0 - g * g) / (2.0 + g * g);
    float c = 1.0 + cos_theta * cos_theta;
    float d = pow(1.0 + g * g - 2 * g * cos_theta, 1.5);

    return a * b * (c / d);
}

// Mie scale changes aerosol density: scattering and absorption scale together.
float3 MieAbsorption(in AtmosphereParameter param, float h)
{
    // Absorption = extinction (4.440) - scattering (3.996), in 1/metre.
    const float3 sigma = ((4.440 - 3.996) * 1e-6).xxx;
    float H_M = param.MieScatteringScalarHeight;
    float rho_h = exp(-(h / H_M));
    return sigma * rho_h * max(param.MieScatteringScale, 0.0);
}

float3 OzoneAbsorption(in AtmosphereParameter param, float h)
{
#define sigma_lambda (float3(0.650f, 1.881f, 0.085f)) * 1e-6
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