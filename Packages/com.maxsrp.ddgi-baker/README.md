# MaxSRP DDGI TOD Baker

The Unity 2022.3 Editor bakes solar bounce lighting with D3D12 hardware ray tracing. The baker builds a manual acceleration structure, traces probe rays and sun visibility with `DispatchRays`, then runs the pinned NVIDIA RTXGI-DDGI compute kernels. The package writes dense `NGATLAS1` atlases or a compact bake manifest with packed texture payloads.

## Assemblies

- **DDGI** is the SDK baseline. It contains the pinned shader manifest and the Unity host translation for volume resources, packed constants, blending, relocation and classification. Update this layer only for a deliberate SDK baseline change and rerun the NativeGI comparison. [Unity-to-SDK mapping](DDGI/SDK-Mapping.md) · [SDK provenance and license](DDGI/Shaders/SDK/UPSTREAM.md)
- **Baker** references **DDGI** and owns the Editor workflow, scene geometry and DXR hit shaders, TOD scheduling, readback, export and parity checks. Place changes to bake policy and tooling here.

## Bake and import

1. Run `Packages/com.maxsrp.ddgi-baker/DDGI/Shaders/SDK/Install.ps1` from PowerShell to install the pinned NVIDIA files locally and materialize the shader entry points. Rerun it after changing a shader template.
2. Open the project with `-force-d3d12` on a DXR-capable GPU.
3. Open **MaxSRP → GI → DDGI TOD Baker** with the target scene active and NGMESH empty. **Scene Materials** is the default. Select **Gray Native Parity** explicitly for a NativeGI `.ngmesh` file.
4. Fit or edit the probe grid, then choose increasing solar-hour keys from 6 to 18. The default spacing is **2 m**. Select **Fit compact 2 m volume (96 rays, 160 iterations)** for the full-scene preset; it also restores the half-day schedule, including sunrise and sunset keys. Select **Bake TOD keys** and use a new output directory.
5. Use **MaxSRP → Lighting → Import Baked Solar TOD** with a dense `report.json`, or **MaxSRP → Lighting → Import Compact Solar TOD** with a compact `manifest.json`. Assign the imported volume and the sun to a **Baked Solar TOD** component.
6. After baking, reopen the Editor with `-force-d3d11` (the project's default API) for normal playback and performance comparisons. Hardware ray tracing and the DX12 session are only required by the baker; the imported runtime data does not require them.

Scene Materials uses the source UV0, base-color texture and transform, metalness, roughness, and Bistro alpha cutoff. The DXR hit shader samples texture mip 0 and uses the mesh normal for diffuse transport. Supported shaders are `MaxSRP/Bistro GBuffer` and `Unlit/PBRGBuffer` in a Linear project. Gray Native Parity uses the original opaque 0.7 Lambert reflectance.

Both modes use a unit white sun and the same solar trajectory. The irradiance atlases retain RGB for each key and share geometry distance moments and probe layout. A compact bake uses one volume, half-float lighting atlases and a packed active-probe export. Its manifest records the grid, material mode, key times and logical texture sizes.

Compact tracing submits batches of eight atlas slices against the same global feedback field. All rays finish before the unchanged SDK blending kernels update that field. Synchronization uses a graphics fence when supported, otherwise a persistent 1×1 marker; it does not read a full probe atlas merely to wait for the GPU.

## Runtime streaming

Compact import creates a streaming volume: shared distance and probe-layout arrays are assets, while RGB irradiance keys are raw files under `Assets/StreamingAssets/MaxSRP/SolarTOD/`. These generated files are ignored by Git. The volume has no direct references to all key textures. **Assets → MaxSRP → Create Streamed Solar TOD Volume** also converts an existing resident volume without replacing its original assets.

The Windows Editor/Player path loads the adjacent time keys asynchronously and uploads at most approximately 4 MiB per update. At rest, at most two keys are resident; near a moving time boundary it can prefetch a third. A cold start uses the first available endpoint, and a time jump retains an available endpoint until the required pair arrives. Publication uses the existing TAA history revision. Disabling/unloading the component releases the cache; resource loss or a changed stream description invalidates and reloads it. Shared arrays have no retained CPU pixel copy after compact import.

Irradiance remains linear RGBAHalf (RGB content with unused alpha), with the original octahedral direction resolution and RGHalf distance resolution. No single-channel reduction, ambient-cube conversion, sparse-probe merging or additional sky-visibility bake is part of this path.

## Shared day/night orbit

The **Baked Solar TOD** component uses a 0–24 clock. Daytime 06:00–18:00 samples the baked solar-hour keys. Nighttime 18:00–06:00 repeats that trajectory twelve hours later: midnight and noon use the same probe data and direction. `Moon Illuminance Lux` and `Moon Color` control the nighttime source; daytime keeps the referenced sun's settings. Keep that directional light enabled so its shadow settings and identity can be shared.

The runtime supplies the resulting direction and RGB intensity to direct lighting, shadows, atmosphere, clouds, sky IBL and probe GI. Point lights and their baked lighting retain their existing settings. Moonlight scales the normalized RGB bake at runtime, so no second nighttime atlas is required.

The existing latitude 39.9°, declination −20° solar trajectory is retained. The default half-day schedule has 11 keys: 06:00, sunrise (about 07:11), 07:30, 09:00, 10:30, 12:00, 13:30, 15:00, 16:30, sunset (about 16:49), 18:00. The baker and runtime disable solar/moon bounce below the horizon. The horizon keys keep temporal interpolation from introducing light before sunrise or after sunset.

## Reproduce a NativeGI comparison

The reference step uses the local NativeGI lab executable and an existing Bistro mesh export. This example bakes a small volume around the street entrance while tracing the full scene. An existing compatible NativeGI report and its atlases can also be used with the Unity batch entry.

```powershell
& .\OfflineBaseline\NativeGI\bin\NativeGI.exe --headless `
  --scene Assets/ThirdParty/Bistro/NativeGI/FullBistro.ngmesh `
  --output OfflineBaseline/DDGIBaker/NativeBistroNoon `
  --volume-center 8,6,-24 --volume-size 15,10,15 `
  --spacing 2 --max-probes 4096 --rays 512 --iterations 64 `
  --keys 1 --time 12 --geometry-passes 4 --width 96 --height 64 --spp 16
```

Launch Unity with the following arguments. The project must be closed before opening it in batch mode. The validation also supports a separate Unity project that references this package and supplies `-ddgiProjectRoot` to resolve the reference's relative mesh path.

```text
-batchmode -quit -force-d3d12 -projectPath D:/UnityProject/MaxSRP
-executeMethod MaxSRP.DDGIBaker.Editor.DdgiBakerBatch.CompareNativeGi
-ddgiReference D:/UnityProject/MaxSRP/OfflineBaseline/DDGIBaker/NativeBistroNoon
-ddgiOutput D:/UnityProject/MaxSRP/OfflineBaseline/DDGIBaker/UnityBistroNoon
-ddgiKey 0 -ddgiGeometryPasses 4
```

`comparison.json` includes encoded atlas errors, distance moments, probe offsets and states, and decoded linear irradiance sampled in world space. The batch entry exits with an error if the reference samples are all dark, the probe classification changes, or the relative linear irradiance RMSE exceeds 1%.

## Local-only generated data

Save imported volume assets and shared atlases under `Assets/LocalGIBakes/Bistro/SolarGI/`. Import/conversion menus create this default directory and reject destinations outside `Assets/LocalGIBakes/`. Bake raw output goes to a new `OfflineBaseline/DDGIBaker/<run>/`; irradiance stream keys remain under `Assets/StreamingAssets/MaxSRP/SolarTOD/`. None of these generated data or their metadata are committed. See [bake data and reproduction](../../Documentation/BakeData.md).
