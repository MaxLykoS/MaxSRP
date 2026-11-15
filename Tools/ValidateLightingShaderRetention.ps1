param([string]$UnityEditorData = 'C:/Program Files/Unity/Hub/Editor/2022.3.62f2c1/Editor/Data')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskRuntime = Join-Path $taskRoot 'Packages/com.maxsrp.render-pipelines.maxsrp/Runtime'
$taskShader = Join-Path $taskRoot 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/PBRLitPass.shader'
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
    $taskModulePath = 'Packages/com.maxsrp.render-pipelines.maxsrp/' + $taskModule[1] + '.asset'
    $taskModuleAsset = Join-Path $taskRoot $taskModulePath
    $taskModuleSource = Join-Path $taskRuntime ($taskModule[2] + '.cs')
    $taskModuleGuid = Get-TaskGuid $taskModulePath
    $taskModuleScriptGuid = Get-TaskGuid ('Packages/com.maxsrp.render-pipelines.maxsrp/Runtime/' + $taskModule[2] + '.cs')
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
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_iblCS', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/PrecomputeIBL.compute', 7200000),
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_aerialPerspectiveCompute', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/AtmosphereScattering/AerialPerspectiveLUT.compute', 7200000),
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_proceduralSkyboxShader', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/AtmosphereScattering/SkyboxProcedural.shader', 4800000),
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_transmittanceShader', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/AtmosphereScattering/TransmittanceLUT.shader', 4800000),
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_multiscatteringShader', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/AtmosphereScattering/MultiscatteringLUT.shader', 4800000),
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_skyviewShader', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/AtmosphereScattering/SkyviewLUT.shader', 4800000),
    @('DefaultAtmosphere', 'MaxAtmosphereConfig', 'm_aerialPerspectiveLUTShader', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/AtmosphereScattering/AerialPerspectiveLUT.shader', 4800000),
    @('DefaultTemporalAA', 'MaxTemporalAAConfig', 'm_temporalShader', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/TemporalAA.shader', 4800000),
    @('DefaultCloud', 'MaxCloudConfig', 'm_layerLightingShader', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/CloudLayerLighting.shader', 4800000),
    @('DefaultCloud', 'MaxCloudConfig', 'm_sixWayShader', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/SixWayCloud.shader', 4800000),
    @('DefaultShadow', 'MaxShadowConfig', 'm_tileClearShader', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/ShadowTileClear.shader', 4800000),
    @('DefaultClusteredLighting', 'MaxClusteredLightingConfig', 'm_compute', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/ClusteredLighting.compute', 7200000),
    @('DefaultAO', 'MaxAOConfig', 'm_compute', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/GTAO/MaxGTAO.compute', 7200000),
    @('DefaultAO', 'MaxAOConfig', 'm_ssaoShader', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/SSAO/MaxSSAO.shader', 4800000),
    @('DefaultPostProcess', 'MaxPostProcessConfig', 'm_exposureCompute', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/PostProcessExposure.compute', 7200000),
    @('DefaultPostProcess', 'MaxPostProcessConfig', 'm_bloomShader', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/PostProcessBloom.shader', 4800000),
    @('DefaultPostProcess', 'MaxPostProcessConfig', 'm_outputShader', 'Packages/com.maxsrp.render-pipelines.maxsrp/Shaders/Realtime/ColorOutput.shader', 4800000)
)
foreach ($taskDependency in $taskDependencies) {
    $taskDependencyGuid = Get-TaskGuid $taskDependency[3]
    $taskDependencyReference = $taskDependency[2] + ': {fileID: ' + $taskDependency[4] + ', guid: ' + $taskDependencyGuid + ', type: 3}'
    Assert-TaskLine (Join-Path $taskRoot ('Packages/com.maxsrp.render-pipelines.maxsrp/' + $taskDependency[0] + '.asset')) ('  ' + $taskDependencyReference)
    Assert-TaskLine (Join-Path $taskRuntime ($taskDependency[1] + '.cs.meta')) ('  - ' + $taskDependencyReference)
}
$taskAOGuid = Get-TaskGuid 'Packages/com.maxsrp.render-pipelines.maxsrp/DefaultAO.asset'
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
function Invoke-TaskCompilation([string]$assemblyName, [string[]]$sources, [string[]]$references, [bool]$editorContext) {
    $arguments = @('/nologo', '/target:library', '/nostdlib+', '/langversion:9.0', '/warn:4', '/nowarn:0649')
    $output = Join-Path $taskContextDirectory ($assemblyName + '.dll')
    $arguments += '/out:"' + $output + '"'
    if ($editorContext) { $arguments += '/define:UNITY_EDITOR' }
    $arguments += $references | Select-Object -Unique | ForEach-Object { '/reference:"' + $_ + '"' }
    $arguments += $sources | ForEach-Object { '"' + $_ + '"' }
    $response = Join-Path $taskContextDirectory ($assemblyName + '.rsp')
    [IO.File]::WriteAllLines($response, $arguments)
    & dotnet $taskCompiler "@$response" | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "$taskContext / $assemblyName Unity API compilation failed." }
    return $output
}
function Get-TaskSources([string]$directory) {
    return @(Get-ChildItem (Join-Path $taskRoot $directory) -Filter '*.cs' -Recurse | ForEach-Object FullName)
}
$taskPipelinePackage = 'Packages/com.maxsrp.render-pipelines.maxsrp'
$taskTerrainPackage = 'Packages/com.maxsrp.terrain'
$taskTerrainManifest = Get-Content (Join-Path $taskRoot "$taskTerrainPackage/package.json") -Raw | ConvertFrom-Json
if (-not $taskTerrainManifest.dependencies.'com.maxsrp.render-pipelines.maxsrp') {
    throw 'MaxTerrain package must depend on the MaxSRP package.'
}
$taskPipelineManifest = Get-Content (Join-Path $taskRoot "$taskPipelinePackage/package.json") -Raw | ConvertFrom-Json
if ($taskPipelineManifest.dependencies.'com.maxsrp.terrain') { throw 'MaxSRP must not depend on MaxTerrain.' }
$taskRuntimeDefinition = Get-Content (Join-Path $taskRuntime 'MaxSRP.Runtime.asmdef') -Raw | ConvertFrom-Json
if ($taskRuntimeDefinition.references -contains 'MaxTerrain') { throw 'MaxSRP.Runtime must not depend on MaxTerrain.' }
$taskTerrainDefinition = Get-Content (Join-Path $taskRoot "$taskTerrainPackage/Runtime/MaxTerrain.asmdef") -Raw | ConvertFrom-Json
if ($taskTerrainDefinition.references -notcontains 'MaxSRP.Runtime') { throw 'MaxTerrain must reference MaxSRP.Runtime.' }
foreach ($taskContext in @('Player', 'Editor')) {
    $taskContextDirectory = Join-Path $taskTemporary $taskContext
    [void][IO.Directory]::CreateDirectory($taskContextDirectory)
    $taskEditorContext = $taskContext -eq 'Editor'
    $taskContextReferences = $taskReferences + $taskEngineReferences
    if ($taskEditorContext) {
        $taskContextReferences += @(Get-ChildItem (Join-Path $UnityEditorData 'Managed/UnityEngine') -Filter 'UnityEditor*.dll' | ForEach-Object FullName)
    }
    # Compile each assembly separately so accessibility and dependency direction
    # are checked as Unity will see them, rather than combining all source files.
    $taskCoreAssembly = Invoke-TaskCompilation 'Unity.RenderPipelines.Core.Runtime' @($taskCoreDeclarations) $taskContextReferences $taskEditorContext
    $taskRuntimeAssembly = Invoke-TaskCompilation 'MaxSRP.Runtime' $taskSources ($taskContextReferences + $taskCoreAssembly) $taskEditorContext
    $taskTerrainAssembly = Invoke-TaskCompilation 'MaxTerrain' (Get-TaskSources "$taskTerrainPackage/Runtime") ($taskContextReferences + $taskRuntimeAssembly) $taskEditorContext
    if ($taskEditorContext) {
        $taskPipelineEditorAssembly = Invoke-TaskCompilation 'MaxSRP.Editor' (Get-TaskSources "$taskPipelinePackage/Editor") ($taskContextReferences + $taskRuntimeAssembly) $true
        $taskTerrainEditorAssembly = Invoke-TaskCompilation 'MaxTerrain.Editor' (Get-TaskSources "$taskTerrainPackage/Editor") ($taskContextReferences + $taskRuntimeAssembly + $taskTerrainAssembly) $true
        # Project-specific Bistro setup, validation and MCP bootstrap are consumers
        # of the packages and stay in the predefined project Editor assembly.
        # The optional MCP bootstrap binds to its separately installed tool package;
        # it does not consume the render packages and is checked by Unity import.
        $taskProjectSources = @(Get-TaskSources 'Assets/Editor' | Where-Object { [IO.Path]::GetFileName($_) -ne 'MaxUnityMcpBootstrap.cs' })
        $taskProjectAssembly = Invoke-TaskCompilation 'Assembly-CSharp-Editor' $taskProjectSources ($taskContextReferences + $taskCoreAssembly + $taskRuntimeAssembly + $taskPipelineEditorAssembly + $taskTerrainAssembly + $taskTerrainEditorAssembly) $true
    }
}
Write-Output "PASS: $($taskAssets.Count) pipeline asset(s), seven PC/Mobile configurations and $($taskDependencies.Count) module shader dependencies retained; independent MaxSRP/MaxTerrain Player and Editor assemblies compiled against Unity APIs."
Write-Output "Compilation artifacts: $taskTemporary (core helper declarations only; not a Unity import, Player build or GPU test)."
