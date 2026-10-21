$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskPipeline = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs'))
$taskShader = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl'))
$taskOutputPass = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/Passes/MaxColorOutputPass.cs'))

# Execute the actual pure layout/resource methods with a small in-memory graphics API.
# This checks ownership and composition logic; it is not a Unity or GPU rendering test.
function Get-TaskBlock([string]$source, [string]$signature) {
    $start = $source.IndexOf($signature)
    if ($start -lt 0) { throw "Missing declaration: $signature" }
    $brace = $source.IndexOf('{', $start)
    $depth = 1
    for ($end = $brace + 1; $depth -gt 0 -and $end -lt $source.Length; ++$end) {
        if ($source[$end] -eq '{') { ++$depth }
        if ($source[$end] -eq '}') { --$depth }
    }
    if ($depth -ne 0) { throw "Unclosed declaration: $signature" }
    return $source.Substring($start, $end - $start)
}
$taskMethods = @(
    'private static long GetOutputKey(',
    'private static void GetOutputSize(',
    'private void PrepareOutputSizes(',
    'private void PruneOutputResources(',
    'private static RenderTexture CreateTarget(',
    'private sealed class OutputResources : IDisposable'
) | ForEach-Object { Get-TaskBlock $taskPipeline $_ }
$taskMethods += (Get-TaskBlock $taskOutputPass 'internal static bool IsTextureDestination(').Replace('internal static', 'private static')
$taskToneSource=Get-TaskBlock $taskShader 'float ToneMapACESChannel('
$taskTone=$taskToneSource.Replace('float','double').Replace('double ToneMapACESChannel(', 'static double ToneMapACES(')
$taskFloatTone=$taskToneSource.Replace('float ToneMapACESChannel(', 'static float ToneMapACESFloat(')
$taskFloatTone=[regex]::Replace($taskFloatTone,'(?<![\w.])(\d+\.\d+)(?![\w.])','${1}f')
$taskHost = @'
using System;
using System.Collections.Generic;
public static class ColorOutputChecks {
 enum CameraType { Game, SceneView, Preview }
 enum RenderTextureFormat { ARGBHalf, Depth }
 enum RenderTextureReadWrite { Linear }
 enum FilterMode { Point }
 enum TextureWrapMode { Clamp }
 struct Vector2Int { public int x,y; public Vector2Int(int x,int y){this.x=x;this.y=y;} }
 struct Rect {
  public float x,y,width,height; public float xMax {get{return x+width;}} public float yMax {get{return y+height;}}
  public Rect(float x,float y,float w,float h){this.x=x;this.y=y;width=w;height=h;}
 }
 static class Mathf {
  public static int Max(int a,int b){return Math.Max(a,b);}
  public static float Max(float a,float b){return Math.Max(a,b);}
  public static int RoundToInt(float x){return (int)Math.Round(x);}
 }
 class Camera {
  public int id,targetDisplay,pixelWidth=1920,pixelHeight=1080;
  public CameraType cameraType=CameraType.Game;
  public Rect rect=new Rect(0,0,1,1),pixelRect=new Rect(0,0,1920,1080);
  public RenderTexture targetTexture; public int GetInstanceID(){return id;}
 }
 class RenderTexture {
  public int width,height,depth,antiAliasing=1,id,destroyCount; public string name;
  public RenderTextureFormat format; public RenderTextureReadWrite readWrite;
  public FilterMode filterMode; public TextureWrapMode wrapMode;
  public bool created; public static bool failNextCreate;
  public RenderTexture(int w,int h,int d,RenderTextureFormat f,RenderTextureReadWrite rw){width=w;height=h;depth=d;format=f;readWrite=rw;}
  public bool Create(){if(failNextCreate){failNextCreate=false;return false;}return created=true;}
  public bool IsCreated(){return created;}
  public int GetInstanceID(){return id;}
 }
 static class MaxRenderResources {
  public static void UnbindTexture(string name,RenderTexture texture){}
  public static void Destroy(ref RenderTexture texture){if(texture!=null){texture.created=false;texture.destroyCount++;texture=null;}}
 }
 class Host {
  private readonly Dictionary<long,OutputResources> m_outputResources=new Dictionary<long,OutputResources>();
  private readonly Dictionary<long,Vector2Int> m_outputSizes=new Dictionary<long,Vector2Int>();
  private readonly Dictionary<long,int> m_outputCameraCounts=new Dictionary<long,int>();
  private readonly List<long> m_unusedOutputs=new List<long>();
__METHODS__
  static void Check(bool ok,string name){if(!ok)throw new Exception(name);}
  public void Run(){
   var a=new Camera{id=1};var b=new Camera{id=2};var scene=new Camera{id=1,cameraType=CameraType.SceneView};
   Check(GetOutputKey(a)==GetOutputKey(b),"Game camera stack must share HDR output");
   Check(GetOutputKey(a)!=GetOutputKey(scene),"Scene view must have an independent HDR output");
   Check(!IsTextureDestination(a)&&IsTextureDestination(scene)&&IsTextureDestination(new Camera{cameraType=CameraType.Preview}),"Game backbuffer and editor texture destination conventions");
   b.targetDisplay=1;Check(GetOutputKey(a)!=GetOutputKey(b),"Displays must be independent");b.targetDisplay=0;
   var rt=new RenderTexture(257,129,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear){id=-5,antiAliasing=4};
   a.targetTexture=rt;b.targetTexture=rt;
   Check(IsTextureDestination(a)&&IsTextureDestination(b),"Explicit RenderTextures keep offscreen orientation");
   Check(GetOutputKey(a)==GetOutputKey(b)&&GetOutputKey(a)!=GetOutputKey(scene),"Shared RenderTexture camera stack key");
   GetOutputSize(a,out int rw,out int rh);Check(rw==257&&rh==129,"RenderTexture dimensions");
   a.targetTexture=null;b.targetTexture=null;
   a.rect=new Rect(0,0,.5f,1);a.pixelRect=new Rect(0,0,960,1080);a.pixelWidth=960;
   b.rect=new Rect(.5f,0,.5f,1);b.pixelRect=new Rect(960,0,960,1080);b.pixelWidth=960;
   PrepareOutputSizes(new[]{a,b,scene});long gameKey=GetOutputKey(a);
   Check(m_outputSizes[gameKey].x==1920&&m_outputSizes[gameKey].y==1080,"Split screen uses full shared dimensions");
   Check(m_outputSizes.Count==2,"Game and scene output grouping");
   a.rect=new Rect(.25f,.25f,.5f,.5f);a.pixelRect=new Rect(480,270,960,540);a.pixelHeight=540;
   GetOutputSize(a,out rw,out rh);Check(rw==1920&&rh==1080,"Offset viewport dimensions");
   a.rect=new Rect(0,0,1f/3f,1);a.pixelRect=new Rect(0,0,1919f/3f,1079);a.pixelWidth=639;a.pixelHeight=1079;
   GetOutputSize(a,out rw,out rh);Check(rw==1919&&rh==1079,"Fractional viewport rounding");
   var output=new OutputResources();output.EnsureSize(a,1920,1080);
   Check(output.NeedsClear&&output.Color.format==RenderTextureFormat.ARGBHalf&&output.Color.readWrite==RenderTextureReadWrite.Linear,"Fresh output is linear HDR and initialized");
   var color=output.Color;var depth=output.Depth;output.NeedsClear=false;output.EnsureSize(b,1920,1080);
   Check(output.Color==color&&output.Depth==depth&&!output.NeedsClear,"Stack must retain linear color and depth");
   output.EnsureSize(a,800,600);
   Check(color.destroyCount==1&&depth.destroyCount==1&&output.NeedsClear&&output.Color.width==800,"Resize releases old HDR targets and requests clear");
   a.targetTexture=rt;output.EnsureSize(a,257,129);
   Check(output.OutputDepth!=null&&output.OutputDepth.antiAliasing==4,"Depthless MSAA destination gets matching output depth");
   var fallback=output.OutputDepth;rt.antiAliasing=2;output.EnsureSize(a,257,129);
   Check(fallback.destroyCount==1&&output.OutputDepth.antiAliasing==2,"MSAA change recreates fallback depth");
   fallback=output.OutputDepth;rt.depth=24;output.EnsureSize(a,257,129);
   Check(fallback.destroyCount==1&&output.OutputDepth==null,"Destination-owned depth releases fallback");
   color=output.Color;output.Dispose();output.Dispose();Check(color.destroyCount==1,"Output disposal is idempotent");
   RenderTexture.failNextCreate=true;
   try {output.EnsureSize(a,257,129);throw new Exception("Creation failure not reported");}
   catch(InvalidOperationException){Check(output.Color==null&&output.Depth==null,"Failed allocation leaves no owned buffers");}
   a.targetTexture=null;PrepareOutputSizes(new[]{a});m_outputResources[gameKey]=new OutputResources();m_outputResources[gameKey].EnsureSize(a,1920,1080);
   var unused=new OutputResources();unused.EnsureSize(scene,300,200);color=unused.Color;
   m_outputResources[GetOutputKey(scene)]=unused;PruneOutputResources(new[]{a});
      Check(m_outputResources.Count==2&&color.destroyCount==0,"Live output survives another camera render invocation");
   unused.LastCamera=null;PruneOutputResources(new[]{a});
   Check(m_outputResources.Count==1&&color.destroyCount==1,"Destroyed output owner is pruned");
   Console.WriteLine("PASS: actual output grouping/layout/resource methods: stacking, separate views/displays, split/offset/fractional viewports, resize, MSAA/depth changes, allocation failure and disposal.");
  }
 }
 static bool isnan(double x){return double.IsNaN(x);}
 static bool isnan(float x){return float.IsNaN(x);}
 static double saturate(double x){return Math.Max(0,Math.Min(1,x));}
 static float saturate(float x){return Math.Max(0,Math.Min(1,x));}
__TONE__
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);}
 public static void Run(){
  new Host().Run();
  Check(ToneMapACES(0)==0,"Black stays black");
  foreach(float value in new[]{1e10f,1e20f,float.MaxValue,float.PositiveInfinity}){
   float output=ToneMapACESFloat(value);
   Check(float.IsFinite(output)&&output==1,"Over-range highlight becomes a black/NaN pixel");
  }
  Check(ToneMapACESFloat(float.NaN)==0&&ToneMapACESFloat(float.NegativeInfinity)==0,"Invalid/negative tone-map input");
  double previous=0;
  for(int i=0;i<10001;i++){
   double value=ToneMapACES(i*.01);
   Check(Math.Abs(ToneMapACESFloat(i*.01f)-value)<3e-7,"Float tone-map curve changed in the normal HDR range");
   Check(!double.IsNaN(value)&&value>=previous-1e-12&&value<=1,"Tone mapping must be finite, monotonic and bounded");previous=value;
  }
  Check(Math.Abs(ToneMapACES(1)-.8037974683544302)<1e-12,"ACES reference white");
  Check(ToneMapACES(4)>ToneMapACES(2)&&ToneMapACES(2)>ToneMapACES(1),"HDR colors above one preserve highlight distinctions");
  double hdrBase=2,overlay=.5,composed=hdrBase+overlay;
  double correct=ToneMapACES(composed),wrong=ToneMapACES(ToneMapACES(hdrBase)+overlay);
  Check(Math.Abs(correct-wrong)>.01,"Regression sample must detect composition after tone mapping");
  Check(Math.Pow(2,1)==2&&Math.Pow(2,-1)==.5,"Exposure EV convention");
  Check(ToneMapACES(composed*2)>correct&&ToneMapACES(composed*.5)<correct,"Exposure precedes tone mapping");
  Console.WriteLine("PASS: actual ACES shader expression: 10001 monotonic/bounded values, float/double agreement, extreme highlights/NaN/Inf, black/white/HDR colors, exposure and a stacking regression sample.");
 }
}
'@
$taskHost = $taskHost.Replace('__METHODS__', ($taskMethods -join "`n")).Replace('__TONE__', ($taskTone+"`n"+$taskFloatTone))
Add-Type -TypeDefinition $taskHost
[ColorOutputChecks]::Run()

