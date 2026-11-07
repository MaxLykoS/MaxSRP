#ifndef MAXSRP_DDGI_PROBE_TRACE_PAYLOAD_INCLUDED
#define MAXSRP_DDGI_PROBE_TRACE_PAYLOAD_INCLUDED

static const float ProbeTraceMissDistance = 1e27f;

struct ProbeTracePayload
{
    float3 normal;
    float3 geometricNormal;
    float signedDistance;
};

#endif
