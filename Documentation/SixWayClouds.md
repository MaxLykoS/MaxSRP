# Six-way main-cloud sample

> 验证入口说明：下文 Validate/Capture 脚本及报告属于历史验证记录，临时夹具未保留在当前仓库。当前可运行 `Tools/ValidateShaders.ps1`，并在 Unity 检查 Console 与场景效果；不将历史记录视为本次重新验证。资源生成与 Setup 工具仍保留。

The default MaxSRP asset now enables **Six Way Cloud Settings** and disables **Cloud Layer Settings**. The old planar cloud implementation and its resources are retained for comparison. To compare, disable Six Way and enable Cloud Layer; do not leave both enabled when comparing the main cloud.

## Source and rendering

The resources are Unity's CC0 `TX_Pyro_Clouds_A_2x4_P/N` sample maps. See `Assets/ThirdParty/UnitySixWayClouds/SOURCE.md` for the official license declaration, download link and original checksums. `Tools/PrepareUnitySixWayClouds.py` reproduces the runtime PNGs. The 2x4 atlas contains **eight different static cloud shapes**, not an animated simulation. Each shape is 1024x512 in the prepared 2048-square atlas.

The sample uses 25 deterministic world-space billboards on a jittered 5x5 grid. Cloud centres move in world XZ at Wind Speed in metres/second; changing heading or speed preserves accumulated displacement. Billboards face the observer with world-up orientation; their centres do not rotate around or follow the camera. A periodic field and outer distance fade hide recycling. Height, cloud width and field size are exposed on the pipeline asset.

The six directions are lighting directions in each billboard's frame, not cubemap views. P.rgb is right/top/back, N.rgb is left/bottom/front, and P.a is opacity. Both maps are sampled as linear data. The sun direction is transformed into that frame, the appropriate signed-axis coefficients are selected, and squared direction components blend the result. Direct light uses the pipeline's photometric solar irradiance and atmospheric transmittance at cloud height. Ambient fill is a six-channel average modulated by zenith sky radiance. This is a simple normalized lighting model, not a full port of HDRP's smoke absorption/material implementation. No extra HG phase is multiplied onto the maps.

Rendering uses one procedural instanced draw (25 instances, two triangles each) after clear sky and before shared TAA. The cloud RGB output is premultiplied once, and blending preserves the destination alpha. Sky-depth rasterization and disabled depth writes let opaque geometry occlude the cloud cards, including cards beyond the camera far plane. This is a sky cloud pass, not a general transparent renderer: local clouds crossing foreground surfaces are not supported.

There is no screen-sized cloud color RT, view raymarch, height-field light bake, independent temporal history or cloud resolve. Map sampling and blending are still subject to overdraw. Desktop imports are uncompressed for this initial visual test; Android and iOS have ASTC 6x6 overrides, which still need device validation. Shader and map references are serialized to retain them in builds.

## IBL and TAA

The low-frequency IBL source intersects rays with the same sorted cards and samples the same six-way maps. Wind refreshes the environment at the configured interval (default one second), camera XZ changes use a 128-metre anchor, and content changes invalidate immediately. Existing atmosphere revisions can also trigger updates. Billboard placement for the environment uses the current observer, whereas clear atmospheric radiance retains the pipeline's reference-height convention; this is an approximation for a camera-local sky environment.

Clouds share the pipeline's sky color history. While wind or camera translation moves the cloud appearance, a conservative **whole-sky** history weight cap of 0.25 avoids assigning mixed cloud/clear-sky pixels one incorrect velocity. Static clouds allow normal accumulation. Six-way content and enable changes participate in the existing cloud revision invalidation. This initial cap sacrifices some clear-sky convergence; it is not a per-cloud reactive mask.

## Verification and limits

- `Tools/UnityMcp/SetupSixWayClouds.cs.txt`: reproducible map importer and pipeline selection.
- `Tools/UnityMcp/ValidateSixWayClouds.cs.txt`: actual SRP scene/sky captures, IBL six-face differences, old cloud RT release, fixed world centres under camera translation, directional response, zero opacity and shared TAA. Restores scene sun and temporary settings without saving the scene.
- `Tools/UnityMcp/ValidateTemporalAA.cs.txt`: 21 production-shader GPU cases, including moving six-way sky and static sky.
- `Tools/ValidateShaders.ps1`: includes both six-way shader entry points and the modified IBL kernel.

Captures and numerical reports are in ignored `OfflineBaseline/CloudSixWay`. The official asset is a demonstration of lit cards, not a complete authored sky. Reused shapes, billboard flatness, simple grid distribution, approximate ambient response and sorted transparency can be visible. There are no ground cloud shadows, cloud-body evolution or fly-through support. Mobile GPU time, power and Player builds have not been validated.
