$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskHelper = Join-Path $taskRoot 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/Helper.hlsl'
$taskSource = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($taskHelper))

# Extract the actual HLSL arithmetic; do not maintain a separate CPU implementation.
function Get-TaskFunction([string]$name) {
    $taskMatch = [regex]::Match($taskSource, '(float3|float)\s+' + $name + '\([^)]*\)\s*\{')
    if (-not $taskMatch.Success) { throw "Missing shader function: $name" }
    $taskDepth = 1
    $taskEnd = $taskMatch.Index + $taskMatch.Length
    while ($taskDepth -gt 0) {
        if ($taskSource[$taskEnd] -eq '{') { $taskDepth++ }
        if ($taskSource[$taskEnd] -eq '}') { $taskDepth-- }
        $taskEnd++
    }
    $taskFunction = $taskSource.Substring($taskMatch.Index, $taskEnd - $taskMatch.Index)
    $taskFunction = $taskFunction.Replace('float3(', 'new Vector3(').Replace('float3', 'Vector3')
    $taskFunction = [regex]::Replace($taskFunction, '(?<![\w.])(\d+\.\d+)(?![\w.])', '${1}f')
    return 'public static ' + $taskFunction
}
$taskFunctions = (@('AtmosphereCameraPosition', 'RayIntersectSphere', 'RayIntersectGroundSphere') |
    ForEach-Object { Get-TaskFunction $_ }) -join "`n"
Add-Type -TypeDefinition @"
using System;
using System.Numerics;
public static class MaxAtmosphereGroundCheck {
    static float length(Vector3 v) { return v.Length(); }
    static float dot(Vector3 a, Vector3 b) { return Vector3.Dot(a, b); }
    static float sqrt(float v) { return (float)Math.Sqrt(v); }
    static float min(float a, float b) { return Math.Min(a, b); }
    static float max(float a, float b) { return Math.Max(a, b); }
    $taskFunctions

    static void Require(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }
    // Independent double-precision quadratic using the rounded float inputs.
    static double GroundReference(double radius, Vector3 start, Vector3 dir) {
        double x = start.X, y = start.Y, z = start.Z;
        double dx = dir.X, dy = dir.Y, dz = dir.Z;
        double r2 = x*x + y*y + z*z;
        double b = x*dx + y*dy + z*dz;
        if (r2 < radius*radius) return 0;
        if (r2 == radius*radius) return b < 0 ? 0 : -1;
        double a = dx*dx + dy*dy + dz*dz;
        double disc = b*b - a*(r2-radius*radius);
        if (disc < 0) return -1;
        double t0 = (-b-Math.Sqrt(disc))/a;
        double t1 = (-b+Math.Sqrt(disc))/a;
        return t0 >= 0 ? t0 : t1 >= 0 ? t1 : -1;
    }
    static float LegacyGround(float radius, Vector3 start, Vector3 dir) {
        float os = start.Length(), sh = Vector3.Dot(-start, dir);
        float oh = (float)Math.Sqrt(os*os-sh*sh);
        float ph = (float)Math.Sqrt(radius*radius-oh*oh);
        if (oh > radius) return -1;
        float t0 = sh-ph, t1 = sh+ph;
        return t0 < 0 ? t1 : t0;
    }
    public static string Run() {
        const float radius = 6360000f;
        int cases = 0, oldZero = 0, oldFar = 0;
        double maxRelative = 0;
        foreach (float height in new float[] {-100f, -0.1f, 0f, 0.01f, 0.1f, 0.24f, 0.26f, 0.5f, 1f, 1.5f, 10f, 100f, 1000f, 60000f}) {
            Vector3 eye = AtmosphereCameraPosition(height, 0, radius);
            Require(eye.Y >= radius+1f, "Observer rounded onto/below surface");
            Require(AtmosphereCameraPosition(height+120f, 120f, radius) == eye, "Sea-level offset mismatch");
            for (int i=0; i<=10000; i++) {
                float mu = -1f + 2f*i/10000f;
                Vector3 dir = new Vector3((float)Math.Sqrt(Math.Max(0,1-(double)mu*mu)), mu, 0);
                float actual = RayIntersectGroundSphere(radius, eye, dir);
                double reference = GroundReference(radius, eye, dir);
                Require(!float.IsNaN(actual) && !float.IsInfinity(actual), "Nonfinite intersection");
                Require((actual >= 0) == (reference >= 0), "Hit/miss mismatch h="+height+" mu="+mu);
                if (reference >= 0) {
                    double relative = Math.Abs(actual-reference)/Math.Max(1,reference);
                    maxRelative = Math.Max(relative,maxRelative);
                    Require(relative < 0.0005, "Distance error h="+height+" mu="+mu+" actual="+actual+" reference="+reference);
                    Require(actual < radius, "Selected opposite side of planet");
                }
                cases++;
            }
            // Near sea level all downward rows of the 128-high SkyView LUT hit ground.
            // At altitude the horizon moves down and some downward rays correctly miss.
            for (int row=0; row<64; row++) {
                float theta = (1f-(row+0.5f)/128f)*(float)Math.PI;
                Vector3 dir = new Vector3((float)Math.Sin(theta),(float)Math.Cos(theta),0);
                float actual = RayIntersectGroundSphere(radius,eye,dir);
                if (height <= 1f)
                    Require(actual > 0 && actual < radius, "Near-ground SkyView row zero/far/miss: "+row);
                double reference = GroundReference(radius,eye,dir);
                Require((actual >= 0) == (reference >= 0), "SkyView horizon classification: "+row);
                cases++;
                if (height == 0) {
                    float old = LegacyGround(radius,new Vector3(0,radius,0),dir);
                    if (old == 0) oldZero++;
                    if (old > radius * 0.01f) oldFar++;
                }
            }
        }
        Vector3 surface = new Vector3(0,radius,0);
        Require(RayIntersectGroundSphere(radius,surface,Vector3.UnitY) < 0, "Surface upward ray blocked");
        Require(RayIntersectGroundSphere(radius,surface,Vector3.UnitX) < 0, "Surface tangent ray blocked");
        Require(RayIntersectGroundSphere(radius,surface,-Vector3.UnitY) == 0, "Surface inward ray ignored");
        Require(RayIntersectGroundSphere(radius,new Vector3(0,radius-1,0),Vector3.UnitY) == 0, "Underground start traverses planet");
        Require(RayIntersectSphere(Vector3.Zero,radius,Vector3.Zero,Vector3.UnitX) == radius, "Inside sphere exit lost");
        Require(RayIntersectSphere(Vector3.Zero,1,new Vector3(2,0,0),Vector3.UnitY) < 0, "Sphere miss not rejected");
        Require(RayIntersectSphere(Vector3.Zero,1,new Vector3(2,1,0),-Vector3.UnitX) == 2, "Sphere tangent lost");
        Require(RayIntersectSphere(Vector3.Zero,1,new Vector3(2,0,0),-2*Vector3.UnitX) == 0.5f, "Nonunit direction unsupported");
        Require(RayIntersectSphere(new Vector3(1,0,0),1,new Vector3(3,0,0),-Vector3.UnitX) == 1, "Translated sphere broken");
        Require(RayIntersectSphere(Vector3.Zero,1,new Vector3(2,0,0),Vector3.Zero) < 0, "Zero direction unsupported");
        Require(oldZero > 0 && oldFar > 0, "Failed to reproduce original near-ground bug");
        return "PASS: "+cases+" ground cases, surface/inside/tangent/miss/nonunit boundaries; max relative distance error "+maxRelative.ToString("G4")+". Legacy zero/far rows: "+oldZero+"/"+oldFar+"; fixed: 0/0.";
    }
}
"@
[MaxAtmosphereGroundCheck]::Run()

