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
float4 _PcssPlaneDx, _PcssPlaneDy;
float4 PlaneFixtureFragment(v2f input) : SV_Target
{
    float2 phase=float2(.3,.7), uv=float2(.5,.5);
    float2 corrected=GetAverageBlockerDepth(uv,_MainShadowMap0,_PcssProbe.x,_PcssProbe.y,phase,_PcssProbe.zw);
    float2 raw=GetAverageBlockerDepth(uv,_MainShadowMap0,_PcssProbe.x,_PcssProbe.y,phase,float2(0,0));
    float2 gradient=ShadowReceiverPlaneGradient(_ShadowVPMatrix0,_PcssPlaneDx.xyz,_PcssPlaneDy.xyz);
    return float4(corrected.y,raw.y,gradient);
}