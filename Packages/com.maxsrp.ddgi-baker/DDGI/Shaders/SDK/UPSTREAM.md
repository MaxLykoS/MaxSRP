# NVIDIA RTXGI-DDGI

This software contains source code provided by NVIDIA Corporation.

The local `rtxgi-sdk` directory contains an unmodified subset of RTXGI-DDGI v1.3.7, commit
[`c233266`](https://github.com/NVIDIAGameWorks/RTXGI-DDGI/tree/c233266e95931231961f35c842c78e7c4bdc50cf).
`ProbeRelocationCS.hlsl` is the exact version from
[`a6c4d99`](https://github.com/NVIDIAGameWorks/RTXGI-DDGI/commit/a6c4d9917ac6223be9ee97f304b7d92c20dda970),
NVIDIA's fix for [issue #78](https://github.com/NVIDIAGameWorks/RTXGI-DDGI/issues/78).
The NVIDIA files are distributed by NVIDIA under the [NVIDIA RTX SDKs License](https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/c233266e95931231961f35c842c78e7c4bdc50cf/License.txt).
Review that license before fetching the SDK.

From this directory, run `./Install.ps1` in PowerShell. The script downloads
the pinned NVIDIA license, headers and HLSL from GitHub and verifies each
file against [Sources.json](Sources.json). It then generates the Unity
shader assets from the package's authored `.txt` templates. Run
`./Install.ps1 -VerifyOnly` to check a local installation without
downloading or regenerating files. The downloaded and generated files
stay local to this checkout.

`UnitySdk.hlsl` and `../ProbeUpdate.compute.txt` supply Unity resource binding and
compute kernel declarations. They use the NVIDIA shaders without changing the
probe update algorithms.
