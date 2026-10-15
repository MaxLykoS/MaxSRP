$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskSource=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs'))
# Execute the complete production cache/ownership code against an in-memory command recorder.
# This verifies invalidation and camera isolation, not GPU output or timing.
$taskHost=@'
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
namespace UnityEngine {
 public class Object {public bool destroyed;}
 public class Shader:Object {static readonly Dictionary<string,int> ids=new Dictionary<string,int>();public static int PropertyToID(string n){if(!ids.ContainsKey(n))ids[n]=ids.Count;return ids[n];}}
 public class Texture:Object {}
 public enum RenderTextureFormat{ARGBFloat}
 public enum RenderTextureReadWrite{Linear}
 public enum FilterMode{Bilinear}
 public enum TextureWrapMode{Clamp,Repeat}
 public enum MeshTopology{Triangles}
 public enum CameraClearFlags{Skybox,Color}
 public struct Rect {public Rect(int x,int y,int w,int h){}}
 public struct Vector3 {public float x,y,z;public Vector3(float a,float b,float c){x=a;y=b;z=c;}}
 public struct Vector4 {public float x,y,z,w;public Vector4(float a,float b,float c,float d){x=a;y=b;z=c;w=d;}}
 public struct Color {public float r,g,b,a;public Color linear=>this;public static Color white=>new Color{r=1,g=1,b=1,a=1};public static Color operator*(Color x,Color y)=>new Color{r=x.r*y.r,g=x.g*y.g,b=x.b*y.b,a=x.a*y.a};}
 public struct Matrix4x4 {public int token;public Matrix4x4 inverse=>this;public static Matrix4x4 identity=>new Matrix4x4{token=1};}
 public class Transform {public Vector3 position;}
 public class Camera {public int id;public bool orthographic;public CameraClearFlags clearFlags=CameraClearFlags.Skybox;public Transform transform=new Transform();public Matrix4x4 worldToCameraMatrix=Matrix4x4.identity,projection=Matrix4x4.identity;public int GetInstanceID()=>id;}
 public static class Mathf {public static float Max(float a,float b)=>Math.Max(a,b);}
 public static class GL {public static Matrix4x4 GetGPUProjectionMatrix(Matrix4x4 p,bool t)=>p;}
 public class RenderTexture:Texture {
  public static int alive;public static bool failNext;public bool created;public int width,height;public string name;public FilterMode filterMode;public TextureWrapMode wrapMode,wrapModeU;
  public RenderTexture(int w,int h,int d,RenderTextureFormat f,RenderTextureReadWrite r){width=w;height=h;alive++;}
  public bool IsCreated()=>created;
  public bool Create(){if(failNext){failNext=false;return false;}return created=true;}
 }
 public class Material:Object {public Material(Shader s){}public void SetTexture(int n,Texture t){}public void SetFloat(int n,float v){}public void SetFloat(string n,float v){}public void SetColor(int n,Color c){}public void SetMatrix(int n,Matrix4x4 m){}public void SetVector(int n,Vector4 v){}}
 public class SerializeField:Attribute{}
 public class MinAttribute:Attribute {public MinAttribute(float x){}}
 public class TooltipAttribute:Attribute {public TooltipAttribute(string x){}}
}
namespace UnityEngine.Rendering {
 using UnityEngine;
 public enum BuiltinRenderTextureType{None}
 public enum RenderBufferLoadAction{Load}
 public enum RenderBufferStoreAction{Store}
 public struct RenderTargetIdentifier {public RenderTargetIdentifier(Texture t){}public RenderTargetIdentifier(BuiltinRenderTextureType t){}}
 public class CommandBuffer {
  public string name;public readonly List<string> draws=new List<string>();public Dictionary<int,Texture> textures=new Dictionary<int,Texture>();
  public void Blit(object s,RenderTexture t,Material m){draws.Add(t.name);}
  public void SetGlobalFloat(int n,float v){} public void SetGlobalVector(int n,Vector4 v){}
  public void SetGlobalTexture(int n,Texture t){textures[n]=t;}
  public void SetRenderTarget(RenderTargetIdentifier[] t,RenderTargetIdentifier d){}
  public void SetRenderTarget(RenderTargetIdentifier t,RenderBufferLoadAction a,RenderBufferStoreAction b,RenderTargetIdentifier d,RenderBufferLoadAction c,RenderBufferStoreAction e){}
  public void SetViewport(Rect r){}public void BeginSample(string n){}public void EndSample(string n){}
  public void DrawProcedural(Matrix4x4 m,Material mat,int p,MeshTopology t,int v){draws.Add("Aerial");}
 }
 public static class CommandBufferPool {public static int outstanding;public static CommandBuffer Get(string n){outstanding++;return new CommandBuffer{name=n};}public static void Release(CommandBuffer c){outstanding--;}}
 public class ScriptableRenderContext {
  public readonly List<string> draws=new List<string>();public readonly Dictionary<int,Texture> textures=new Dictionary<int,Texture>();public bool failNext;
  public void SetupCameraProperties(Camera c){}
  public void ExecuteCommandBuffer(CommandBuffer c){if(failNext){failNext=false;throw new Exception("Execute failure");}draws.AddRange(c.draws);foreach(var t in c.textures)textures[t.Key]=t.Value;}
 }
}
namespace UnityEditor {
 public static class EditorApplication {public static event Action projectChanged;public static void Changed(){projectChanged?.Invoke();}public static int Listeners=>projectChanged?.GetInvocationList().Length??0;}
 public static class ShaderUtil {public static void SetAsyncCompilation(UnityEngine.Rendering.CommandBuffer c,bool v){}public static void RestoreAsyncCompilation(UnityEngine.Rendering.CommandBuffer c){}}
}
namespace MaxSRP {
 using UnityEngine;using UnityEngine.Rendering;
 public struct SunLightData {public Vector4 Direction;public Color LinearColor;public bool Valid;public Vector4 AtmosphereDirection=>Valid?Direction:new Vector4(0,1,0,0);}
 public static class CameraViewport {public static Matrix4x4 GetProjection(Camera c)=>c.projection;}
 public static class ShaderBindings {public static void SetPerCameraShaderVariables(ScriptableRenderContext c,Camera v,bool t){}}
 public static class MaxRenderResources {
  public static void UnbindTexture(string n,Texture t){}
  public static void Destroy<T>(ref T obj) where T:UnityEngine.Object {if(obj!=null&&!obj.destroyed){obj.destroyed=true;if(obj is RenderTexture)RenderTexture.alive--;}obj=null;}
 }
 public static class AtmosphereCacheChecks {
  static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
  static string Draws(ScriptableRenderContext c){string s=string.Join(",",c.draws);c.draws.Clear();return s;}
  static float environmentHeight=2;static Color sunColor=Color.white;static bool sunEnabled=true;
  static SunLightData Sun(Vector4 direction)=>new SunLightData{Direction=direction,LinearColor=sunEnabled?sunColor:new Color(),Valid=sunEnabled};
  static void Expect(MaxProceduralSkyboxPass p,ScriptableRenderContext c,Camera cam,Vector4 sun,string expected){p.PrepareAtmosphere(c,cam,Sun(sun),environmentHeight);var actual=Draws(c);Check(actual==expected,"Expected "+expected+" got "+actual);Check(CommandBufferPool.outstanding==0,"Leaked command buffer");}
  public static string Run(){
   var s=new MaxProceduralSkyboxPass.AtmosphereSettings();var c=new ScriptableRenderContext();var a=new Camera{id=1};var b=new Camera{id=2};
   a.transform.position=new Vector3(0,20,0);b.transform.position=new Vector3(0,100,0);var sun=new Vector4(0,1,0,0);
   var p=new MaxProceduralSkyboxPass(s);
   Expect(p,c,a,sun,"TransmittanceLUT,MultiscatteringLUT,EnvironmentSkyviewLUT,SkyviewLUT,Aerial");
   var aFog=c.textures[Shader.PropertyToID("_AerialPerspectiveLUT")];
   for(int i=0;i<100;i++)Expect(p,c,a,sun,"");
   Expect(p,c,b,sun,"SkyviewLUT,Aerial");var bFog=c.textures[Shader.PropertyToID("_AerialPerspectiveLUT")];Check(aFog!=bFog,"Cameras share aerial output");
   for(int i=0;i<20;i++){p.PruneDestroyedCameras();Expect(p,c,a,sun,"");Check(c.textures[Shader.PropertyToID("_AerialPerspectiveLUT")]==aFog,"Cache hit binds wrong camera");Expect(p,c,b,sun,"");}
   a.worldToCameraMatrix=new Matrix4x4{token=2};Expect(p,c,a,sun,"Aerial");
   a.projection=new Matrix4x4{token=2};Expect(p,c,a,sun,"Aerial");
   a.orthographic=true;Expect(p,c,a,sun,"Aerial");
   a.transform.position=new Vector3(0,40,0);Expect(p,c,a,sun,"SkyviewLUT,Aerial");
   sun=new Vector4(.5f,.8f,0,0);Expect(p,c,a,sun,"EnvironmentSkyviewLUT,SkyviewLUT,Aerial");Expect(p,c,b,sun,"SkyviewLUT,Aerial");
   s._SunDiskAngle=2;Expect(p,c,a,sun,"");
   sunColor=new Color{r=2,g=2,b=2,a=1};Expect(p,c,a,sun,"EnvironmentSkyviewLUT,SkyviewLUT,Aerial");
   sunColor=new Color{r=.5f,g=.8f,b=1,a=1};Expect(p,c,a,sun,"EnvironmentSkyviewLUT,SkyviewLUT,Aerial");
   s._AerialPerspectiveDistance=10000;Expect(p,c,a,sun,"Aerial");
   s._SeaLevel=10;Expect(p,c,a,sun,"SkyviewLUT,Aerial");
   s._MieAnisotropy=.5f;Expect(p,c,a,sun,"MultiscatteringLUT,EnvironmentSkyviewLUT,SkyviewLUT,Aerial");
   s._RayleighScatteringScale=2;Expect(p,c,a,sun,"TransmittanceLUT,MultiscatteringLUT,EnvironmentSkyviewLUT,SkyviewLUT,Aerial");
   Expect(p,c,b,sun,"SkyviewLUT,Aerial");Expect(p,c,a,sun,"");
   s.EnableAerialPerspective=false;Expect(p,c,a,sun,"");a.clearFlags=CameraClearFlags.Color;Expect(p,c,a,sun,"");
   s.EnableAerialPerspective=true;Expect(p,c,a,sun,""); // Valid fog can be reused after disabling.
   sunColor=new Color{r=3,g=3,b=3,a=1};Expect(p,c,a,sun,"EnvironmentSkyviewLUT,Aerial"); // IBL still needs its environment with a solid camera background.
   a.clearFlags=CameraClearFlags.Skybox;Expect(p,c,a,sun,"SkyviewLUT");
   ((RenderTexture)aFog).created=false;Expect(p,c,a,sun,"Aerial");Check(aFog.destroyed,"Lost aerial RT not released");
   var trans=(RenderTexture)typeof(MaxProceduralSkyboxPass).GetField("m_transmittanceLUT",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(p);
   trans.created=false;Expect(p,c,a,sun,"TransmittanceLUT,MultiscatteringLUT,EnvironmentSkyviewLUT,SkyviewLUT,Aerial");
   UnityEditor.EditorApplication.Changed();Expect(p,c,a,sun,"TransmittanceLUT,MultiscatteringLUT,EnvironmentSkyviewLUT,SkyviewLUT,Aerial");
   environmentHeight=8;int previousEnvironment=p.EnvironmentRevision;Expect(p,c,a,sun,"EnvironmentSkyviewLUT");Check(p.EnvironmentRevision>previousEnvironment,"Reference height does not update environment");
   sunColor=new Color{r=.5f,g=1,b=1,a=1};Expect(p,c,a,sun,"EnvironmentSkyviewLUT,SkyviewLUT,Aerial");
   sunEnabled=false;Expect(p,c,a,sun,"EnvironmentSkyviewLUT,SkyviewLUT,Aerial");Expect(p,c,a,sun,"");sunEnabled=true;
   sunColor=new Color{r=4,g=4,b=4,a=1};c.failNext=true;bool failed=false;try{p.PrepareAtmosphere(c,a,Sun(sun),environmentHeight);}catch{failed=true;}
   Check(failed&&CommandBufferPool.outstanding==0,"Recording failure not released");Expect(p,c,a,sun,"TransmittanceLUT,MultiscatteringLUT,EnvironmentSkyviewLUT,SkyviewLUT,Aerial");
   var caches=(System.Collections.IDictionary)typeof(MaxProceduralSkyboxPass).GetField("m_cameraLuts",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(p);
   caches[1].GetType().GetField("Owner").SetValue(caches[1],null);p.PruneDestroyedCameras();p.ReleaseCamera(1);Check(RenderTexture.alive==6,"Destroyed camera LUTs not pruned");
   RenderTexture.failNext=true;failed=false;try{p.PrepareAtmosphere(c,a,Sun(sun),environmentHeight);}catch{failed=true;}
   Check(failed&&RenderTexture.alive==6,"Allocation failure leaks texture");
   Expect(p,c,a,sun,"TransmittanceLUT,MultiscatteringLUT,EnvironmentSkyviewLUT,SkyviewLUT,Aerial");
   p.Dispose();p.Dispose();Check(RenderTexture.alive==0&&UnityEditor.EditorApplication.Listeners==0,"Dispose leaks LUT/event ownership");
   return "PASS: complete production atmosphere cache; 100 frozen frames, alternating cameras/rebinding, sun/medium/phase/height/view/projection/distance/toggles, RT loss, Editor invalidation, failed recording/allocation, pruning and disposal.";
  }
 }
}
'@
Add-Type -TypeDefinition ("#define UNITY_EDITOR`n#pragma warning disable CS0649`n"+$taskHost+"`n"+$taskSource.Replace('using System;','').Replace('using System.Collections.Generic;','').Replace('using UnityEngine;','').Replace('using UnityEngine.Rendering;',''))
[MaxSRP.AtmosphereCacheChecks]::Run()
