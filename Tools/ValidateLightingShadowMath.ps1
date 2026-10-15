$ErrorActionPreference='Stop'
$taskRoot=Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime'
function Read-TaskShader([string]$name) {
    [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes((Join-Path $taskRoot $name)))
}
function Get-TaskFunction([string]$source,[string]$signature) {
    $a=$source.IndexOf($signature); if($a -lt 0){throw "Missing function: $signature"}
    $brace=$source.IndexOf('{',$a); $depth=1
    for($end=$brace+1;$depth -gt 0;$end++){if($source[$end] -eq '{'){$depth++};if($source[$end] -eq '}'){$depth--}}
    [regex]::Replace($source.Substring($a,$end-$a),'//[^\r\n]*','')
}
function Convert-TaskFunction([string]$source,[bool]$reversed,[bool]$rgb,[bool]$astc,[double]$nearClip) {
    $enabled=$true; $stack=[Collections.Generic.Stack[object]]::new(); $lines=[Collections.Generic.List[string]]::new()
    foreach($line in ($source -split "`n")) {
        if($line -match '^\s*#if defined\(([^)]+)\)') {
            $condition=switch($Matches[1]) {'UNITY_REVERSED_Z' {$reversed};'UNITY_NO_DXT5nm' {$rgb};'UNITY_ASTC_NORMALMAP_ENCODING' {$astc};default {throw 'Unknown shader branch'}}
            $stack.Push(@($enabled,$condition));$enabled=$enabled -and $condition
        } elseif($line -match '^\s*#else') {$state=$stack.Peek();$enabled=$state[0] -and -not $state[1]
        } elseif($line -match '^\s*#endif') {$state=$stack.Pop();$enabled=$state[0]
        } elseif($enabled) {$lines.Add($line)}
    }
    $s=$lines -join "`n"
    $s=$s.Replace('UNITY_NEAR_CLIP_VALUE',"$nearClip.0").Replace('(float3x3)','')
    $s=$s.Replace('p, dir, entry, exit)', 'p, dir, out entry, out exit)')
    $s=$s.Replace('return UNITY_SAMPLE_TEX2D_LOD(_TransmittanceLUT, uv, 0.0).rgb;', 'return SampleTransmittanceLut(uv);')
    $s=$s.Replace('(3.996 * 1e-6).xxx','new Vector3(3.996e-6f)').Replace('((4.440 - 3.996) * 1e-6).xxx','new Vector3((4.440f - 3.996f) * 1e-6f)')
    $s=$s.Replace('float3(', 'new Vector3(').Replace('float2(', 'new Vector2(').Replace('float3','Vector3').Replace('float2','Vector2').Replace('float4','Vector4').Replace('const Vector3','Vector3')
    $s=$s.Replace('packedNormal.xyz','new Vector3(packedNormal.X,packedNormal.Y,packedNormal.Z)').Replace('packedNormal.wy','new Vector2(packedNormal.W,packedNormal.Y)').Replace('packedNormal.xy','new Vector2(packedNormal.X,packedNormal.Y)')
    $s=$s.Replace('packedNormal.x','packedNormal.X').Replace('packedNormal.w','packedNormal.W').Replace('unity_OrthoParams.w','unity_OrthoParams.W')
    $s=[regex]::Replace($s,'(?<![\w.])(\d+\.\d+)(?![\w.])','${1}f')
    $s=[regex]::Replace($s,'(?<![\w.])(\d+[eE][+-]?\d+)(?!\w)','${1}f')
    $s=$s.Replace('packedNormal.Z) * 2.0f - 1.0f','packedNormal.Z) * 2.0f - Vector3.One')
    $s=$s.Replace('packedNormal.Y) * 2.0f - 1.0f','packedNormal.Y) * 2.0f - Vector2.One')
    $s=$s.Replace('float2 xy','Vector2 xy')
    'static '+$s
}
$taskSpace=Read-TaskShader 'SpaceTransform.hlsl'; $taskShadow=Read-TaskShader 'Shadow.hlsl'; $taskPacking=Read-TaskShader 'Packing.hlsl'; $taskLighting=Read-TaskShader 'Lighting.hlsl'; $taskScattering=Read-TaskShader 'AtomsphereScattering/Scattering.hlsl'
$taskAtmosHelper=Read-TaskShader 'AtomsphereScattering/Helper.hlsl';$taskMarch=Read-TaskShader 'AtomsphereScattering/RayMarching.hlsl'
$taskFunctions=@(
    (Get-TaskFunction $taskSpace 'float DeviceDepthToClipDepth('),
    (Get-TaskFunction $taskSpace 'float ClipDepthToDeviceDepth('),
    (Get-TaskFunction $taskShadow 'bool IsShadowOccluded('),
    (Get-TaskFunction $taskShadow 'float ShadowDistanceFromNear('),
    (Get-TaskFunction $taskShadow 'float BiasedShadowDepth('),
    (Get-TaskFunction $taskShadow 'float PCSSFilterRadius('),
    (Get-TaskFunction $taskPacking 'float3 DecodeNormalFromTexture('),
    (Get-TaskFunction $taskLighting 'float3 GetSurfaceViewDirection('),
    (Get-TaskFunction $taskScattering 'float3 MieCoefficient('),
    (Get-TaskFunction $taskScattering 'float3 MieAbsorption('),
    (Get-TaskFunction $taskAtmosHelper 'bool AtmosphereRayInterval('),
    (Get-TaskFunction $taskAtmosHelper 'float2 GetTransmittanceLutUv('),
    (Get-TaskFunction $taskMarch 'float3 TransmittanceToAtmosphere(')
)
$taskHeader=@'
using System;
using System.Numerics;
'@
$taskBody=@'
 struct AtmosphereParameter {public float MieScatteringScalarHeight,MieScatteringScale,PlanetRadius,AtmosphereHeight;}
 static Vector4 unity_OrthoParams;
 static Matrix4x4 unity_MatrixInvV=Matrix4x4.Identity;
 static Vector3 _WorldSpaceCameraPos;
 static float max(float a,float b)=>Math.Max(a,b);
 static float min(float a,float b)=>Math.Min(a,b);
 static float length(Vector3 p)=>p.Length();
 static float dot(Vector3 a,Vector3 b)=>Vector3.Dot(a,b);
 static float clamp(float x,float a,float b)=>Math.Max(a,Math.Min(b,x));
 static Vector3 SampleTransmittanceLut(Vector2 uv){Check(!float.IsNaN(uv.X+uv.Y)&&uv.X>=0&&uv.X<=1.001f&&uv.Y>=0&&uv.Y<=1.001f,"Invalid exterior LUT coordinate");return new Vector3(.25f,.5f,.75f);}
 static float sqrt(float a)=>(float)Math.Sqrt(a);
 static float exp(float a)=>(float)Math.Exp(a);
 static float saturate(float a)=>Math.Max(0,Math.Min(1,a));
 static float lerp(float a,float b,float t)=>a+(b-a)*t;
 static float dot(Vector2 a,Vector2 b)=>Vector2.Dot(a,b);
 static Vector3 mul(Matrix4x4 m,Vector3 v)=>Vector3.TransformNormal(v,m);
 static Vector3 normalize(Vector3 v)=>Vector3.Normalize(v);
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 __FUNCTIONS__
 public static string Run(){
  var random=new Random(20261015);bool reverse=__REVERSE__;
  for(int i=0;i<10000;i++){
   float d=(float)random.NextDouble(),clip=DeviceDepthToClipDepth(d);
   Check(Math.Abs(ClipDepthToDeviceDepth(clip)-d)<1e-6,"Depth roundtrip");
   float eye=.3f+(float)random.NextDouble()*999.7f;
   // Independently project a point, map clip depth to texture depth, then
   // reconstruct using the actual shader conversion (including GL -1..1).
   float z=__DEPTH_FORMULA__;
   float sampled=__SAMPLED_FORMULA__;
   float rebuilt=DeviceDepthToClipDepth(sampled);
   double reference=__RECONSTRUCT_FORMULA__;
   Check(Math.Abs(reference-eye)/eye<.001,"Projected depth reconstructs wrong world distance");
   float range=2*(.1f+(float)random.NextDouble()*5000),bias=(float)random.NextDouble();
   float biased=BiasedShadowDepth(d,bias,range*.5f);
   Check(Math.Abs((ShadowDistanceFromNear(d,range)-ShadowDistanceFromNear(biased,range))-bias)<.002,"World-space bias varies with range/Z");
   float caster=(float)random.NextDouble();Check(IsShadowOccluded(d,caster)==(reverse?caster>d:caster<d),"Reversed/ordinary shadow sign");
   float radius=PCSSFilterRadius((float)random.NextDouble()*range,i%3==0?0:(float)random.NextDouble()*range,range,10,100);
   Check(!float.IsNaN(radius)&&!float.IsInfinity(radius)&&radius>=0&&radius<=1,"PCSS radius is not finite/bounded");
  }
  Check(!IsShadowOccluded(.5f,.5f),"Equal depth self-shadows");
  float receiver=reverse?.8f:.2f,casterNear=reverse?1:0;
  Check(IsShadowOccluded(receiver,casterNear),"Near-plane blocker is ignored");
  Check(PCSSFilterRadius(200,0,1000,10,100)==1,"Near-plane penumbra cap");
  Check(IsShadowOccluded(reverse?.5f:.5f,reverse?.500001f:.499999f),"Zero bias should retain blocker");
  Check(!IsShadowOccluded(BiasedShadowDepth(.5f,.01f,500),reverse?.500001f:.499999f),"Configured bias has no effect");
  foreach(float scale in new float[]{0,1,2})foreach(float height in new float[]{0,1200,10000}){
   var p=new AtmosphereParameter{MieScatteringScalarHeight=1200,MieScatteringScale=scale};
   Vector3 sigma=MieCoefficient(p,height)+MieAbsorption(p,height);
   double reference=4.440e-6*Math.Exp(-height/1200.0)*scale;
   Check(Math.Abs(sigma.X-reference)<1e-11,"Mie extinction differs from 4.440e-6 reference");
  }
  var packed=__NORMAL_INPUT__;Vector3 normal=DecodeNormalFromTexture(packed);
  Check(Vector3.Distance(normal,Vector3.UnitZ)<1e-6,"Neutral imported normal is tilted");
  var slope=__SLOPE_INPUT__;normal=DecodeNormalFromTexture(slope);
  Check(Vector3.Distance(normal,new Vector3(.6f,0,.8f))<1e-5,"Imported normal encoding mismatch");
  var atmosphere=new AtmosphereParameter{PlanetRadius=6360000,AtmosphereHeight=60000};var spaceEye=new Vector3(0,6430000,0);
  Check(TransmittanceToAtmosphere(atmosphere,spaceEye,Vector3.UnitY)==Vector3.One,"Sun in space disappeared");
  Check(TransmittanceToAtmosphere(atmosphere,spaceEye,Vector3.UnitX)==Vector3.One,"Vacuum tangent queries invalid LUT");
  Check(TransmittanceToAtmosphere(atmosphere,spaceEye,Vector3.Normalize(new Vector3(1,-.1f,0)))==new Vector3(.25f,.5f,.75f),"Exterior shell traversal not attenuated");
  foreach(var rotation in new Matrix4x4[]{Matrix4x4.Identity,Matrix4x4.CreateRotationY(.7f),Matrix4x4.CreateRotationX(-.5f)}){
   unity_MatrixInvV=rotation;unity_OrthoParams=new Vector4(0,0,0,1);
   var a=GetSurfaceViewDirection(new Vector3(5,0,10));var b=GetSurfaceViewDirection(new Vector3(-30,20,100));
   Check(Vector3.Distance(a,b)<1e-6 && Vector3.Distance(a,Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ,rotation)))<1e-6,"Orthographic rays must be parallel");
   unity_OrthoParams=Vector4.Zero;_WorldSpaceCameraPos=new Vector3(1,2,3);var position=new Vector3(5,0,10);
   Check(Vector3.Distance(GetSurfaceViewDirection(position),Vector3.Normalize(_WorldSpaceCameraPos-position))<1e-6,"Perspective direction changed");
  }
  return "PASS: __NAME__: actual shader arithmetic; 10000 depth roundtrips, projected depth reconstruction, bias/occlusion and finite PCSS radii; Mie density, imported normals, perspective/orthographic views.";
 }
