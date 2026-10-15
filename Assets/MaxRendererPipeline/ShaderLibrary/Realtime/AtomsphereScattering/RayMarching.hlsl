#ifndef MAX_ATMOSPHERE_RAYMARCHING
#define MAX_ATMOSPHERE_RAYMARCHING

#include "Helper.hlsl"
#include "Scattering.hlsl"
#include "AtmosphereParameter.hlsl"

// 查表计算任意点 p 沿着任意方向 dir 到大气层边缘的 transmittance
float3 TransmittanceToAtmosphere(in AtmosphereParameter param, float3 p, float3 dir)
{
    float bottomRadius = param.PlanetRadius;
    float topRadius = param.PlanetRadius + param.AtmosphereHeight;

    // Interior samples already lie in the LUT domain; only exterior observers
    // need a sphere interval and entry shift.
    if (length(p) > topRadius)
    {
        float entry, exit;
        if (!AtmosphereRayInterval(topRadius, p, dir, entry, exit)) return float3(1.0, 1.0, 1.0);
        p += dir * entry;
    }
    float3 upVector = normalize(p);
    float cos_theta = dot(upVector, dir);
    float r = length(p);

    float2 uv = GetTransmittanceLutUv(bottomRadius, topRadius, cos_theta, r);
    // Ray-march lookups have no meaningful screen-space derivatives.
    return UNITY_SAMPLE_TEX2D_LOD(_TransmittanceLUT, uv, 0.0).rgb;
}

// Solar rays that intersect the planet receive no direct sunlight.
// lightDir is a unit vector toward the sun; test each integration point's local horizon.
float3 TransmittanceToSun(in AtmosphereParameter param, float3 p, float3 lightDir)
{
    float r = length(p);
    float radius = param.PlanetRadius;
    if (r < radius) return 0.0;
    float mu = dot(p / r, lightDir);
    float horizonSin2 = max(0.0, (r - radius) * (r + radius)) / (r * r);
    if (mu < 0.0 && mu * mu >= horizonSin2) return 0.0;
    return TransmittanceToAtmosphere(param, p, lightDir);
}


// 积分计算任意两点 p1, p2 之间的 transmittance
float3 Transmittance(in AtmosphereParameter param, float3 p1, float3 p2)
{
    const int N_SAMPLE = 32;

    float3 dir = normalize(p2 - p1);
    float distance = length(p2 - p1);
    float ds = distance / float(N_SAMPLE);
    float3 sum = 0.0;
    float3 p = p1 + (dir * ds) * 0.5;

    for (int i = 0; i < N_SAMPLE; i++)
    {
        float h = max(0.0, length(p) - param.PlanetRadius);

        float3 scattering = RayleighCoefficient(param, h) + MieCoefficient(param, h);
        float3 absorption = OzoneAbsorption(param, h) + MieAbsorption(param, h);
        float3 extinction = scattering + absorption;

        sum += extinction * ds;
        p += dir * ds;
    }

    return exp(-sum);
}

// Flat-world surface height maps to the same local planetary column as the sky.
// This includes planetary occlusion and works independently of aerial fog.
float3 SurfaceSunTransmittance(float3 positionWS, float3 lightDir)
{
    AtmosphereParameter param = GetAtmosphereParameter();
    float3 p = AtmosphereCameraPosition(positionWS.y, param.SeaLevel, param.PlanetRadius);
    return TransmittanceToSun(param, p, lightDir);
}

// Integral of exp(-extinction * distance) over a step with constant extinction.
// Use optical thickness for the small-value test; the limit at zero extinction is ds.
float AtmosphereStepIntegral(float extinction, float ds)
{
    float tau = extinction * ds;
    if (tau < 0.01f)
        return ds * (1.0f - tau * (0.5f - tau * (1.0f / 6.0f - tau / 24.0f)));
    // The denominator guard also keeps a speculatively evaluated branch finite at tau=0.
    return ds * (1.0f - exp(-tau)) / max(tau, 0.01f);
}

