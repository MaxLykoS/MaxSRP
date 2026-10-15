$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskLibrary = Join-Path $taskRoot 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering'
function Read-TaskShader([string]$name) {
    [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes((Join-Path $taskLibrary $name)))
}
function Get-TaskFunction([string]$source, [string]$signature) {
    $taskStart = $source.IndexOf($signature)
    if ($taskStart -lt 0) { throw "Missing HLSL function: $signature" }
    $taskBrace = $source.IndexOf('{', $taskStart)
    $taskDepth = 1
    for ($taskEnd = $taskBrace + 1; $taskDepth -gt 0; $taskEnd++) {
        if ($source[$taskEnd] -eq '{') { $taskDepth++ }
        if ($source[$taskEnd] -eq '}') { $taskDepth-- }
    }
    $taskFunction = $source.Substring($taskStart, $taskEnd - $taskStart)
    $taskFunction = $taskFunction.Replace('float3(', 'new Vector3(').Replace('float2(', 'new Vector2(')
    $taskFunction = $taskFunction.Replace('float3', 'Vector3').Replace('float2', 'Vector2')
    $taskFunction = $taskFunction.Replace('.yz', '.YZ').Replace('.x', '.X').Replace('.y', '.Y').Replace('.z', '.Z').Replace('.w', '.W')
    $taskFunction = [regex]::Replace($taskFunction, '(?<![\w.])(\d+\.\d+)(?![\w.])', '${1}f')
    $taskFunction = [regex]::Replace($taskFunction, '(?<![\w.])(\d+[eE][+-]?\d+)(?!\w)', '${1}f')
    $taskFunction = $taskFunction.Replace('float(N_SAMPLE)', '(float)(N_SAMPLE)')
    $taskFunction = $taskFunction.Replace('eyePos, viewDir, entry, exit)', 'eyePos, viewDir, out entry, out exit)')
    $taskFunction = $taskFunction.Replace('_AerialPerspectiveParams.YZ - 0.5f', '_AerialPerspectiveParams.YZ - new Vector2(0.5f)')
    return 'static ' + $taskFunction
}
$taskHelper = Read-TaskShader 'Helper.hlsl'
$taskMarch = Read-TaskShader 'RayMarching.hlsl'
$taskAerial = Read-TaskShader 'AerialPerspective.hlsl'
$taskFunctions = @(
    (Get-TaskFunction $taskHelper 'float RayIntersectSphere('),
    (Get-TaskFunction $taskHelper 'float RayIntersectGroundSphere('),
    (Get-TaskFunction $taskHelper 'bool AtmosphereRayInterval('),
    (Get-TaskFunction $taskMarch 'float AtmosphereStepIntegral('),
    (Get-TaskFunction $taskMarch 'float3 AtmosphereStepIntegral('),
    (Get-TaskFunction $taskMarch 'float3 GetAtmosphereSegment('),
    (Get-TaskFunction $taskAerial 'float AerialSliceDistance('),
    (Get-TaskFunction $taskAerial 'float2 AerialSliceUV('),
    (Get-TaskFunction $taskAerial 'void AerialDepthSlices('),
    (Get-TaskFunction $taskAerial 'float3 ValidAerialTransmission('),
    (Get-TaskFunction $taskAerial 'float3 ValidAerialScattering('),
    (Get-TaskFunction $taskAerial 'float3 ComposeAerialPerspective(')
) -join "`n"
$taskComposition = [regex]::Match($taskAerial, 'return ComposeAerialPerspective\(surfaceRadiance, transmittance, scattering\);').Value
if (-not $taskComposition) { throw 'Missing linear RGB aerial composition.' }
$taskHost = @'
using System;
using System.Numerics;
public static class AerialPerspectiveChecks {
    struct Layout {
        public float X,Y,Z,W;
        public Vector2 YZ { get{return new Vector2(Y,Z);} }
    }
    struct AtmosphereParameter {
        public float PlanetRadius,AtmosphereHeight;
        public Vector3 SolarIrradiance,Extinction,Source;
    }
    static Layout _AerialPerspectiveParams=new Layout {X=32000,Y=32,Z=32,W=32};
    static float length(Vector3 p){return p.Length();}
    static float dot(Vector3 p,Vector3 d){return Vector3.Dot(p,d);}
    static float sqrt(float v){return (float)Math.Sqrt(v);}
    static float floor(float v){return (float)Math.Floor(v);}
    static float min(float a,float b){return Math.Min(a,b);}
    static float max(float a,float b){return Math.Max(a,b);}
    static float exp(float v){return (float)Math.Exp(v);}
    static Vector3 exp(Vector3 v){return new Vector3(exp(v.X),exp(v.Y),exp(v.Z));}
    static float clamp(float v,float lo,float hi){return Math.Max(lo,Math.Min(v,hi));}
    static Vector2 clamp(Vector2 v,float lo,Vector2 hi){return new Vector2(clamp(v.X,lo,hi.X),clamp(v.Y,lo,hi.Y));}
    static float saturate(float v){return clamp(v,0,1);}
    static bool isfinite(float v){return float.IsFinite(v);}
    // Constant-medium stubs isolate the actual integration from density/phase/LUT approximations.
    static Vector3 RayleighCoefficient(AtmosphereParameter p,float h){return p.Extinction;}
    static Vector3 MieCoefficient(AtmosphereParameter p,float h){return Vector3.Zero;}
    static Vector3 OzoneAbsorption(AtmosphereParameter p,float h){return Vector3.Zero;}
    static Vector3 MieAbsorption(AtmosphereParameter p,float h){return Vector3.Zero;}
    static Vector3 TransmittanceToSun(AtmosphereParameter p,Vector3 pos,Vector3 dir){return Vector3.One;}
    static Vector3 Scattering(AtmosphereParameter p,Vector3 pos,Vector3 sun,Vector3 dir){return p.Source;}
    static Vector3 GetMultiScattering(AtmosphereParameter p,Vector3 pos,Vector3 dir){return Vector3.Zero;}
    __FUNCTIONS__
    static Vector3 Compose(Vector3 surfaceRadiance,Vector3 transmittance,Vector3 scattering){__COMPOSITION__}
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static float Channel(Vector3 v,int i){return i==0?v.X:i==1?v.Y:v.Z;}
    // Independent packed texture with values linear in screen coordinates and physical distance.
    static double Texel(int x,int y){
        int tile=x/32,local=x%32;
        double d=32000.0*tile*tile/(31*31);
        return 2*(local+.5)/32+3*(y+.5)/32+0.001*d;
    }
    static double Atlas(Vector2 uv){
        double x=uv.X*1024-.5,y=uv.Y*32-.5;
        int x0=Math.Max(0,Math.Min(1023,(int)Math.Floor(x))),x1=Math.Min(1023,x0+1);
        int y0=Math.Max(0,Math.Min(31,(int)Math.Floor(y))),y1=Math.Min(31,y0+1);
        double tx=Math.Max(0,Math.Min(1,x-x0)),ty=Math.Max(0,Math.Min(1,y-y0));
        double a=Texel(x0,y0)*(1-tx)+Texel(x1,y0)*tx;
        double b=Texel(x0,y1)*(1-tx)+Texel(x1,y1)*tx;
        return a*(1-ty)+b*ty;
    }
    public static string Run(){
        Require(AerialSliceDistance(0)==0 && AerialSliceDistance(31)==32000,"Depth endpoints incorrect");
        var random=new Random(13);double maxAtlasError=0;
        for(int i=0;i<10000;i++){
            var screen=new Vector2((float)(random.NextDouble()*1.4-.2),(float)(random.NextDouble()*1.4-.2));
            float distance=(float)(random.NextDouble()*40000-1000),s0,s1,w;
            AerialDepthSlices(distance,out s0,out s1,out w);
            Require(s0>=0 && s1<=31 && w>=0 && w<=1,"Invalid depth interpolation");
            double actual=Atlas(AerialSliceUV(screen,s0))*(1-w)+Atlas(AerialSliceUV(screen,s1))*w;
            double reference=2*clamp(screen.X,.5f/32,31.5f/32)+3*clamp(screen.Y,.5f/32,31.5f/32)+.001*clamp(distance,0,32000);
            double error=Math.Abs(actual-reference);maxAtlasError=Math.Max(maxAtlasError,error);
            Require(error<0.00002,"Atlas slice bleeding/interpolation error "+error);
        }
        for(int slice=0;slice<32;slice++)foreach(float edge in new float[]{0,1,-.2f,1.2f}){
            Vector2 uv=AerialSliceUV(new Vector2(edge,edge),slice);
            Require(uv.X*1024>=slice*32+.5f && uv.X*1024<=(slice+1)*32-.5f,"Bilinear lookup crosses tile boundary");
        }
        var param=new AtmosphereParameter {PlanetRadius=6360000,AtmosphereHeight=60000,
            SolarIrradiance=new Vector3(2,1.6f,1.2f),Extinction=new Vector3(2e-5f,3e-5f,4e-5f),Source=new Vector3(1e-5f,2e-5f,3e-5f)};
        var eye=new Vector3(0,param.PlanetRadius+1,0);int mediumCases=0;
        foreach(float distance in new float[]{0,.001f,1,33,100,1000,10000,32000}){
            Vector3 t;Vector3 s=GetAtmosphereSegment(param,eye,Vector3.UnitY,Vector3.UnitY,distance,out t);
            for(int c=0;c<3;c++){
                double sigma=Channel(param.Extinction,c),expectedT=Math.Exp(-sigma*distance);
                double expectedS=Channel(param.Source,c)*Channel(param.SolarIrradiance,c)*(1-expectedT)/sigma;
                Require(Math.Abs(Channel(t,c)-expectedT)<3e-6,"RGB extinction integration error");
                Require(Math.Abs(Channel(s,c)-expectedS)<3e-6,"RGB scattering integration error");
            }
            if(distance==0)Require(t==Vector3.One && s==Vector3.Zero,"Zero distance must be neutral");
            mediumCases++;
        }
        Vector3 groundT;GetAtmosphereSegment(param,eye,-Vector3.UnitY,Vector3.UnitY,32000,out groundT);
        Require(Math.Abs(groundT.X-Math.Exp(-2e-5))<1e-6,"Integration continued through planet");
        // Observers above the atmosphere: exclude vacuum, respect original ray distance,
        // and clip at the first ground hit rather than crossing the planet.
        var outside=new Vector3(0,param.PlanetRadius+70000,0);
        foreach(float limit in new float[]{0,1000,10000,20000,100000,-1}){
            float expectedLength=limit<0?60000:Math.Max(0,Math.Min(limit,70000)-10000);
            Vector3 t,s=GetAtmosphereSegment(param,outside,-Vector3.UnitY,Vector3.UnitY,limit,out t);
            for(int c=0;c<3;c++){
                double sigma=Channel(param.Extinction,c),expectedT=Math.Exp(-sigma*expectedLength);
                double expectedS=Channel(param.Source,c)*Channel(param.SolarIrradiance,c)*(1-expectedT)/sigma;
                Require(Math.Abs(Channel(t,c)-expectedT)<4e-6,"Exterior segment includes vacuum or misses air");
                Require(Math.Abs(Channel(s,c)-expectedS)<4e-6,"Exterior scattering interval incorrect");
            }
        }
        Vector3 exteriorT;var exteriorS=GetAtmosphereSegment(param,outside,Vector3.UnitY,Vector3.UnitY,-1,out exteriorT);
        Require(exteriorT==Vector3.One && exteriorS==Vector3.Zero,"Exterior miss must be neutral");
        var topPoint=new Vector3(0,param.PlanetRadius+param.AtmosphereHeight,0);
        var tangentS=GetAtmosphereSegment(param,topPoint,Vector3.UnitX,Vector3.UnitY,-1,out exteriorT);
        Require(exteriorT==Vector3.One && tangentS==Vector3.Zero,"Top tangent must be neutral");
        param.Extinction=param.Source=Vector3.Zero;
        Vector3 vacuumT;Vector3 vacuumS=GetAtmosphereSegment(param,eye,Vector3.UnitY,Vector3.UnitY,32000,out vacuumT);
        Require(vacuumT==Vector3.One && vacuumS==Vector3.Zero,"Zero atmosphere changes surface radiance");
        var surfaceHDR=new Vector3(2,1,.5f);var haze=new Vector3(.01f,.02f,.03f);
        var composed=Compose(surfaceHDR,new Vector3(.9f,.5f,.1f),haze);
        Require(Vector3.Distance(composed,new Vector3(1.81f,.52f,.08f))<1e-6,"Composition lost RGB transmission/HDR");
        Require(Compose(surfaceHDR,Vector3.One,Vector3.Zero)==surfaceHDR,"Neutral LUT changes surfaces");
        Require(Compose(surfaceHDR,Vector3.Zero,haze)==haze,"Opaque atmosphere retains surface light");
        foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity}){
            var bad=new Vector3(invalid);
            Require(Compose(surfaceHDR,bad,bad)==surfaceHDR,"Invalid fog removes valid surface light");
        }
        var partialFog=Compose(surfaceHDR,new Vector3(float.NaN,.5f,.1f),new Vector3(float.NaN,.02f,.03f));
        Require(Vector3.Distance(partialFog,new Vector3(2,.52f,.08f))<1e-6,"Fog repair discards valid channels");
        // Project LUT rays back to independent camera geometry across FOV/viewport/Z conventions.
        int rayCases=0;
        foreach(float fov in new float[]{30,60,100})foreach(float aspect in new float[]{.75f,1,1.7777778f})
        foreach(bool top in new bool[]{false,true})foreach(bool reversed in new bool[]{false,true}){
            var camera=new Vector3(11,20,-7);var forward=Vector3.Normalize(new Vector3(.8f,.1f,-.7f));
            var view=Matrix4x4.CreateLookAt(camera,camera+forward,Vector3.UnitY);
            var projection=Matrix4x4.CreatePerspectiveFieldOfView(fov*(float)Math.PI/180,aspect,.3f,1000);
            if(top)projection.M22=-projection.M22;
            if(reversed){projection.M33=.3f/(1000-.3f);projection.M43=1000*.3f/(1000-.3f);}
            Matrix4x4 invProjection,invView;Matrix4x4.Invert(projection,out invProjection);Matrix4x4.Invert(view,out invView);
            foreach(var uv in new Vector2[]{new Vector2(.5f,.5f),new Vector2(.1f,.2f),new Vector2(.9f,.8f)}){
                var ndc=uv*2-Vector2.One;if(top)ndc.Y=-ndc.Y;
                var point=Vector4.Transform(new Vector4(ndc,reversed?1:0,1),invProjection);
                var ray=Vector3.Normalize(Vector3.TransformNormal(new Vector3(point.X,point.Y,point.Z),invView));
                var projected=Vector4.Transform(new Vector4(camera+ray*100,1),view*projection);
                Require(Math.Abs(projected.X/projected.W-ndc.X)<.00001 && Math.Abs(projected.Y/projected.W-ndc.Y)<.00001,"Aerial ray misses scene pixel");
                rayCases++;
            }
        }
        // Independently project orthographic per-pixel ray endpoints back to their screen pixels.
        foreach(float aspect in new float[]{.75f,1,1.7777778f})
        foreach(bool top in new bool[]{false,true})foreach(bool reversed in new bool[]{false,true}){
            var camera=new Vector3(11,200,-7);var forward=Vector3.Normalize(new Vector3(.8f,.1f,-.7f));
            var view=Matrix4x4.CreateLookAt(camera,camera+forward,Vector3.UnitY);
            var projection=Matrix4x4.CreateOrthographic(20*aspect,20,.3f,1000);
            if(top)projection.M22=-projection.M22;
            if(reversed){projection.M33=1/(1000-.3f);projection.M43=1000/(1000-.3f);}
            Matrix4x4 invProjection,invView;Matrix4x4.Invert(projection,out invProjection);Matrix4x4.Invert(view,out invView);
            foreach(var uv in new Vector2[]{new Vector2(.5f,.5f),new Vector2(.1f,.2f),new Vector2(.9f,.8f)}){
                var ndc=uv*2-Vector2.One;if(top)ndc.Y=-ndc.Y;
                var point=Vector4.Transform(new Vector4(ndc,reversed?1:0,1),invProjection);
                var offset=Vector3.TransformNormal(new Vector3(point.X/point.W,point.Y/point.W,0),invView);
                var ray=Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ,invView));
                var surface=camera+offset+ray*300;
                var projected=Vector4.Transform(new Vector4(surface,1),view*projection);
                Require(Math.Abs(projected.X/projected.W-ndc.X)<.00001 && Math.Abs(projected.Y/projected.W-ndc.Y)<.00001,"Orthographic aerial ray misses pixel");
                var viewSurface=Vector3.Transform(surface,view);
                Require(Math.Abs(-viewSurface.Z-300)<.0001,"Orthographic depth differs from ray segment length");
                if(uv.X!=.5f)Require(Vector3.Distance(surface,camera)>300,"Orthographic test does not expose lateral distance error");
                rayCases++;
            }
        }
        return "PASS: 10000 actual HLSL atlas/depth samples and tile edges (max error "+maxAtlasError.ToString("G4")+"); "+mediumCases+" interior and 8 exterior/tangent constant-medium RGB segment cases, vacuum/ground clipping; "+rayCases+" FOV/aspect/origin/Z ray reprojections and orthographic depth.";
    }
}
'@
Add-Type -TypeDefinition ($taskHost.Replace('__FUNCTIONS__', $taskFunctions).Replace('__COMPOSITION__', $taskComposition))
[AerialPerspectiveChecks]::Run()

