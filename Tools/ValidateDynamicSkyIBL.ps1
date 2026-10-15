$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskHost=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'DynamicSkyIBLTestHost.cs'))
$taskSources=@('Runtime/MaxPhysicalLight.cs','Runtime/SunLightData.cs','Runtime/MaxLightConfigurator.cs','Runtime/Passes/MaxUpdateGIPass.cs') | ForEach-Object {
    $s=[IO.File]::ReadAllText((Join-Path $taskRoot ('Assets/MaxRendererPipeline/'+$_)))
    $s=[regex]::Replace($s,'(?m)^using [^;]+;\r?\n','')
    $s.Replace('namespace MaxSRP','namespace MaxSRP')
}
Add-Type -TypeDefinition ($taskHost+"`n"+($taskSources -join "`n"))
[MaxSRP.SingleSunChecks]::Run()
[MaxSRP.DynamicIBLChecks]::Run()
$taskPipeline=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/MaxRendererPipeline.cs'))
if($taskPipeline.Contains('BakeAndSubmit(') -or $taskPipeline.Contains('setting.ENVMap')){throw 'Static indoor IBL initialization remains'}
if($taskPipeline.IndexOf('m_iblGIPass.Execute(') -gt $taskPipeline.IndexOf('m_LightPass.Execute(')){throw 'Lighting reads stale IBL'}
$taskRuntime=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/Passes/MaxUpdateGIPass.cs'))
foreach($taskForbidden in @('.GetData(','.SetPixels(','.Apply(')) {if($taskRuntime.Contains($taskForbidden)){throw 'CPU readback/upload in dynamic IBL'}}
Write-Output 'PASS: dynamic IBL precedes Lighting, no static environment bake or CPU readback/upload in the runtime IBL path.'
$taskLighting=[IO.File]::ReadAllText((Join-Path $taskRoot 'Assets/MaxRendererPipeline/Runtime/Passes/MaxLightPass.cs'))
if(-not $taskLighting.Contains('ibl.SetLightingBindings(m_iblBindings)') -or
   -not $taskLighting.Contains('MeshTopology.Triangles, 3, 1, m_iblBindings')){throw 'Lighting draw bypasses its owned IBL bindings'}
$taskSync=$taskLighting.IndexOf('ShaderUtil.SetAsyncCompilation(cmd, false)')
$taskDraw=$taskLighting.IndexOf('cmd.DrawProcedural(')
$taskRestore=$taskLighting.IndexOf('ShaderUtil.RestoreAsyncCompilation(cmd)')
if($taskSync -lt 0 -or $taskSync -gt $taskDraw -or $taskRestore -lt $taskDraw){throw 'Lighting may draw with an asynchronous placeholder'}
Write-Output 'PASS: Lighting captures owned IBL inputs per draw; synchronous Editor compilation brackets the draw.'
