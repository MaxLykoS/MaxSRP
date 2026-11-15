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

            if (!asset.AtmosphereConfiguration || !asset.TemporalAAConfiguration || !asset.CloudConfiguration ||
                !asset.ShadowConfiguration || !asset.ClusteredLightingConfiguration || !asset.AOConfiguration || !asset.PostProcessConfiguration)
                throw new BuildFailedException($"MaxSRP pipeline '{AssetDatabase.GetAssetPath(asset)}' requires all seven module configuration assets.");

            Shader shader = asset.LightingShader;
            // isSupported describes the Editor's graphics device, not the build target.
            // Check the serialized dependency here; runtime validates device support.
            if (shader == null || !AssetDatabase.Contains(shader) || shader.name != "MaxSRP/PBRLitPass")
                throw new BuildFailedException(
                    $"MaxSRP pipeline '{AssetDatabase.GetAssetPath(asset)}' requires an asset reference to " +
                    "MaxSRP/PBRLitPass in Lighting Shader. Assign it before building the Player.");
            Shader temporal = asset.TemporalShader;
            if ((asset.ClusteredLightingConfiguration.GetProfile(MaxRenderPlatform.PC).Enabled ||
                asset.ClusteredLightingConfiguration.GetProfile(MaxRenderPlatform.Mobile).Enabled) &&
                (asset.ClusteredLightingCS == null || !AssetDatabase.Contains(asset.ClusteredLightingCS)))
                throw new BuildFailedException($"MaxSRP pipeline '{AssetDatabase.GetAssetPath(asset)}' requires its Clustered Lighting CS asset reference.");
            if (temporal == null || !AssetDatabase.Contains(temporal) || temporal.name != "Hidden/MaxSRP/TemporalAA")
                throw new BuildFailedException($"MaxSRP pipeline '{AssetDatabase.GetAssetPath(asset)}' requires its Temporal Shader asset reference.");
            var atmosphere = asset.AtmosphereConfiguration;
            if (!atmosphere.IBLCS || !atmosphere.AerialPerspectiveCompute || !atmosphere.ProceduralSkyboxShader ||
                !atmosphere.TransmittanceShader || !atmosphere.MultiscatteringShader || !atmosphere.SkyviewShader ||
                !atmosphere.AerialPerspectiveLUTShader)
                throw new BuildFailedException($"MaxSRP atmosphere configuration '{AssetDatabase.GetAssetPath(atmosphere)}' requires its shader and compute asset references.");
            var ao = asset.AOConfiguration;
            if (!ao.Compute || !AssetDatabase.Contains(ao.Compute) || !ao.SSAOShader || !AssetDatabase.Contains(ao.SSAOShader))
                throw new BuildFailedException($"MaxSRP AO configuration '{AssetDatabase.GetAssetPath(ao)}' requires PC GTAO and Mobile SSAO shader asset references.");
            foreach (MaxRenderPlatform platform in new[] { MaxRenderPlatform.PC, MaxRenderPlatform.Mobile })
            {
                CloudProfile cloud = asset.CloudConfiguration.GetProfile(platform);
                if ((cloud.Layer.Enabled && (!cloud.Layer.LightingShader || !AssetDatabase.Contains(cloud.Layer.LightingShader))) ||
                    (cloud.SixWay.Enabled && (!cloud.SixWay.Shader || !AssetDatabase.Contains(cloud.SixWay.Shader))))
                    throw new BuildFailedException($"MaxSRP cloud configuration '{AssetDatabase.GetAssetPath(asset.CloudConfiguration)}' requires its enabled {platform} shaders.");
            }
            var post = asset.PostProcessConfiguration;
            if (post && (!post.ExposureCompute || !AssetDatabase.Contains(post.ExposureCompute) ||
                !post.BloomShader || !AssetDatabase.Contains(post.BloomShader) || post.BloomShader.name != "Hidden/MaxSRP/PostProcessBloom" ||
                !post.OutputShader || !AssetDatabase.Contains(post.OutputShader) || post.OutputShader.name != "Hidden/MaxSRP/ColorOutput"))
                throw new BuildFailedException($"MaxSRP post-process configuration '{AssetDatabase.GetAssetPath(post)}' requires its Exposure Compute, Bloom Shader and Output Shader asset references.");
        }
    }
}