$taskPipeline = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs'))
$taskSky = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs'))
$taskLighting = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Shaders/Realtime/PBRLitPass.hlsl'))
$taskProducer = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/AerialPerspectiveLUTPass.hlsl'))
if ($taskPipeline.IndexOf('m_proceduralSkyboxPass.PrepareAtmosphere(context, camera,') -gt $taskPipeline.IndexOf('m_LightPass.Execute(context, camera') -or
    -not $taskPipeline.Contains('m_proceduralSkyboxPass.PrepareAtmosphere(context, camera,')) { throw 'Lighting consumes a stale atmosphere LUT.' }
if (-not $taskLighting.Contains('c = ApplyAerialPerspective(c, o.uv, atmosphereDistance);') -or
    $taskLighting.IndexOf('if (isBackground)') -gt $taskLighting.IndexOf('ApplyAerialPerspective(c,')) { throw 'Atmosphere composition/background routing incorrect.' }
foreach ($taskToken in @('SV_Target0', 'SV_Target1', '_AerialInverseProjection', '_AerialCameraToWorld', 'UNITY_NEAR_CLIP_VALUE', 'GetAtmosphereSegment')) {
    if (-not $taskProducer.Contains($taskToken)) { throw "Missing aerial producer: $taskToken" }
}
foreach ($taskToken in @('cmd.SetRenderTarget(cache.AerialTargets', 'CameraViewport.GetProjection(camera), true', 'camera.worldToCameraMatrix.inverse',
    'cmd.SetGlobalTexture(ShaderProperties._AerialTransmittanceLUT', 'MaxRenderResources.Destroy(ref Transmittance)',
    'cmd.SetGlobalFloat(ShaderProperties._AerialPerspectiveEnabled')) {
    if (-not $taskSky.Contains($taskToken)) { throw "Missing aerial data binding/ownership: $taskToken" }
}
if ($taskProducer.Contains('t1 / t2') -or $taskProducer.Contains('fixed4 frag') -or $taskSky.Contains('m_aerialPerspectiveMat')) {
    throw 'Legacy gray transmittance/unused compositing path remains.'
}
Write-Output 'PASS: camera-specific MRT LUT preparation before Lighting, RGB composition before tone mapping, background bypass, enable guard and texture disposal.'
