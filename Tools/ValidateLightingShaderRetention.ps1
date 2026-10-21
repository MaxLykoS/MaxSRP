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
Write-Output "PASS: $($taskAssets.Count) pipeline asset(s) and new-asset defaults reference the Lighting Shader; Player/Editor Unity API compilation passed."
Write-Output "Compilation artifacts: $taskTemporary (not a Player build)."