float3 AtmosphereStepIntegral(float3 extinction, float ds)
{
    return float3(AtmosphereStepIntegral(extinction.x, ds),
        AtmosphereStepIntegral(extinction.y, ds), AtmosphereStepIntegral(extinction.z, ds));
}
// 积分计算多重散射查找表
float3 IntegralMultiScattering(
    in AtmosphereParameter param, float3 samplePoint, float3 lightDir)
{
    const int N_DIRECTION = 64;
    const int N_SAMPLE = 32;
    float3 RandomSphereSamples[64] = {
        float3(-0.7838,-0.620933,0.00996137),
        float3(0.106751,0.965982,0.235549),
        float3(-0.215177,-0.687115,-0.693954),
        float3(0.318002,0.0640084,-0.945927),
        float3(0.357396,0.555673,0.750664),
        float3(0.866397,-0.19756,0.458613),
        float3(0.130216,0.232736,-0.963783),
        float3(-0.00174431,0.376657,0.926351),
        float3(0.663478,0.704806,-0.251089),
        float3(0.0327851,0.110534,-0.993331),
        float3(0.0561973,0.0234288,0.998145),
        float3(0.0905264,-0.169771,0.981317),
        float3(0.26694,0.95222,-0.148393),
        float3(-0.812874,-0.559051,-0.163393),
        float3(-0.323378,-0.25855,-0.910263),
        float3(-0.1333,0.591356,-0.795317),
        float3(0.480876,0.408711,0.775702),
        float3(-0.332263,-0.533895,-0.777533),
        float3(-0.0392473,-0.704457,-0.708661),
        float3(0.427015,0.239811,0.871865),
        float3(-0.416624,-0.563856,0.713085),
        float3(0.12793,0.334479,-0.933679),
        float3(-0.0343373,-0.160593,-0.986423),
        float3(0.580614,0.0692947,0.811225),
        float3(-0.459187,0.43944,0.772036),
        float3(0.215474,-0.539436,-0.81399),
        float3(-0.378969,-0.31988,-0.868366),
        float3(-0.279978,-0.0109692,0.959944),
        float3(0.692547,0.690058,0.210234),
        float3(0.53227,-0.123044,-0.837585),
        float3(-0.772313,-0.283334,-0.568555),
        float3(-0.0311218,0.995988,-0.0838977),
        float3(-0.366931,-0.276531,-0.888196),
        float3(0.488778,0.367878,-0.791051),
        float3(-0.885561,-0.453445,0.100842),
        float3(0.71656,0.443635,0.538265),
        float3(0.645383,-0.152576,-0.748466),
        float3(-0.171259,0.91907,0.354939),
        float3(-0.0031122,0.9457,0.325026),
        float3(0.731503,0.623089,-0.276881),
        float3(-0.91466,0.186904,0.358419),
        float3(0.15595,0.828193,-0.538309),
        float3(0.175396,0.584732,0.792038),
        float3(-0.0838381,-0.943461,0.320707),
        float3(0.305876,0.727604,0.614029),
        float3(0.754642,-0.197903,-0.62558),
        float3(0.217255,-0.0177771,-0.975953),
        float3(0.140412,-0.844826,0.516287),
        float3(-0.549042,0.574859,-0.606705),
        float3(0.570057,0.17459,0.802841),
        float3(-0.0330304,0.775077,0.631003),
        float3(-0.938091,0.138937,0.317304),
        float3(0.483197,-0.726405,-0.48873),
        float3(0.485263,0.52926,0.695991),
        float3(0.224189,0.742282,-0.631472),
        float3(-0.322429,0.662214,-0.676396),
        float3(0.625577,-0.12711,0.769738),
        float3(-0.714032,-0.584461,-0.385439),
        float3(-0.0652053,-0.892579,-0.446151),
        float3(0.408421,-0.912487,0.0236566),
        float3(0.0900381,0.319983,0.943135),
        float3(-0.708553,0.483646,0.513847),
        float3(0.803855,-0.0902273,0.587942),
        float3(-0.0555802,-0.374602,-0.925519),
    };
    const float uniform_phase = 1.0 / (4.0 * PI);
    const float sphereSolidAngle = 4.0 * PI / float(N_DIRECTION);

    float3 G_2 = float3(0, 0, 0);
    float3 f_ms = float3(0, 0, 0);

    for (int i = 0; i < N_DIRECTION; i++)
    {
        // 光线和大气层求交
        float3 viewDir = RandomSphereSamples[i];
        float dis = RayIntersectSphere(float3(0, 0, 0), param.PlanetRadius + param.AtmosphereHeight, samplePoint, viewDir);
        float d = RayIntersectGroundSphere(param.PlanetRadius, samplePoint, viewDir);
        if (dis <= 0.0) continue;
        if (d >= 0.0) dis = min(dis, d);
        if (dis <= 0.0) continue;
        float ds = dis / float(N_SAMPLE);

        float3 p = samplePoint + (viewDir * ds) * 0.5;
        float3 opticalDepth = float3(0, 0, 0);

        for (int j = 0; j < N_SAMPLE; j++)
        {
            float h = max(0.0, length(p) - param.PlanetRadius);
            float3 sigma_s = RayleighCoefficient(param, h) + MieCoefficient(param, h);  // scattering
            float3 sigma_a = OzoneAbsorption(param, h) + MieAbsorption(param, h);       // absorption
            float3 sigma_t = sigma_s + sigma_a;                                         // extinction

            float3 t1 = TransmittanceToSun(param, p, lightDir);
            float3 s = Scattering(param, p, lightDir, viewDir);
            float3 stepWeight = exp(-opticalDepth) * AtmosphereStepIntegral(sigma_t, ds);

            // 用 1.0 代替太阳光颜色, 该变量在后续的计算中乘上去
            G_2 += t1 * s * stepWeight * uniform_phase * 1.0;
            f_ms += stepWeight * sigma_s * uniform_phase;

            opticalDepth += sigma_t * ds;
            p += viewDir * ds;
        }
    }

    G_2 *= sphereSolidAngle;
    f_ms *= sphereSolidAngle;
    return G_2 * (1.0 / (1.0 - f_ms));
}

