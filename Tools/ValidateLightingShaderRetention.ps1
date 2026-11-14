param([string]$UnityEditorData = 'C:/Program Files/Unity/Hub/Editor/2022.3.62f2c1/Editor/Data')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskRuntime = Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime'
$taskShader = Join-Path $taskRoot 'Assets/MaxRendererPipeline/Shaders/Realtime/PBRLitPass.shader'
$taskGuid = [regex]::Match([IO.File]::ReadAllText("$taskShader.meta"), '(?m)^guid: (\w+)').Groups[1].Value
if (-not $taskGuid) { throw 'Lighting shader GUID is missing.' }
$taskReference = "m_lightingShader: {fileID: 4800000, guid: $taskGuid, type: 3}"
$taskScriptGuid = [regex]::Match([IO.File]::ReadAllText((Join-Path $taskRuntime 'MaxRendererPipelineAsset.cs.meta')), '(?m)^guid: (\w+)').Groups[1].Value
$taskAssets = @(Get-ChildItem (Join-Path $taskRoot 'Assets') -Filter '*.asset' -Recurse | Where-Object {
    [IO.File]::ReadAllText($_.FullName).Contains("m_Script: {fileID: 11500000, guid: $taskScriptGuid, type: 3}")
})
if ($taskAssets.Count -eq 0) { throw 'No pipeline assets found.' }
foreach ($taskAsset in $taskAssets) {
    if (-not [IO.File]::ReadAllText($taskAsset.FullName).Contains($taskReference)) {
        throw "Missing serialized Lighting Shader dependency: $($taskAsset.FullName)"
    }
}
if (-not [IO.File]::ReadAllText((Join-Path $taskRuntime 'MaxRendererPipelineAsset.cs.meta')).Contains($taskReference)) {
    throw 'New pipeline asset default reference is missing.'
}
# The pipeline owns the quality selector and configuration references. Each
# module retains its own PC/Mobile parameters and shader dependencies.
function Get-TaskGuid([string]$relativePath) {
    $metaPath = Join-Path $taskRoot ($relativePath + '.meta')
    $guid = [regex]::Match([IO.File]::ReadAllText($metaPath), '(?m)^guid: ([a-f0-9]{32})\r?$').Groups[1].Value
    if (-not $guid) { throw "Missing or invalid GUID: $metaPath" }
    return $guid
}
function Assert-TaskLine([string]$path, [string]$line) {
    if (-not [regex]::IsMatch([IO.File]::ReadAllText($path), '(?m)^' + [regex]::Escape($line) + '\r?$')) {
        throw "Serialized dependency missing: $path / $line"
    }
}
$taskModules = @(
    @('m_atmosphereConfiguration', 'DefaultAtmosphere', 'MaxAtmosphereConfig'),
    @('m_temporalAAConfiguration', 'DefaultTemporalAA', 'MaxTemporalAAConfig'),
    @('m_cloudConfiguration', 'DefaultCloud', 'MaxCloudConfig'),
    @('m_shadowConfiguration', 'DefaultShadow', 'MaxShadowConfig'),
    @('m_clusteredLightingConfiguration', 'DefaultClusteredLighting', 'MaxClusteredLightingConfig'),
    @('m_aoConfiguration', 'DefaultAO', 'MaxAOConfig'),
    @('m_postProcessConfiguration', 'DefaultPostProcess', 'MaxPostProcessConfig')
)
$taskPipelineMeta = Join-Path $taskRuntime 'MaxRendererPipelineAsset.cs.meta'
foreach ($taskModule in $taskModules) {
    $taskModulePath = 'Assets/MaxRendererPipeline/' + $taskModule[1] + '.asset'
    $taskModuleAsset = Join-Path $taskRoot $taskModulePath
    $taskModuleSource = Join-Path $taskRuntime ($taskModule[2] + '.cs')
    $taskModuleGuid = Get-TaskGuid $taskModulePath
    $taskModuleScriptGuid = Get-TaskGuid ('Assets/MaxRendererPipeline/Runtime/' + $taskModule[2] + '.cs')
    Assert-TaskLine $taskModuleAsset ('  m_Script: {fileID: 11500000, guid: ' + $taskModuleScriptGuid + ', type: 3}')
    Assert-TaskLine $taskPipelineMeta ('  - ' + $taskModule[0] + ': {fileID: 11400000, guid: ' + $taskModuleGuid + ', type: 2}')
    foreach ($taskAsset in $taskAssets) {
        # Configurations may be replaced with project-authored assets. Require a
        # non-null typed asset reference, rather than hard-coding every pipeline
        # to the default configuration GUID.
        $taskConfigPattern = '(?m)^  ' + [regex]::Escape($taskModule[0]) + ': \{fileID: 11400000, guid: [a-f0-9]{32}, type: 2\}\r?$'
        if (-not [regex]::IsMatch([IO.File]::ReadAllText($taskAsset.FullName), $taskConfigPattern)) {
            throw "Missing configuration reference: $($taskAsset.FullName) / $($taskModule[0])"
        }
    }
    $taskModuleText = [IO.File]::ReadAllText($taskModuleAsset)
    $taskModuleCode = [IO.File]::ReadAllText($taskModuleSource)
    foreach ($taskProfile in @('m_pc', 'm_mobile')) {
        if ($taskModuleText -notmatch ('(?m)^  ' + $taskProfile + ':\r?$') -or
            $taskModuleCode -notmatch ('\b' + $taskProfile + '\b')) {
            throw "PC/Mobile profile missing: $taskModuleAsset / $taskProfile"
        }
    }
    if ($taskModuleText -match '(?m)^  m_platform:' -or
        $taskModuleCode -match '\b(?:private|public|internal)\s+\w+\s+m_platform\b') {
        throw "Only the pipeline may own the platform selector: $taskModuleAsset"
    }
}
foreach ($taskAsset in $taskAssets) {
    $taskPipelineText = [IO.File]::ReadAllText($taskAsset.FullName)
    if ($taskPipelineText -notmatch '(?m)^  m_platform: [01]\r?$') { throw "Pipeline quality selector missing: $($taskAsset.FullName)" }
    if ($taskPipelineText -match '(?m)^  m_(?:clusteredLightingSettings|clusteredLightingCS|colorOutputSettings|temporalAASettings|temporalShader|cascadeSettings|skyIBLSettings|cloudLayerSettings|sixWayCloudSettings|iblCS|aerialPerspectiveCompute|atmosphereScatteringSetting):') {
        throw "Legacy inline module data remains on the pipeline: $($taskAsset.FullName)"
    }
}

