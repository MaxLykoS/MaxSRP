        #include "MaxPcssPatternProbe.hlsl"
        float4 _PcssProbe;
        float4 ConvergenceFragment(v2f i) : SV_Target
        {
            float3 p, n; float valid;
            PatternSurface(i,p,n,valid);
            float3 dx,dy;
            ShadowReceiverDerivatives(p,dx,dy);
            if(_PcssProbe.z<0.5) {dx=0;dy=0;}
            if(_PcssProbe.y>0.5) n=float3(0,1,0);
            float v=valid>0.5 ? MainLightShadow(p,n,dx,dy) : 1;
            return float4(v.xxx*_PcssProbe.x,1);
        }
Texture2D _PcssFixtureDepth;
float4 _PcssPlaneDx, _PcssPlaneDy;
float4 PlaneFixtureFragment(v2f input) : SV_Target
{
    float2 phase=float2(.3,.7), uv=float2(.5,.5);
    float2 corrected=GetAverageBlockerDepth(uv,_PcssFixtureDepth,_PcssProbe.x,_PcssProbe.y,phase,_PcssProbe.zw);
    float2 raw=GetAverageBlockerDepth(uv,_PcssFixtureDepth,_PcssProbe.x,_PcssProbe.y,phase,float2(0,0));
    float2 gradient=ShadowReceiverPlaneGradient(_ShadowVPMatrix0,_PcssPlaneDx.xyz,_PcssPlaneDy.xyz);
    return float4(corrected.y,raw.y,gradient);
}
float4 BiasNormalFixtureFragment(v2f input) : SV_Target
{
    return float4(ShadowReceiverBiasNormal(_PcssProbe.xyz, _PcssPlaneDx.xyz, _PcssPlaneDy.xyz), 1);
}

float4 _AtlasProbe, _AtlasFixtureDepths;
float AtlasDepthFixtureFragment(v2f input) : SV_Depth
{
    int tile = (input.uv.x >= 0.5 ? 1 : 0) + (input.uv.y >= 0.5 ? 2 : 0);
    return _AtlasFixtureDepths[tile];
}
float4 AtlasFilterFixtureFragment(v2f input) : SV_Target
{
    float2 ndc = _AtlasProbe.xy * 2.0 - 1.0;
#if UNITY_UV_STARTS_AT_TOP
    ndc.y = -ndc.y;
#endif
    float v = FilterDirectionalShadow(float4(ndc, DeviceDepthToClipDepth(_AtlasProbe.z), 1),
        _MainShadowAtlas, sampler_MainShadowAtlas, float4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1),
        2, 1, float2(.31,.67), .8, 0, float2(0,0), _ShadowAtlasTile0, _ShadowResolution0, _AtlasProbe.w > 0.5);
    return float4(v.xxx, 1);
}
float4 AtlasInspectFragment(v2f input) : SV_Target
{
    float d = _MainShadowAtlas.SampleLevel(sampler_PointClamp, input.uv, 0).r;
    return float4(d.xxx, 1);
}
float4 ShadowWorldFixtureFragment(v2f input) : SV_Target
{
    float v = SampleDirectionalCascade((int)_AtlasProbe.x, _PcssPlaneDx.xyz, float3(0,1,0),
        1.0, float2(.31,.67), float3(1,0,0), float3(0,0,1));
    int c=(int)_AtlasProbe.x;
    float4 q = c==0 ? mul(_ShadowVPMatrix0,float4(_PcssPlaneDx.xyz,1)) : c==1 ? mul(_ShadowVPMatrix1,float4(_PcssPlaneDx.xyz,1)) : c==2 ? mul(_ShadowVPMatrix2,float4(_PcssPlaneDx.xyz,1)) : mul(_ShadowVPMatrix3,float4(_PcssPlaneDx.xyz,1));
    float4 tile = c==0 ? _ShadowAtlasTile0 : c==1 ? _ShadowAtlasTile1 : c==2 ? _ShadowAtlasTile2 : _ShadowAtlasTile3;
    float d = _MainShadowAtlas.SampleLevel(sampler_PointClamp,ShadowAtlasUV(ShadowClipToUV(q.xy/q.w),tile),0).r;
    return float4(v,d,ClipDepthToDeviceDepth(q.z/q.w),ShadowClipToUV(q.xy/q.w).y);
}
float4 CascadeDebugFragment(v2f input) : SV_Target
{
    float3 p,n; float valid; PatternSurface(input,p,n,valid);
    float eye = -mul(_ShadowReceiverView, float4(p,1)).z;
    float depth = (eye - _CascadeDepthRange.x) * _CascadeDepthRange.z;
    if (valid < .5 || depth < 0 || depth >= 1) return float4(-1,0,0,1);
    int c = depth <= _Split0 ? 0 : depth <= _Split1 ? 1 : depth <= _Split2 ? 2 : 3;
    float end = c==0 ? _Split0 : c==1 ? _Split1 : _Split2;
    float band = c==0 ? _CascadeBlendWidths.x : c==1 ? _CascadeBlendWidths.y : _CascadeBlendWidths.z;
    float width = c==0 ? _OrthoWidth0 : c==1 ? _OrthoWidth1 : c==2 ? _OrthoWidth2 : _OrthoWidth3;
    float resolution = c==0 ? _ShadowResolution0 : c==1 ? _ShadowResolution1 : c==2 ? _ShadowResolution2 : _ShadowResolution3;
    return float4(c, c < 3 ? ShadowCascadeBlendWeight(depth,end,band) : 0, width/resolution, 1);
}
