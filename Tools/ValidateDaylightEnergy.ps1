$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskLibrary=Join-Path $taskRoot 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering'
function ReadShader($name){[Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes((Join-Path $taskLibrary $name)))}
function Extract($source,$signature){
 $a=$source.IndexOf($signature);if($a -lt 0){throw "Missing $signature"};$b=$source.IndexOf('{',$a);$depth=1
 for($end=$b+1;$depth;$end++){if($source[$end] -eq '{'){$depth++};if($source[$end] -eq '}'){$depth--}}
 [regex]::Replace($source.Substring($a,$end-$a),'//[^\r\n]*','')
}
$taskHelper=ReadShader 'Helper.hlsl';$taskScattering=ReadShader 'Scattering.hlsl';$taskMarch=ReadShader 'RayMarching.hlsl'
$taskFunctions=@()
foreach($signature in @('float3 AtmosphereCameraPosition(','float RayIntersectSphere(','float RayIntersectGroundSphere(','bool AtmosphereRayInterval(','void UvToTransmittanceLutParams(','float2 GetTransmittanceLutUv(')){$taskFunctions+=Extract $taskHelper $signature}
foreach($signature in @('float3 SolarDiskRadiance(','float3 RayleighCoefficient(','float RayleiPhase(','float3 MieCoefficient(','float MiePhase(','float3 MieAbsorption(','float3 OzoneAbsorption(','float3 Scattering(')){$taskFunctions+=Extract $taskScattering $signature}
foreach($signature in @('float3 TransmittanceToAtmosphere(','float3 TransmittanceToSun(','float3 SurfaceSunTransmittance(','float3 Transmittance(','float AtmosphereStepIntegral(','float3 AtmosphereStepIntegral(','float3 IntegralMultiScattering(','float3 GetMultiScattering(','float3 GetAtmosphereSegment(','float3 GetSkyView(')){$taskFunctions+=Extract $taskMarch $signature}
$taskConverted=foreach($s in $taskFunctions){
 $s=$s.Replace('UNITY_SAMPLE_TEX2D_LOD(_TransmittanceLUT, uv, 0.0).rgb','Sample(transmittance,256,64,uv)')
 $s=$s.Replace('UNITY_SAMPLE_TEX2D_LOD(_MultiscatteringLUT, uv, 0.0).rgb','Sample(multiple,32,32,uv)')
 $s=$s.Replace('float3 RandomSphereSamples[64] = {','float3[] RandomSphereSamples = new Vector3[] {')
 if($s.StartsWith('float3 TransmittanceToSun(')){$s=$s.Replace('return 0.0;','return Vector3.Zero;')}
 $s=$s.Replace('p, dir, entry, exit)','p, dir, out entry, out exit)').Replace('eyePos, viewDir, entry, exit)','eyePos, viewDir, out entry, out exit)')
 $s=$s.Replace('lightDir, maxDis, transmittance)','lightDir, maxDis, out transmittance)')
 $s=$s.Replace('float(N_SAMPLE)','(float)N_SAMPLE').Replace('float(N_DIRECTION)','(float)N_DIRECTION')
 $s=$s.Replace('(3.996 * 1e-6).xxx','new Vector3(3.996e-6f)').Replace('((4.440 - 3.996) * 1e-6).xxx','new Vector3((4.440f - 3.996f) * 1e-6f)')
 $s=$s.Replace('#define sigma_lambda (float3(0.650f, 1.881f, 0.085f)) * 1e-6','float3 sigma_lambda = float3(0.650f, 1.881f, 0.085f) * 1e-6;')
 $s=$s.Replace('float3 sum = 0.0;','float3 sum = new Vector3(0);')
 $s=$s.Replace('1.0 / (1.0 - f_ms)','1.0 / (Vector3.One - f_ms)')
 $s=$s.Replace('float3(', 'new Vector3(').Replace('float2(', 'new Vector2(').Replace('float3','Vector3').Replace('float2','Vector2')
 $s=$s.Replace('const Vector3','Vector3')
 $s=[regex]::Replace($s,'\.([xyzw])\b',{param($m)'.'+$m.Groups[1].Value.ToUpperInvariant()})
 $s=[regex]::Replace($s,'(?<![\w.])(\d+\.\d+(?:[eE][+-]?\d+)?)(?![\w.])','${1}f')
 $s=[regex]::Replace($s,'(?<![\w.])(\d+[eE][+-]?\d+)(?!\w)','${1}f')
 $s=$s.Replace('1.0f / (Vector3.One - f_ms)','Vector3.One / (Vector3.One - f_ms)')
 'static '+$s
}
$taskHost=@'
using System;
using System.Numerics;
using System.Collections.Generic;
public static class DaylightEnergyChecks {
 const float PI=(float)Math.PI;
 struct AtmosphereParameter {
  public float SeaLevel,PlanetRadius,AtmosphereHeight,SunDiskAngle,RayleighScatteringScale,RayleighScatteringScalarHeight;
  public float MieScatteringScale,MieScatteringScalarHeight,MieAnisotropy,OzoneAbsorptionScale,OzoneLevelCenterHeight,OzoneLevelWidth;
  public Vector3 SolarIrradiance;
 }
 static AtmosphereParameter atmosphere=new AtmosphereParameter{SeaLevel=0,PlanetRadius=6360000,AtmosphereHeight=60000,SunDiskAngle=1,
  RayleighScatteringScale=1,RayleighScatteringScalarHeight=8000,MieScatteringScale=1,MieScatteringScalarHeight=1200,MieAnisotropy=.8f,
  OzoneAbsorptionScale=1,OzoneLevelCenterHeight=25000,OzoneLevelWidth=15000,SolarIrradiance=new Vector3(1.2f)};
 static AtmosphereParameter GetAtmosphereParameter()=>atmosphere;
 static Vector3[] transmittance=new Vector3[256*64],multiple=new Vector3[32*32];
 static float abs(float x)=>Math.Abs(x);static float max(float a,float b)=>Math.Max(a,b);static float min(float a,float b)=>Math.Min(a,b);
 static float sqrt(float x)=>(float)Math.Sqrt(x);static float sin(float x)=>(float)Math.Sin(x);
 static float pow(float x,float y)=>(float)Math.Pow(x,y);static float exp(float x)=>(float)Math.Exp(x);
 static float clamp(float x,float a,float b)=>Math.Clamp(x,a,b);
 static float length(Vector3 x)=>x.Length();static Vector3 normalize(Vector3 x)=>Vector3.Normalize(x);
 static float dot(Vector3 a,Vector3 b)=>Vector3.Dot(a,b);
 static Vector3 exp(Vector3 v)=>new Vector3(exp(v.X),exp(v.Y),exp(v.Z));
 static Vector3 Sample(Vector3[] data,int w,int h,Vector2 uv){
  float x=clamp(uv.X*w-.5f,0,w-1),y=clamp(uv.Y*h-.5f,0,h-1);int x0=(int)x,y0=(int)y,x1=Math.Min(w-1,x0+1),y1=Math.Min(h-1,y0+1);
  return Vector3.Lerp(Vector3.Lerp(data[y0*w+x0],data[y0*w+x1],x-x0),Vector3.Lerp(data[y1*w+x0],data[y1*w+x1],x-x0),y-y0);
 }
 static float Y(Vector3 x)=>dot(x,new Vector3(.2126f,.7152f,.0722f));
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 __FUNCTIONS__
 static void Bake(bool multiscatter=true){
  var p=atmosphere;
  for(int y=0;y<64;y++)for(int x=0;x<256;x++){
   float mu,r;UvToTransmittanceLutParams(p.PlanetRadius,p.PlanetRadius+p.AtmosphereHeight,new Vector2((x+.5f)/256,(y+.5f)/64),out mu,out r);
   var d=new Vector3(sqrt(max(0,1-mu*mu)),mu,0);var eye=new Vector3(0,r,0);
   float dis=RayIntersectSphere(Vector3.Zero,p.PlanetRadius+p.AtmosphereHeight,eye,d);
   transmittance[y*256+x]=Transmittance(p,eye,eye+d*dis);
  }
  if(!multiscatter)return;
  for(int y=0;y<32;y++)for(int x=0;x<32;x++){
   float mu=2*(x+.5f)/32-1;var d=new Vector3(sqrt(max(0,1-mu*mu)),mu,0);
   multiple[y*32+x]=IntegralMultiScattering(p,new Vector3(0,p.PlanetRadius+(y+.5f)*p.AtmosphereHeight/32,0),d);
  }
 }
 static Vector3 SkyIrradiance(Vector3 sun){
  // Independent midpoint quadrature over solid angle; not the IBL importance sampler.
  const int elevations=48,azimuths=96;Vector3 e=Vector3.Zero;var eye=AtmosphereCameraPosition(2,atmosphere.SeaLevel,atmosphere.PlanetRadius);
  for(int y=0;y<elevations;y++){
   float mu=(y+.5f)/elevations,r=sqrt(1-mu*mu);
   for(int x=0;x<azimuths;x++){
    float phi=2*PI*(x+.5f)/azimuths;var d=new Vector3(r*(float)Math.Cos(phi),mu,r*sin(phi));
    e+=GetSkyView(atmosphere,eye,d,sun,-1)*mu*(2*PI/elevations/azimuths);
   }
  }
  return e;
 }
 public static string[] Run(){
  Bake();var lines=new List<string>();
  // Independent Beer-Lambert reference for a vertical column with exponential
  // Rayleigh/Mie densities and a triangular ozone profile, in metres.
  var rayleigh=new Vector3(5.802e-6f,13.558e-6f,33.1e-6f)*8000*(1-exp(-60000f/8000));
  var mie=new Vector3(4.440e-6f)*1200*(1-exp(-60000f/1200));
  var ozone=new Vector3(.650e-6f,1.881e-6f,.085e-6f)*15000;
  var reference=exp(-(rayleigh+mie+ozone));
  Check(Vector3.Distance(SurfaceSunTransmittance(new Vector3(0,2,0),Vector3.UnitY),reference)<.01,"Solar transmission differs from analytic vertical optical depth");
  foreach(float degrees in new[]{90f,45f,10f}){
   float a=degrees*PI/180;var sun=new Vector3((float)Math.Cos(a),sin(a),0);
   var direct=atmosphere.SolarIrradiance*SurfaceSunTransmittance(new Vector3(0,2,0),sun)*sun.Y;
   var sky=SkyIrradiance(sun);float share=Y(sky)/Y(direct+sky);
   Check(float.IsFinite(share)&&share>0&&share<1,"Daylight energy nonfinite");
   if(degrees==90){Check(share>.02&&share<.4,"Noon sky incorrectly dominates sun");
    Check(Math.Abs((direct+sky).Z/(direct+sky).X-1)<.25,"Noon white plane strongly tinted");}
   // Removing the legacy gain changes sky, not sunlight: demonstrate former imbalance.
   float previousShare=Y(sky*31.4f)/Y(direct+sky*31.4f);
   lines.Add(string.Format("Sun {0} deg: horizontal direct={1:F0} lux, sky={2:F0} lux, sky share={3:P1}; legacy gain share={4:P1}; white RGB={5}",degrees,Y(direct)*100000,Y(sky)*100000,share,previousShare,(direct+sky)/PI));
   var before=sky;atmosphere.SolarIrradiance*=2;var doubled=SkyIrradiance(sun);atmosphere.SolarIrradiance/=2;
   Check(Vector3.Distance(doubled,before*2)<1e-5,"Sky does not scale with shared sun input");
  }
  var p=atmosphere;
  foreach(float radius in new[]{.2725f,1f,2f}){
   p.SunDiskAngle=radius;var disk=SolarDiskRadiance(p);float sine=sin(radius*PI/180);
   Check(Vector3.Distance(disk*(PI*sine*sine),p.SolarIrradiance)<1e-6,"Disk radiance does not integrate to sun illuminance");
  }
  Check(SurfaceSunTransmittance(new Vector3(0,2,0),-Vector3.UnitY)==Vector3.Zero,"Night receives direct sunlight");
  Check(SurfaceSunTransmittance(new Vector3(0,70000,0),Vector3.UnitY)==Vector3.One,"Space loses sunlight");
  atmosphere.SolarIrradiance=Vector3.Zero;Check(SkyIrradiance(Vector3.UnitY)==Vector3.Zero,"Disabled sun leaves sky energy");
  atmosphere=p;atmosphere.RayleighScatteringScale=atmosphere.MieScatteringScale=atmosphere.OzoneAbsorptionScale=0;Bake(false);
  Check(Vector3.Distance(SurfaceSunTransmittance(new Vector3(0,2,0),Vector3.UnitY),Vector3.One)<1e-6,"Vacuum attenuates sunlight");
  lines.Add("PASS: production HLSL density/phase, 256x64 transmittance + 32x32 multiple-scattering LUTs and 32-step sky integrator; independent 48x96 hemisphere quadrature, solar linearity, disk energy, planetary occlusion, space, vacuum and disabled sun.");
  return lines.ToArray();
 }
}
'@
Add-Type -TypeDefinition $taskHost.Replace('__FUNCTIONS__',($taskConverted -join "`n"))
[DaylightEnergyChecks]::Run()
# Check the actual consumer and upload path, including fog-disabled cameras.
$taskLighting=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Lighting.hlsl'))
if(-not $taskLighting.Contains('_DirectionalLightColor.rgb * SurfaceSunTransmittance(surface.P, surface.L)')){throw 'Direct sun bypasses atmosphere'}
$taskRuntime=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs'))
if($taskRuntime.Contains('_SunLightIntensity') -or $taskRuntime.Contains('_SunLightColor')){throw 'Independent atmosphere energy control remains'}
foreach($name in @('_SeaLevel','_PlanetRadius','_AtmosphereHeight')){if(-not $taskRuntime.Contains("cmd.SetGlobalFloat(ShaderProperties.$name,")){throw "Missing surface atmosphere global $name"}}
if(-not $taskRuntime.Contains('cmd.SetGlobalTexture(ShaderProperties._TransmittanceLUT, m_transmittanceLUT)')){throw 'Missing direct solar transmission LUT'}
Write-Output 'PASS: shared solar input, surface atmospheric attenuation and optical globals independent of fog/sky camera toggles.'
