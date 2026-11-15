param(
    [string]$UnityPluginApi = 'C:/Program Files/Unity/Hub/Editor/2022.3.62f2c1/Editor/Data/PluginAPI'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $projectRoot 'Packages/com.maxsrp.terrain/Plugins/DirectDeltaBc3D3D11.dll'
$source = Join-Path $PSScriptRoot 'DirectDeltaBc3D3D11.cpp'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) { throw 'Visual Studio x64 C++ tools and a Windows SDK are required.' }
$vcvars = Join-Path $visualStudio 'VC/Auxiliary/Build/vcvars64.bat'
if (!(Test-Path -LiteralPath (Join-Path $UnityPluginApi 'IUnityGraphicsD3D11.h'))) {
    throw 'Pass the Unity Editor/Data/PluginAPI directory as -UnityPluginApi.'
}

$buildDirectory = Join-Path ([IO.Path]::GetTempPath()) ('DirectDeltaBc3D3D11-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildDirectory | Out-Null
$buildFile = Join-Path $buildDirectory 'build.cmd'
$stagedDll = Join-Path $buildDirectory 'DirectDeltaBc3D3D11.dll'
try {
    @"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b 1
cl /nologo /W4 /O2 /MT /EHsc /std:c++17 /utf-8 /LD /I"$UnityPluginApi" /Fo"$buildDirectory\DirectDeltaBc3D3D11.obj" "$source" /link /OUT:"$stagedDll" /IMPLIB:"$buildDirectory\DirectDeltaBc3D3D11.lib" /Brepro
exit /b %errorlevel%
"@ | Set-Content -LiteralPath $buildFile -Encoding ascii
    & $buildFile
    if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }
    Copy-Item -LiteralPath $stagedDll -Destination $output -Force
    Get-FileHash -LiteralPath $output -Algorithm SHA256
}
finally {
    Get-ChildItem -LiteralPath $buildDirectory -File | Remove-Item
    Remove-Item -LiteralPath $buildDirectory
}
