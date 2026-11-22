using System;
using UnityEngine;

namespace MaxSRP
{
    [Flags]
    internal enum MaxPassChanges
    {
        None = 0,
        DDGI = 1 << 0,
        Lighting = 1 << 1,
        ClusteredLighting = 1 << 2,
        ColorOutput = 1 << 3,
        TemporalAA = 1 << 4,
        Shadows = 1 << 5,
        Atmosphere = 1 << 6,
        SkyIBL = 1 << 7,
        SixWayCloud = 1 << 8,
        PostProcess = 1 << 9
    }

    internal sealed class MaxRenderConfiguration
    {
        private readonly MaxRendererPipelineAsset m_asset;
        internal Snapshot Current { get; private set; }

        internal MaxRenderConfiguration(MaxRendererPipelineAsset asset)
        {
            m_asset = asset;
            Current = new Snapshot(asset, asset.ConfigurationRevision);
        }

        // Unchanged render batches only read and compare the asset revision.
        internal bool TryRefresh(out Snapshot next, out MaxPassChanges changes)
        {
            int revision = m_asset.ConfigurationRevision;
            if (revision == Current.Revision)
            {
                next = Current;
                changes = MaxPassChanges.None;
                return false;
            }

            next = new Snapshot(m_asset, revision);
            changes = next.ChangesFrom(Current);
            return true;
        }

        // Publish only after the pipeline has constructed all changed passes successfully.
        internal void Commit(Snapshot next) => Current = next;

        internal sealed class Snapshot
        {
            internal readonly int Revision;
            internal readonly MaxRenderPlatform Platform;
            internal readonly Shader LightingShader, TemporalShader, ShadowTileClearShader;
            internal readonly ComputeShader ClusteredLightingCS, IBLCS;
            internal readonly float DDGISamplingSpacing;
            internal readonly TemporalAASettings TemporalAA;
            internal readonly TemporalAAQuality TemporalQuality;
            internal readonly TemporalAADebugView TemporalDebugView;
            internal readonly CascadeSettings CascadeSetting;
            internal readonly ClusteredLightingSettings ClusteredLighting;
            internal readonly MaxProceduralSkyboxPass.AtmosphereSettings AtmosphereScatteringSetting;
            internal readonly MaxAtmospherePassResources AtmosphereResources;
            internal readonly SkyIBLSettings SkyIBL;
            internal readonly SixWayCloudSettings SixWayCloud;
            internal readonly MaxPostProcessSettings PostProcess;
            internal readonly ColorOutputSettings ColorOutput;

            private readonly Shader m_legacyOutputShader;
            private readonly MaxAmbientOcclusion m_ao, m_aoOnly;
            private readonly string m_shadowState, m_atmosphereState, m_sixWayState;

            internal Snapshot(MaxRendererPipelineAsset asset, int revision)
            {
                asset.ValidateConfigurations();
                Revision = revision;
                Platform = asset.Platform;
                LightingShader = asset.LightingShader;
                TemporalShader = asset.TemporalShader;
                TemporalAA = asset.TemporalAA;
                TemporalQuality = TemporalAA.GetQuality(Platform);
                TemporalDebugView = asset.TemporalAAConfiguration.DebugView;
                CascadeSetting = asset.CascadeSetting;
                ShadowTileClearShader = asset.ShadowConfiguration.TileClearShader;
                ClusteredLighting = asset.ClusteredLighting;
                ClusteredLightingCS = asset.ClusteredLightingCS;
                DDGISamplingSpacing = asset.DDGISamplingSpacing;
                IBLCS = asset.IBLCS;

                var atmosphere = asset.AtmosphereConfiguration.GetProfile(Platform);
                AtmosphereScatteringSetting = atmosphere.Atmosphere;
                AtmosphereResources = new MaxAtmospherePassResources(asset.AtmosphereConfiguration,
                    Platform, atmosphere.GroundAlbedo, asset.AerialPerspectiveCompute);
                SkyIBL = atmosphere.SkyIBL;

                SixWayCloud = asset.CloudConfiguration.GetProfile(Platform).SixWay;
                PostProcess = new MaxPostProcessSettings(asset.PostProcessConfiguration, Platform);
                ColorOutput = asset.ColorOutput;
                m_legacyOutputShader = ColorOutput.Shader;
                m_ao = new MaxAmbientOcclusion(asset.AOConfiguration, Platform, false);
                m_aoOnly = new MaxAmbientOcclusion(asset.AOConfiguration, Platform, true);

                // These mutable profiles are held by their passes. Snapshot their contents
                // when a revision arrives so edits to the same object are also detected.
                m_shadowState = JsonUtility.ToJson(CascadeSetting);
                m_atmosphereState = JsonUtility.ToJson(AtmosphereScatteringSetting);
                m_sixWayState = JsonUtility.ToJson(SixWayCloud);
            }

            internal MaxPassChanges ChangesFrom(Snapshot previous)
            {
                var changes = MaxPassChanges.None;
                bool platformChanged = Platform != previous.Platform;
                bool iblChanged = IBLCS != previous.IBLCS;
                bool ddgiChanged = !DDGISamplingSpacing.Equals(previous.DDGISamplingSpacing);
                bool atmosphereChanged = !AtmosphereResources.Equals(previous.AtmosphereResources) ||
                    !ReferenceEquals(AtmosphereScatteringSetting, previous.AtmosphereScatteringSetting) ||
                    m_atmosphereState != previous.m_atmosphereState;

                if (ddgiChanged) changes |= MaxPassChanges.DDGI;
                if (platformChanged || ddgiChanged || LightingShader != previous.LightingShader)
                    changes |= MaxPassChanges.Lighting;
                if (ClusteredLightingCS != previous.ClusteredLightingCS)
                    changes |= MaxPassChanges.ClusteredLighting;
                if (PostProcess.OutputShader != previous.PostProcess.OutputShader || m_legacyOutputShader != previous.m_legacyOutputShader)
                    changes |= MaxPassChanges.ColorOutput;
                if (platformChanged || TemporalShader != previous.TemporalShader || TemporalQuality != previous.TemporalQuality)
                    changes |= MaxPassChanges.TemporalAA;
                if (platformChanged || ShadowTileClearShader != previous.ShadowTileClearShader ||
                    !ReferenceEquals(CascadeSetting, previous.CascadeSetting) || m_shadowState != previous.m_shadowState)
                    changes |= MaxPassChanges.Shadows;
                if (atmosphereChanged) changes |= MaxPassChanges.Atmosphere;
                if (iblChanged) changes |= MaxPassChanges.SkyIBL;
                if (atmosphereChanged || !ReferenceEquals(SixWayCloud, previous.SixWayCloud) ||
                    m_sixWayState != previous.m_sixWayState)
                    changes |= MaxPassChanges.SixWayCloud;
                if (platformChanged) changes |= MaxPassChanges.PostProcess;
                return changes;
            }

            internal MaxAmbientOcclusion ResolveAmbientOcclusion(Camera camera, MaxTimeOfDayState environment)
            {
                if (!m_ao.HasProfile || camera.stereoEnabled ||
                    (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView))
                    return default;

                if (environment != null && environment.AmbientOcclusionEnabled)
                    return environment.AOOnly ? m_aoOnly : m_ao;

                var legacy = MaxSceneComponents<MaxAOSettings>.Find(camera);
                return legacy ? (legacy.AOOnly ? m_aoOnly : m_ao) : default;
            }
        }
    }
}
