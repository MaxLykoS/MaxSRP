# Unity Cloud Layer source asset

`DefaultCloudMap.png` is an unmodified source asset from Unity Technologies Graphics.

- Upstream path: `Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipelineResources/Texture/DefaultCloudMap.png`
- Pinned commit: `1f3c6db33b4b6b3f228f494934d8978faf3e2b6f`
- Source: https://github.com/Unity-Technologies/Graphics/blob/1f3c6db33b4b6b3f228f494934d8978faf3e2b6f/Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipelineResources/Texture/DefaultCloudMap.png
- SHA-256: `31adcdba4682a53dc03daea286b8c3d200b5013e06e5b641bbb07a3d6481404c`
- Copyright Unity Technologies ApS; Unity Companion License for Unity-dependent projects. See `LICENSE.md` and https://unity.com/legal/licenses/unity-companion-license.

The project's `Tools/GenerateCloudDensity.py` extracts the red (cumulus) channel,
unprojects its upper-hemisphere outline onto a 16 km square plane at a 2 km
reference altitude, joins the tile edges, then square-root encodes density into
the 2048-square linear R8 runtime texture
`Assets/MaxRendererPipeline/Res/MainCloudDensity.png`. It bakes no lighting and adds
no additional runtime layer. Only this derived texture is referenced by the SRP.

Unity's cloud map channel meanings are documented at
https://docs.unity.cn/Packages/com.unity.render-pipelines.high-definition@14.0/manual/Override-Cloud-Layer.html.
