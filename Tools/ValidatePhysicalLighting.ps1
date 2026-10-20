$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
function ReadSource($name){[Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes((Join-Path $taskRoot ('Assets/MaxRendererPipeline/'+$name))))}
function Block($source,$signature){
 $a=$source.IndexOf($signature);if($a -lt 0){throw "Missing $signature"};$b=$source.IndexOf('{',$a);$depth=1
 for($end=$b+1;$depth;$end++){if($source[$end] -eq '{'){$depth++};if($source[$end] -eq '}'){$depth--}}
 [regex]::Replace($source.Substring($a,$end-$a),'//[^\r\n]*','')
}
$taskLighting=ReadSource 'ShaderLibrary/Realtime/Lighting.hlsl';$taskBRDF=ReadSource 'ShaderLibrary/Realtime/BRDF.hlsl'
$taskFunctions=@()
foreach($name in @('float DistributionGGX(','float VisibilitySmithGGXCorrelated(','float3 fresnelSchlick(','float3 CookTorranceBRDF(')){$taskFunctions+=Block $taskBRDF $name}
$taskFunctions+=Block (ReadSource 'ShaderLibrary/Realtime/LightInput.hlsl') 'float DistanceAtten('
foreach($name in @('float3 PBR_DirectLitDirectionalLight(','float3 PBR_DirectLitPointLight(')){$taskFunctions+=Block $taskLighting $name}
$taskFunctions+=Block (ReadSource 'ShaderLibrary/Realtime/IBLValidation.hlsl') 'float3 FiniteIBLColor('
$taskGI=ReadSource 'ShaderLibrary/Realtime/GlobalIllumination.hlsl'
$taskFunctions+=Block $taskGI 'float3 fresnelSchlickRoughness('
foreach($name in @('float3 GetDiffuseIBL(','float3 GetSpecularIBL(')){
 $s=Block $taskGI $name
 $s=[regex]::Replace($s,'texCUBElod\(_IBLSpec,[^;]+\.rgb','sampleSpec')
 $s=[regex]::Replace($s,'texCUBElod\(_IBLDiffuse,[^;]+\.rgb','sampleDiffuse')
 $s=[regex]::Replace($s,'UNITY_SAMPLE_TEX2D\(_BRDFLUT,[^;]+\.rg','sampleBRDF')
 $s=$s.Replace('envBRDF.x','envBRDF.X').Replace('envBRDF.y','envBRDF.Y').Replace('+ envBRDF.Y','+ new Vector3(envBRDF.Y)')
 $s=$s.Replace('float2','Vector2')
 $taskFunctions+=$s
}
$taskConverted=foreach($s in $taskFunctions){
 $s=$s.Replace('if (!any(_DirectionalLightColor.rgb > 0.0))','if (_DirectionalLightColor.X <= 0 && _DirectionalLightColor.Y <= 0 && _DirectionalLightColor.Z <= 0)')
 $s=$s.Replace('_DirectionalLightDirection.xyz','XYZ(_DirectionalLightDirection)').Replace('_DirectionalLightColor.rgb','XYZ(_DirectionalLightColor)')
 $s=$s.Replace('pointLight.positionRange.xyz','XYZ(pointLight.positionRange)').Replace('pointLight.color.rgb','XYZ(pointLight.color)')
 $s=$s.Replace('pointLightPos.xyz','pointLightPos').Replace('pointLight.positionRange.w','pointLight.positionRange.W')
 $s=$s.Replace('1.0 - F0','new Vector3(1) - F0')
 $s=$s.Replace('max(float3(1.0 - roughness, 1.0 - roughness, 1.0 - roughness), F0)','Vector3.Max(new Vector3(1.0 - roughness), F0)')
 $s=$s.Replace('float3(', 'new Vector3(').Replace('float3','Vector3')
 $s=[regex]::Replace($s,'\.([xyz])\b',{param($m)'.'+$m.Groups[1].Value.ToUpperInvariant()})
 $s=[regex]::Replace($s,'(?<![\w.])(\d+\.\d+(?:[eE][+-]?\d+)?)(?![\w.])','${1}f')
 $s=[regex]::Replace($s,'(?<![\w.])(\d+[eE][+-]?\d+)(?!\w)','${1}f')
 'public static '+$s
}
$taskShaderHost=@'
namespace PhysicalShaderMath {
 using System;using System.Numerics;
 public static class Check {
  const float PI=(float)Math.PI;
  public struct Surface {public Vector3 P,L,V,F0,N,albedo;public float metalness,roughness;}
  public struct PointLight {public Vector4 positionRange,color;}
  static Vector4 _DirectionalLightColor,_DirectionalLightDirection;
  static int _PointLightCount;
  static PointLight point;
  static Vector3 sampleSpec,sampleDiffuse;
  static Vector2 sampleBRDF;
  static bool isfinite(float value)=>float.IsFinite(value);
  static PointLight GetPointLight(int i)=>point;
  static Vector3 XYZ(Vector4 v)=>new Vector3(v.X,v.Y,v.Z);
  static float dot(Vector3 a,Vector3 b)=>Vector3.Dot(a,b);
  static Vector3 normalize(Vector3 a)=>Vector3.Normalize(a);
  static float max(float a,float b)=>Math.Max(a,b);
  static float pow(float a,float b)=>(float)Math.Pow(a,b);
  static float saturate(float a)=>Math.Clamp(a,0,1);
  static float rcp(float a)=>1/a;
  static float rsqrt(float a)=>1/(float)Math.Sqrt(a);
  static float sqrt(float a)=>(float)Math.Sqrt(a);
  static Vector3 SurfaceSunTransmittance(Vector3 p,Vector3 l)=>Vector3.One;
  __FUNCTIONS__
  public static void Run(){
   var s=new Surface{P=Vector3.Zero,N=Vector3.UnitY,V=Vector3.UnitY,F0=new Vector3(.04f),albedo=new Vector3(.18f),metalness=0,roughness=.8f};
   _PointLightCount=1;point=new PointLight{positionRange=new Vector4(0,1,0,10000),color=new Vector4(.01f,.01f,.01f,0)};
   _DirectionalLightColor=point.color;_DirectionalLightDirection=new Vector4(0,1,0,0);
   var a=PBR_DirectLitDirectionalLight(s);var b=PBR_DirectLitPointLight(s);
   foreach(float cosine in new[]{-1.000001f,0f,.5f,1f,1.000001f,1.2f}){
    var f=fresnelSchlick(cosine,s.F0);var fr=fresnelSchlickRoughness(cosine,s.F0,s.roughness);
    Require(float.IsFinite(f.X+fr.X)&&f.X>=.04f&&f.X<=1&&fr.X>=.04f&&fr.X<=.2f+1e-6f,"Fresnel cosine overflow");
   }
   foreach(float r in new[]{0f,.001f,.05f,.1f,.5f,1f}){
    foreach(float h in new[]{.9f,1f,1.000001f}){
     float distribution=DistributionGGX(s.N,s.N*h,r);
     Require(float.IsFinite(distribution)&&distribution>=0,"GGX peak produces a non-finite value");
    }
   }
   float peak=DistributionGGX(s.N,s.N,.05f);
   Require(Math.Abs(peak-1/(Math.PI*Math.Pow(.05,4)))/peak<2e-6,"GGX minimum material roughness changed");
   // Independent double-precision lambda definition, not the shader's combined expression.
   foreach(float r in new[]{.05f,.1f,.3f,.6f,1f})
    foreach(float nv in new[]{.0001f,.01f,.3f,.5f,.8f,1f})
     foreach(float nl in new[]{.0001f,.01f,.3f,.5f,.8f,1f}){
      double a2=Math.Pow(r,4);
      double lv=(Math.Sqrt(1+a2*(1/(nv*(double)nv)-1))-1)/2;
      double ll=(Math.Sqrt(1+a2*(1/(nl*(double)nl)-1))-1)/2;
      double expected=1/(4*nv*(double)nl*(1+lv+ll));
      float actual=VisibilitySmithGGXCorrelated(nv,nl,r);
      Require(float.IsFinite(actual)&&Math.Abs(actual/expected-1)<2e-6,"Correlated Smith-GGX visibility differs from lambda reference");
      Require(actual==VisibilitySmithGGXCorrelated(nl,nv,r),"Smith-GGX light/view reciprocity");
     }
   foreach(float c in new[]{-1f,0f})
    Require(VisibilitySmithGGXCorrelated(c,.5f,.3f)==0&&VisibilitySmithGGXCorrelated(.5f,c,.3f)==0,"Back-facing specular must remain zero");
   // Measured independent Cycles white-metal card, N.L=N.V=.5 and roughness=.3.
   var card=new Surface{N=Vector3.UnitY,L=Vector3.Normalize(new Vector3(-.8660254038f,.5f,0)),
    V=Vector3.Normalize(new Vector3(.8660254038f,.5f,0)),roughness=.3f,metalness=1,F0=Vector3.One,albedo=Vector3.One};
   float cardValue=CookTorranceBRDF(card).X*.5f;
   Require(Math.Abs(cardValue/19.41429138-1)<2e-5,"Direct GGX differs from independent Cycles single-scatter card");
   var opposite=s;opposite.L=-opposite.V;
   Require(CookTorranceBRDF(opposite)==Vector3.Zero,"Opposite light/view normalizes a zero half vector");
   // A normal-map lobe can turn away from V while the visible geometric plane faces V.
   // Crossing N.V=0 must not discard all of the directly lit diffuse term.
   var tilted=s;tilted.V=Vector3.Normalize(new Vector3(0,.3f,1));
   var horizonHalf=Vector3.Normalize(tilted.V+Vector3.UnitY);
   var horizonDiffuse=(Vector3.One-fresnelSchlick(Vector3.Dot(horizonHalf,tilted.V),tilted.F0))*tilted.albedo/(float)Math.PI;
   for(int i=0;i<=400;i++){
    tilted.N=Vector3.Normalize(new Vector3(0,1,-.35f+i*.00025f));
    var lit=PBR_DirectLitDirectionalLight(tilted);
    Require(float.IsFinite(lit.X)&&lit.X>0,"Normal-map N.V horizon discards directly lit diffuse");
    // Exact Smith visibility has a nonzero grazing limit on the front side.
    // The regression concerns losing diffuse, not continuity of total specular at N.V=0.
    Require(lit.X>=horizonDiffuse.X*.01f*tilted.N.Y*.9999f,"Normal-map horizon discards the Lambert diffuse floor");
   }
   tilted.N=Vector3.Normalize(new Vector3(0,1,-.35f));
   var tiltedHalf=Vector3.Normalize(tilted.V+Vector3.UnitY);
   var expectedDiffuse=(Vector3.One-fresnelSchlick(Vector3.Dot(tiltedHalf,tilted.V),tilted.F0))*tilted.albedo/(float)Math.PI;
   Require(Vector3.Distance(PBR_DirectLitDirectionalLight(tilted),expectedDiffuse*.01f*tilted.N.Y)<1e-8,"Back-facing shading normal suppresses Lambert diffuse");
   Require(Vector3.Distance(PBR_DirectLitDirectionalLight(tilted),PBR_DirectLitPointLight(tilted))<1e-8,"Point-light normal-map diffuse differs from directional light");
   tilted.metalness=1;tilted.F0=tilted.albedo;
   Require(PBR_DirectLitDirectionalLight(tilted)==Vector3.Zero,"Back-facing specular should remain zero");
   Require(Vector3.Distance(a,b)<1e-8,"1000 lux directional != 1000 cd at 1m");
   sampleDiffuse=new Vector3(4,8,16);sampleSpec=new Vector3(4,8,16);sampleBRDF=new Vector2(.5f,.01f);
   var normalDiffuse=GetDiffuseIBL(s.N,1,.04f,s.roughness,s.albedo,new Vector3(.96f));
   var normalSpec=GetSpecularIBL(s.N,s.roughness,1,s.F0);
   Require(normalDiffuse.Z>normalDiffuse.X&&normalSpec.Z>normalSpec.X,"Valid HDR IBL was discarded");
   foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity}){
    sampleDiffuse=new Vector3(invalid);sampleSpec=new Vector3(invalid);
    var diffuse=GetDiffuseIBL(s.N,1,.04f,s.roughness,s.albedo,new Vector3(.96f));
    var spec=GetSpecularIBL(s.N,s.roughness,1,s.F0);
    Require(a+diffuse+spec==a,"Bad IBL eliminates valid direct lighting");
    sampleSpec=new Vector3(4,8,16);sampleBRDF=new Vector2(invalid);
    Require(GetSpecularIBL(s.N,s.roughness,1,s.F0)==Vector3.Zero,"Bad BRDF LUT poisons lighting");
    sampleBRDF=new Vector2(.5f,.01f);
   }
   sampleDiffuse=new Vector3(-1,4,16);
   var partial=GetDiffuseIBL(s.N,1,.04f,s.roughness,s.albedo,new Vector3(.96f));
   Require(partial.X==0&&partial.Y>0&&partial.Z>partial.Y,"Valid IBL channels lost");
   Require(GetDiffuseIBL(s.N,1,.04f,s.roughness,s.albedo,new Vector3(float.NaN))==Vector3.Zero,"Invalid Fresnel still poisons diffuse result");
   point.positionRange.Y=2;var farther=PBR_DirectLitPointLight(s);
   Require(Vector3.Distance(b,farther*4)<1e-8,"Point light does not follow inverse square falloff");
   point.positionRange.Y=0;var center=PBR_DirectLitPointLight(s);
   Require(float.IsFinite(center.X+center.Y+center.Z),"Point center produces NaN");
   Require(DistanceAtten(0,0)==0&&DistanceAtten(1,0)==0&&DistanceAtten(4,4)==0,"Range boundary/zero range");
   for(int i=0;i<1000;i++){
    float d=.01f+i*.01f,range=12;
    double expected=Math.Pow(Math.Max(0,1-d*d/(range*range)),2)/Math.Max(d*d,.001);
    Require(Math.Abs(DistanceAtten(d*d,range*range)-expected)<1e-4*Math.Max(1,expected),"Distance falloff regression");
   }
  }
  static void Require(bool b,string m){if(!b)throw new Exception(m);}
 }
}
'@
$taskHost=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'DynamicSkyIBLTestHost.cs'))+"`nnamespace UnityEngine {public class SerializeField:Attribute {}}"
$taskSources=@('Runtime/MaxPhysicalLight.cs','Runtime/SunLightData.cs','Runtime/MaxLightConfigurator.cs','Runtime/Passes/MaxUpdateGIPass.cs') | ForEach-Object {[regex]::Replace((ReadSource $_),'(?m)^using [^;]+;\r?\n','')}
$taskSettings=Block (ReadSource 'Runtime/Passes/MaxColorOutputPass.cs') 'public sealed class ColorOutputSettings'
$taskSettings="namespace MaxSRP {public enum ToneMappingMode {None,ACES}`n"+$taskSettings+'}'
$taskChecks=@'
namespace MaxSRP {
 public static class PhotometryChecks {
  static void Close(float a,float b,string message){Checks.Require(Math.Abs(a-b)<Math.Max(1e-6f,Math.Abs(b)*2e-6f),message);}
  public static string Run(){
   var light=new Light{type=LightType.Point,intensity=1000};Close(LightPhotometry.PointCandela(light),1000,"Untagged point is candela");
   light.photometry=new MaxPhysicalLight{PointUnit=PointLightUnit.Lumen};light.intensity=4*(float)Math.PI*1000;
   Close(LightPhotometry.PointCandela(light),1000,"Lumen to candela");Close(LightPhotometry.PointColor(light).r,.01f,"Point HDR scale");
   light.color=new Color(.5f,.8f,1);Close(LightPhotometry.PointColor(light).r,.0021404115f,"Linear color conversion");
   light.useColorTemperature=true;Close(LightPhotometry.PointColor(light).r,.0010702057f,"Color temperature applied once");light.useColorTemperature=false;
   light.photometry.PointUnit=PointLightUnit.Candela;light.intensity=1000;Close(LightPhotometry.PointCandela(light),1000,"Candela metadata");
   light.intensity=-10;Close(LightPhotometry.PointCandela(light),0,"Negative intensity");
   // The actual configurator must ignore finalColor and upload the physical Light intensity.
   light.intensity=1000;var list=new[]{new VisibleLight{light=light,lightType=LightType.Point,range=10,finalColor=new Color(999,999,999)}};
   var culling=new CullingResults{visibleLights=new Unity.Collections.NativeArray<VisibleLight>(list)};
   new MaxLightConfigurator().SetupMultiShaderLightingParams(new ScriptableRenderContext(),ref culling,default);
   var uploaded=(Vector4[])Shader.Globals[Shader.PropertyToID("_PointLightColors")];Close(uploaded[0].x,.0021404115f,"Configurator retains unitless finalColor");
   // Both direct paths must shade identically for equal surface illuminance.
   PhysicalShaderMath.Check.Run();
   var settings=new ColorOutputSettings();float e=settings.GetExposureMultiplier();Close(e,100000f/(1.2f*32768),"EV100 daylight scale");
   settings.ExposureEV=1;Close(settings.GetExposureMultiplier(),2*e,"Exposure compensation sign");
   settings.CameraEV100=16;Close(settings.GetExposureMultiplier(),e,"EV100 and compensation cancel");
   settings.ExposureEV=0;
   for(int i=-10;i<24;i++){settings.CameraEV100=i;float value=settings.GetExposureMultiplier();settings.CameraEV100=i+1;Close(settings.GetExposureMultiplier(),value/2,"Camera EV100 step");}
   settings.CameraEV100=15;
   // Independent unit cancellation from physical luminance through the shared HDR scale.
   double day18=.18*100000/Math.PI,room18=.18*500/Math.PI;
   double day=day18/100000*settings.GetExposureMultiplier();settings.CameraEV100=8;
   double room=room18/100000*settings.GetExposureMultiplier();
   Checks.Require(day>.14&&day<.15&&room>.09&&room<.10,"Gray card physical exposure");
   return "PASS: actual units/configurator/BRDF/exposure; lumen-candela, HDR normalization, color/temperature, equal sun-point illuminance, 401 normal-map horizon crossings, Fresnel/zero-half-vector/GGX boundaries and invalid IBL isolation, inverse-square/range/center, EV100/compensation, daylight and indoor gray cards (before tone mapping: "+day+", "+room+").";
  }
 }
}
'@
Add-Type -TypeDefinition ("#pragma warning disable CS0649`n"+$taskHost+"`n"+($taskSources -join "`n")+"`n"+$taskSettings+"`n"+$taskShaderHost.Replace('__FUNCTIONS__',($taskConverted -join "`n"))+"`n"+$taskChecks)
[MaxSRP.PhotometryChecks]::Run()
$taskOutput=ReadSource 'Runtime/Passes/MaxColorOutputPass.cs'
if(-not $taskOutput.Contains('settings.GetExposureMultiplier()')){throw 'Final output bypasses physical exposure'}
$taskScene=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/Scenes/SkyboxScene.unity'))
$taskExpected=@{'2138677392'=$null;'44756188'=7500;'922907208'=15000;'2144985078'=1500}
foreach($id in $taskExpected.Keys){
 $block=[regex]::Match($taskScene,"(?ms)--- !u!108 &$id\r?\nLight:.*?(?=^---|\z)").Value
 if($null -eq $taskExpected[$id]){
  # The user can adjust solar illuminance without invalidating unit migration.
  $value=[double]::Parse([regex]::Match($block,'(?m)^  m_Intensity: ([^\r\n]+)').Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture)
  if(-not [double]::IsFinite($value) -or $value -le 0){throw "Invalid scene solar lux $value"}
 } elseif(-not $block.Contains('  m_Intensity: '+$taskExpected[$id])){throw "Scene intensity $id"}
}
if(([regex]::Matches($taskScene,'guid: 62f4526ec59b428eae6bff439f557bf3')).Count -ne 4){throw 'Missing explicit scene light units'}
$taskIds=[regex]::Matches($taskScene,'(?m)^--- !u!\d+ &(\d+)') | ForEach-Object {$_.Groups[1].Value}
if(($taskIds | Group-Object | Where-Object Count -gt 1)){throw 'Duplicate scene fileID'}
$taskAsset=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Max Renderer Pipeline Asset.asset'))
if(-not $taskAsset.Contains('CameraEV100: 15') -or $taskAsset.Contains('m_sunIlluminanceLux')){throw 'Physical scene exposure/solar source not migrated'}
Write-Output 'PASS: sky scene four light units/intensities, unique scene identities, EV100=15 asset and single per-light solar intensity.'
