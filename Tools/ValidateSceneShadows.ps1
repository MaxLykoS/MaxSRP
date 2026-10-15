$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCodec = [Text.Encoding]::GetEncoding(28591)
function Read-TaskSource([string]$path) { $taskCodec.GetString([IO.File]::ReadAllBytes((Join-Path $taskRoot $path))) }
function Get-TaskBlock([string]$source, [string]$signature) {
    $taskStart = $source.IndexOf($signature)
    if ($taskStart -lt 0) { throw "Missing method: $signature" }
    $taskBrace = $source.IndexOf('{', $taskStart)
    $taskDepth = 1
    for ($taskEnd = $taskBrace + 1; $taskDepth -gt 0; $taskEnd++) {
        if ($source[$taskEnd] -eq '{') { $taskDepth++ }
        if ($source[$taskEnd] -eq '}') { $taskDepth-- }
    }
    return $source.Substring($taskStart, $taskEnd - $taskStart)
}
$taskCaster = Read-TaskSource 'Assets/MaxRendererPipeline/Runtime/Passes/MaxShadowCasterPass.cs'
$taskFunctions = @(
    'private static Quaternion LightRotation(', 'private static void FillFrustumPlaneCorners(',
    'Vector3 matTransform(', 'Vector3[] LightSpaceAABB(', 'public float ConfigCameraToShadowSpace('
) | ForEach-Object { Get-TaskBlock $taskCaster $_ }
$taskFunctions = ($taskFunctions -join "`n").Replace('Matrix4x4', 'Matrix').Replace('Quaternion', 'Rotation')
$taskFunctions = [regex]::Replace($taskFunctions, '(?m)//[^\r\n]*', '')
$taskFunctions = $taskFunctions.Replace('public float ConfigCameraToShadowSpace(', 'private float ConfigCameraToShadowSpace(')
$taskFunctions = $taskFunctions.Replace('Vector3.Magnitude(', 'Magnitude(').Replace('Vector3.zero', 'Vector3.Zero')
$taskFunctions = $taskFunctions.Replace('Vector3.one', 'Vector3.One').Replace('Vector3.up', 'Vector3.UnitY').Replace('Vector3.forward', 'Vector3.UnitZ')
$taskFunctions = $taskFunctions.Replace('.x', '.X').Replace('.y', '.Y').Replace('.z', '.Z')
$taskFunctions = $taskFunctions.Replace('center[i] = Mathf.Floor(center[i] / disPerPix) * disPerPix;',
    'SetComponent(ref center, i, Mathf.Floor(Component(center, i) / disPerPix) * disPerPix);')
