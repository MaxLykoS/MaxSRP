param([string]$ArtifactDirectory = (Join-Path $PSScriptRoot '../../OfflineBaseline/SkyboxScene'), [int]$Width=1024, [int]$Height=512)
$ErrorActionPreference='Stop'
$baselineRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$baselineOut=[IO.Path]::GetFullPath($ArtifactDirectory)
$baselineManifest=Get-Content (Join-Path $baselineOut 'manifest.json') -Raw | ConvertFrom-Json
# Reuse the production-HLSL extraction/compiler in the existing numerical validator.
# Compile its helper without executing tests or changing the validator's defaults.
$baselineSource=[IO.File]::ReadAllText((Join-Path $baselineRoot 'Tools/ValidateDaylightEnergy.ps1'))
$baselineDefinition=$baselineSource.Substring(0,$baselineSource.IndexOf('Add-Type -TypeDefinition'))
$baselineDefinition=$baselineDefinition.Replace('$taskRoot=Split-Path -Parent $PSScriptRoot',('$taskRoot='''+$baselineRoot.Replace("'","''")+''''))
Invoke-Expression $baselineDefinition
$baselineExporter=@'
 public static string ExportBaseline(float[] p, float[] q, float[] sunRGB, int width, int height, string path) {
  atmosphere=new AtmosphereParameter {SeaLevel=p[0],PlanetRadius=p[1],AtmosphereHeight=p[2],SunDiskAngle=p[3],
   RayleighScatteringScale=p[4],RayleighScatteringScalarHeight=p[5],MieScatteringScale=p[6],MieScatteringScalarHeight=p[7],
   MieAnisotropy=p[8],OzoneAbsorptionScale=p[9],OzoneLevelCenterHeight=p[10],OzoneLevelWidth=p[11],
   SolarIrradiance=new Vector3(sunRGB[0],sunRGB[1],sunRGB[2])};
  var rotation=Quaternion.Normalize(new Quaternion(q[0],q[1],q[2],q[3]));
  Vector3 sun=-Vector3.Transform(Vector3.UnitZ,rotation);
  Bake();
  Vector3 eye=AtmosphereCameraPosition(2+atmosphere.SeaLevel,atmosphere.SeaLevel,atmosphere.PlanetRadius);
  using(var writer=new System.IO.BinaryWriter(System.IO.File.Create(path))) {
   for(int y=0;y<height;y++) for(int x=0;x<width;x++) {
    // Blender equirectangular environment mapping. Buffer rows start at the south pole.
    double lat=((y+.5)/height-.5)*Math.PI,phi=(.5-(x+.5)/width)*2*Math.PI;
    var dir=new Vector3((float)(Math.Cos(lat)*Math.Cos(phi)),(float)Math.Sin(lat),(float)(Math.Cos(lat)*Math.Sin(phi)));
    Vector3 color=GetSkyView(atmosphere,eye,dir,sun,-1);
    if(!float.IsFinite(color.X+color.Y+color.Z))throw new Exception("Invalid source sky radiance");
    writer.Write(Math.Max(0,color.X));writer.Write(Math.Max(0,color.Y));writer.Write(Math.Max(0,color.Z));writer.Write(1f);
   }
  }
  var transmission=SurfaceSunTransmittance(new Vector3(0,atmosphere.SeaLevel+2,0),sun);
  var e=atmosphere.SolarIrradiance*transmission;
  return System.Text.Json.JsonSerializer.Serialize(new {width,height,buffer="sky_linear_rgba.f32",hdr_scale=100000,
   sun_direction_unity=new[]{sun.X,sun.Y,sun.Z},sun_transmission=new[]{transmission.X,transmission.Y,transmission.Z},
   sun_ground_rgb=new[]{e.X,e.Y,e.Z},sun_angular_radius_degrees=atmosphere.SunDiskAngle,
   reference_height=2,sky_includes_sun_disk=false,sky_is_independent_atmosphere_reference=false});
 }
'@
$taskHost=$taskHost.Replace(' public static string[] Run(){',($baselineExporter+"`n public static string[] Run(){"))
Add-Type -TypeDefinition $taskHost.Replace('__FUNCTIONS__',($taskConverted -join "`n"))
$baselineAtmosphere=$baselineManifest.atmosphere
$baselineValues=[float[]]@($baselineAtmosphere._SeaLevel,$baselineAtmosphere._PlanetRadius,$baselineAtmosphere._AtmosphereHeight,
 $baselineAtmosphere._SunDiskAngle,$baselineAtmosphere._RayleighScatteringScale,$baselineAtmosphere._RayleighScatteringScalarHeight,
 $baselineAtmosphere._MieScatteringScale,$baselineAtmosphere._MieScatteringScalarHeight,$baselineAtmosphere._MieAnisotropy,
 $baselineAtmosphere._OzoneAbsorptionScale,$baselineAtmosphere._OzoneLevelCenterHeight,$baselineAtmosphere._OzoneLevelWidth)
$baselineSun=$baselineManifest.lights | Where-Object type -eq 1 | Select-Object -First 1
if ($baselineSun.use_temperature) { throw 'Color-temperature conversion must be explicitly implemented before exporting this scene.' }
$baselineQ=$baselineSun.transform.m_LocalRotation
$baselineRGB=[float[]]@('r','g','b' | ForEach-Object {
 $v=[double]$baselineSun.color.$_; if($v -le .04045){$v/=12.92}else{$v=[Math]::Pow(($v+.055)/1.055,2.4)}
 $v*$baselineSun.intensity/100000.0
})
$baselineInfo=[DaylightEnergyChecks]::ExportBaseline($baselineValues,[float[]]@($baselineQ.x,$baselineQ.y,$baselineQ.z,$baselineQ.w),
 $baselineRGB,$Width,$Height,(Join-Path $baselineOut 'sky_linear_rgba.f32'))
[IO.File]::WriteAllText((Join-Path $baselineOut 'sky_info.json'),$baselineInfo,[Text.UTF8Encoding]::new($false))
Write-Output $baselineInfo
