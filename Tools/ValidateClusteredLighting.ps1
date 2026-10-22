param(
 [switch]$RunUnity,
 [string]$UnityMcpPython=(Join-Path $env:USERPROFILE '.codex/unity-mcp/Server/.venv/Scripts/python.exe')
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskFixture=Join-Path $PSScriptRoot 'UnityMcp/ValidateClusteredLighting.cs.txt'
$taskClient=Join-Path $PSScriptRoot 'UnityMcp/client.py'
$taskReport=Join-Path $taskRoot 'OfflineBaseline/ClusteredLighting/gpu_validation.json'
if(-not $RunUnity){
 Write-Output 'GPU fixture ready: Tools/UnityMcp/ValidateClusteredLighting.cs.txt'
 Write-Output 'After the Editor finishes compilation, run Tools/ValidateClusteredLighting.ps1 -RunUnity.'
 Write-Output 'This executes production compute kernels, checks camera/light fixtures, and writes OfflineBaseline/ClusteredLighting/gpu_validation.json without changing the scene.'
 return
}
if(-not(Test-Path -LiteralPath $UnityMcpPython)){throw "Unity MCP Python not found: $UnityMcpPython"}
$taskStarted=[DateTime]::UtcNow
$taskCode=[IO.File]::ReadAllText($taskFixture)
$taskArguments=@{action='execute';code=$taskCode} | ConvertTo-Json -Compress
Push-Location -LiteralPath $taskRoot
try {
 $taskResult=& $UnityMcpPython $taskClient call execute_code $taskArguments
 if($LASTEXITCODE -ne 0){Write-Output $taskResult;throw 'Clustered GPU fixture failed; see MCP response and gpu_validation.json.'}
} finally {Pop-Location}
if(-not(Test-Path -LiteralPath $taskReport)){Write-Output $taskResult;throw 'GPU fixture returned without writing a report.'}
$taskValidation=[IO.File]::ReadAllText($taskReport) | ConvertFrom-Json
if(-not $taskValidation.passed){throw ('GPU fixture failed: '+$taskValidation.failure)}
if([DateTime]::Parse($taskValidation.utc).ToUniversalTime() -lt $taskStarted.AddSeconds(-2)){throw 'GPU report is stale.'}
if($taskValidation.cases.Count -lt 8 -or $taskValidation.falseNegatives -ne 0 -or $taskValidation.insideLightPairs -le 0){throw 'GPU report is incomplete.'}
Write-Output ('PASS: production ClusteredLighting.compute on '+$taskValidation.graphicsAPI+'; '+$taskValidation.cases.Count+' fixtures, '+$taskValidation.checkedPoints+' points, '+$taskValidation.insideLightPairs+' receiving point/light pairs, zero false negatives.')
Write-Output ('PASS: indices beyond 31='+$taskValidation.referencesAbove31+', overflowing cells='+$taskValidation.overflowCells+'. Complete report: '+$taskReport)
