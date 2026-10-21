param([string]$UnityEditorData='C:/Program Files/Unity/Hub/Editor/2022.3.62f2c1/Editor/Data')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskCgRoot=Join-Path $UnityEditorData 'CGIncludes'
$taskLegacy=[System.Text.Encoding]::GetEncoding(28591)
$taskUtf8=[System.Text.UTF8Encoding]::new($false,$true)
$taskCache=@{}
function Expand-TaskShader([string]$path){
 $path=[System.IO.Path]::GetFullPath($path)
 if($taskCache.ContainsKey($path)){return $taskCache[$path]}
 $bytes=[System.IO.File]::ReadAllBytes($path);try{$s=$taskUtf8.GetString($bytes).TrimStart([char]0xFEFF)}catch{$s=$taskLegacy.GetString($bytes)}
 $directory=Split-Path -Parent $path
 $pattern=[regex]::new('(?m)^[ \t]*#include[ \t]+"([^"]+)"[^\r\n]*')
 $s=$pattern.Replace($s,[System.Text.RegularExpressions.MatchEvaluator]{param($m)
  $include=Join-Path $directory $m.Groups[1].Value
  if(-not(Test-Path -LiteralPath $include)){$include=Join-Path $taskCgRoot $m.Groups[1].Value}
  return (Expand-TaskShader $include)
 })
 $taskCache[$path]=$s
 return $s
}
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class MaxSrpNativeShaderCheck {
 [DllImport("d3dcompiler_47.dll", CallingConvention=CallingConvention.StdCall)]
 private static extern int D3DCompile(byte[] source, UIntPtr size, string name, IntPtr defines, IntPtr include, string entry, string target, uint flags, uint flags2, out IntPtr code, out IntPtr errors);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr BufferPointer(IntPtr self);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate UIntPtr BufferSize(IntPtr self);
 private static string ReadBlob(IntPtr blob) {
  if(blob==IntPtr.Zero) return "";
  IntPtr vtable=Marshal.ReadIntPtr(blob);
  var pointer=(BufferPointer)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable,3*IntPtr.Size),typeof(BufferPointer));
  var size=(BufferSize)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable,4*IntPtr.Size),typeof(BufferSize));
  byte[] bytes=new byte[(int)size(blob).ToUInt64()];Marshal.Copy(pointer(blob),bytes,0,bytes.Length);
  return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
 }
 public static void Compile(string source,string name,string entry,string target) {
  byte[] bytes=Encoding.UTF8.GetBytes(source);IntPtr code=IntPtr.Zero,errors=IntPtr.Zero;
  try { int result=D3DCompile(bytes,(UIntPtr)bytes.Length,name,IntPtr.Zero,IntPtr.Zero,entry,target,4096,0,out code,out errors);
   if(result<0) throw new Exception(ReadBlob(errors));
  } finally { if(code!=IntPtr.Zero) Marshal.Release(code);if(errors!=IntPtr.Zero) Marshal.Release(errors); }
 }
}
"@
$taskDefines="#define SHADER_API_D3D11 1`n#define UNITY_COMPILER_HLSL 1`n#define SHADER_TARGET 50`n#define UNITY_VERSION 202230`n"
$taskChecks=@(
 @('Assets/MaxRendererPipeline/Editor/MaxPcssConvergenceProbe.hlsl','ConvergenceFragment','ps_5_0'),
 @('Assets/MaxRendererPipeline/Editor/MaxPcssConvergenceProbe.hlsl','PlaneFixtureFragment','ps_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/TemporalAAPass.hlsl','TaaVertex','vs_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/TemporalAAPass.hlsl','TaaResolve','ps_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/TemporalAAPass.hlsl','TaaCopy','ps_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/MotionVectorsPass.hlsl','MaxMotionVertex','vs_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/MotionVectorsPass.hlsl','MaxMotionFragment','ps_5_0'),
 @('Assets/MaxRendererPipeline/Editor/MaxSkyHaloDiagnostics.compute','SkyHaloComparison','cs_5_0'),
 @('Assets/MaxRendererPipeline/Editor/MaxAtmosphereReferenceProbes.compute','AtmosphereReferenceProbes','cs_5_0'),
 @('Assets/MaxRendererPipeline/Editor/MaxImportedTextureExport.compute','ExportImportedMip','cs_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/PrecomputeIBL.compute','PrecomputeDiffuse','cs_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/PrecomputeIBL.compute','PrecomputeSpec','cs_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/PrecomputeIBL.compute','PrecomputeBRDFLut','cs_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/PrecomputeIBL.compute','SkyToEnvironment','cs_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/PrecomputeIBL.compute','FilterSkyDiffuse','cs_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/PrecomputeIBL.compute','FilterSkySpecular','cs_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/PrecomputeIBL.compute','BuildDynamicBRDF','cs_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/PBRLitPass.hlsl','PBRVertex','vs_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/PBRLitPass.hlsl','PBRFragment','ps_5_0'),
 @('Assets/MaxRendererPipeline/Editor/MaxPcssPatternProbe.hlsl','PatternRawFragment','ps_5_0'),
 @('Assets/MaxRendererPipeline/Editor/MaxPcssPatternProbe.hlsl','PatternFragment','ps_5_0'),
 @('Assets/MaxRendererPipeline/Editor/MaxPcssReuseFixture.hlsl','ReuseFixtureFragment','ps_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/PBRGBufferPass.hlsl','PBRGBufferVertex','vs_5_0'),
 @('Assets/MaxRendererPipeline/Shaders/Realtime/PBRGBufferPass.hlsl','PBRGBufferFragment','ps_5_0')
)
foreach($name in @('TransmittanceLUT','MultiscatteringLUT','SkyviewLUT','AerialPerspectiveLUT','SkyboxProcedural')){
 $path='Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/'+$name+'Pass.hlsl'
 $taskChecks+=,@($path,'vert','vs_5_0')
 $taskChecks+=,@($path,'frag','ps_5_0')
}
$taskChecks+=,@('Assets/MaxRendererPipeline/Shaders/Realtime/PBRGBufferPass.hlsl','ShadowCasterVertex','vs_5_0')
$taskChecks+=,@('Assets/MaxRendererPipeline/Shaders/Realtime/PBRGBufferPass.hlsl','ShadowCasterFragment','ps_5_0')
$taskChecks+=,@('Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl','ColorOutputVertex','vs_5_0')
$taskChecks+=,@('Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl','ColorOutputFragment','ps_5_0')
foreach($check in $taskChecks){
 $source=$taskDefines+(Expand-TaskShader (Join-Path $taskRoot $check[0]))
 [MaxSrpNativeShaderCheck]::Compile($source,$check[0],$check[1],$check[2])
 Write-Output ('PASS: HLSL '+$check[1]+' ('+$check[2]+')')
}

