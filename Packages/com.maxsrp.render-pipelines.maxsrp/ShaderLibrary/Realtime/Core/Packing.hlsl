#ifndef MAX_PACKING_INCLUDED
#define MAX_PACKING_INCLUDED

// GBuffer1.a is two-bit UNorm. 1 allows camera-only reprojection.
// 1/3 tags a geometric receiver normal in GBuffer3.rgb and requires an
// explicit terrain motion vector. The velocity itself lives in MotionVectors.
// GBuffer2.rg stores sky visibility and lightmap validity. GBuffer3.a
// stores material AO, with negative values reserved for terrain debug views.
static const float MaxGBufferReceiverNormalTag = 1.0 / 3.0;

// UE 5.8.4 Common.ush, EncodeVelocityToTexture / DecodeVelocityFromTexture.
// Store signed NDC displacement in the valid 16-bit range; zero is the clear value.
float _MaxVelocityGamma;
static const float MaxVelocityScale = 0.499 * 0.5;
static const float MaxVelocityBias = 32767.0 / 65535.0;

#if defined(MAX_TAA_VELOCITY_UINT)
#define MAX_VELOCITY_TARGET uint2
#else
#define MAX_VELOCITY_TARGET float2
#endif

MAX_VELOCITY_TARGET MaxWriteVelocity(float4 current, float4 previous, bool moved, bool reject)
{
    // UE Gen4 considers X=0 missing. Preserve MaxSRP's existing reject and
    // covered-stationary states in Y within that reserved invalid range.
    if (reject || !moved)
    {
#if defined(MAX_TAA_VELOCITY_UINT)
        return uint2(0, reject ? 65535u : 32768u);
#else
        return float2(0, reject ? 1.0 : 0.5);
#endif
    }
    float2 velocity = current.xy / current.w - previous.xy / previous.w;
    if (_MaxVelocityGamma > 0.5)
        velocity = sign(velocity) * sqrt(abs(velocity)) * sqrt(2.0);
    float2 encoded = floor(saturate(velocity * MaxVelocityScale + MaxVelocityBias) * 65535.0 + 0.5);
    encoded.x = max(encoded.x, 1.0); // Keep the reserved range even at saturation.
#if defined(MAX_TAA_VELOCITY_UINT)
    return uint2(encoded);
#else
    return encoded / 65535.0;
#endif
}

float2 MaxDecodeVelocityUV(float2 encoded)
{
    float2 velocity = (encoded - MaxVelocityBias) / MaxVelocityScale;
    if (_MaxVelocityGamma > 0.5)
        velocity = 0.5 * velocity * abs(velocity);
#if UNITY_UV_STARTS_AT_TOP
    velocity.y = -velocity.y;
#endif
    return 0.5 * velocity;
}

// Match Unity's imported RGB, AG/DXT5nm, BC5 and ASTC normal encodings.
float3 DecodeNormalFromTexture(float4 packedNormal)
{
#if defined(UNITY_NO_DXT5nm)
    return packedNormal.xyz * 2.0 - 1.0;
#else
#if defined(UNITY_ASTC_NORMALMAP_ENCODING)
    float2 xy = packedNormal.wy * 2.0 - 1.0;
#else
    packedNormal.x *= packedNormal.w;
    float2 xy = packedNormal.xy * 2.0 - 1.0;
#endif
    return float3(xy, sqrt(max(0.0, 1.0 - dot(xy, xy))));
#endif
}

#endif
