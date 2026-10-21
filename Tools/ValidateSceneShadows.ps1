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
    'public bool UpdateCascade(', 'public static float CascadeTransitionWidth(',
    'private static Quaternion LightRotation(', 'private static void FillFrustumPlaneCorners(', 'private static void FillFrustumPlaneCornersView(',
    'Vector3 matTransform(', 'private static Vector4 StableCascadeSphere(', 'public float ConfigCameraToShadowSpace('
) | ForEach-Object { Get-TaskBlock $taskCaster $_ }
$taskFunctions = ($taskFunctions -join "`n").Replace('Matrix4x4', 'Matrix').Replace('Quaternion', 'Rotation')
$taskFunctions = [regex]::Replace($taskFunctions, '(?m)//[^\r\n]*', '')
$taskFunctions = $taskFunctions.Replace('public float ConfigCameraToShadowSpace(', 'private float ConfigCameraToShadowSpace(')
$taskFunctions = $taskFunctions.Replace('public bool UpdateCascade(', 'private bool UpdateCascade(')
$taskFunctions = $taskFunctions.Replace('Vector3.Magnitude(', 'Magnitude(').Replace('Vector3.zero', 'Vector3.Zero')
$taskFunctions = $taskFunctions.Replace('Vector3.one', 'Vector3.One').Replace('Vector3.up', 'Vector3.UnitY').Replace('Vector3.forward', 'Vector3.UnitZ')
$taskFunctions = $taskFunctions.Replace('.x', '.X').Replace('.y', '.Y').Replace('.z', '.Z')
$taskFunctions = [regex]::Replace($taskFunctions, '\.w\b', '.W')
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
        public static float Clamp(float x,float a,float b){return Math.Max(a,Math.Min(b,x));}
        public static float Min(params float[] x){float v=x[0];foreach(float p in x)v=Math.Min(v,p);return v;}
        public static float Max(params float[] x){float v=x[0];foreach(float p in x)v=Math.Max(v,p);return v;}
    }
    Vector4[] m_cascadeSpheres=new Vector4[4];
    class ShadowSettings {public float MaxPenumbraRadius=.75f,ShadingPointNormalBias=.1f;}
    class CascadeSettings {
        public float ShadowDistance=500,CascadeBlendFraction=.1f,DistanceFadeFraction=.1f;
        public void Set(){}
        public ShadowSettings GetCascade(int i){return new ShadowSettings();}
    }
    CascadeSettings m_cascadeSettings=new CascadeSettings();
    static class ShaderProperties {public const int _OrthoDistance=0,_CascadeDepthRange=1,_Split0=2,_Split1=3,_Split2=4,_Split3=5;}
    static class Shader {public static void SetGlobalFloat(int id,float value){}public static void SetGlobalFloat(string id,float value){}public static void SetGlobalMatrix(string id,Matrix value){}public static void SetGlobalVector(int id,Vector4 value){}public static void SetGlobalVector(string id,Vector4 value){}}
    float m_OrthoDistance;
    float[] m_Splts={.07f,.13f,.25f,.55f},m_splitEnds=new float[4];
    Vector3[] m_nearCorners=new Vector3[4],m_farCorners=new Vector3[4];
    Vector3[] m_f0Near=new Vector3[4],m_f0Far=new Vector3[4],m_f1Near=new Vector3[4],m_f1Far=new Vector3[4];
    Vector3[] m_f2Near=new Vector3[4],m_f2Far=new Vector3[4],m_f3Near=new Vector3[4],m_f3Far=new Vector3[4];
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
        var test=new SceneShadowChecks();int cornerCases=0,coverageCases=0,expandedRanges=0,overlapChecks=0;
        foreach(bool ortho in new bool[]{false,true})foreach(float aspect in new float[]{.75f,1.7777778f,3})
        foreach(float near in ortho?new float[]{.3f,-5000}:new float[]{.03f,.3f}){
            var cam=new Camera();float far=10000;
            var raw=ortho?Matrix4x4.CreateOrthographic(300*aspect,300,near,far):Matrix4x4.CreatePerspectiveFieldOfView(100*(float)Math.PI/180,aspect,near,far);
            if(!ortho){raw.M31=.15f;raw.M32=-.1f;} // Off-centre/custom projection.
            cam.projectionMatrix=CPUProjection(raw);
            var pos=new Vector3(120,75,-90);var target=pos+Vector3.Normalize(new Vector3(.4f,-.1f,.8f));
            cam.worldToCameraMatrix=new Matrix(Matrix4x4.CreateLookAt(pos,target,Vector3.UnitY));
            cam.nearClipPlane=near;cam.farClipPlane=far;
            foreach(float fraction in new float[]{0,.1f,.3f})foreach(var direction in new Vector3[]{new Vector3(1,-1,1),-Vector3.UnitY}){
                test.m_cascadeSettings.CascadeBlendFraction=fraction;
                var light=Vector3.Normalize(direction);Require(test.UpdateCascade(cam,ref light),"Cascade update rejected supported camera");
                float[] edges={0,.07f,.2f,.45f,1};
                for(int boundary=0;boundary<3;boundary++){
                    float end=edges[boundary+1];
                    float width=CascadeTransitionWidth(edges[boundary],end,edges[boundary+2],fraction);
                    var a=new Vector3[4];var b=new Vector3[4];
                    FillFrustumPlaneCorners(cam,near+(500-near)*(end-width),a);
                    FillFrustumPlaneCorners(cam,near+(500-near)*end,b);
                    foreach(int cascade in new[]{boundary,boundary+1}){
                        var shadow=new Camera();test.ConfigCameraToShadowSpace(shadow,light,cascade,500,1024);
                        var inversePose=Matrix.TRS(shadow.transform.position,shadow.transform.rotation,Vector3.One).inverse;
                        foreach(var plane in new[]{a,b})foreach(var point in plane){
                            var local=inversePose.MultiplyPoint(point);float halfWidth=test.m_OrthoWidths[cascade]*.5f;
                            Require(Math.Abs(local.X)+.85f<=halfWidth+.01f && Math.Abs(local.Y)+.85f<=halfWidth+.01f,"Blended receiver/PCSS kernel falls outside adjacent cascade");
                            Require(Math.Abs(local.Z)<=shadow.farClipPlane+.01f,"Blended receiver depth outside adjacent cascade");
                            overlapChecks++;
                        }
                    }
                }
            }
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
                    var viewNear=new Vector3[4];var viewFar=new Vector3[4];
                    FillFrustumPlaneCornersView(cam,d0,viewNear);FillFrustumPlaneCornersView(cam,d1,viewFar);
                    test.m_cascadeSpheres[cascade]=StableCascadeSphere(viewNear,viewFar,cam.worldToCameraMatrix.inverse);
                    var shadow=new Camera();float halfRange=test.ConfigCameraToShadowSpace(shadow,light,cascade,500,1024);
                    if(halfRange>500)expandedRanges++;
                    Require(halfRange==shadow.farClipPlane && -halfRange==shadow.nearClipPlane,"PCSS depth range differs from caster camera");
                    var inversePose=Matrix.TRS(shadow.transform.position,shadow.transform.rotation,Vector3.One).inverse;
                    foreach(var point in original){
                        var local=inversePose.MultiplyPoint(point);
                        float width=test.m_OrthoWidths[cascade];
                        Require(Math.Abs(local.X)<=width*.5f+.01f && Math.Abs(local.Y)<=width*.5f+.01f,"Caster XY fit loses frustum corner after snapping");
                        Require(Math.Abs(local.X)+.85f<=width*.5f+.01f && Math.Abs(local.Y)+.85f<=width*.5f+.01f,"Caster XY fit loses penumbra/normal-bias padding");
                        Require(Math.Abs(local.Z)<=halfRange+.01f,"Caster depth clips wide/orthographic frustum");
                        coverageCases++;
                    }
                }
            }
        }
        Require(expandedRanges>0,"Test did not exercise frusta that exceeded the old light-depth range");
        int stableChecks=0;
        foreach(bool ortho in new[]{false,true})foreach(float aspect in new[]{.75f,1.7777778f}){
            var cam=new Camera();cam.nearClipPlane=.3f;cam.farClipPlane=10000;
            var raw=ortho?Matrix4x4.CreateOrthographic(300*aspect,300,.3f,10000):Matrix4x4.CreatePerspectiveFieldOfView(1.1f,aspect,.3f,10000);
            if(!ortho){raw.M31=.15f;raw.M32=-.1f;}
            cam.projectionMatrix=CPUProjection(raw);
            float[] baselineWidths=new float[4];Vector2[] baselineGrid=new Vector2[4];
            var light=Vector3.Normalize(new Vector3(.4f,-.8f,.3f));var fixedPoint=new Vector3(17,2,-13);
            for(int pose=0;pose<24;pose++){
                float angle=pose==0?0:pose<4?pose*.001f:pose*.17f;
                var orientation=Matrix4x4.CreateFromYawPitchRoll(angle,angle*.3f,angle*.2f);
                var pos=new Vector3(120+pose*.031f,75,-90+pose*.017f);
                var forward=Vector3.TransformNormal(Vector3.UnitZ,orientation);var up=Vector3.TransformNormal(Vector3.UnitY,orientation);
                cam.worldToCameraMatrix=new Matrix(Matrix4x4.CreateLookAt(pos,pos+forward,up));
                Require(test.UpdateCascade(cam,ref light),"Stable test cascade update failed");
                for(int cascade=0;cascade<4;cascade++){
                    var shadow=new Camera();test.ConfigCameraToShadowSpace(shadow,light,cascade,500,1024);
                    float width=test.m_OrthoWidths[cascade];
                    var point=Matrix.TRS(shadow.transform.position,shadow.transform.rotation,Vector3.One).inverse.MultiplyPoint(fixedPoint);
                    var grid=new Vector2(point.X,point.Y)*(1024/width);
                    if(pose==0){baselineWidths[cascade]=width;baselineGrid[cascade]=grid;}
                    Require(width==baselineWidths[cascade],"Camera pose changed shadow extent/texel scale");
                    var delta=grid-baselineGrid[cascade];
                    Require(Math.Abs(delta.X-Math.Round(delta.X))<.005 && Math.Abs(delta.Y-Math.Round(delta.Y))<.005,"World point moved by a fractional shadow texel");
                    stableChecks++;
                }
            }
        }
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
        return "PASS: actual cascade update/frustum/bounds/fit methods: "+cornerCases+" custom-perspective/orthographic/negative-near corners, "+coverageCases+" coverage checks; "+overlapChecks+" adjacent cascade overlap/kernel checks; "+reconstructionCases+" camera-specific depth reconstructions; "+stableChecks+" rotation/translation extent and texel-grid invariance checks.";
    }
}
'@
Add-Type -TypeDefinition ($taskHost.Replace('__FUNCTIONS__', $taskFunctions))
[SceneShadowChecks]::Run()

