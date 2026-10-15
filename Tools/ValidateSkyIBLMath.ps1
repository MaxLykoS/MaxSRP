$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
function Extract([string]$source,[string]$signature) {
    $a=$source.IndexOf($signature);if($a -lt 0){throw "Missing $signature"}
    $b=$source.IndexOf('{',$a);$depth=1
    for($end=$b+1;$depth;$end++){if($source[$end] -eq '{'){$depth++};if($source[$end] -eq '}'){$depth--}}
    [regex]::Replace($source.Substring($a,$end-$a),'//[^\r\n]*','')
}
$taskCommon=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Shaders/Realtime/PrecomputeIBL.compute'))
$taskDynamic=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Shaders/Realtime/DynamicSkyIBL.hlsl'))
$taskAtmos=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Helper.hlsl'))
$taskFunctions=@(
    (Extract ([IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/IBLValidation.hlsl'))) 'float3 FiniteIBLColor('),
    (Extract $taskCommon 'float3 DirectionFromCubemapTexel('),
    (Extract $taskCommon 'float RadicalInverse_VdC('),
    (Extract $taskCommon 'float2 Hammersley('),
    (Extract $taskCommon 'float3 ImportanceSampleGGX('),
    (Extract $taskDynamic 'float2 DynamicSkyUV('),
    (Extract $taskDynamic 'float DynamicSourceMip('),
    (Extract $taskAtmos 'float3 UVToViewDir(')
)
foreach($taskKernel in @('FilterSkyDiffuse','FilterSkySpecular')) {
    $s=Extract $taskDynamic "void $taskKernel("
    $s=$s.Replace("void $taskKernel(uint3 id : SV_DispatchThreadID)","float3 $taskKernel(float3 N)")
    $s=[regex]::Replace($s,'if \(id.x[^\r\n]+return;','')
    $s=$s.Replace('float3 N = DynamicCubeDirection(id);','')
    $s=[regex]::Replace($s,'_DynamicRadianceMap.SampleLevel\(_Sampler_LinearClamp, L, DynamicSourceMip\((.+)\)\).rgb','Sample(L, DynamicSourceMip($1))')
    $s=[regex]::Replace($s,'_DynamicOutput\[id\] = float4\((sum / .+), 1.0\);','return $1;')
    $taskFunctions+=$s
}
$taskConverted=foreach($s in $taskFunctions) {
    $s=[regex]::Replace($s,'(<<|>>) (\d+)u','$1 $2')
    $s=$s.Replace('float(bits)','(float)bits').Replace('float(i)','(float)i').Replace('float(N)','(float)N')
    $s=$s.Replace('float3 sum = 0.0;','float3 sum = new Vector3(0);')
    $s=$s.Replace('float3(', 'new Vector3(').Replace('float2(', 'new Vector2(').Replace('float3','Vector3').Replace('float2','Vector2')
    $s=[regex]::Replace($s,'\.([xyzw])\b',{param($m)'.'+$m.Groups[1].Value.ToUpperInvariant()})
    $s=[regex]::Replace($s,'(?<![\w.])(\d+\.\d+(?:[eE][+-]?\d+)?)(?![\w.])','${1}f')
    $s=[regex]::Replace($s,'(?<![\w.])(\d+[eE][+-]?\d+)(?!\w)','${1}f')
    'static '+$s
}
$taskHost=@'
using System;
using System.Numerics;
public static class SkyIBLMath {
 const float PI=(float)Math.PI;
 static uint _DynamicSampleCount=128;
 static float _DynamicSourceResolution=128,_DynamicSourceMaxMip=7,_DynamicRoughness;
 static float abs(float x)=>Math.Abs(x);
 static bool isfinite(float x)=>float.IsFinite(x);
 static float sqrt(float x)=>(float)Math.Sqrt(x);
 static float sin(float x)=>(float)Math.Sin(x);
 static float cos(float x)=>(float)Math.Cos(x);
 static float asin(float x)=>(float)Math.Asin(x);
 static float atan2(float y,float x)=>(float)Math.Atan2(y,x);
 static float log2(float x)=>(float)Math.Log(x,2);
 static float max(float a,float b)=>Math.Max(a,b);
 static uint max(uint a,uint b)=>Math.Max(a,b);
 static float clamp(float x,float a,float b)=>Math.Clamp(x,a,b);
 static Vector3 normalize(Vector3 x)=>Vector3.Normalize(x);
 static Vector3 cross(Vector3 a,Vector3 b)=>Vector3.Cross(a,b);
 static float dot(Vector3 a,Vector3 b)=>Vector3.Dot(a,b);
 static bool analytic;
 static Vector3 axis=new Vector3(.2f,.4f,-.3f);
 static Vector3 Sample(Vector3 L,float mip){
   Check(float.IsFinite(mip)&&mip>=0&&mip<=7,"Source mip out of bounds");
   Check(Math.Abs(L.Length()-1)<1e-5,"Unnormalized sample");
   return analytic?new Vector3(1+Vector3.Dot(axis,L)):new Vector3(4,8,16);
 }
 static void Check(bool b,string message){if(!b)throw new Exception(message);}
 __FUNCTIONS__
 public static string Run(){
   var bad=new Vector3(float.NaN,float.PositiveInfinity,float.NegativeInfinity);
   Check(FiniteIBLColor(bad)==Vector3.Zero,"Invalid IBL radiance poisons direct light");
   Check(FiniteIBLColor(new Vector3(-1,4,16))==new Vector3(0,4,16),"Finite HDR values lost");
   var direct=new Vector3(.3f,.25f,.2f);
   Check(direct+FiniteIBLColor(bad)==direct,"Invalid indirect light removes direct contribution");
   var random=new Random(20261015);float error=0;
   var faces=new[]{Vector3.UnitX,-Vector3.UnitX,Vector3.UnitY,-Vector3.UnitY,Vector3.UnitZ,-Vector3.UnitZ};
   for(int f=0;f<6;f++)Check(Vector3.Distance(DirectionFromCubemapTexel(f,.5f,.5f),faces[f])<1e-6,"Cube face axis");
   // Independent Direct3D cube addressing equations; includes every face edge.
   for(int f=0;f<6;f++)for(int i=0;i<200;i++){
     float u=i<4?(i%2): (float)random.NextDouble(),v=i<4?(i/2):(float)random.NextDouble();
     var d=DirectionFromCubemapTexel(f,u,v);float a,b,major;
     switch(f){case 0:major=d.X;a=-d.Z;b=-d.Y;break;case 1:major=-d.X;a=d.Z;b=-d.Y;break;
     case 2:major=d.Y;a=d.X;b=d.Z;break;case 3:major=-d.Y;a=d.X;b=-d.Z;break;
     case 4:major=d.Z;a=d.X;b=-d.Y;break;default:major=-d.Z;a=-d.X;b=-d.Y;break;}
     Check(Math.Abs((a/major+1)/2-u)<1e-6&&Math.Abs((b/major+1)/2-v)<1e-6,"Cube addressing roundtrip");
     Check(Vector3.Distance(UVToViewDir(DynamicSkyUV(d)),d)<2e-5,"Sky UV orientation differs from sky producer");
   }
   for(int i=0;i<106;i++){
     var n=i<6?faces[i]:normalize(new Vector3((float)random.NextDouble()-.5f,(float)random.NextDouble()-.5f,(float)random.NextDouble()-.5f));
     analytic=false;
     Check(Vector3.Distance(FilterSkyDiffuse(n),new Vector3(4,8,16))<1e-4,"Diffuse HDR or pi normalization");
     for(int m=1;m<=7;m++){
       _DynamicRoughness=m/7f;
       Check(Vector3.Distance(FilterSkySpecular(n),new Vector3(4,8,16))<1e-4,"Specular HDR or weight normalization");
     }
     analytic=true;float expected=1+2f/3*Vector3.Dot(axis,n);
     error=Math.Max(error,Math.Abs(FilterSkyDiffuse(n).X-expected));
   }
   Check(error<.006,"Diffuse convolution differs from analytic hemisphere integral: "+error);
   return "PASS: extracted production HLSL; 1200 cube/sky addressing roundtrips, 106 normals including poles, white HDR diffuse/specular across all roughness mips, bounded LOD; analytic cosine convolution max error="+error;
 }
}
'@
Add-Type -TypeDefinition $taskHost.Replace('__FUNCTIONS__',($taskConverted -join "`n"))
[SkyIBLMath]::Run()
