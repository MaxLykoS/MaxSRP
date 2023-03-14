#define K_PI                    3.1415926535f
#define K_HALF_PI               1.5707963267f
#define K_QUARTER_PI            0.7853981633f
#define K_TWO_PI                6.283185307f
#define K_T_MAX                 10000
#define K_RAY_ORIGIN_PUSH_OFF   0.002

// RayFlags
#define RAY_FLAG_NONE 0x00
#define RAY_FLAG_FORCE_OPAQUE 0x01
#define RAY_FLAG_FORCE_NON_OPAQUE 0x02
#define RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH 0x04
#define RAY_FLAG_SKIP_CLOSEST_HIT_SHADER 0x08
#define RAY_FLAG_CULL_BACK_FACING_TRIANGLES 0x10
#define RAY_FLAG_CULL_FRONT_FACING_TRIANGLES 0x20
#define RAY_FLAG_CULL_OPAQUE 0x40
#define RAY_FLAG_CULL_NON_OPAQUE 0x80
#define RAY_FLAG_SKIP_TRIANGLES 0x100
#define RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES 0x200

uint WangHash(inout uint seed)
{
    seed = (seed ^ 61) ^ (seed >> 16);
    seed *= 9;
    seed = seed ^ (seed >> 4);
    seed *= 0x27d4eb2d;
    seed = seed ^ (seed >> 15);
    return seed;
}

float RandomFloat01(inout uint seed)
{
    return float(WangHash(seed)) / float(0xFFFFFFFF);
}

float3 RandomUnitVector(inout uint state)
{
    float z = RandomFloat01(state) * 2.0f - 1.0f;
    float a = RandomFloat01(state) * K_TWO_PI;
    float r = sqrt(1.0f - z * z);
    float x = r * cos(a);
    float y = r * sin(a);
    return float3(x, y, z);
}

float FresnelReflectAmountOpaque(float n1, float n2, float3 incident, float3 normal)
{
    // Schlick's aproximation
    float r0 = (n1 - n2) / (n1 + n2);
    r0 *= r0;
    float cosX = -dot(normal, incident);
    float x = 1.0 - cosX;
    float xx = x * x;
    return r0 + (1.0 - r0) * xx * xx * x;
}

// n1表示空气下的折射率 n2表示表面材料的折射率
float FresnelReflectAmountTransparent(float n1, float n2, float3 incident, float3 normal)
{
    #define f90 1.0f
    #define f0 0.02f
    // Schlick's aproximation
    float r0 = (n1 - n2) / (n1 + n2);
    r0 *= r0;
    float cosX = -dot(normal, incident);

    if (n1 > n2)
    {
        float n = n1 / n2;
        float sinT2 = n * n * (1.0 - cosX * cosX);
        // Total internal reflection
        if (sinT2 >= 1.0f)
            return f90;
        cosX = sqrt(1.0f - sinT2);
    }

    float x = 1.0f - cosX;
    float xx = x * x;
    float ret = r0 + (1.0f - r0) * xx * xx * x;
    return lerp(f0, f90, ret);
}

uint GenRandomUint(uint2 seed, uint disturbance)
{
    return uint(uint(seed.x) * uint(1973) + uint(seed.y) * uint(9277) + uint(disturbance) * uint(26699)) | uint(1);
}