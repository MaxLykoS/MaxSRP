$ErrorActionPreference='Stop'
# Compile the actual production BRDF through the existing arithmetic test host.
. (Join-Path $PSScriptRoot '../ValidatePhysicalLighting.ps1')
$formulaOut=Join-Path $PSScriptRoot '../../OfflineBaseline/FormulaComparison'
$points=Get-Content (Join-Path $formulaOut 'cycles_formula_points.json') -Raw | ConvertFrom-Json
$rows=foreach($row in $points.cases){
 $surface=[PhysicalShaderMath.Check+Surface]::new()
 $c=[float]$row.cosine;$s=[float][Math]::Sqrt(1-$c*$c)
 $surface.N=[Numerics.Vector3]::UnitZ
 $surface.V=[Numerics.Vector3]::new($s,0,$c);$surface.L=[Numerics.Vector3]::new(-$s,0,$c)
 $surface.roughness=[float]$row.roughness;$surface.metalness=[float]$row.metalness
 $surface.albedo=[Numerics.Vector3]::new([float]$row.base_color)
 $surface.F0=[Numerics.Vector3]::new([float](.04*(1-$row.metalness)+$row.base_color*$row.metalness))
 $response=[PhysicalShaderMath.Check]::CookTorranceBRDF($surface).X*$c
 [ordered]@{cosine=$row.cosine;roughness=$row.roughness;base_color=$row.base_color;metalness=$row.metalness;
  unity_production_response=$response;cycles_single_response=$row.cycles_GGX;cycles_multi_response=$row.cycles_MULTI_GGX;
  unity_to_cycles_single=$response/$row.cycles_GGX;unity_to_cycles_multi=$response/$row.cycles_MULTI_GGX}
}
$rng=[Random]::new(20261019);$maxVisibility=0.0;$maxDistribution=0.0
for($i=0;$i -lt 10000;$i++){
 $nv=[float](.0001+.9999*$rng.NextDouble());$nl=[float](.0001+.9999*$rng.NextDouble())
 $r=[float](.05+.95*$rng.NextDouble());$nh=[float]$rng.NextDouble();$a2=[Math]::Pow([double]$r,4)
 $lv=.5*([Math]::Sqrt(1+$a2*(1/([double]$nv*$nv)-1))-1)
 $ll=.5*([Math]::Sqrt(1+$a2*(1/([double]$nl*$nl)-1))-1)
 $cyclesV=1/(4*[double]$nv*$nl*(1+$lv+$ll))
 $unityV=[PhysicalShaderMath.Check]::VisibilitySmithGGXCorrelated($nv,$nl,$r)
 $maxVisibility=[Math]::Max($maxVisibility,[Math]::Abs($unityV/$cyclesV-1))
 $cyclesD=$a2/([Math]::PI*[Math]::Pow(1-$nh*$nh+$nh*$nh*$a2,2))
 $h=[Numerics.Vector3]::new([float][Math]::Sqrt(1-$nh*$nh),0,$nh)
 $unityD=[PhysicalShaderMath.Check]::DistributionGGX([Numerics.Vector3]::UnitZ,$h,$r)
 $maxDistribution=[Math]::Max($maxDistribution,[Math]::Abs($unityD/$cyclesD-1))
}
$fresnelRows=foreach($cosine in @(1.0,.5,[Math]::Cos(75*[Math]::PI/180))){
 $eta=1.5;$ct=[Math]::Sqrt(1-(1-$cosine*$cosine)/($eta*$eta))
 $rs=($cosine-$eta*$ct)/($cosine+$eta*$ct)
 $rp=($eta*$cosine-$ct)/($eta*$cosine+$ct)
 $exact=.5*($rs*$rs+$rp*$rp);$schlick=.04+.96*[Math]::Pow(1-$cosine,5)
 [ordered]@{cosine=$cosine;dielectric_exact=$exact;schlick=$schlick;schlick_to_exact=$schlick/$exact}
}
$metalError=($rows|Where-Object metalness -eq 1|ForEach-Object {[Math]::Abs($_.unity_to_cycles_single-1)}|Measure-Object -Maximum).Maximum
$mixError=0.0
foreach($row in ($rows|Where-Object metalness -eq .5)){
 $ends=@($rows|Where-Object {$_.cosine -eq $row.cosine -and $_.roughness -eq $row.roughness -and $_.base_color -eq $row.base_color -and $_.metalness -ne .5})
 $expected=.5*($ends[0].cycles_single_response+$ends[1].cycles_single_response)
 $mixError=[Math]::Max($mixError,[Math]::Abs($row.cycles_single_response/$expected-1))
}
if($maxVisibility -gt 5e-7 -or $maxDistribution -gt 5e-5){throw 'Production GGX differs from the independent Cycles equations.'}
if($metalError -gt 1e-5 -or $mixError -gt 1e-5){throw 'Cycles flat-card endpoint/mixing checks failed.'}
[ordered]@{scope='D/G: production HLSL extracted to C# float arithmetic versus independent Cycles equations in double. Material: identical-input untextured Cycles flat-card renders versus production BRDF*NdotL.';
 build_hash=$points.build_hash;random_samples=10000;max_visibility_relative_error=$maxVisibility;max_distribution_relative_error=$maxDistribution;
 production_brdf_sha256=(Get-FileHash (Join-Path $PSScriptRoot '../../Assets/MaxRendererPipeline/ShaderLibrary/Realtime/BRDF.hlsl') -Algorithm SHA256).Hash;
 max_pure_metal_single_relative_error=$metalError;max_cycles_half_metal_linear_mix_relative_error=$mixError;dielectric_fresnel=$fresnelRows;
 cases=$rows} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $formulaOut 'formula_comparison.json') -Encoding utf8
Write-Output "FORMULA_COMPARISON: 10000 parameter sets; max visibility relative error $maxVisibility; max distribution relative error $maxDistribution; $($rows.Count) flat-card cases."
