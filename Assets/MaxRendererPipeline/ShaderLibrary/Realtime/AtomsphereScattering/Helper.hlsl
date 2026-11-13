#ifndef MAX_ATMOSPHERE_HELPER
#define MAX_ATMOSPHERE_HELPER

#ifndef PI
#define PI 3.14159265359
#endif
// Unity world positions and cloud geometry remain metres; atmosphere transport is km.
#define ATMOSPHERE_METRES_TO_KM 0.001
#define ATMOSPHERE_KM_TO_METRES 1000.0
#define ATMOSPHERE_MIN_ALTITUDE_KM 0.001

// Legacy MaxSRP uniform world-space latitude/longitude map, preserved for reconstruction.
// Its generator used UVToViewDir(uv), all consumers used ViewDirToUV(direction),
// the horizon was at v=0.5, zenith at v=1, and the azimuth seam used repeat wrap.
// To restore that route, replace the paired SkyViewLut* helpers in generation,
// camera sky, environment IBL and both cloud ambient lookups together.
#if 0
float3 UVToViewDir(float2 uv)
{
    float theta = (1.0 - uv.y) * PI;
    float phi = (uv.x * 2 - 1) * PI;

    float x = sin(theta) * cos(phi);
    float z = sin(theta) * sin(phi);
    float y = cos(theta);

    return float3(x, y, z);
}

float2 ViewDirToUV(float3 v)
{
    float2 uv = float2(atan2(v.z, v.x), asin(v.y));
    uv /= float2(2.0 * PI, PI);
    uv += float2(0.5, 0.5);

    return uv;
}
#endif

// UE SkyView uses a two-hemisphere latitude map with quadratic concentration
// around the actual spherical horizon, plus its unit/sub-UV edge convention.
// Helpers evaluate directions in a y-up LUT reference frame. Camera generation
// and lookup rotate with the same horizontal view frame, while environment IBL
// uses the fixed world frame. Both retain exactly the same nonlinear latitude map.
float2 SkyViewLutUnitToSubUV(float2 uv, float4 sizeAndInvSize)
{
    return (uv + 0.5 * sizeAndInvSize.zw) *
        (sizeAndInvSize.xy / (sizeAndInvSize.xy + 1.0));
}

float2 SkyViewLutSubToUnitUV(float2 uv, float4 sizeAndInvSize)
{
    return (uv - 0.5 * sizeAndInvSize.zw) *
        (sizeAndInvSize.xy / (sizeAndInvSize.xy - 1.0));
}

void SkyViewLutHorizonAngles(float viewHeight, float bottomRadius,
    out float beta, out float zenithHorizonAngle)
{
    float height = max(viewHeight, bottomRadius);
    // Factor the difference of squares to retain metre-scale altitude in km.
    float horizonDistance = sqrt(max(0.0, (height - bottomRadius) * (height + bottomRadius)));
    beta = acos(clamp(horizonDistance / max(height, 1e-6), 0.0, 1.0));
    zenithHorizonAngle = PI - beta;
}

float3 SkyViewLutUVToLocalDir(float2 uv, float viewHeight,
    float bottomRadius, float4 sizeAndInvSize)
{
    uv = saturate(SkyViewLutSubToUnitUV(uv, sizeAndInvSize));
    float beta, zenithHorizonAngle;
    SkyViewLutHorizonAngles(viewHeight, bottomRadius, beta, zenithHorizonAngle);
    float zenithAngle;
    if (uv.y < 0.5)
    {
        float coord = 1.0 - 2.0 * uv.y;
        zenithAngle = zenithHorizonAngle * (1.0 - coord * coord);
    }
    else
    {
        float coord = 2.0 * uv.y - 1.0;
        zenithAngle = zenithHorizonAngle + beta * coord * coord;
    }
    float longitude = uv.x * (2.0 * PI);
    float sinZenith = sin(zenithAngle);
    return float3(sinZenith * cos(longitude), cos(zenithAngle), sinZenith * sin(longitude));
}

float2 SkyViewLutLocalDirToUV(float3 direction, float viewHeight,
    float bottomRadius, float4 sizeAndInvSize)
{
    float beta, zenithHorizonAngle;
    SkyViewLutHorizonAngles(viewHeight, bottomRadius, beta, zenithHorizonAngle);
    float zenithAngle = acos(clamp(direction.y, -1.0, 1.0));
    float2 uv;
    if (zenithAngle < zenithHorizonAngle)
    {
        float coord = 1.0 - zenithAngle / max(zenithHorizonAngle, 1e-6);
        uv.y = (1.0 - sqrt(saturate(coord))) * 0.5;
    }
    else
    {
        float coord = (zenithAngle - zenithHorizonAngle) / max(beta, 1e-6);
        uv.y = sqrt(saturate(coord)) * 0.5 + 0.5;
    }
    // Longitude is arbitrary at the poles. Avoid atan2(0,0), including explicit
    // zenith ambient queries in the cloud and environment paths.
    uv.x = dot(direction.xz, direction.xz) < 1e-12 ? 0.5 :
        (atan2(-direction.z, -direction.x) + PI) / (2.0 * PI);
    return SkyViewLutUnitToSubUV(uv, sizeAndInvSize);
}

