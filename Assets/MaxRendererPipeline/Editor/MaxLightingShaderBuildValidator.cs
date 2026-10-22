using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP.EditorDiagnostics
{
    // Check configured assets, including Quality overrides that are not active in the Editor.
    internal sealed class MaxLightingShaderBuildValidator : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var checkedAssets = new HashSet<MaxRendererPipelineAsset>();
            Validate(GraphicsSettings.defaultRenderPipeline, checkedAssets);
            for (int i = 0; i < QualitySettings.names.Length; ++i)
                Validate(QualitySettings.GetRenderPipelineAssetAt(i), checkedAssets);
        }

        private static void Validate(RenderPipelineAsset pipeline, HashSet<MaxRendererPipelineAsset> checkedAssets)
        {
            var asset = pipeline as MaxRendererPipelineAsset;
            if (asset == null || !checkedAssets.Add(asset)) return;

            Shader shader = asset.LightingShader;
            // isSupported describes the Editor's graphics device, not the build target.
            // Check the serialized dependency here; runtime validates device support.
            if (shader == null || !AssetDatabase.Contains(shader) || shader.name != "MaxSRP/PBRLitPass")
                throw new BuildFailedException(
                    $"MaxSRP pipeline '{AssetDatabase.GetAssetPath(asset)}' requires an asset reference to " +
                    "MaxSRP/PBRLitPass in Lighting Shader. Assign it before building the Player.");
            Shader temporal = asset.TemporalShader;
            if (asset.ClusteredLighting.Enabled &&
                (asset.ClusteredLightingCS == null || !AssetDatabase.Contains(asset.ClusteredLightingCS)))
                throw new BuildFailedException($"MaxSRP pipeline '{AssetDatabase.GetAssetPath(asset)}' requires its Clustered Lighting CS asset reference.");
            if (temporal == null || !AssetDatabase.Contains(temporal) || temporal.name != "Hidden/MaxSRP/TemporalAA")
                throw new BuildFailedException($"MaxSRP pipeline '{AssetDatabase.GetAssetPath(asset)}' requires its Temporal Shader asset reference.");
        }
    }
}
