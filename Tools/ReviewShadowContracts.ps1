$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
function Read-ReviewSource([string]$path) {
    [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes((Join-Path $taskRoot $path)))
}
function Get-ReviewBlock([string]$source, [string]$signature) {
    $start = $source.IndexOf($signature)
    if ($start -lt 0) { throw "Missing source function: $signature" }
    $brace = $source.IndexOf('{', $start)
    $depth = 1
    for ($end = $brace + 1; $depth -gt 0; ++$end) {
        if ($source[$end] -eq '{') { ++$depth }
        if ($source[$end] -eq '}') { --$depth }
    }
    $source.Substring($start, $end - $start)
}
$shadow = Read-ReviewSource 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/Shadow.hlsl'
$radius = Get-ReviewBlock $shadow 'float PCSSFilterRadius('
$radius = $radius.Replace('float PCSSFilterRadius(', 'public static float PCSSFilterRadius(')
$radius = [regex]::Replace($radius, '(?<![\w.])(\d+\.\d+(?:[eE][+-]?\d+)?|\d+[eE][+-]?\d+)(?![\w.])', '${1}f')
Add-Type -TypeDefinition @"
using System;
public static class ReviewShadowRadius {
    static float max(float a,float b) => Math.Max(a,b);
    static float min(float a,float b) => Math.Min(a,b);
    static float saturate(float a) => Math.Clamp(a,0,1);
    $radius
}
"@
$tanRadius = [single][Math]::Tan([Math]::PI / 180.0)
$r0 = [ReviewShadowRadius]::PCSSFilterRadius(20, 10, $tanRadius, 0.75, 100)
$r1 = [ReviewShadowRadius]::PCSSFilterRadius(120, 110, $tanRadius, 0.75, 100)
if ([Math]::Abs($r0 - $r1) -gt 1e-8) { throw 'Directional penumbra depends on the shadow near origin.' }

$pipeline = Read-ReviewSource 'Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs'
$prune = Get-ReviewBlock $pipeline 'private void PruneCameraResources('
$pruneOutput = Get-ReviewBlock $pipeline 'private void PruneOutputResources('
Add-Type -TypeDefinition @"
using System.Collections.Generic;
public class ReviewCameraCaches {
    class Camera { public int id; }
    static long GetOutputKey(Camera camera) { return camera.id; }
    class CameraResources { public Camera Camera; public bool disposed; public void Dispose(){disposed=true;} }
    class OutputResources { public Camera LastCamera; public bool disposed; public void Dispose(){disposed=true;} }
    Dictionary<int,CameraResources> m_cameraResources=new Dictionary<int,CameraResources>();
    List<int> m_unusedCameras=new List<int>();
    Dictionary<long,OutputResources> m_outputResources=new Dictionary<long,OutputResources>();
    List<long> m_unusedOutputs=new List<long>();
    Dictionary<long,int> m_outputSizes=new Dictionary<long,int>();
    $prune
    $pruneOutput
    public static bool[] Run(){
        var test=new ReviewCameraCaches();
        var a=new Camera{id=1}; var b=new Camera{id=2};
        var resourceA=new CameraResources{Camera=a}; var resourceB=new CameraResources{Camera=b};
        var outputA=new OutputResources{LastCamera=a}; var outputB=new OutputResources{LastCamera=b};
        test.m_cameraResources.Add(1,resourceA); test.m_cameraResources.Add(2,resourceB);
        test.m_outputResources.Add(1,outputA); test.m_outputResources.Add(2,outputB);
        test.m_outputSizes.Add(1,1);
        test.PruneCameraResources(new[]{a}); test.PruneOutputResources(new[]{a});
        return new[]{!resourceA.disposed, resourceB.disposed, !outputA.disposed, outputB.disposed};
    }
}
"@
$cacheResult = [ReviewCameraCaches]::Run()
$report = [ordered]@{
    Scope = 'Source-extracted scalar PCSS function and cache release functions with API fixtures; screen-space blur removal check. Not a Unity GPU/scene reproduction.'
    PCSSOriginInvariance = @{
        FixedReceiverBlockerSeparation = 10
        UVRadiusNearOrigin0 = $r0
        UVRadiusNearOriginMinus100 = $r1
        RadiusRatio = $r0 / $r1
        ExpectedDirectionalProperty = 'Moving an orthographic light camera along its axis must not alter penumbra for unchanged receiver/blocker/light geometry.'
    }
    ScreenSpaceShadowsRemoved = -not $pipeline.Contains('resources.Shadows')
    SeparateRenderCallCache = @{
        CurrentCameraPreserved = $cacheResult[0]
        OtherLiveCameraResourcesDisposed = $cacheResult[1]
        CurrentOutputPreserved = $cacheResult[2]
        OtherLiveOutputDisposed = $cacheResult[3]
    }
    GBufferColorMiBAt1920x1080 = 1920 * 1080 * (4 + 4 + 8 + 16) / 1MB
}
$report | ConvertTo-Json -Depth 6