// Both the camera LUT and the environment LUT use the selected profile's size.
// The environment observer differs from the camera and must retain its own height.
float4 _AtmosphereSkyViewSizeAndInvSize;
float _AtmosphereEnvironmentSkyViewHeight;

// All arguments and the returned planetary position are kilometres. Near Earth
// radius a float ULP is about 0.49 metres; retain the one-metre altitude floor.
// Apply the same policy to sky LUT generation, the solar disk and aerial LUTs.
float3 AtmosphereCameraPosition(float cameraY, float seaLevel, float planetRadius)
{
    float altitude = max(cameraY - seaLevel, ATMOSPHERE_MIN_ALTITUDE_KM);
    return float3(0.0, planetRadius + altitude, 0.0);
}

float RayIntersectSphere(float3 center, float radius, float3 rayStart, float3 rayDir)
{
    float3 offset = rayStart - center;
    float r = length(offset);
    float a = dot(rayDir, rayDir);
    if (a <= 0.0) return -1.0;
    float b = dot(offset, rayDir);
    // Factor the difference of squares before multiplying: r*r - radius*radius
    // loses the entire near-ground distance at planetary scales.
    float c = (r - radius) * (r + radius);
    float discriminant = b * b - a * c;
    if (discriminant < 0.0) return -1.0;

    // Compute the root without subtracting nearly equal values, then obtain
    // the other root from their product c/a. Check misses before sqrt.
    float root = sqrt(discriminant);
    float q = -b - (b >= 0.0 ? root : -root);
    if (q == 0.0) return 0.0;
    float t0 = q / a;
    float t1 = c / q;
    float nearHit = min(t0, t1);
    float farHit = max(t0, t1);
    return nearHit >= 0.0 ? nearHit : (farHit >= 0.0 ? farHit : -1.0);
}

// A solid ground boundary differs from an atmosphere boundary: surface rays
// heading up (or tangent) are unblocked, heading down is an immediate hit.
// Starts below the surface are blocked rather than tracing through the planet.
float RayIntersectGroundSphere(float radius, float3 rayStart, float3 rayDir)
{
    float r = length(rayStart);
    if (r < radius) return 0.0;
    if (r == radius)
        return dot(rayStart, rayDir) < 0.0 ? 0.0 : -1.0;
    return RayIntersectSphere(float3(0.0, 0.0, 0.0), radius, rayStart, rayDir);
}

// Positive ray interval inside a sphere. Unlike a nearest-hit query this
// preserves both entry and exit for observers outside the atmosphere.
bool AtmosphereRayInterval(float radius, float3 rayStart, float3 rayDir,
    out float entry, out float exit)
{
    entry = 0.0;
    exit = 0.0;
    float a = dot(rayDir, rayDir);
    if (a <= 0.0) return false;
    float r = length(rayStart);
    float b = dot(rayStart, rayDir);
    float c = (r - radius) * (r + radius);
    float discriminant = b * b - a * c;
    if (discriminant <= 0.0) return false; // A tangent has no integration length.
    float root = sqrt(discriminant);
    float q = -b - (b >= 0.0 ? root : -root);
    if (q == 0.0) return false;
    float t0 = q / a;
    float t1 = c / q;
    entry = max(0.0, min(t0, t1));
    exit = max(t0, t1);
    return exit > entry;
}

void UvToTransmittanceLutParams(float bottomRadius, float topRadius, float2 uv, out float mu, out float r)
{
    float x_mu = uv.x;
    float x_r = uv.y;

    float H = sqrt(max(0.0f, topRadius * topRadius - bottomRadius * bottomRadius));
    float rho = H * x_r;
    r = sqrt(max(0.0f, rho * rho + bottomRadius * bottomRadius));

    float d_min = topRadius - r;
    float d_max = rho + H;
    float d = d_min + x_mu * (d_max - d_min);
    mu = d == 0.0f ? 1.0f : (H * H - rho * rho - d * d) / (2.0f * r * d);
    mu = clamp(mu, -1.0f, 1.0f);
}

float2 GetTransmittanceLutUv(float bottomRadius, float topRadius, float mu, float r)
{
    r = clamp(r, bottomRadius, topRadius);
    mu = clamp(mu, -1.0, 1.0);
    float H = sqrt(max(0.0f, topRadius * topRadius - bottomRadius * bottomRadius));
    float rho = sqrt(max(0.0f, r * r - bottomRadius * bottomRadius));

    float discriminant = r * r * (mu * mu - 1.0f) + topRadius * topRadius;
    float d = max(0.0f, (-r * mu + sqrt(max(0.0, discriminant))));

    float d_min = topRadius - r;
    float d_max = rho + H;

    float x_mu = (d - d_min) / (d_max - d_min);
    float x_r = rho / H;

    return float2(x_mu, x_r);
}
#endif