'@
foreach($taskVariant in @('ReversedAG','OrdinaryGLBC5','OrdinaryRGB','ReversedASTC')) {
    $taskReverse=$taskVariant.StartsWith('Reversed');$taskRgb=$taskVariant -eq 'OrdinaryRGB';$taskAstc=$taskVariant -eq 'ReversedASTC';$taskGl=$taskVariant -eq 'OrdinaryGLBC5'
    $taskClip=if($taskReverse){1}elseif($taskGl){-1}else{0}
    $taskCode=($taskFunctions | ForEach-Object {Convert-TaskFunction $_ $taskReverse $taskRgb $taskAstc $taskClip}) -join "`n"
    $taskRun=$taskBody.Replace('__FUNCTIONS__',$taskCode).Replace('__REVERSE__',$taskReverse.ToString().ToLowerInvariant()).Replace('__NAME__',$taskVariant)
    if($taskReverse){$taskRun=$taskRun.Replace('__DEPTH_FORMULA__','.3f*(1000-eye)/(eye*(1000-.3f))').Replace('__SAMPLED_FORMULA__','z').Replace('__RECONSTRUCT_FORMULA__','1000*.3/(.3+rebuilt*(1000-.3))')}
    elseif($taskGl){$taskRun=$taskRun.Replace('__DEPTH_FORMULA__','(1000+.3f)/(1000-.3f)-2*1000*.3f/((1000-.3f)*eye)').Replace('__SAMPLED_FORMULA__','(z+1)*.5f').Replace('__RECONSTRUCT_FORMULA__','2*1000*.3/((1000+.3)-rebuilt*(1000-.3))')}
    else{$taskRun=$taskRun.Replace('__DEPTH_FORMULA__','1000*(eye-.3f)/(eye*(1000-.3f))').Replace('__SAMPLED_FORMULA__','z').Replace('__RECONSTRUCT_FORMULA__','1000*.3/(1000-rebuilt*(1000-.3))')}
    if($taskRgb){$taskRun=$taskRun.Replace('__NORMAL_INPUT__','new Vector4(.5f,.5f,1,1)').Replace('__SLOPE_INPUT__','new Vector4(.8f,.5f,.9f,1)')}
    elseif($taskVariant -eq 'OrdinaryGLBC5'){$taskRun=$taskRun.Replace('__NORMAL_INPUT__','new Vector4(.5f,.5f,0,1)').Replace('__SLOPE_INPUT__','new Vector4(.8f,.5f,0,1)')}
    else{$taskRun=$taskRun.Replace('__NORMAL_INPUT__','new Vector4(1,.5f,1,.5f)').Replace('__SLOPE_INPUT__','new Vector4(1,.5f,1,.8f)')}
    Add-Type -TypeDefinition ($taskHeader+"`npublic static class $taskVariant {`n"+$taskRun+"`n}")
    Invoke-Expression "[$taskVariant]::Run()"
}
$taskPoint=Get-TaskFunction $taskLighting 'float3 PBR_DirectLitPointLight('
if($taskPoint.Contains('surface.V =')){throw 'Point lights override the camera view direction'}
Write-Output 'PASS: point lights retain the shared camera-dependent view direction.'
$taskScreen=[IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets/MaxRendererPipeline/Shaders/Realtime/ScreenSpaceShadowMapPass.hlsl'))
foreach($taskCascade in 0..3){if(-not $taskScreen.Contains("_DepthNormalBias$taskCascade * biasScale")){throw "Cascade $taskCascade has no receiver bias"}}
foreach($taskShader in @('PBRGBuffer','PBRLitPass')) {
    $taskPath=Join-Path (Split-Path -Parent $PSScriptRoot) "Assets/MaxRendererPipeline/Shaders/Realtime/$taskShader.shader"
    if(-not [IO.File]::ReadAllText($taskPath).Contains('_NormalMap("Normal Map",2D) = "bump" {}')){throw "Non-neutral default normal: $taskShader"}
}
Write-Output 'PASS: all four cascade depth-bias inputs are connected; both material normal defaults are neutral.'