# Compile actual non-default branches too. GL depth is emulated with ordinary Z
# and [-1,1] clip depth under the HLSL compiler; this is not a native GLSL/GPU test.
foreach($variant in @('OrdinaryGLDepth','RGBNormal','ASTCNormal')) {
 foreach($check in @(
  @('Assets/MaxRendererPipeline/Shaders/Realtime/PBRLitPass.hlsl','PBRFragment'),
  @('Assets/MaxRendererPipeline/Shaders/Realtime/PBRGBufferPass.hlsl','PBRGBufferFragment'),
  @('Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/AerialPerspectiveLUTPass.hlsl','frag'),
  @('Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyboxProceduralPass.hlsl','frag')
 )) {
  $source=$taskDefines+(Expand-TaskShader (Join-Path $taskRoot $check[0]))
  if($variant -eq 'OrdinaryGLDepth'){$source=[regex]::Replace($source,'(?m)^#define UNITY_REVERSED_Z 1\r?$','// Ordinary Z for GL depth emulation')}
  elseif($variant -eq 'RGBNormal'){$source="#define UNITY_NO_DXT5nm 1`n"+$source}
  else{$source="#define UNITY_ASTC_NORMALMAP_ENCODING 1`n"+$source}
  [MaxSrpNativeShaderCheck]::Compile($source,$check[0],$check[1],'ps_5_0')
  Write-Output ('PASS: '+$variant+' '+$check[1])
 }
}

foreach($variant in @('OrdinaryGLDepth','BottomOrigin')) {
 foreach($check in @(
  @('Assets/MaxRendererPipeline/Shaders/Realtime/TemporalAAPass.hlsl','TaaResolve','ps_5_0'),
  @('Assets/MaxRendererPipeline/Shaders/Realtime/MotionVectorsPass.hlsl','MaxMotionVertex','vs_5_0'),
  @('Assets/MaxRendererPipeline/Shaders/Realtime/MotionVectorsPass.hlsl','MaxMotionFragment','ps_5_0')
 )) {
  $source=$taskDefines+(Expand-TaskShader (Join-Path $taskRoot $check[0]))
  if($variant -eq 'OrdinaryGLDepth'){$source=[regex]::Replace($source,'(?m)^#define UNITY_REVERSED_Z 1\r?$','// Ordinary depth emulation')}
  else{$source=[regex]::Replace($source,'(?m)^#define UNITY_UV_STARTS_AT_TOP 1\r?$','#define UNITY_UV_STARTS_AT_TOP 0')}
  [MaxSrpNativeShaderCheck]::Compile($source,$check[0],$check[1],$check[2])
  Write-Output ('PASS: '+$variant+' '+$check[1])
 }
}