# Check that each consumer uses the shared origin and solid-ground policy.
foreach ($taskName in @('SkyviewLUT', 'SkyboxProcedural', 'AerialPerspectiveLUT')) {
    $taskPath = Join-Path $taskRoot "Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/${taskName}Pass.hlsl"
    $taskText = [IO.File]::ReadAllText($taskPath)
    $taskPositionInput = if ($taskName -eq 'AerialPerspectiveLUT') { '_AerialCameraPosition.y' } elseif ($taskName -eq 'SkyviewLUT') { '_AtmosphereCameraY' } else { '_WorldSpaceCameraPos.y' }
    if (-not $taskText.Contains("AtmosphereCameraPosition($taskPositionInput, param.SeaLevel, param.PlanetRadius)")) {
        throw "Inconsistent atmosphere observer: $taskName"
    }
}
$taskMarch = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/AtomsphereScattering/RayMarching.hlsl'))
if ($taskMarch.Contains('if (d > 0)') -or $taskMarch.Contains('RayIntersectSphere(float3(0, 0, 0), param.PlanetRadius,')) {
    throw 'Integration ignores solid-ground zero hits.'
}
if (-not $taskMarch.Contains('if (d >= 0.0) dis = min(dis, d);') -or
    -not $taskMarch.Contains('if (ground >= 0.0) exit = min(exit, ground);') -or
    -not $taskMarch.Contains('if (dis <= 0.0) continue;') -or -not $taskMarch.Contains('if (dis <= 0.0) return color;')) {
    throw 'Sky/multiscattering integration lacks zero-length guards.'
}
$taskSky = [IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyboxProceduralPass.hlsl'))
if (-not $taskSky.Contains('RayIntersectGroundSphere(param.PlanetRadius, eyePos, viewDir)')) {
    throw 'Solar disk uses a different ground boundary.'
}
Write-Output 'PASS: shared sky/solar/aerial observer, solid ground clipping and zero-length integration guards.'