$taskLightPass = Read-TaskSource 'Assets/MaxRendererPipeline/Runtime/Passes/MaxLightPass.cs'
$taskShadowShader = Read-TaskSource 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl'
$taskPipeline = Read-TaskSource 'Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs'
$taskLighting = Read-TaskSource 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Lighting.hlsl'
foreach ($taskToken in @('_ShadowReceiverView', '_MainLightShadowsAvailable')) {
    if (-not $taskLightPass.Contains($taskToken) -or -not $taskShadowShader.Contains($taskToken)) { throw "Missing direct shadow receiver input: $taskToken" }
}
if (-not $taskCaster.Contains('cmd.SetGlobalMatrix("_ShadowCasterVP", shadowVP)') -or
    -not $taskShadowShader.Contains('mul(_ShadowCasterVP,') -or
    -not $taskCaster.Contains('m_shadowCamera.projectionMatrix, true')) { throw 'Caster projection convention differs.' }
if ($taskPipeline.Contains('resources.Shadows') -or $taskLighting.Contains('_ScreenSpaceShadowMapBlur') -or
    -not $taskLighting.Contains('float2 uv, float visibility)') -or
    -not $taskPipeline.Contains('viewport, m_iblGIPass, hasShadows)')) { throw 'Direct PCSS wiring/availability is incorrect.' }