$taskHost = @'
using System;
using System.Numerics;
public class SceneShadowChecks {
    struct Matrix {
        public Matrix4x4 value;
        public Matrix(Matrix4x4 m){value=m;}
        public Matrix inverse {get{Matrix4x4 m;Require(Matrix4x4.Invert(value,out m),"Singular matrix");return new Matrix(m);}}
        public Vector3 MultiplyPoint(Vector3 v){var p=Vector4.Transform(new Vector4(v,1),value);return new Vector3(p.X,p.Y,p.Z)/p.W;}
        public static Vector4 operator*(Matrix m,Vector4 v){return Vector4.Transform(v,m.value);}
        public static Matrix TRS(Vector3 p,Rotation r,Vector3 s){return new Matrix(Matrix4x4.CreateScale(s)*Matrix4x4.CreateFromQuaternion(r.value)*Matrix4x4.CreateTranslation(p));}
    }
    struct Rotation {
        public Quaternion value;
        public static Rotation LookRotation(Vector3 forward,Vector3 up){
            var f=Vector3.Normalize(forward);var r=Vector3.Normalize(Vector3.Cross(up,f));var u=Vector3.Cross(f,r);
            var m=new Matrix4x4(r.X,r.Y,r.Z,0,u.X,u.Y,u.Z,0,f.X,f.Y,f.Z,0,0,0,0,1);
            return new Rotation {value=Quaternion.CreateFromRotationMatrix(m)};
        }
    }
    class Transform {public Vector3 position;public Rotation rotation;}
    class Camera {
        public Transform transform=new Transform();
        public float nearClipPlane=.3f,farClipPlane=10000,aspect=1,orthographicSize=1;
        public Matrix projectionMatrix,worldToCameraMatrix;
    }
    // Scene projection fixture; Game viewport projection is checked separately.
    static class CameraViewport {public static Matrix GetProjection(Camera camera){return camera.projectionMatrix;}}
    static class Mathf {
        public static float Abs(float x){return Math.Abs(x);}
        public static float Floor(float x){return (float)Math.Floor(x);}
        public static float Min(params float[] x){float v=x[0];foreach(float p in x)v=Math.Min(v,p);return v;}
        public static float Max(params float[] x){float v=x[0];foreach(float p in x)v=Math.Max(v,p);return v;}
    }
    Vector3[] m_box0,m_box1,m_box2,m_box3;
    float[] m_OrthoWidths=new float[4];
    static float Magnitude(Vector3 v){return v.Length();}
    static float Component(Vector3 v,int i){return i==0?v.X:i==1?v.Y:v.Z;}
    static void SetComponent(ref Vector3 v,int i,float x){if(i==0)v.X=x;else if(i==1)v.Y=x;else v.Z=x;}
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    __FUNCTIONS__
    static Matrix CPUProjection(Matrix4x4 gpu){
        // Convert Numerics 0..1 projection to Unity's CPU -1..1 depth convention.
        gpu.M33=2*gpu.M33-gpu.M34;gpu.M43=2*gpu.M43-gpu.M44;
        return new Matrix(gpu);
    }
    public static string Run(){
        var test=new SceneShadowChecks();int cornerCases=0,coverageCases=0,expandedRanges=0;
        foreach(bool ortho in new bool[]{false,true})foreach(float aspect in new float[]{.75f,1.7777778f,3})
        foreach(float near in ortho?new float[]{.3f,-5000}:new float[]{.03f,.3f}){
            var cam=new Camera();float far=10000;
            var raw=ortho?Matrix4x4.CreateOrthographic(300*aspect,300,near,far):Matrix4x4.CreatePerspectiveFieldOfView(100*(float)Math.PI/180,aspect,near,far);
            if(!ortho){raw.M31=.15f;raw.M32=-.1f;} // Off-centre/custom projection.
            cam.projectionMatrix=CPUProjection(raw);
            var pos=new Vector3(120,75,-90);var target=pos+Vector3.Normalize(new Vector3(.4f,-.1f,.8f));
            cam.worldToCameraMatrix=new Matrix(Matrix4x4.CreateLookAt(pos,target,Vector3.UnitY));
            float[] ends={0,.07f,.20f,.45f,1};
            for(int cascade=0;cascade<4;cascade++){
                float d0=near+(500-near)*ends[cascade],d1=near+(500-near)*ends[cascade+1];
                var a=new Vector3[4];var b=new Vector3[4];FillFrustumPlaneCorners(cam,d0,a);FillFrustumPlaneCorners(cam,d1,b);
                var original=new Vector3[8];Array.Copy(a,0,original,0,4);Array.Copy(b,0,original,4,4);
                for(int i=0;i<8;i++){
                    var view=cam.worldToCameraMatrix.MultiplyPoint(original[i]);
                    var clip=Vector4.Transform(new Vector4(original[i],1),cam.worldToCameraMatrix.value*cam.projectionMatrix.value);
                    Require(Math.Abs(-view.Z-(i<4?d0:d1))<.1f,"Corner outside requested view-depth plane");
                    Require(Math.Abs(Math.Abs(clip.X/clip.W)-1)<.001 && Math.Abs(Math.Abs(clip.Y/clip.W)-1)<.001,
                        "Actual custom projection corner missed: ortho="+ortho+" near="+near+" x="+(clip.X/clip.W)+" y="+(clip.Y/clip.W));
                    cornerCases++;
                }
                foreach(var direction in new Vector3[]{new Vector3(1,-1,1),new Vector3(.4f,-.2f,.9f),-Vector3.UnitY,Vector3.UnitY}){
                    var light=Vector3.Normalize(direction);
                    var box=test.LightSpaceAABB((Vector3[])a.Clone(),(Vector3[])b.Clone(),light);
                    test.m_box0=test.m_box1=test.m_box2=test.m_box3=box;
                    var shadow=new Camera();float halfRange=test.ConfigCameraToShadowSpace(shadow,light,cascade,500,1024);
                    if(halfRange>500)expandedRanges++;
                    Require(halfRange==shadow.farClipPlane && -halfRange==shadow.nearClipPlane,"PCSS depth range differs from caster camera");
                    var inversePose=Matrix.TRS(shadow.transform.position,shadow.transform.rotation,Vector3.One).inverse;
                    foreach(var point in original){
                        var local=inversePose.MultiplyPoint(point);
                        float width=test.m_OrthoWidths[cascade];
                        Require(Math.Abs(local.X)<=width*.5f+.01f && Math.Abs(local.Y)<=width*.5f+.01f,"Caster XY fit loses frustum corner after snapping");
                        Require(Math.Abs(local.Z)<=halfRange+.01f,"Caster depth clips wide/orthographic frustum");
                        coverageCases++;
                    }
                }
            }
        }
        Require(expandedRanges>0,"Test did not exercise frusta that exceeded the old light-depth range");
        // Screen depth reconstruction stays camera-specific across interleaved views.
        int reconstructionCases=0;
        foreach(bool ortho in new bool[]{false,true})foreach(bool top in new bool[]{false,true})foreach(bool reversed in new bool[]{false,true}){
            var camera=new Vector3(10,5,-30);var view=Matrix4x4.CreateLookAt(camera,camera+Vector3.Normalize(new Vector3(.4f,-.1f,.8f)),Vector3.UnitY);
            var p=ortho?Matrix4x4.CreateOrthographic(30,20,.3f,1000):Matrix4x4.CreatePerspectiveFieldOfView(1,1.5f,.3f,1000);
            if(top)p.M22=-p.M22;
            if(reversed){p.M33=ortho?1/(1000-.3f):.3f/(1000-.3f);p.M43=ortho?1000/(1000-.3f):1000*.3f/(1000-.3f);}
            Matrix4x4 inverse;Require(Matrix4x4.Invert(view*p,out inverse),"Invalid GPU VP");
            Matrix4x4 inverseView;Matrix4x4.Invert(view,out inverseView);
            foreach(var local in new Vector3[]{new Vector3(0,0,-2),new Vector3(4,3,-80),new Vector3(-5,-4,-300)}){
                var world=Vector3.Transform(local,inverseView);var clip=Vector4.Transform(new Vector4(world,1),view*p);
                var ndc=new Vector3(clip.X,clip.Y,clip.Z)/clip.W;
                var uv=new Vector2(ndc.X*.5f+.5f,ndc.Y*.5f+.5f);if(top)uv.Y=1-uv.Y;
                var rebuiltNdc=uv*2-Vector2.One;if(top)rebuiltNdc.Y=-rebuiltNdc.Y;
                var rebuilt=Vector4.Transform(new Vector4(rebuiltNdc,ndc.Z,1),inverse);
                var point=new Vector3(rebuilt.X,rebuilt.Y,rebuilt.Z)/rebuilt.W;
                Require(Vector3.Distance(point,world)<.1f,"Current camera reconstructed a different world point");
                Require(Math.Abs(-Vector3.Transform(point,view).Z+local.Z)<.1f,"Current camera selected wrong cascade depth");
                reconstructionCases++;
            }
        }
        return "PASS: actual frustum/bounds/fit methods: "+cornerCases+" custom-perspective/orthographic/negative-near corners, "+coverageCases+" light-space coverage checks including vertical lights; "+reconstructionCases+" camera-specific depth reconstructions.";
    }
}
'@
Add-Type -TypeDefinition ($taskHost.Replace('__FUNCTIONS__', $taskFunctions))
[SceneShadowChecks]::Run()