if (-not $taskPipeline.Contains('m_LightPass.Execute(context, camera, output.Color, output.Depth, viewport, m_iblGIPass, hasShadows)') -or
    -not $taskPipeline.Contains('DrawSkybox(context, camera, cmd, output.Color, output.Depth, viewport)')) {
    throw 'Lighting and sky must render into the shared HDR color/depth targets.'
}
if ($taskPipeline.IndexOf('m_colorOutputPass.Execute(context, camera') -lt $taskPipeline.IndexOf('m_proceduralSkyboxPass.DrawSkybox(context, camera')) {
    throw 'Color output must run after sky rendering.'
}
if (-not $taskShader.Contains('return float4(color, source.a)') -or
    -not $taskShader.Contains('depthOut = UNITY_SAMPLE_DEPTH')) {
    throw 'Final output must preserve alpha and copy scene depth.'
}
$taskShaderGuid = [regex]::Match([IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutput.shader.meta')), 'guid: (\w+)').Groups[1].Value
$taskAsset = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Max Renderer Pipeline Asset.asset'))
if (-not $taskAsset.Contains("m_colorOutputShader: {fileID: 4800000, guid: $taskShaderGuid, type: 3}")) {
    throw 'Pipeline asset must serialize the output shader reference for Player builds.'
}
Write-Output 'PASS: HDR lighting/sky wiring, output order, alpha/depth output and serialized Player shader reference.'

# Independent projection/raster reference: after Unity's offscreen GPU projection,
# a point above the camera center must sample the same source row on both destinations.
foreach ($taskTopOrigin in @($false, $true)) {
    foreach ($taskTextureDestination in @($false, $true)) {
        foreach ($taskWorldY in @(-0.8, -0.3, 0.0, 0.4, 0.9)) {
            $taskSourceClipY = if ($taskTopOrigin) { -$taskWorldY } else { $taskWorldY }
            $taskSourceRow = if ($taskTopOrigin) { (1 - $taskSourceClipY) * 0.5 } else { ($taskSourceClipY + 1) * 0.5 }
            $taskDestinationClipY = if ($taskTopOrigin -and $taskTextureDestination) { -$taskWorldY } else { $taskWorldY }
            $taskUV = ($taskDestinationClipY + 1) * 0.5
            if ($taskTopOrigin) { $taskUV = 1 - $taskUV }
            if ($taskTopOrigin -and -not $taskTextureDestination) { $taskUV = 1 - $taskUV }
            if ([Math]::Abs($taskUV - $taskSourceRow) -gt 1e-12) { throw 'Final color/depth samples the wrong source row.' }
        }
    }
}
$taskBindings = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/ShaderBinding.cs'))
$taskOutputPass = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/Passes/MaxColorOutputPass.cs'))
if (-not $taskBindings.Contains('Matrix4x4 cameraProjection = CameraViewport.GetProjection(camera)') -or
    -not $taskBindings.Contains('GL.GetGPUProjectionMatrix(cameraProjection, renderIntoTexture)') -or
    -not $taskBindings.Contains('unity_MatrixInvP, invProjectionMatrix') -or
    -not $taskOutputPass.Contains('!IsTextureDestination(camera) && SystemInfo.graphicsUVStartsAtTop') -or
    -not $taskShader.Contains('if (_ColorOutputParameters.z > 0.5)')) {
    throw 'Offscreen projection and target-aware final orientation must remain connected.'
}
Write-Output 'PASS: top/bottom-origin projection and sampling reference for screen/texture destinations; camera inverse projection wiring.'

# GPU conversion belongs to shader uploads, with the final target-specific UV
# conversion applied only at output. A Built-in pipeline matrix command mixed
# with these explicit SRP matrices caused the reported Game-view Y regression.
$taskCasterBindings = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/Passes/MaxShadowCasterPass.cs'))
if ($taskBindings.Contains('cmd.SetViewProjectionMatrices(') -or $taskCasterBindings.Contains('cmd.SetViewProjectionMatrices(')) {
    throw 'Built-in-only SetViewProjectionMatrices must not be mixed with explicit custom-SRP GPU matrix uploads.'
}
if (-not $taskBindings.Contains('cmd.SetGlobalMatrix(ShaderBindings.unity_MatrixVP, viewProjMatrix)') -or
    -not $taskBindings.Contains('cmd.SetGlobalMatrix(ShaderBindings.unity_MatrixInvVP, invViewProjMatrix)') -or
    -not $taskCasterBindings.Contains('cmd.SetGlobalMatrix("_ShadowCasterVP", shadowVP)')) {
    throw 'Direct SRP camera/receiver/caster matrix binding missing.'
}
Write-Output 'PASS: direct custom-SRP GPU matrix uploads; incompatible Built-in matrix commands excluded from camera and shadow paths.'

$taskSkyPath = Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs'
$taskSkyPass = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($taskSkyPath))
$taskSkyDraw = Get-TaskBlock $taskSkyPass 'public void DrawSkybox('
if (-not $taskSkyDraw.Contains('cmd.DrawProcedural(Matrix4x4.identity, m_skyboxMat, 0, MeshTopology.Triangles, 3)') -or
    $taskSkyDraw.Contains('context.DrawSkybox(') -or $taskSkyDraw -match 'RenderSettings\.skybox\s*=') {
    throw 'Sky must issue an explicit material draw without using global skybox selection.'
}
$taskSkyShaderPath = Join-Path $taskRoot 'Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyboxProceduralPass.hlsl'
$taskSkyShader = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($taskSkyShaderPath))
if (-not $taskSkyShader.Contains('vert(uint vertexID : SV_VertexID)') -or
    -not $taskSkyShader.Contains('farWorld.xyz - _WorldSpaceCameraPos * farWorld.w') -or
    -not $taskSkyShader.Contains('unity_OrthoParams.w > 0.5')) {
    throw 'Fullscreen sky must reconstruct perspective/orthographic view directions.'
}
$taskSkyShaderLab = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyboxProcedural.shader'))
if (-not $taskSkyShaderLab.Contains('ZWrite Off') -or -not $taskSkyShaderLab.Contains('ZTest LEqual')) {
    throw 'Sky must retain scene depth and only draw the far background.'
}
foreach ($taskReversed in @($false, $true)) {
    $taskFar = if ($taskReversed) { 0.0 } else { 1.0 }
    $taskObject = 0.5
    $taskHitsObject = if ($taskReversed) { $taskFar -ge $taskObject } else { $taskFar -le $taskObject }
    if ($taskHitsObject) { throw 'Sky overwrites an opaque depth sample.' }
}
foreach ($taskTopOrigin in @($false, $true)) {
    foreach ($taskReversed in @($false, $true)) {
        $taskCamera = [Numerics.Vector3]::new(11, 3, -7)
        $taskForward = [Numerics.Vector3]::Normalize([Numerics.Vector3]::new(0.8, 0.1, -0.7))
        $taskView = [Numerics.Matrix4x4]::CreateLookAt($taskCamera, $taskCamera + $taskForward, [Numerics.Vector3]::UnitY)
        $taskProjection = [Numerics.Matrix4x4]::CreatePerspectiveFieldOfView([Math]::PI / 3, 1.6, 0.3, 400)
        if ($taskTopOrigin) { $taskProjection.M22 = -$taskProjection.M22 }
        if ($taskReversed) { $taskProjection.M33 = 0.3 / (400 - 0.3); $taskProjection.M43 = 400 * 0.3 / (400 - 0.3) }
        $taskVP = $taskView * $taskProjection
        $taskInverse = [Numerics.Matrix4x4]::Identity
        if (-not [Numerics.Matrix4x4]::Invert($taskVP, [ref]$taskInverse)) { throw 'Singular test projection.' }
        foreach ($taskNdc in @(@(0.0, 0.0), @(-0.7, -0.5), @(0.7, 0.5))) {
            $taskFar = if ($taskReversed) { 0.0 } else { 1.0 }
            $taskWorld = [Numerics.Vector4]::Transform([Numerics.Vector4]::new($taskNdc[0], $taskNdc[1], $taskFar, 1), $taskInverse)
            $taskRay = [Numerics.Vector3]::Normalize([Numerics.Vector3]::new($taskWorld.X, $taskWorld.Y, $taskWorld.Z) - $taskCamera * $taskWorld.W)
            $taskReprojected = [Numerics.Vector4]::Transform([Numerics.Vector4]::new($taskCamera + $taskRay * 100, 1), $taskVP)
            if ([Math]::Abs($taskReprojected.X / $taskReprojected.W - $taskNdc[0]) -gt 0.0001 -or
                [Math]::Abs($taskReprojected.Y / $taskReprojected.W - $taskNdc[1]) -gt 0.0001) {
                throw 'Sky ray does not match the corresponding scene pixel.'
            }
            if ($taskNdc[0] -eq 0 -and $taskNdc[1] -eq 0 -and [Numerics.Vector3]::Dot($taskRay, $taskForward) -lt 0.99999) {
                throw 'Translated/rotated sky center ray misses camera forward.'
            }
        }
        $taskInverseView = [Numerics.Matrix4x4]::Identity
        [void][Numerics.Matrix4x4]::Invert($taskView, [ref]$taskInverseView)
        $taskOrthoRay = [Numerics.Vector3]::Normalize([Numerics.Vector3]::TransformNormal(-[Numerics.Vector3]::UnitZ, $taskInverseView))
        if ([Numerics.Vector3]::Dot($taskOrthoRay, $taskForward) -lt 0.99999) { throw 'Orthographic sky ray is incorrect.' }
    }
}
Write-Output 'PASS: explicit sky material draw, far-background depth test, perspective ray reprojection and orthographic camera direction.'
