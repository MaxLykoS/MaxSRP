# MaxSRP DDGI TOD Baker

The Unity 2022.3 Editor bakes solar bounce lighting with D3D12 hardware ray tracing. The baker builds a manual acceleration structure, traces probe rays and sun visibility with `DispatchRays`, then runs the pinned NVIDIA RTXGI-DDGI compute kernels. The package writes `NGATLAS1` and `report.json` for the MaxSRP solar probe importer.

## Assemblies

- **DDGI** is the SDK baseline. It contains the pinned shader manifest and the Unity host translation for volume resources, packed constants, blending, relocation and classification. Update this layer only for a deliberate SDK baseline change and rerun the NativeGI comparison. [Unity-to-SDK mapping](DDGI/SDK-Mapping.md) · [SDK provenance and license](DDGI/Shaders/SDK/UPSTREAM.md)
- **Baker** references **DDGI** and owns the Editor workflow, scene geometry and DXR hit shaders, TOD scheduling, readback, export and parity checks. Place changes to bake policy and tooling here.

## Bake and import

1. Run `Packages/com.maxsrp.ddgi-baker/DDGI/Shaders/SDK/Install.ps1` from PowerShell to install the pinned NVIDIA files locally and materialize the shader entry points. Rerun it after changing a shader template.
2. Open the project with `-force-d3d12` on a DXR-capable GPU.
3. Open **MaxSRP → GI → DDGI TOD Baker**. Leave NGMESH empty to collect enabled static `MeshRenderer` geometry in the active scene, or select a NativeGI `.ngmesh` file.
4. Fit or edit the probe grid, then choose one or more increasing solar-hour keys from 9 to 15. Select **Bake TOD keys**. The output directory must be new.
5. In MaxSRP, use **MaxSRP → Lighting → Import Baked Solar TOD** and select the generated `report.json` to create a probe volume asset. In projects with the MaxSRP solar receiver, assign the imported volume and the sun to a **Baked Solar TOD** component.

The bake matches NativeGI's opaque gray Lambert model with diffuse reflectance 0.7, unit white sun, and fixed solar trajectory. It stores one irradiance atlas per key plus shared distance moments and probe state. The current scene geometry path includes triangle submeshes and skips materials with `_Cutoff > 1`. Material textures and per-pixel cutouts are outside this bake model.

## Reproduce the TinyBistro comparison

The reference step uses the NativeGI lab executable and local TinyBistro geometry. An existing NativeGI report and its atlases can also be used with the Unity batch entry. Run the commands from the MaxSRP project root and use new output directories for subsequent runs.

```powershell
& .\OfflineBaseline\NativeGI\bin\NativeGI.exe --headless `
  --scene Assets/ThirdParty/Bistro/NativeGI/TinyBistro.ngmesh `
  --output OfflineBaseline/DDGIBaker/NativeTinyNoon `
  --spacing 1.5 --max-probes 4096 --rays 512 --iterations 64 `
  --keys 1 --time 12 --geometry-passes 4 --width 96 --height 64 --spp 16
```

Launch Unity with the following arguments. The project must be closed before opening it in batch mode. The validation also supports a separate Unity project that references this package and supplies `-ddgiProjectRoot` to resolve the reference's relative mesh path.

```text
-batchmode -quit -force-d3d12 -projectPath D:/UnityProject/MaxSRP
-executeMethod MaxSRP.DDGIBaker.Editor.DdgiBakerBatch.CompareNativeGi
-ddgiReference D:/UnityProject/MaxSRP/OfflineBaseline/DDGIBaker/NativeTinyNoon
-ddgiOutput D:/UnityProject/MaxSRP/OfflineBaseline/DDGIBaker/UnityTinyNoon
-ddgiKey 0 -ddgiGeometryPasses 4
```

`comparison.json` includes encoded atlas errors, distance moments, probe offsets and states, and decoded linear irradiance sampled in world space. The batch entry exits with an error if the reference samples are all dark, the probe classification changes, or the relative linear irradiance RMSE exceeds 1%.
