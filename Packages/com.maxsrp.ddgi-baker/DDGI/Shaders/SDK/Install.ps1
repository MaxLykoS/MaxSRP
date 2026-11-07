[CmdletBinding()]
param([switch]$VerifyOnly)

$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Sources.json') -Raw | ConvertFrom-Json
$baseCommit = 'c233266e95931231961f35c842c78e7c4bdc50cf'
$relocationCommit = 'a6c4d9917ac6223be9ee97f304b7d92c20dda970'
$relocationPath = 'rtxgi-sdk/shaders/ddgi/ProbeRelocationCS.hlsl'
$rawRoot = 'https://raw.githubusercontent.com/NVIDIAGameWorks/RTXGI-DDGI'

if ($manifest.commit -cne $baseCommit -or $manifest.relocationCommit -cne $relocationCommit -or
    @($manifest.files).Count -ne 20) {
    throw 'Unexpected RTXGI-DDGI source set in Sources.json.'
}

function Test-SdkFile {
    param([string]$Path, $Entry)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -ne $Entry.bytes) { return $false }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Entry.sha256
}

function Publish-VerifiedFile {
    param([string]$Staged, [string]$Destination, $Entry)

    if (-not (Test-SdkFile $Staged $Entry)) {
        throw "Staged file failed verification: $Destination"
    }
    if (Test-SdkFile $Destination $Entry) { return $false }
    try {
        if ([IO.File]::Exists($Destination)) {
            [IO.File]::Replace($Staged, $Destination, [NullString]::Value)
        } else {
            [IO.File]::Move($Staged, $Destination)
        }
        return $true
    } catch [IO.IOException] {
        if (-not (Test-SdkFile $Destination $Entry)) { throw }
        return $false
    }
}

$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    if ($entry.path -cnotmatch '^(License\.txt|rtxgi-sdk/[a-zA-Z0-9_./]+\.(hlsl|h))$' -or
        ($entry.path -split '/') -contains '..' -or ($entry.path -split '/') -contains '.' -or
        $entry.path.Contains('//') -or $entry.bytes -le 0 -or
        $entry.sha256 -cnotmatch '^[0-9a-f]{64}$' -or -not $paths.Add($entry.path)) {
        throw "Invalid RTXGI-DDGI source entry: $($entry.path)"
    }
}
if (-not $paths.Contains('License.txt') -or -not $paths.Contains($relocationPath)) {
    throw 'Sources.json must include the NVIDIA license and patched relocation shader.'
}

$problems = @()
$installed = 0
foreach ($entry in $manifest.files) {
    $destination = Join-Path $PSScriptRoot $entry.path
    if (Test-SdkFile $destination $entry) { continue }
    if ($VerifyOnly) {
        $problems += $entry.path
        continue
    }

    $commit = if ($entry.path -ceq $relocationPath) { $relocationCommit } else { $baseCommit }
    $directory = Split-Path -Parent $destination
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $staged = Join-Path $directory ('.sdk-download-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                Invoke-WebRequest -UseBasicParsing -Uri "$rawRoot/$commit/$($entry.path)" -OutFile $staged -TimeoutSec 30
                break
            } catch {
                if ($attempt -eq 3) { throw }
                Start-Sleep -Seconds $attempt
            }
        }
        if (Publish-VerifiedFile $staged $destination $entry) { $installed++ }
    } finally {
        if ([IO.File]::Exists($staged)) { [IO.File]::Delete($staged) }
    }
}

$materialized = 0
foreach ($relative in @('../ProbeUpdate.compute', '../../../Baker/Shaders/ProbeTrace.raytrace')) {
    $destination = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $relative))
    $template = "$destination.txt"
    if (-not (Test-Path -LiteralPath $template -PathType Leaf)) { continue }
    $entry = @{
        bytes = (Get-Item -LiteralPath $template).Length
        sha256 = (Get-FileHash -LiteralPath $template -Algorithm SHA256).Hash
    }
    if (Test-SdkFile $destination $entry) { continue }
    if ($VerifyOnly) {
        $problems += $relative
        continue
    }

    $directory = Split-Path -Parent $destination
    $staged = Join-Path $directory ('.sdk-download-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::Copy($template, $staged)
        if (Publish-VerifiedFile $staged $destination $entry) { $materialized++ }
    } finally {
        if ([IO.File]::Exists($staged)) { [IO.File]::Delete($staged) }
    }
}

if ($problems.Count -gt 0) {
    throw ("Missing or invalid RTXGI-DDGI files or shader wrappers. Run Install.ps1 to restore them.`n" + ($problems -join "`n"))
}
Write-Output "Verified 20 pinned RTXGI-DDGI files ($installed downloaded, $materialized shader wrappers generated)."
