# Hex Tiling reference

Author: Morten S. Mikkelsen (mmikk). Copyright (c) 2022 mmikk.

Repository: https://github.com/mmikk/hextile-demo

Commit fetched on 2026-11-05: `43c3ed7e18e1e4539fa9323f72d5e2a65147ebb3`.

License: MIT, retained in [LICENSE.txt](LICENSE.txt).

`hextiling.h`, `hextiling_rws.h` and `surfgrad_framework.h` are upstream reference files. The analytical `HexProbe.shader` includes the unmodified reference implementation as a GPU oracle; the terrain shader uses the namespaced adaptation in `Shaders/DeltaHexTiling.cginc` instead.

Adaptations: Texture2DArray AH/NRAO packing; RG normal reconstruction; this project's tangent-space handedness; stable material-ID hash seed; fixed 256m world blocks for the reference RWS coordinate split; one common color-weight set for AH/roughness/AO and the reference slope weights for normals. The terrain's original ordered material-height blend remains outside Hex Tiling. The three ID-control reads are not repeated per random texture patch.