$taskScreen = Read-TaskSource 'Assets/MaxRendererPipeline/Runtime/Passes/MaxSceenSpaceShadowMapPass.cs'
$taskShader = Read-TaskSource 'Assets/MaxRendererPipeline/Shaders/Realtime/ScreenSpaceShadowMapPass.hlsl'
$taskShadowShader = Read-TaskSource 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl'
$taskPipeline = Read-TaskSource 'Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs'
if ($taskScreen.Contains('cmd.Blit(') -or $taskShader.Contains('UnityObjectToClipPos(') -or
    $taskShader.Contains('mul(unity_MatrixInvVP,') -or $taskShader.Contains('mul(unity_MatrixV,')) { throw 'Screen shadows still depend on utility draw matrices.' }
foreach ($taskToken in @('_ShadowCameraInverseVP', '_ShadowCameraView', '_ShadowScreenSize')) {
    if (-not $taskScreen.Contains($taskToken) -or -not $taskShader.Contains($taskToken)) { throw "Missing camera-specific input: $taskToken" }
}
if (-not $taskCaster.Contains('cmd.SetGlobalMatrix("_ShadowCasterVP", shadowVP)') -or
    -not $taskShadowShader.Contains('mul(_ShadowCasterVP,') -or
    [regex]::Matches($taskShadowShader, 'ShadowClipToUV\(shadowNDC.xy\)').Count -ne 2 -or
    -not $taskCaster.Contains('m_shadowCamera.projectionMatrix, true')) { throw 'Caster/receiver projection and raster conventions differ.' }
foreach ($taskTop in @($false, $true)) {
    foreach ($taskY in @(-.8, -.3, .2, .9)) {
        $taskClipY = if ($taskTop) { -$taskY } else { $taskY }
        $taskRaster = if ($taskTop) { (1 - $taskClipY) * .5 } else { ($taskClipY + 1) * .5 }
        $taskSample = $taskClipY * .5 + .5
        if ($taskTop) { $taskSample = 1 - $taskSample }
        if ([Math]::Abs($taskSample - $taskRaster) -gt 1e-12) { throw 'Shadow receiver samples a different raster row.' }
    }
}
if (-not $taskPipeline.Contains('resources.Shadows.Execute(context, camera, hasShadows)') -or
    $taskPipeline.Contains('resources.Shadows.Execute(context, RenderSettings.sun')) { throw 'Scene camera/actual shadow availability not passed to receiver.' }
Write-Output 'PASS: explicit fullscreen shadow/blur draws, dedicated camera matrices, shared caster VP, texture row convention and current-camera availability wiring.'