# Assert both default assets and new-asset script references. Dependencies are
# top-level fields; profiles share these shaders while retaining their parameters.
$taskDependencies = @(
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_iblCS', 'Assets/MaxRendererPipeline/Shaders/Realtime/PrecomputeIBL.compute', 7200000),
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_aerialPerspectiveCompute', 'Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/AerialPerspectiveLUT.compute', 7200000),
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_proceduralSkyboxShader', 'Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyboxProcedural.shader', 4800000),
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_transmittanceShader', 'Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/TransmittanceLUT.shader', 4800000),
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_multiscatteringShader', 'Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/MultiscatteringLUT.shader', 4800000),
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_skyviewShader', 'Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/SkyviewLUT.shader', 4800000),
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_aerialPerspectiveLUTShader', 'Assets/MaxRendererPipeline/Shaders/Realtime/AtmosphereScattering/AerialPerspectiveLUT.shader', 4800000),
    @('DefaultTemporalAA', 'MaxTemporalAAConfig', 'm_temporalShader', 'Assets/MaxRendererPipeline/Shaders/Realtime/TemporalAA.shader', 4800000),
    @('DefaultCloud', 'MaxCloudConfig', 'm_layerLightingShader', 'Assets/MaxRendererPipeline/Shaders/Realtime/CloudLayerLighting.shader', 4800000),
    @('DefaultCloud', 'MaxCloudConfig', 'm_sixWayShader', 'Assets/MaxRendererPipeline/Shaders/Realtime/SixWayCloud.shader', 4800000),
    @('DefaultShadow', 'MaxShadowConfig', 'm_tileClearShader', 'Assets/MaxRendererPipeline/Shaders/Realtime/ShadowTileClear.shader', 4800000),
    @('DefaultClusteredLighting', 'MaxClusteredLightingConfig', 'm_compute', 'Assets/MaxRendererPipeline/Shaders/Realtime/ClusteredLighting.compute', 7200000),
    @('DefaultAO', 'MaxAOConfig', 'm_compute', 'Assets/MaxRendererPipeline/Shaders/Realtime/GTAO/MaxGTAO.compute', 7200000),
    @('DefaultAO', 'MaxAOConfig', 'm_ssaoShader', 'Assets/MaxRendererPipeline/Shaders/Realtime/SSAO/MaxSSAO.shader', 4800000),
    @('DefaultPostProcess', 'MaxPostProcessConfig', 'm_exposureCompute', 'Assets/MaxRendererPipeline/Shaders/Realtime/PostProcessExposure.compute', 7200000),
    @('DefaultPostProcess', 'MaxPostProcessConfig', 'm_bloomShader', 'Assets/MaxRendererPipeline/Shaders/Realtime/PostProcessBloom.shader', 4800000),
    @('DefaultPostProcess', 'MaxPostProcessConfig', 'm_outputShader', 'Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutput.shader', 4800000)
)
foreach ($taskDependency in $taskDependencies) {
    $taskDependencyGuid = Get-TaskGuid $taskDependency[3]
    $taskDependencyReference = $taskDependency[2] + ': {fileID: ' + $taskDependency[4] + ', guid: ' + $taskDependencyGuid + ', type: 3}'
    Assert-TaskLine (Join-Path $taskRoot ('Assets/MaxRendererPipeline/' + $taskDependency[0] + '.asset')) ('  ' + $taskDependencyReference)
    Assert-TaskLine (Join-Path $taskRuntime ($taskDependency[1] + '.cs.meta')) ('  - ' + $taskDependencyReference)
}
$taskAOGuid = Get-TaskGuid 'Assets/MaxRendererPipeline/DefaultAO.asset'
Assert-TaskLine (Join-Path $taskRuntime 'MaxAOSettings.cs.meta') ('  - m_legacyConfiguration: {fileID: 11400000, guid: ' + $taskAOGuid + ', type: 2}')

