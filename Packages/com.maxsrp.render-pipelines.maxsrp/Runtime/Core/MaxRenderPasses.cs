using System;
using System.Collections.Generic;

namespace MaxSRP
{
    // 持有按管线当前档位构造的一组渲染通道。
    internal sealed class MaxRenderPasses : IDisposable
    {
        internal readonly MaxRenderOpaquePass Opaque;
        internal readonly MaxLightPass Lighting;
        internal readonly MaxDDGIPass DDGI;
        internal readonly MaxClusteredLightingPass ClusteredLighting;
        internal readonly MaxColorOutputPass ColorOutput;
        internal readonly MaxTemporalAAPass TemporalAA;
        internal readonly MaxShadowCasterPass Shadows;
        internal readonly MaxProceduralSkyboxPass Atmosphere;
        internal readonly MaxIBLGIPass SkyIBL;
        internal readonly MaxSixWayCloudPass SixWayCloud;
        internal readonly MaxPostProcessPass PostProcess;

        private readonly List<MaxRenderPass> m_ownedPasses = new List<MaxRenderPass>();

        internal MaxRenderPasses(MaxRenderConfiguration.Snapshot settings, MaxRenderPasses previous = null,
            MaxPassChanges changes = MaxPassChanges.None)
        {
            try
            {
                Opaque = Select(previous?.Opaque, MaxPassChanges.None, () => new MaxRenderOpaquePass());
                PostProcess = Select(previous?.PostProcess, changes & MaxPassChanges.PostProcess, () => new MaxPostProcessPass());
                DDGI = Select(previous?.DDGI, changes & MaxPassChanges.DDGI, () => new MaxDDGIPass(settings.DDGISamplingSpacing));
                Lighting = Select(previous?.Lighting, changes & MaxPassChanges.Lighting,
                    () => new MaxLightPass(settings.LightingShader, settings.Platform, DDGI));
                ClusteredLighting = Select(previous?.ClusteredLighting, changes & MaxPassChanges.ClusteredLighting,
                    () => new MaxClusteredLightingPass(settings.ClusteredLightingCS));
                ColorOutput = Select(previous?.ColorOutput, changes & MaxPassChanges.ColorOutput,
                    () => new MaxColorOutputPass(settings.ColorOutput, settings.PostProcess.OutputShader));
                TemporalAA = Select(previous?.TemporalAA, changes & MaxPassChanges.TemporalAA,
                    () => new MaxTemporalAAPass(settings.TemporalShader, settings.TemporalQuality, settings.Platform));
                Shadows = Select(previous?.Shadows, changes & MaxPassChanges.Shadows,
                    () => new MaxShadowCasterPass(settings.CascadeSetting, settings.ShadowTileClearShader,
                        settings.Platform == MaxRenderPlatform.Mobile ? 3 : 4));
                Atmosphere = Select(previous?.Atmosphere, changes & MaxPassChanges.Atmosphere,
                    () => new MaxProceduralSkyboxPass(settings.AtmosphereScatteringSetting, settings.AtmosphereResources));
                SkyIBL = Select(previous?.SkyIBL, changes & MaxPassChanges.SkyIBL, () => new MaxIBLGIPass(settings.IBLCS));
                SixWayCloud = Select(previous?.SixWayCloud, changes & MaxPassChanges.SixWayCloud,
                    () => new MaxSixWayCloudPass(settings.SixWayCloud));
            }
            catch
            {
                // The previous group still owns anything borrowed during construction.
                if (previous != null) m_ownedPasses.RemoveAll(previous.m_ownedPasses.Contains);
                Dispose();
                throw;
            }
        }

        private T Select<T>(T previous, MaxPassChanges change, Func<T> create) where T : MaxRenderPass
        {
            T pass = previous != null && change == MaxPassChanges.None ? previous : create();
            m_ownedPasses.Add(pass);
            return pass;
        }

        internal void TakeOwnershipFrom(MaxRenderPasses previous)
        {
            previous.m_ownedPasses.RemoveAll(m_ownedPasses.Contains);
            if (previous.TemporalAA != TemporalAA)
                previous.TemporalAA.TransferHistoriesTo(TemporalAA);

            if (previous.SixWayCloud != SixWayCloud)
                SixWayCloud.ContinueContentRevision(previous.SixWayCloud.ContentRevision);
        }

        internal void PruneDestroyedCameras()
        {
            foreach (MaxRenderPass pass in m_ownedPasses)
                pass.PruneDestroyedCameras();
        }

        // 按注册倒序释放已构造的通道，包括构造中途失败的情况。
        public void Dispose()
        {
            for (int index = m_ownedPasses.Count - 1; index >= 0; --index)
                m_ownedPasses[index].Dispose();
            m_ownedPasses.Clear();
        }
    }
}
