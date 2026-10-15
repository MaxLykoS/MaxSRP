using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine
{
    public class Object { public bool destroyed; }
    public class MonoBehaviour:Object {}
    public class DisallowMultipleComponent:Attribute {}
    public class RequireComponent:Attribute {public RequireComponent(Type t){}}
    public class AddComponentMenu:Attribute {public AddComponentMenu(string s){}}
    public struct Vector3 { public float x,y,z; public Vector3(float a,float b,float c){x=a;y=b;z=c;} }
    public struct Vector4
    {
        public float x,y,z,w; public Vector4(float a,float b,float c,float d){x=a;y=b;z=c;w=d;}
        public static implicit operator Vector4(Vector3 v)=>new Vector4(v.x,v.y,v.z,0);
        public static Vector4 operator-(Vector4 v)=>new Vector4(-v.x,-v.y,-v.z,-v.w);
    }
    public struct Color
    {
        public float r,g,b,a; public Color(float x,float y,float z,float w=1){r=x;g=y;b=z;a=w;}
        static float Linear(float x)=>x<=.04045f?x/12.92f:(float)Math.Pow((x+.055f)/1.055f,2.4);
        public Color linear=>new Color(Linear(r),Linear(g),Linear(b),a);
        public static Color operator*(Color a,Color b)=>new Color(a.r*b.r,a.g*b.g,a.b*b.b,a.a*b.a);
        public static Color operator*(Color c,float v)=>new Color(c.r*v,c.g*v,c.b*v,c.a*v);
        public static Color clear=>new Color(0,0,0,0);
        public static implicit operator Vector4(Color c)=>new Vector4(c.r,c.g,c.b,c.a);
    }
    public enum LightType {Directional,Point,Spot}
    public enum LightShadows {None,Hard,Soft}
    public class Transform {public Vector3 forward=new Vector3(0,0,1),position;}
    public class Light:Object {public MaxSRP.MaxPhysicalLight photometry;public bool useColorTemperature;public float colorTemperature=6500;public bool TryGetComponent<T>(out T value)where T:class{value=photometry as T;return value!=null;}public bool enabled=true,active=true;public bool isActiveAndEnabled=>enabled&&active;public LightType type=LightType.Directional;public float intensity=1;public Color color=new Color(1,1,1);public LightShadows shadows=LightShadows.Soft;public Transform transform=new Transform();}
    public static class RenderSettings {public static Light sun;}
    public class Texture:Object {public int Version;}
    public class MaterialPropertyBlock
    {
        public readonly Dictionary<string,object> values=new Dictionary<string,object>();
        public void SetTexture(string name,Texture value){values[name]=value;}
        public void SetInteger(string name,int value){values[name]=value;}
    }
    public class RenderTexture:Texture
    {
        public static int Alive,FailAt,Attempts; public int width,height,volumeDepth,mipmapCount; public string name;
        public bool created,useMipMap,autoGenerateMips,enableRandomWrite;public TextureDimension dimension;public FilterMode filterMode;public TextureWrapMode wrapMode;
        public RenderTextureFormat format;public RenderTextureReadWrite readWrite;public int WrittenWidth;
        public readonly HashSet<string> written=new HashSet<string>();
        public RenderTexture(int w,int h,int d,RenderTextureFormat f,RenderTextureReadWrite r){width=w;height=h;format=f;readWrite=r;Alive++;}
        public bool Create(){Attempts++;if(FailAt==Attempts)return false;created=true;mipmapCount=1;if(useMipMap)for(int n=width;n>1;n>>=1)mipmapCount++;return true;}
        public bool IsCreated()=>created;
    }
    public enum RenderTextureFormat {ARGBHalf}
    public enum RenderTextureReadWrite {Linear}
    public enum FilterMode {Bilinear,Trilinear}
    public enum TextureWrapMode {Clamp}
    public class Shader
    {
        static readonly Dictionary<string,int> ids=new Dictionary<string,int>();
        public static readonly Dictionary<int,object> Globals=new Dictionary<int,object>();
        public static int PropertyToID(string n){if(!ids.ContainsKey(n))ids[n]=ids.Count;return ids[n];}
    }
    public class ComputeShader:Object
    {
        readonly string[] names={"SkyToEnvironment","FilterSkyDiffuse","FilterSkySpecular","BuildDynamicBRDF"};
        public int FindKernel(string name){int i=Array.IndexOf(names,name);if(i<0)throw new Exception("Missing kernel");return i;}
        public string Name(int k)=>names[k];
    }
    public static class SystemInfo {public static bool supportsComputeShaders=true,supports2DArrayTextures=true;public static CopyTextureSupport copyTextureSupport=CopyTextureSupport.DifferentTypes;}
    public static class Mathf
    {
        public const float PI=(float)Math.PI;
        public static float Max(float a,float b)=>Math.Max(a,b);
        public static float Clamp(float x,float a,float b)=>Math.Clamp(x,a,b);
        public static float Pow(float x,float y)=>(float)Math.Pow(x,y);
        public static Color CorrelatedColorTemperatureToRGB(float x)=>new Color(.5f,1,2);
        public static int Clamp(int x,int a,int b)=>Math.Max(a,Math.Min(b,x));
        public static int ClosestPowerOfTwo(int n){int p=1;while(p<n)p*=2;return p-n<n-p/2?p:p/2;}
    }
    public class MinAttribute:Attribute {public MinAttribute(float n){}}
    public class RangeAttribute:Attribute {public RangeAttribute(float a,float b){}}
    public class TooltipAttribute:Attribute {public TooltipAttribute(string s){}}
}
namespace Unity.Collections
{
    public enum Allocator {Temp}
    public static class NativeHooks {public static int Maps;}
    public struct NativeArray<T>:IDisposable
    {
        readonly T[] values;readonly bool owned;public NativeArray(T[] v,bool o=false){values=v;owned=o;if(o)NativeHooks.Maps++;}
        public int Length=>values.Length;public T this[int i]{get=>values[i];set=>values[i]=value;}public void Dispose(){if(owned)NativeHooks.Maps--;}
    }
}
namespace UnityEngine.Rendering
{
    public enum TextureDimension {Tex2D,Tex2DArray,Cube}
    [Flags] public enum CopyTextureSupport {None=0,DifferentTypes=16}
    public struct VisibleLight {public LightType lightType;public Light light;public float range;public Color finalColor;}
    public struct CullingResults
    {
        public Unity.Collections.NativeArray<VisibleLight> visibleLights;
        public Unity.Collections.NativeArray<int> GetLightIndexMap(Unity.Collections.Allocator a)=>new Unity.Collections.NativeArray<int>(new int[visibleLights.Length],true);
        public void SetLightIndexMap(Unity.Collections.NativeArray<int> map){for(int i=0;i<map.Length;i++)Checks.Require(map[i]==-1,"Per-object map changed");}
    }
    public class CommandBuffer
    {
        public readonly List<string> trace=new List<string>();public readonly Dictionary<int,object> globals=new Dictionary<int,object>();
        readonly Dictionary<string,int> ints=new Dictionary<string,int>();readonly Dictionary<string,float> floats=new Dictionary<string,float>();
        readonly Dictionary<string,Texture> textures=new Dictionary<string,Texture>();
        public void SetComputeIntParam(ComputeShader s,string n,int v){ints[n]=v;}
        public void SetComputeFloatParam(ComputeShader s,string n,float v){floats[n]=v;}
        public void SetComputeTextureParam(ComputeShader s,int k,string n,Texture t){textures[k+":"+n]=t;}
        public void DispatchCompute(ComputeShader s,int k,int x,int y,int z)
        {
            string name=s.Name(k);int w=ints["_DynamicWidth"];trace.Add(name);
            Checks.Require(x*8>=w&&y*8>=w,"Dispatch misses texels");
            if(name=="BuildDynamicBRDF")
            {
                var lut=(RenderTexture)textures[k+":_DynamicBRDFLUT"];Checks.Require(z==1&&w==256&&lut.enableRandomWrite,"BRDF UAV layout");return;
            }
            var stage=(RenderTexture)textures[k+":_DynamicOutput"];
            Checks.Require(z==6&&stage.dimension==TextureDimension.Tex2DArray&&stage.volumeDepth==6&&stage.enableRandomWrite,"Array UAV layout");
            Checks.Require(w<=stage.width,"Staging write out of bounds");stage.WrittenWidth=w;
            if(name=="SkyToEnvironment")stage.Version=textures[k+":_EnvironmentSky"].Version;
            else
            {
                var source=(RenderTexture)textures[k+":_DynamicRadianceMap"];
                Checks.Require(source.written.Count==6*source.mipmapCount,"Convolution reads uninitialized cube mips");
                Checks.Require(ints["_DynamicSampleCount"]>=32&&floats["_DynamicSourceMaxMip"]==source.mipmapCount-1,"Sampling controls mismatch");
                if(name=="FilterSkySpecular")Checks.Require(floats["_DynamicRoughness"]>0&&floats["_DynamicRoughness"]<=1,"Specular mip roughness");
                stage.Version=source.Version;
            }
            for(int face=0;face<6;face++)stage.written.Add(face+":0");
        }
        public void CopyTexture(RenderTexture src,int sf,int sm,int sx,int sy,int width,int height,RenderTexture dst,int df,int dm,int dx,int dy)
        {
            Checks.Require(src.created&&dst.created&&src.format==dst.format&&src.readWrite==RenderTextureReadWrite.Linear&&dst.readWrite==RenderTextureReadWrite.Linear,"Copy format/HDR mismatch");
            Checks.Require(sf>=0&&sf<6&&df==sf&&sm<src.mipmapCount&&dm<dst.mipmapCount,"Copy face/mip mismatch");
            Checks.Require(sx==0&&sy==0&&dx==0&&dy==0&&width==height&&width==Math.Max(1,dst.width>>dm)&&width<=src.width,"Copy bounds");
            Checks.Require(src.written.Contains(sf+":"+sm)&&(src.dimension!=TextureDimension.Tex2DArray||width<=src.WrittenWidth),"Copy reads unwritten face/region");
            dst.written.Add(df+":"+dm);dst.Version=src.Version;trace.Add("Copy");
        }
        public void CopyTexture(RenderTexture src,int sf,int sm,RenderTexture dst,int df,int dm)=>CopyTexture(src,sf,sm,0,0,Math.Max(1,src.width>>sm),Math.Max(1,src.height>>sm),dst,df,dm,0,0);
        static bool HasBaseFaces(RenderTexture t){for(int f=0;f<6;f++)if(!t.written.Contains(f+":0"))return false;return true;}
        public void GenerateMips(RenderTexture t){Checks.Require(!t.autoGenerateMips&&t.useMipMap&&HasBaseFaces(t),"Mips generated before six base faces");for(int m=1;m<t.mipmapCount;m++)for(int f=0;f<6;f++)t.written.Add(f+":"+m);trace.Add("GenerateMips");}
        public void BeginSample(string n){} public void EndSample(string n){}
        public void SetGlobalTexture(string n,Texture t){if(n!="_BRDFLUT"){var cube=(RenderTexture)t;Checks.Require(cube.written.Count==6*cube.mipmapCount,"Partly initialized IBL published");}globals[Shader.PropertyToID(n)]=t;trace.Add("Publish");}
        public void SetGlobalInteger(string n,int v){globals[Shader.PropertyToID(n)]=v;}
        public void SetGlobalInteger(int n,int v){globals[n]=v;}
        public void SetGlobalColor(int n,Color c){globals[n]=c;}
        public void SetGlobalVector(int n,Vector4 v){globals[n]=v;}
        public void SetGlobalVectorArray(int n,Vector4[] v){globals[n]=(Vector4[])v.Clone();}
    }
    public static class CommandBufferPool {public static int outstanding;public static CommandBuffer Get(string n){outstanding++;return new CommandBuffer();}public static void Release(CommandBuffer c){outstanding--;}}
    public class ScriptableRenderContext
    {
        public bool failNext;public readonly List<string> trace=new List<string>();
        public void ExecuteCommandBuffer(CommandBuffer cmd){if(failNext){failNext=false;throw new InvalidOperationException("Injected execute failure");}trace.AddRange(cmd.trace);foreach(var pair in cmd.globals)Shader.Globals[pair.Key]=pair.Value;}
        public int Count(string op){int n=0;foreach(var x in trace)if(x==op)n++;return n;}
    }
}
namespace MaxSRP
{
    public static class MaxRenderResources
    {
        public static void UnbindTexture(string n,Texture t){int id=Shader.PropertyToID(n);if(Shader.Globals.TryGetValue(id,out var owned)&&ReferenceEquals(t,owned))Shader.Globals.Remove(id);}
        public static void Destroy<T>(ref T t)where T:UnityEngine.Object {if(t!=null&&!t.destroyed){t.destroyed=true;if(t is RenderTexture)RenderTexture.Alive--;}t=null;}
    }
    public static class DynamicIBLChecks
    {
        static RenderTexture Global(string name)=>(RenderTexture)Shader.Globals[Shader.PropertyToID(name)];
        public static string Run()
        {
            var context=new ScriptableRenderContext();var cs=new ComputeShader();var settings=new SkyIBLSettings();var sky=new Texture{Version=1};
            var pass=new MaxIBLGIPass(cs);pass.Execute(context,sky,1,settings);
            Checks.Require(context.Count("BuildDynamicBRDF")==1&&context.Count("SkyToEnvironment")==1&&context.Count("FilterSkyDiffuse")==1&&context.Count("FilterSkySpecular")==7,"Initial update dispatches");
            Checks.Require(RenderTexture.Alive==7,"Owned GPU texture count");
            var oldDiffuse=Global("_IBLDiffuse");var oldSpec=Global("_IBLSpec");
            var bindings=new MaterialPropertyBlock();pass.SetLightingBindings(bindings);
            Checks.Require(bindings.values["_IBLDiffuse"]==oldDiffuse&&bindings.values["_IBLSpec"]==oldSpec&&
                (int)bindings.values["_MaxMipCount"]==8,"Lighting bindings do not match published output");
            Shader.Globals[Shader.PropertyToID("_IBLDiffuse")]=new Texture();
            Shader.Globals[Shader.PropertyToID("_IBLSpec")]=new Texture();
            pass.SetLightingBindings(bindings);
            Checks.Require(bindings.values["_IBLDiffuse"]==oldDiffuse&&bindings.values["_IBLSpec"]==oldSpec,
                "An unrelated global writer replaces the actual Lighting inputs");
            pass.Execute(context,sky,1,settings);
            Checks.Require(oldDiffuse.Version==1&&oldSpec.Version==1&&(int)Shader.Globals[Shader.PropertyToID("_MaxMipCount")]==8,"Initial pair/mips");
            for(int i=0;i<100;i++){context.trace.Clear();pass.Execute(context,sky,1,settings);Checks.Require(context.Count("Publish")==3&&context.Count("Copy")==0&&context.Count("FilterSkySpecular")==0&&context.Count("BuildDynamicBRDF")==0,"Frozen camera rebuilds IBL");}
            sky.Version=2;context.trace.Clear();pass.Execute(context,sky,2,settings);
            Checks.Require(Global("_IBLDiffuse")!=oldDiffuse&&Global("_IBLSpec")!=oldSpec&&Global("_IBLDiffuse").Version==2&&Global("_IBLSpec").Version==2&&context.Count("BuildDynamicBRDF")==0,"Sun update/atomic pair/BRDF cache");
            pass.SetLightingBindings(bindings);Checks.Require(bindings.values["_IBLSpec"]==Global("_IBLSpec"),"Lighting keeps previous generation after swap");
            oldDiffuse=Global("_IBLDiffuse");oldSpec=Global("_IBLSpec");sky.Version=3;context.failNext=true;
            try{pass.Execute(context,sky,3,settings);throw new Exception("Missing failure");}catch(InvalidOperationException){}
            Checks.Require(Global("_IBLDiffuse")==oldDiffuse&&Global("_IBLSpec")==oldSpec&&CommandBufferPool.outstanding==0,"Failure publishes partial pair");
            context.trace.Clear();pass.Execute(context,sky,3,settings);Checks.Require(Global("_IBLSpec").Version==3&&context.Count("SkyToEnvironment")==1,"Retry skips pending update");
            settings.SpecularSamples=256;context.trace.Clear();pass.Execute(context,sky,3,settings);Checks.Require(context.Count("SkyToEnvironment")==1&&context.Count("BuildDynamicBRDF")==0,"Sample-count invalidation");
            settings.SpecularResolution=256;context.trace.Clear();pass.Execute(context,sky,3,settings);Checks.Require(Global("_IBLSpec").width==256&&context.Count("FilterSkySpecular")==8&&context.Count("BuildDynamicBRDF")==0&&RenderTexture.Alive==7,"Resolution change/lifetime");
            Global("_BRDFLUT").created=false;context.trace.Clear();pass.Execute(context,sky,3,settings);Checks.Require(context.Count("BuildDynamicBRDF")==1&&context.Count("SkyToEnvironment")==0,"BRDF loss rebuilds environment");
            Global("_IBLSpec").created=false;context.trace.Clear();pass.Execute(context,sky,3,settings);Checks.Require(context.Count("SkyToEnvironment")==1&&context.Count("BuildDynamicBRDF")==0,"Lost output is cached");
            var replacement=new Texture{Version=4};context.trace.Clear();pass.Execute(context,replacement,3,settings);Checks.Require(Global("_IBLSpec").Version==4&&context.Count("SkyToEnvironment")==1,"New source with same revision ignored");
            pass.Dispose();pass.Dispose();Checks.Require(RenderTexture.Alive==0&&CommandBufferPool.outstanding==0,"Disposal leaks");
            // Fail every allocation position and verify a subsequent retry is complete.
            for(int fail=1;fail<=7;fail++)
            {
                RenderTexture.FailAt=RenderTexture.Attempts+fail;pass=new MaxIBLGIPass(cs);
                bool threw=false;try{pass.Execute(context,sky,5,settings);}catch(InvalidOperationException){threw=true;}
                Checks.Require(threw,"Allocation failure not injected");RenderTexture.FailAt=0;pass.Execute(context,sky,5,settings);pass.Dispose();Checks.Require(RenderTexture.Alive==0,"Failed allocation leaks");
            }
            SystemInfo.copyTextureSupport=CopyTextureSupport.None;bool rejected=false;try{new MaxIBLGIPass(cs);}catch(NotSupportedException){rejected=true;}SystemInfo.copyTextureSupport=CopyTextureSupport.DifferentTypes;
            Checks.Require(rejected&&RenderTexture.Alive==0,"Unsupported GPU path allocates resources");
            return "PASS: complete dynamic IBL command/ownership code; all faces/mips/HDR copies, 100 cached frames, atomic pair swaps, source/revision/quality/resolution changes, isolated BRDF loss, execution/allocation failures, capability guard and disposal.";
        }
    }
    public static class SingleSunChecks
    {
        public static string Run()
        {
            var sun=new Light{intensity=200000,color=new Color(.5f,.8f,1)};sun.transform.forward=new Vector3(.3f,-.8f,.5f);RenderSettings.sun=sun;
            var state=SunLightData.Capture();Checks.Require(state.HasSun&&state.CastsShadows&&state.Direction.Equals(new Vector4(-.3f,.8f,-.5f,0)),"Sun selection/direction");
            Checks.Require(Math.Abs(state.LinearColor.r-.4280823f)<1e-5&&state.LinearColor.b==2,"Sun HDR linear intensity");
            var lights=new List<VisibleLight>();for(int i=0;i<40;i++)lights.Add(new VisibleLight{lightType=LightType.Point,range=10,light=new Light{type=LightType.Point},finalColor=new Color(1,2,3)});
            lights.Add(new VisibleLight{lightType=LightType.Directional,light=new Light{intensity=100},finalColor=new Color(100,100,100)});
            var culling=new CullingResults{visibleLights=new Unity.Collections.NativeArray<VisibleLight>(lights.ToArray())};var context=new ScriptableRenderContext();var pass=new MaxLightConfigurator();pass.SetupMultiShaderLightingParams(context,ref culling,state);
            Checks.Require((int)Shader.Globals[Shader.PropertyToID("_PointLightCount")]==32&&((Vector4)Shader.Globals[Shader.PropertyToID("_DirectionalLightDirection")]).Equals(state.Direction),"Point cap/other directional replaced sun");
            Checks.Require(((Color)Shader.Globals[Shader.PropertyToID("_DirectionalLightColor")]).Equals(state.LinearColor),"Lighting ignores shared sun color");
            sun.intensity=120000;Checks.Require(Math.Abs(SunLightData.Capture().LinearColor.b-1.2f)<1e-6,"Lux conversion");
            sun.intensity=800000;Checks.Require(state.LinearColor.b==2,"Snapshot follows later mutation");
            foreach(int mode in new[]{0,1,2,3,4})
            {
                sun.enabled=mode!=0;sun.active=mode!=1;sun.type=mode==2?LightType.Point:LightType.Directional;sun.intensity=mode==3?0:1;RenderSettings.sun=mode==4?null:sun;
                state=SunLightData.Capture();Checks.Require(!state.HasSun&&!state.CastsShadows&&state.Direction.Equals(default(Vector4))&&state.LinearColor.Equals(default(Color))&&state.AtmosphereDirection.Equals(new Vector4(0,1,0,0)),"Invalid sun retains source or unsafe atmosphere direction");
                pass.SetupMultiShaderLightingParams(context,ref culling,state);Checks.Require(((Vector4)Shader.Globals[Shader.PropertyToID("_DirectionalLightDirection")]).Equals(default(Vector4))&&((Color)Shader.Globals[Shader.PropertyToID("_DirectionalLightColor")]).Equals(default(Color)),"Old sun globals remain");
            }
            sun.enabled=sun.active=true;sun.type=LightType.Directional;sun.intensity=1;sun.shadows=LightShadows.None;RenderSettings.sun=sun;Checks.Require(SunLightData.Capture().HasSun&&!SunLightData.Capture().CastsShadows,"Shadow toggle disables illumination");
            Checks.Require(Unity.Collections.NativeHooks.Maps==0&&CommandBufferPool.outstanding==0,"Sun/light setup leaks");
            return "PASS: actual single-sun capture/light configuration; linear HDR color, camera-independent snapshot, absent culled sun, ignored extra directionals, 40 point lights, disabled/inactive/wrong-type/zero/null sun, safe zero clearing and shadow toggle.";
        }
    }
}
public static class Checks {public static void Require(bool ok,string message){if(!ok)throw new Exception(message);}}
