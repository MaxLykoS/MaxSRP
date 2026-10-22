$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskPath = Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/Passes/MaxShadowCasterPass.cs'
$taskSource = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($taskPath))
function Get-TaskBlock([string]$signature) {
    $taskStart = $taskSource.IndexOf($signature)
    if ($taskStart -lt 0) { throw "Missing method: $signature" }
    $taskBrace = $taskSource.IndexOf('{', $taskStart)
    $taskDepth = 1
    for ($taskEnd = $taskBrace + 1; $taskDepth -gt 0; $taskEnd++) {
        if ($taskSource[$taskEnd] -eq '{') { $taskDepth++ }
        if ($taskSource[$taskEnd] -eq '}') { $taskDepth-- }
    }
    return $taskSource.Substring($taskStart, $taskEnd - $taskStart)
}
$taskExecute = (Get-TaskBlock 'public bool Execute(').Replace('public bool Execute(', 'private bool Execute(')
$taskDispose = Get-TaskBlock 'public void Dispose('
# Execute the actual orchestration and disposal with a small graphics API model.
# Camera.aspect's auto-to-manual setter behavior follows Unity's documented API.
$taskHost = @'
using System;
public class ShadowCameraChecks {
    Camera m_shadowCamera;
    Cascade m_cascade=new Cascade();
    class AtmosphereSettings {public float _SunDiskAngle=1;}
    AtmosphereSettings m_atmosphereSettings=new AtmosphereSettings();
    static class Mathf {
        public const float Deg2Rad=(float)(Math.PI/180);
        public static float Clamp(float value,float a,float b){return Math.Max(a,Math.Min(b,value));}
        public static float Tan(float angle){return (float)Math.Tan(angle);}
    }
    RenderTexture m_shadowAtlas=new RenderTexture {width=4096,height=4096};
    RectInt[] m_tileRects={new RectInt(),new RectInt(),new RectInt(),new RectInt()};
    void EnsureShadowAtlas(){}
    ShaderTagId m_shaderTagID=new ShaderTagId();
    enum HideFlags { HideAndDontSave }
    struct Vector3 { public static Vector3 forward {get{return new Vector3();}} public static Vector3 operator-(Vector3 v){return v;} }
    struct Vector4 {public static explicit operator Vector3(Vector4 v){return new Vector3();}}
    struct SunLightData {public Vector4 Direction {get{return new Vector4();}}public bool CastsShadows {get{return RenderSettings.sun!=null;}}}
    struct Quaternion { public static Vector3 operator*(Quaternion a,Vector3 b){return b;} }
    struct Matrix4x4 { public int token; public static Matrix4x4 operator*(Matrix4x4 a,Matrix4x4 b){return a;} }
    struct Rect { public float x,y,width,height; public Rect(float a,float b,float c,float d){x=a;y=b;width=c;height=d;} }
    class Transform { public Quaternion rotation=new Quaternion(); }
    class RenderTexture {public int width=4096,height=4096;}
    struct RectInt {public int x {get{return 0;}} public int y {get{return 0;}}public int width {get{return 2044;}} public int height {get{return 2044;}}}
    class GameObject {
        public HideFlags hideFlags; public GameObject(string name){}
        public T AddComponent<T>() where T:Camera,new() { return new T {gameObject=this}; }
    }
    class Camera {
        public GameObject gameObject;
        public bool enabled=true,orthographic,manualAspect;
        public float viewportAspect=1.25f,manualValue,nearClipPlane=.3f,farClipPlane=1000;
        public float aspect {get{return manualAspect?manualValue:viewportAspect;}set{manualAspect=true;manualValue=value;}}
        public RenderTexture targetTexture;
        public Rect rect=new Rect(.1f,.2f,.7f,.6f);
        public Matrix4x4 worldToCameraMatrix=new Matrix4x4 {token=71};
        public Matrix4x4 projectionMatrix=new Matrix4x4 {token=93};
        public void CopyFrom(Camera other) {
            enabled=other.enabled;orthographic=other.orthographic;manualAspect=other.manualAspect;
            manualValue=other.manualValue;viewportAspect=other.viewportAspect;rect=other.rect;
            nearClipPlane=other.nearClipPlane;farClipPlane=other.farClipPlane;
            projectionMatrix=other.projectionMatrix;worldToCameraMatrix=other.worldToCameraMatrix;targetTexture=other.targetTexture;
        }
        public void ResetProjectionMatrix(){projectionMatrix=new Matrix4x4();}
        public void ResetWorldToCameraMatrix(){worldToCameraMatrix=new Matrix4x4();}
        public void ResetCullingMatrix(){}
        public bool TryGetCullingParameters(out CullingParameters value){value=new CullingParameters();return !Hooks.cullMiss;}
    }
    class Light {public Transform transform=new Transform();}
    static class RenderSettings {public static Light sun=new Light();}
    static class GL {public static Matrix4x4 GetGPUProjectionMatrix(Matrix4x4 p,bool texture){return p;}}
    static class Shader {
        public static float sunTan;
        public static void SetGlobalMatrix(string name,Matrix4x4 value){}
        public static void SetGlobalFloat(string name,float value){if(name=="_ShadowLightTanAngularRadius")sunTan=value;}
    }
    struct ShaderTagId {}
    struct CullingParameters {}
    struct CullingResults {}
    struct SortingSettings {public SortingSettings(Camera camera){Require(camera!=Hooks.main,"Sorting used source camera");}}
    struct DrawingSettings {public DrawingSettings(ShaderTagId tag,SortingSettings sorting){}}
    struct FilteringSettings {public static FilteringSettings defaultValue {get{return new FilteringSettings();}}}
    class Cascade {
        public float m_OrthoDistance=500;
        public float[] m_OrthoWidths=new float[4];
        public bool UpdateCascade(Camera camera,ref Vector3 dir){Require(camera==Hooks.main,"Cascade frustum lost source camera");return !Hooks.emptyRange;}
        public float ConfigCameraToShadowSpace(Camera camera,Vector3 dir,int i,float distance,float resolution){
            Require(camera!=Hooks.main,"Shadow setup mutated source camera");
            camera.aspect=1;camera.nearClipPlane=-distance;camera.farClipPlane=distance;
            return distance;
        }
    }
    class CommandBuffer {
        public void SetRenderTarget(RenderTexture rt){Hooks.target=rt;}
        public void SetGlobalMatrix(string name,Matrix4x4 matrix){}
        public void SetViewport(Rect rect){Require(rect.width==rect.height && (rect.width==4096 || rect.width==2044),"Invalid clear/tile viewport");Hooks.viewportWidth=rect.width;}
        public void ClearRenderTarget(bool depth,bool color,Color c){Require(Hooks.viewportWidth==4096 && depth && !color,"Atlas must be cleared whole, not once per tile");Hooks.clears++;}
    }
    struct Color {public static Color clear {get{return new Color();}}}
    static class CommandBufferPool {
        public static int outstanding;
        public static CommandBuffer Get(string name){outstanding++;return new CommandBuffer();}
        public static void Release(CommandBuffer cmd){outstanding--;}
    }
    struct ScriptableRenderContext {
        public void SetupCameraProperties(Camera camera){
            Require(camera!=Hooks.main && !camera.enabled && camera.orthographic,"Invalid shadow camera");
            Require(camera.rect.width==1 && camera.rect.height==1 && camera.targetTexture==null,"Inherited camera target/rect");
            Require(camera.projectionMatrix.token==0 && camera.worldToCameraMatrix.token==0,"Inherited custom source matrices");
            if(Hooks.failSetup)throw new InvalidOperationException("Injected setup failure");
        }
        public void ExecuteCommandBuffer(CommandBuffer cmd){}
        public CullingResults Cull(ref CullingParameters p){return new CullingResults();}
        public void DrawRenderers(CullingResults c,ref DrawingSettings d,ref FilteringSettings f){Hooks.draws++;}
        public void Submit(){}
    }
    static class MaxRenderResources {
        public static int destroyedObjects;
        public static void Destroy<T>(ref T value)where T:class{if(value is GameObject)destroyedObjects++;value=null;}
        public static void UnbindTexture(string name,RenderTexture value){}
    }
    static class Hooks {public static Camera main;public static RenderTexture target;public static bool cullMiss,emptyRange,failSetup;public static int draws,clears;public static float viewportWidth;}
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    __EXECUTE__
    __DISPOSE__
    public static string Run() {
        var source=new Camera {targetTexture=new RenderTexture()};Hooks.main=source;
        var originalTarget=source.targetTexture;
        var pass=new ShadowCameraChecks();var context=new ScriptableRenderContext();
        pass.Execute(context,source);
        var shadow=pass.m_shadowCamera;
        Require(Math.Abs(Shader.sunTan-Math.Tan(Math.PI/180))<1e-7,"Shadow angle is not the sky solar radius");
        Require(Hooks.draws==4 && Hooks.clears==1 && shadow!=source,"Four cascades were not drawn on the separate camera");
        Require(!source.manualAspect && source.enabled && !source.orthographic,"Source auto-aspect/type/enabled changed");
        Require(source.nearClipPlane==.3f && source.farClipPlane==1000 && source.rect.width==.7f && source.rect.height==.6f,"Source clips/rect changed");
        Require(source.targetTexture==originalTarget && source.projectionMatrix.token==93 && source.worldToCameraMatrix.token==71,"Source target/custom matrices changed");
        source.viewportAspect=1920f/1080;
        pass.m_atmosphereSettings._SunDiskAngle=2;
        pass.Execute(context,source);
        Require(Math.Abs(Shader.sunTan-Math.Tan(2*Math.PI/180))<1e-7,"Edited solar radius did not update shadows");
        Require(pass.m_shadowCamera==shadow && source.aspect==source.viewportAspect,"Resize did not retain auto aspect/camera reuse");
        // A centered angular disk's pixel width/height is viewportAspect/cameraAspect.
        Require(source.viewportAspect/source.aspect==1,"Centered solar disk is stretched after resize");
        var legacy=new Camera();float saved=legacy.aspect;legacy.aspect=1;legacy.aspect=saved;legacy.viewportAspect=1920f/1080;
        Require(legacy.viewportAspect/legacy.aspect>1.4f,"Did not reproduce old flattened disk after resize");
        source.aspect=1.6f;pass.Execute(context,source);
        Require(source.manualAspect && source.aspect==1.6f,"Intentional manual aspect lost");
        Hooks.cullMiss=true;Require(!pass.Execute(context,source),"Failed culling retained stale shadow availability");Hooks.cullMiss=false;
        Hooks.failSetup=true;try{pass.Execute(context,source);throw new Exception("Failure injection missing");}catch(InvalidOperationException){}Hooks.failSetup=false;
        Require(source.aspect==1.6f && CommandBufferPool.outstanding==0,"Failure changed source state/leaked command buffer");
        Hooks.emptyRange=true;Require(!pass.Execute(context,source),"Empty range retained stale shadow availability");Hooks.emptyRange=false;
        RenderSettings.sun=null;Require(!pass.Execute(context,source),"No sun retained stale shadow availability");
        pass.Dispose();pass.Dispose();
        Require(pass.m_shadowCamera==null && MaxRenderResources.destroyedObjects==1,"Hidden shadow object disposal not idempotent");
        return "PASS: actual shadow Execute/Dispose; auto/manual aspect, resize, custom projection/view, split viewport, target, camera reuse, four cascades, culling miss/exception/empty/no-sun and resource cleanup. Legacy centered disk width/height: 1.422; fixed auto-aspect: 1.";
    }
}
'@
Add-Type -TypeDefinition ($taskHost.Replace('pass.Execute(context,source)', 'pass.Execute(context,source,new SunLightData())').Replace('__EXECUTE__', $taskExecute).Replace('__DISPOSE__', $taskDispose))
[ShadowCameraChecks]::Run()
$taskSky = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/Passes/MaxProceduralSkybox.cs'))
$taskSync = $taskSky.IndexOf('UnityEditor.ShaderUtil.SetAsyncCompilation(cmd, false);')
$taskBake = $taskSky.IndexOf('Update(cmd, camera, sun.AtmosphereDirection, skyEnabled, fogEnabled, environmentHeight);', $taskSync)
if ($taskSync -lt 0 -or $taskBake -lt 0) { throw 'Missing atmosphere LUT compilation scope/update.' }
$taskRestore = $taskSky.IndexOf('UnityEditor.ShaderUtil.RestoreAsyncCompilation(cmd);', $taskBake)
$taskSubmit = $taskSky.IndexOf('context.ExecuteCommandBuffer(cmd);', $taskBake)
if ($taskSync -lt 0 -or $taskBake -lt $taskSync -or $taskRestore -lt $taskBake -or $taskSubmit -lt $taskRestore) {
    throw 'LUT compilation mode is not scoped and restored before command submission.'
}
Write-Output 'PASS: atmosphere LUT synchronous compilation is scoped to the Editor command buffer.'