// 读取多重散射查找表
float3 GetMultiScattering(in AtmosphereParameter param, float3 p, float3 lightDir)
{
    float h = max(0.0, length(p) - param.PlanetRadius);
    float3 sigma_s = RayleighCoefficient(param, h) + MieCoefficient(param, h);

    float cosSunZenithAngle = dot(normalize(p), lightDir);
    float2 uv = float2(cosSunZenithAngle * 0.5 + 0.5, h / param.AtmosphereHeight);
    float3 G_ALL = UNITY_SAMPLE_TEX2D_LOD(_MultiscatteringLUT, uv, 0.0).rgb;

    return G_ALL * sigma_s;
}

// 计算天空颜色
float3 GetAtmosphereSegment(
    in AtmosphereParameter param, float3 eyePos, float3 viewDir, float3 lightDir, float maxDis,
    out float3 transmittance)
{
    transmittance = float3(1.0, 1.0, 1.0);
    const int N_SAMPLE = 32;
    float3 color = float3(0, 0, 0);

    // 光线和大气层, 星球求交
    float entry, exit;
    if (!AtmosphereRayInterval(param.PlanetRadius + param.AtmosphereHeight,
        eyePos, viewDir, entry, exit)) return color;
    float ground = RayIntersectGroundSphere(param.PlanetRadius, eyePos, viewDir);
    if (ground >= 0.0) exit = min(exit, ground);
    // maxDis is measured from the original observer, including the vacuum segment.
    if (maxDis >= 0.0) exit = min(exit, maxDis);
    float dis = exit - entry;
    if (dis <= 0.0) return color;
    float ds = dis / float(N_SAMPLE);
    float3 p = eyePos + viewDir * (entry + ds * 0.5);
    float3 sunLuminance = param.SolarIrradiance;
    float3 opticalDepth = float3(0, 0, 0);

    for (int i = 0; i < N_SAMPLE; i++)
    {
        // 积累沿途的湮灭系数
        float h = max(0.0, length(p) - param.PlanetRadius);
        float3 extinction = RayleighCoefficient(param, h) + MieCoefficient(param, h) +  // scattering
            OzoneAbsorption(param, h) + MieAbsorption(param, h);        // absorption

        float3 t1 = TransmittanceToSun(param, p, lightDir);
        float3 s = Scattering(param, p, lightDir, viewDir);
        float3 stepWeight = exp(-opticalDepth) * AtmosphereStepIntegral(extinction, ds);

        // 单次散射
        float3 inScattering = t1 * s * stepWeight * sunLuminance;
        color += inScattering;

        // 多重散射
        float3 multiScattering = GetMultiScattering(param, p, lightDir);
        color += multiScattering * stepWeight * sunLuminance;

        opticalDepth += extinction * ds;
        p += viewDir * ds;
    }

    transmittance = exp(-opticalDepth);
    return color;
}

float3 GetSkyView(
    in AtmosphereParameter param, float3 eyePos, float3 viewDir, float3 lightDir, float maxDis)
{
    float3 transmittance;
    return GetAtmosphereSegment(param, eyePos, viewDir, lightDir, maxDis, transmittance);
}

#endif