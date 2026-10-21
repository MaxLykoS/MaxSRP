#define MAX_PCSS_DIAGNOSTIC_QUAD 1
#include "../Shaders/Realtime/PBRLitPass.hlsl"
float _PcssFixtureCase;

float4 ReuseFixtureFragment(v2f input) : SV_Target
{
    float right = (float)((uint)input.pH.x & 1u);
    float3 position = float3(input.pH.x * 0.001, 0, input.pH.y * 0.001);
    float3 normal = float3(0, 1, 0);
    float key = 0, valid = 1, visibility = right, width = 100;
    if (_PcssFixtureCase == 1) position.y = right * 0.1; // Parallel separate planes.
    if (_PcssFixtureCase == 2) valid = 1.0 - right; // Background neighbor.
    if (_PcssFixtureCase == 3) normal = lerp(normal, float3(1, 0, 0), right);
    if (_PcssFixtureCase == 4) key = right * 2; // Different cascades.
    if (_PcssFixtureCase == 5) key = right; // Inside/outside cascade blend band.
    if (_PcssFixtureCase == 6) width = 0.001; // Pixel footprint exceeds a shadow texel.
    if (_PcssFixtureCase == 7) visibility = 0;
    if (_PcssFixtureCase == 8) visibility = 1;
    float result = PcssFilterQuad(position, normal, input.pH.xy, key, valid, visibility, width);
    return float4(result, visibility, valid, 1);
}