Write-Output 'PASS: Lighting evaluates CSM/PCSS directly with current-camera view/size/availability; no screen-space shadow pass.'
foreach ($taskToken in @('_ShadowNoiseWorldToLight', '_ShadowNoiseWorldTexelSize')) {
    if (-not $taskCaster.Contains($taskToken) -or -not $taskShadowShader.Contains($taskToken)) { throw "Missing world-anchored PCSS sampling phase: $taskToken" }
}
if ($taskShadowShader.Contains('screenUV') -or $taskShadowShader.Contains('_ShadowReceiverScreenSize')) {
    throw 'PCSS sampling rotation still follows moving screen pixels.'
}
Write-Output 'PASS: PCSS rotation is anchored to the light/world frame, independent of screen pixels.'
$taskFragment = Read-TaskSource 'Assets/MaxRendererPipeline/Shaders/Realtime/PBRLitPass.hlsl'
$taskReuse = $taskFragment.IndexOf('visibility = MainLightShadow(')
$taskBackgroundReturn = $taskFragment.IndexOf('    if (isBackground)')
if ($taskReuse -lt 0 -or $taskBackgroundReturn -gt $taskReuse -or $taskFragment.Contains('MainLightShadowFiltered(')) {
    throw 'Runtime lighting must evaluate raw PCSS on geometry without spatial quad filtering.'
}
Write-Output 'PASS: raw PCSS on geometry; no runtime spatial quad reuse.'
$taskDerivatives = $taskFragment.IndexOf('ShadowReceiverDerivatives(pW.xyz, receiverDx, receiverDy)')
if ($taskDerivatives -lt 0 -or $taskDerivatives -gt $taskBackgroundReturn -or
    -not $taskFragment.Contains('MainLightShadow(pW.xyz, normal, receiverDx, receiverDy)')) {
    throw 'Geometric receiver derivatives must precede background control flow and be passed explicitly to PCSS.'
}
Write-Output 'PASS: receiver derivatives precede background divergence; all cascades use the explicit geometric plane.'
