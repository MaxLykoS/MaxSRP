#ifndef MAX_VIEW_DIRECTION_INCLUDED
#define MAX_VIEW_DIRECTION_INCLUDED

#include "CommonInput.hlsl"

float3 GetSurfaceViewDirection(float3 positionWS)
{
    if (unity_OrthoParams.w > 0.5)
        return normalize(mul((float3x3)unity_MatrixInvV, float3(0.0, 0.0, 1.0)));
    return normalize(_WorldSpaceCameraPos - positionWS);
}

#endif
