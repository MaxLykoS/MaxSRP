$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskSource=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/CameraViewport.cs'))
$taskBindings=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/ShaderBinding.cs'))
$taskOrthoStart=$taskBindings.IndexOf('            float orthoWidth =')
$taskOrthoEnd=$taskBindings.IndexOf('Vector4 orthoParams =', $taskOrthoStart)
$taskOrthoEnd=$taskBindings.IndexOf(';', $taskOrthoEnd)+1
if($taskOrthoStart -lt 0 -or $taskOrthoEnd -le $taskOrthoStart){throw 'Missing actual orthographic shader parameters'}
$taskOrthoCode=$taskBindings.Substring($taskOrthoStart,$taskOrthoEnd-$taskOrthoStart)
$taskHost=@"
using System;
using UnityEngine;
namespace UnityEngine {
 public enum CameraType {Game,SceneView,Preview,Reflection}
 public struct Rect { public float x,y,width,height; public Rect(float a,float b,float c,float d){x=a;y=b;width=c;height=d;} }
 public struct Matrix4x4 {public float m00,m11,m02,m12,m22,m23;}
 public struct Vector2Int {public int x,y;public Vector2Int(int a,int b){x=a;y=b;}}
 public struct Vector4 {public float x,y,z,w;public Vector4(float a,float b,float c,float d){x=a;y=b;z=c;w=d;}}
 public class RenderTexture {public int width,height;}
 public class Camera {public CameraType cameraType=CameraType.Game;public RenderTexture targetTexture;public Rect pixelRect;public Matrix4x4 projectionMatrix;public bool orthographic=true;public float orthographicSize=2;}
 public static class Mathf {public static float Abs(float x){return Math.Abs(x);}public static int Max(int a,int b){return Math.Max(a,b);}public static int RoundToInt(float x){return (int)Math.Round(x);}}
}
namespace MaxSRP {
 public static class CameraViewportChecks {
  static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
  static Vector4 ActualOrthoParams(Camera camera,Matrix4x4 cameraProjection){float isOrthographic=camera.orthographic?1:0; __ORTHO_CODE__ return orthoParams;}
  public static string Run(){
   var random=new Random(14);
   for(int i=0;i<10000;i++){
    int w=random.Next(1,3841),h=random.Next(1,2161);
    var camera=new Camera {pixelRect=new Rect(40,70,w,h),projectionMatrix=new Matrix4x4 {m00=(float)random.NextDouble()+.2f,m11=1.7f,m02=.1f,m12=-.2f,m22=-1.01f,m23=-.6f}};
    var original=camera.projectionMatrix;var region=CameraViewport.GetViewport(camera);var p=CameraViewport.GetProjection(camera);var size=CameraViewport.GetRenderSize(camera);
    Require(region.x==40 && region.y==70 && region.width==w && region.height==h,"Added fixed-aspect viewport/bars");
    Require(size.x==w && size.y==h,"GBuffer differs from native viewport");
    Require(Math.Abs(region.width*p.m00-region.height*p.m11)<.001,"Circle becomes an ellipse");
    Require(p.m11==original.m11 && p.m02==original.m02 && p.m12==original.m12 && p.m22==original.m22 && p.m23==original.m23,"Vertical FOV/lens shift/depth changed");
    Require(camera.projectionMatrix.m00==original.m00,"Camera settings mutated");
    var ortho=ActualOrthoParams(camera,p);
    Require(Math.Abs(ortho.x*p.m00-1)<1e-6 && Math.Abs(ortho.y*p.m11-1)<1e-6 && ortho.w==1,"Orthographic shader extents use a different projection");
   }
   var rt=new Camera {pixelRect=new Rect(0,0,1920,1080),targetTexture=new RenderTexture {width=800,height=600},projectionMatrix=new Matrix4x4 {m00=1,m11=1}};
   Require(CameraViewport.GetRenderSize(rt).x==800 && CameraViewport.GetRenderSize(rt).y==600 && CameraViewport.GetProjection(rt).m00==.75f,"RenderTexture size/projection mismatch");
   rt.targetTexture=null;var wide=CameraViewport.GetProjection(rt);rt.pixelRect=new Rect(0,0,1080,1920);var portrait=CameraViewport.GetProjection(rt);
   Require(wide.m00<1 && portrait.m00>1,"Projection does not track window resize");
   rt.pixelRect=new Rect(960,0,960,1080);Require(CameraViewport.GetRenderSize(rt).x==960 && Math.Abs(CameraViewport.GetProjection(rt).m00-1.125f)<1e-6,"Split viewport mismatch");
   foreach(var type in new CameraType[]{CameraType.SceneView,CameraType.Preview,CameraType.Reflection}){rt.cameraType=type;Require(CameraViewport.GetProjection(rt).m00==1,"Non-Game projection changed");}
   return "PASS: actual viewport/projection helper and orthographic shader extents; 10000 native viewport/circle/buffer cases, resize, split/offset viewport, RenderTexture, non-Game isolation and FOV/lens/depth/settings preservation.";
  }
 }
}
"@
Add-Type -TypeDefinition ($taskHost.Replace('__ORTHO_CODE__',$taskOrthoCode)+"`n"+$taskSource.Replace('using UnityEngine;',''))
[MaxSRP.CameraViewportChecks]::Run()
$taskPaths=@('Runtime/MaxRendererPipeline.cs','Runtime/ShaderBinding.cs','Runtime/Passes/MaxSceenSpaceShadowMapPass.cs','Runtime/Passes/MaxShadowCasterPass.cs','Runtime/Passes/MaxProceduralSkybox.cs')
foreach($taskPath in $taskPaths){$taskText=[IO.File]::ReadAllText((Join-Path $taskRoot ('Assets/MaxRendererPipeline/'+$taskPath)));if(-not $taskText.Contains('CameraViewport.GetProjection(camera)')){throw ('Projection consumer mismatch: '+$taskPath)}}
$taskPipeline=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs'))
$taskBindings=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/ShaderBinding.cs'))
if(-not $taskPipeline.Contains('cullingParams.SetCullingPlane(') -or -not $taskBindings.Contains('cmd.SetGlobalMatrix(ShaderBindings.unity_MatrixVP, viewProjMatrix)')){throw 'Culling/shader projection mismatch'}
if($taskBindings.Contains('cmd.SetViewProjectionMatrices(')){throw 'Built-in-only matrix command reintroduced into custom SRP'}
if($taskSource.Contains('FitViewport(') -or $taskPipeline.Contains('ClearViewportBars(')){throw 'Fixed-aspect/black-bar logic remains'}
Write-Output 'PASS: shared native viewport projection for culling/raster/shadows/atmosphere; fixed-aspect bar logic removed.'
