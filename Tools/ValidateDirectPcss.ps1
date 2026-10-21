$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskShadow = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl'))
$taskSpace = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/SpaceTransform.hlsl'))
function Get-TaskFunction([string]$source, [string]$signature) {
    $start = $source.IndexOf($signature)
    if ($start -lt 0) { throw "Missing shader function: $signature" }
    $brace = $source.IndexOf('{', $start); $depth = 1
    for ($end = $brace + 1; $depth; ++$end) {
        if ($source[$end] -eq '{') { ++$depth }; if ($source[$end] -eq '}') { --$depth }
    }
    return $source.Substring($start, $end - $start)
}
$taskFunctions = @(Get-TaskFunction $taskSpace 'float ClipDepthToDeviceDepth(')
foreach ($signature in @('bool IsShadowOccluded(', 'float ShadowDistanceFromNear(', 'float BiasedShadowDepth(',
    'float2 ShadowClipToUV(', 'float PCSSFilterRadius(', 'float2 PcssSobolSquare(', 'float2 PcssSquareToDisk(',
    'float2 PcssDiskSample(', 'float2 GetAverageBlockerDepth(', 'float PCSS(', 'float PcssNeighborWeight(')) {
    $taskFunctions += Get-TaskFunction $taskShadow $signature
}
$taskHost = @'
using System;
using System.Numerics;
public static class __CLASS__ {
    static int _PcssBlockerSamples, _PcssFilterSamples;
    static float _ShadowLightTanAngularRadius=.1f;
    static object sampler_PointClamp=new object();
    class SamplerComparisonState {}
    static float max(float a,float b)=>Math.Max(a,b);
    static float min(float a,float b)=>Math.Min(a,b);
    static float saturate(float x)=>Math.Max(0,Math.Min(1,x));
    static float abs(float x)=>Math.Abs(x);
    static float length(Vector3 x)=>x.Length();
    static float dot(Vector3 a,Vector3 b)=>Vector3.Dot(a,b);
    static float dot(Vector2 a,Vector2 b)=>Vector2.Dot(a,b);
    static void sincos(float angle,out float sine,out float cosine){sine=(float)Math.Sin(angle);cosine=(float)Math.Cos(angle);}
    static uint reversebits(uint value){uint result=0;for(int i=0;i<32;i++){result=(result<<1)|(value&1u);value>>=1;}return result;}
    static Vector4 mul(Matrix4x4 m,Vector4 v)=>Vector4.Transform(v,m);
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    // Point depth + bilinear comparison model. These are API fixtures, not a GPU test.
    class Texture2D {
        public const int Size=512;
        public int reads,compares;
        public bool allClear,allBlocked;
        public float blockerDepth, slope;
        float Clear=>__REVERSE__ ? 0:1;
        float Depth(int x,int y){x=Math.Clamp(x,0,Size-1);return allClear?Clear:(allBlocked||x<Size/2)?blockerDepth+slope*((x+.5f)/Size-.5f):Clear;}
        public Vector4 SampleLevel(object sampler,Vector2 uv,int lod){reads++;return new Vector4(Depth((int)Math.Floor(uv.X*Size),(int)Math.Floor(uv.Y*Size)),0,0,0);}
        float Compare(int x,int y,float receiver)=>IsShadowOccluded(receiver,Depth(x,y))?0:1;
        public float SampleCmpLevelZero(SamplerComparisonState sampler,Vector2 uv,float receiver){
            compares++;
            float x=uv.X*Size-.5f,y=uv.Y*Size-.5f;int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y);
            float fx=x-ix,fy=y-iy;
            return (Compare(ix,iy,receiver)*(1-fx)+Compare(ix+1,iy,receiver)*fx)*(1-fy)
                +(Compare(ix,iy+1,receiver)*(1-fx)+Compare(ix+1,iy+1,receiver)*fx)*fy;
        }
    }
    __FUNCTIONS__
    static float DeviceDepth(float ordinary)=>__REVERSE__?1-ordinary:ordinary;
    static float Evaluate(Texture2D map,float u,float ordinaryDepth,float cap=.3f){
        return PCSS(new Vector4(u*2-1,0,DeviceDepth(ordinaryDepth),1),map,new SamplerComparisonState(),Matrix4x4.Identity,
            2,1,new Vector2(1,0),cap,0,Vector2.Zero);
    }
    static float PenumbraWidth(Texture2D map,float receiver){
        int partial=0;
        for(int i=0;i<=1000;i++){
            float value=Evaluate(map,.35f+i*.0003f,receiver);
            Check(!float.IsNaN(value)&&value>=0&&value<=1,"PCSS visibility out of range");
            if(value>.01f&&value<.99f)partial++;
        }
        return partial*.0003f;
    }
    public static string Run(){
        int cases=0;
        foreach(int blockerCount in new[]{16,32,64})foreach(int filterCount in new[]{16,32,64}){
            _PcssBlockerSamples=blockerCount;_PcssFilterSamples=filterCount;
            var map=new Texture2D {blockerDepth=DeviceDepth(.25f),allClear=true};
            Check(Evaluate(map,.5f,.75f)==1 && map.reads==blockerCount && map.compares==0,"No-blocker early out failed");
            map.allClear=false;map.allBlocked=true;map.reads=map.compares=0;
            Check(Evaluate(map,.5f,.75f)==0 && map.reads==blockerCount && map.compares==filterCount,"Fully shadowed PCSS/sample budget failed");
            map.allBlocked=false;
            Check(Evaluate(map,.2f,.75f)==0 && Evaluate(map,.8f,.75f)==1,"Hard regions lost energy");
            float edge=Evaluate(map,.5f,.75f);Check(edge>.1f && edge<.9f,"No soft transition at blocker edge");
            float close=PenumbraWidth(map,.35f),far=PenumbraWidth(map,.75f);
            Check(far>close*2 && close>0,"PCSS does not harden on contact");
            // Move both receiver and blocker by the same light-depth offset.
            float original=Evaluate(map,.52f,.75f);
            map.blockerDepth=DeviceDepth(.35f);
            Check(Math.Abs(original-Evaluate(map,.52f,.85f))<.0001f,"Origin translation changes actual PCSS result");
            map.blockerDepth=DeviceDepth(.25f);map.reads=map.compares=0;
            Check(Math.Abs(Evaluate(map,.5f,.75f,0)-.5f)<1e-6f && map.reads==0 && map.compares==1,"Zero cap hardware PCF fallback failed");
            cases++;
        }
        foreach(int count in new[]{16,32,64}){
            _PcssBlockerSamples=count;
            var plane=new Texture2D {allBlocked=true,blockerDepth=DeviceDepth(.5f),slope=__REVERSE__?-.4f:.4f};
            var raw=GetAverageBlockerDepth(new Vector2(.5f),plane,DeviceDepth(.49f),.15f,new Vector2(.3f,.7f),Vector2.Zero);
            var corrected=GetAverageBlockerDepth(new Vector2(.5f),plane,DeviceDepth(.49f),.15f,new Vector2(.3f,.7f),new Vector2(plane.slope,0));
            Check(raw.Y>0&&corrected.Y==0,"Sloped receiver falsely counted its own plane as blockers");
            var mean=Vector2.Zero;var quadrants=new bool[4];float r2=0;
            int columns=count==32?8:(int)Math.Sqrt(count),rows=count/columns;
            var strata=new bool[count];
            for(int i=0;i<count;i++){
                var v=PcssDiskSample(i,count,new Vector2(1,0));
                Check(v.LengthSquared()<=1,"Disk sample outside radius");mean+=v;r2+=v.LengthSquared();
                quadrants[(v.X>=0?1:0)+(v.Y>=0?2:0)]=true;
                var square=PcssSobolSquare(i,new Vector2(.37f,.68f));
                int bin=(int)(square.Y*rows)*columns+(int)(square.X*columns);
                Check(bin>=0 && bin<count && !strata[bin],"Sobol samples repeat a 2D stratum");strata[bin]=true;
            }
            // A single unshifted finite net is not an exact radial quadrature.
            // Check its coarse discrepancy, then unbiased moments across shifts.
            Check((mean/count).Length()<.12f && Math.Abs(r2/count-.5f)<2.0f/count,"Disk discrepancy exceeds the finite-net bound");
            foreach(bool populated in quadrants)Check(populated,"Disk misses a quadrant");
            double moment=0;var ensembleMean=Vector2.Zero;
            for(int x=0;x<16;x++)for(int y=0;y<16;y++)for(int pair=0;pair<2;pair++)for(int i=0;i<count;i++){
                var phase=new Vector2((x+.37f)/16,(y+.68f)/16);
                if(pair==1)phase=Vector2.One-phase;
                var v=PcssDiskSample(i,count,phase);moment+=v.LengthSquared();ensembleMean+=v;
            }
            Check(Math.Abs(moment/(count*512)-.5)<.005 && (ensembleMean/(count*512)).Length()<.005,"Shifted disk moments N="+count+" r2="+(moment/(count*512))+" mean="+(ensembleMean/(count*512)).Length());
        }
        var origin=Vector3.Zero;var n=Vector3.UnitY;var neighbor=new Vector3(.001f,0,0);
        Check(PcssNeighborWeight(origin,n,0,1,neighbor,n,0,1)>.99f,"Coplanar neighbor rejected");
        Check(PcssNeighborWeight(origin,n,0,1,neighbor+new Vector3(0,.01f,0),n,0,1)==0,"Parallel offset surface leaks");
        Check(PcssNeighborWeight(origin,n,0,1,neighbor,Vector3.UnitX,0,1)==0,"Normal discontinuity leaks");
        Check(PcssNeighborWeight(origin,n,0,1,neighbor,n,1,1)==0,"Cascade/blend boundary leaks");
        Check(PcssNeighborWeight(origin,n,0,1,neighbor,n,0,0)==0,"Background leaks");
        return "PASS: __CLASS__: production PCSS/blocker/filter; "+cases+" budgets, contact hardening, translation, energy endpoints, early out; Sobol 2D strata/shifted disk moments and receiver-plane/normal/background/cascade rejection. Not a GPU test.";
    }
}
'@
foreach ($taskReverse in @($false, $true)) {
    $taskConverted = foreach ($taskFunction in $taskFunctions) {
        $taskLines = [Collections.Generic.List[string]]::new(); $taskEnabled = $true
        foreach ($taskLine in ($taskFunction -split "`n")) {
            if ($taskLine -match '^\s*#if defined\(UNITY_REVERSED_Z\)') { $taskEnabled = $taskReverse }
            elseif ($taskLine -match '^\s*#if UNITY_UV_STARTS_AT_TOP') { $taskEnabled = $false }
            elseif ($taskLine -match '^\s*#else') { $taskEnabled = -not $taskEnabled }
            elseif ($taskLine -match '^\s*#endif') { $taskEnabled = $true }
            elseif ($taskEnabled) { $taskLines.Add($taskLine) }
        }
        $taskCode = $taskLines -join "`n"
        $taskCode = $taskCode.Replace('any(uv < 0.0) || any(uv > 1.0)', 'uv.X < 0.0 || uv.Y < 0.0 || uv.X > 1.0 || uv.Y > 1.0')
        $taskCode = $taskCode.Replace('shadowNDC.xy', 'new Vector2(shadowNDC.X, shadowNDC.Y)').Replace('UNITY_NEAR_CLIP_VALUE', '0.0')
        $taskCode = $taskCode.Replace('float4x4', 'Matrix4x4').Replace('[loop]', '').Replace('[unroll]', '').Replace('float2(', 'new Vector2(')
        $taskCode = $taskCode.Replace('float2', 'Vector2').Replace('float3', 'Vector3').Replace('float4', 'Vector4')
        $taskCode = [regex]::Replace($taskCode, '\.([xyzw])\b', { param($m) '.' + $m.Groups[1].Value.ToUpperInvariant() })
        $taskCode = $taskCode.Replace('.r;', '.X;')
        $taskCode = [regex]::Replace($taskCode, '(?<![\w.])(\d+\.\d+(?:[eE][+-]?\d+)?|\d+[eE][+-]?\d+)(?![\w.])', '${1}f')
        $taskCode = $taskCode.Replace('ndc * 0.5f + 0.5f', 'ndc * 0.5f + new Vector2(0.5f)')
        $taskCode = $taskCode.Replace('new Vector2(x >> 9, y >> 9) + 0.5f', 'new Vector2(x >> 9, y >> 9) + new Vector2(0.5f)')
        $taskCode = $taskCode.Replace('square * 2.0f - 1.0f', 'square * 2.0f - new Vector2(1.0f)')
        $taskCode = $taskCode.Replace('sincos(angle, sine, cosine)', 'sincos(angle, out sine, out cosine)')
        'static ' + $taskCode
    }
    $taskClass = if ($taskReverse) { 'DirectPcssReversed' } else { 'DirectPcssOrdinary' }
    $taskCode = $taskHost.Replace('__CLASS__', $taskClass)
    $taskCode = $taskCode.Replace('__FUNCTIONS__', ($taskConverted -join "`n")).Replace('__REVERSE__', $taskReverse.ToString().ToLowerInvariant())
    Add-Type -TypeDefinition $taskCode
    Invoke-Expression "[$taskClass]::Run()"
}
