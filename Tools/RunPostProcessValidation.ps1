param(
    [string]$UnityEditor = 'C:/Program Files/Unity/Hub/Editor/2022.3.62f2c1/Editor/Unity.exe',
    [string]$UnityMcpPython = 'C:/Users/pc/.codex/unity-mcp/Server/.venv/Scripts/python.exe'
)
$ErrorActionPreference = 'Stop'
$postRoot = Split-Path -Parent $PSScriptRoot
$postReportDirectory = Join-Path $postRoot 'OfflineBaseline/PostProcessing'
[void][IO.Directory]::CreateDirectory($postReportDirectory)
$postStarted = [DateTime]::UtcNow
$postOpen = @(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" | Where-Object {
    $_.CommandLine -notmatch 'AssetImportWorker' -and
    $_.CommandLine -match [regex]::Escape($postRoot)
})
if ($postOpen.Count -gt 0) {
    if (-not (Test-Path -LiteralPath $UnityMcpPython)) { throw 'Project is open: run MaxSRP/Validation/Post Processing (GPU) in Unity, or provide UnityMcpPython.' }
    function Invoke-PostUnityTool([string]$tool, [string]$arguments) {
        # The MCP server writes its startup banner to stderr even on success.
        $postOldPreference = $ErrorActionPreference
        $postArgumentsFile = Join-Path $postReportDirectory 'mcp-arguments.json'
        [IO.File]::WriteAllText($postArgumentsFile, $arguments, [Text.UTF8Encoding]::new($false))
        try {
            $ErrorActionPreference = 'Continue'
            $postReply = & $UnityMcpPython (Join-Path $PSScriptRoot 'UnityMcp/client.py') call $tool --arguments-file $postArgumentsFile 2> (Join-Path $postReportDirectory 'mcp-stderr.log')
        } finally { $ErrorActionPreference = $postOldPreference }
        $postResult = ($postReply -join "`n") | ConvertFrom-Json
        if (-not $postResult) { throw 'Unity MCP returned no response. See mcp-stderr.log.' }
        if ($postResult.isError) { throw ($postReply -join "`n") }
        $postPayload = $postResult.structuredContent
        if ($postPayload.result) { $postPayload = $postPayload.result }
        if ($postPayload.success -eq $false) { throw $postPayload.message }
    }
    Invoke-PostUnityTool 'refresh_unity' '{"mode":"force","scope":"all","compile":"request","wait_for_ready":true}'
    Invoke-PostUnityTool 'execute_code' '{"action":"execute","code":"MaxSRP.MaxPostProcessValidation.Run(); return \"Post-process GPU validation completed.\";"}'
} else {
    if (-not (Test-Path -LiteralPath $UnityEditor)) { throw "Unity Editor not found: $UnityEditor" }
    $postLog = Join-Path $postReportDirectory 'unity.log'
    $postArguments = @('-batchmode', '-force-d3d11', '-projectPath', ('"' + $postRoot + '"'),
        '-executeMethod', 'MaxSRP.MaxPostProcessValidation.RunBatch', '-logFile', ('"' + $postLog + '"'))
    $postProcess = Start-Process -FilePath $UnityEditor -ArgumentList $postArguments -WindowStyle Hidden -PassThru
    while (-not $postProcess.WaitForExit(1000)) { }
    if ($postProcess.ExitCode -ne 0) { throw "Unity validation failed. See $postLog" }
}
$postReportPath = Join-Path $postReportDirectory 'validation.json'
if (-not (Test-Path -LiteralPath $postReportPath)) { throw 'GPU validation did not write its report.' }
if ((Get-Item -LiteralPath $postReportPath).LastWriteTimeUtc -lt $postStarted) { throw 'GPU validation report is stale.' }
$postReport = Get-Content -LiteralPath $postReportPath -Raw | ConvertFrom-Json
if (-not $postReport.success) { throw "GPU validation failed. See $postReportPath" }
Write-Output "PASS: post-process GPU validation. Report: $postReportPath"