# Bind production sources to installed Unity engine/editor APIs for both contexts.
# Only the two unchanged SRP Core helpers below use declaration-only stand-ins;
# package assemblies in Library are deliberately not accessed.
# This does not launch Unity, read Library, build a Player, or execute on a GPU.
$taskSdk = (& dotnet --list-sdks | Select-Object -Last 1)
if ($taskSdk -notmatch '^(\S+) \[(.+)\]') { throw 'A .NET SDK is required.' }
$taskCompiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn/bincore/csc.dll'
$taskReferences = @((Join-Path $UnityEditorData 'NetStandard/ref/2.1.0/netstandard.dll'))
$taskReferences += @(Get-ChildItem (Join-Path $UnityEditorData 'NetStandard/compat/2.1.0/shims') -Filter '*.dll' | ForEach-Object FullName)
$taskEngineReferences = @(Get-ChildItem (Join-Path $UnityEditorData 'Managed/UnityEngine') -Filter 'UnityEngine*.dll' | ForEach-Object FullName)
$taskSources = @(Get-ChildItem $taskRuntime -Filter '*.cs' -Recurse | ForEach-Object FullName)
$taskTemporary = Join-Path ([IO.Path]::GetTempPath()) ('MaxSRP-LightingRetention-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($taskTemporary)
$taskCoreDeclarations = Join-Path $taskTemporary 'SrpCoreDeclarations.cs'
[IO.File]::WriteAllText($taskCoreDeclarations, @'
namespace UnityEngine.Rendering {
    public static class CommandBufferPool {
        public static CommandBuffer Get(string name) { throw new System.NotImplementedException(); }
        public static void Release(CommandBuffer buffer) { throw new System.NotImplementedException(); }
    }
    public static class CoreUtils {
        public static void Destroy(UnityEngine.Object value) { throw new System.NotImplementedException(); }
    }
}
'@)
foreach ($taskContext in @('Player', 'Editor')) {
    $taskArguments = @('/nologo', '/target:library', '/nostdlib+', '/langversion:9.0', '/warn:4', '/nowarn:0649')
    $taskArguments += '/out:"' + (Join-Path $taskTemporary "$taskContext.dll") + '"'
    $taskContextReferences = $taskReferences + $taskEngineReferences
    $taskContextSources = $taskSources + $taskCoreDeclarations
    if ($taskContext -eq 'Editor') {
        $taskArguments += '/define:UNITY_EDITOR'
        $taskContextReferences += @(Get-ChildItem (Join-Path $UnityEditorData 'Managed/UnityEngine') -Filter 'UnityEditor*.dll' | ForEach-Object FullName)
        $taskContextSources += Join-Path $taskRoot 'Assets/MaxRendererPipeline/Editor/MaxLightingShaderBuildValidator.cs'
    }
    $taskArguments += $taskContextReferences | Select-Object -Unique | ForEach-Object { '/reference:"' + $_ + '"' }
    $taskArguments += $taskContextSources | ForEach-Object { '"' + $_ + '"' }
    $taskResponse = Join-Path $taskTemporary "$taskContext.rsp"
    [IO.File]::WriteAllLines($taskResponse, $taskArguments)
    & dotnet $taskCompiler "@$taskResponse"
    if ($LASTEXITCODE -ne 0) { throw "$taskContext Unity API compilation failed." }
}
Write-Output "PASS: $($taskAssets.Count) pipeline asset(s), seven PC/Mobile configurations and $($taskDependencies.Count) module shader dependencies retained; Player/Editor Unity API compilation passed."
Write-Output "Compilation artifacts: $taskTemporary (not a Player build)."
