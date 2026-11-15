#ifndef MAX_IBL_VALIDATION_INCLUDED
#define MAX_IBL_VALIDATION_INCLUDED

// Cached GPU lighting data must not poison otherwise valid direct lighting.
// Reject invalid components before filtering and before adding indirect light.
float3 FiniteIBLColor(float3 value)
{
    return float3(isfinite(value.x) ? max(value.x, 0.0) : 0.0,
        isfinite(value.y) ? max(value.y, 0.0) : 0.0,
        isfinite(value.z) ? max(value.z, 0.0) : 0.0);
}

#endif
