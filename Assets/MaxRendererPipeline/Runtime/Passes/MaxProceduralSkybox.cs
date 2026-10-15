using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public class MaxProceduralSkyboxPass : IDisposable
    {
        private Material m_skyboxMat, m_transmittanceMat, m_multiscatteringMat, m_skyviewMat, m_aerialPerspectiveLUTMat;
        private RenderTexture m_transmittanceLUT, m_multiscatteringLUT;
        private Material m_environmentSkyviewMat;
        private RenderTexture m_environmentSkyviewLUT;
        private bool m_environmentValid;
        private ViewKey m_environmentKey;
        private SunLightData m_sun;
        public RenderTexture EnvironmentSky => m_environmentSkyviewLUT;
        public int EnvironmentRevision { get; private set; }
        private readonly Dictionary<int, CameraLuts> m_cameraLuts = new Dictionary<int, CameraLuts>();
        private readonly List<int> m_unusedCameraLuts = new List<int>();
        private readonly AtmosphereSettings m_setting;
        private bool m_transmittanceValid, m_multiscatteringValid;
        private MediumKey m_mediumKey;
        private float m_mieAnisotropy;
        private int m_mediumRevision;

        private struct MediumKey
        {
            public Vector4 PlanetRayleigh, MieOzone;
            public float OzoneWidth;
            public MediumKey(AtmosphereSettings s)
            {
                PlanetRayleigh = new Vector4(s._PlanetRadius, s._AtmosphereHeight,
                    s._RayleighScatteringScale, s._RayleighScatteringScalarHeight);
                MieOzone = new Vector4(s._MieScatteringScale, s._MieScatteringScalarHeight,
                    s._OzoneAbsorptionScale, s._OzoneLevelCenterHeight);
                OzoneWidth = s._OzoneLevelWidth;
            }
            public bool Equals(MediumKey other) => PlanetRayleigh.Equals(other.PlanetRayleigh) &&
                MieOzone.Equals(other.MieOzone) && OzoneWidth.Equals(other.OzoneWidth);
        }

        private struct ViewKey
        {
            public int MediumRevision;
            public float Altitude;
            public Color SunColor;
            public Vector4 SunDirection;
            public bool Equals(ViewKey other) => MediumRevision == other.MediumRevision &&
                Altitude.Equals(other.Altitude) &&
                SunColor.Equals(other.SunColor) && SunDirection.Equals(other.SunDirection);
        }

        private sealed class CameraLuts : IDisposable
        {
            public Camera Owner;
            public RenderTexture Sky, Scattering, Transmittance;
            public RenderTargetIdentifier[] AerialTargets;
            public bool SkyValid, AerialValid;
            public ViewKey SkyKey, AerialKey;
            public Matrix4x4 Projection, CameraToWorld;
            public float Distance;
            public bool Orthographic;

            public void EnsureSky()
            {
                if (Sky != null && Sky.IsCreated()) return;
                MaxRenderResources.Destroy(ref Sky);
                SkyValid = false;
                Sky = CreateLut(256, 128, "SkyviewLUT");
            }

            public void EnsureAerial()
            {
                if (Scattering != null && Scattering.IsCreated() &&
                    Transmittance != null && Transmittance.IsCreated()) return;
                MaxRenderResources.Destroy(ref Scattering);
                MaxRenderResources.Destroy(ref Transmittance);
                AerialTargets = null;
                AerialValid = false;
                try
                {
                    Scattering = CreateLut(32 * 32, 32, "AerialPerspectiveLUT");
                    Transmittance = CreateLut(32 * 32, 32, "AerialTransmittanceLUT");
                    AerialTargets = new[] { new RenderTargetIdentifier(Scattering), new RenderTargetIdentifier(Transmittance) };
                }
                catch
                {
                    MaxRenderResources.Destroy(ref Scattering);
                    MaxRenderResources.Destroy(ref Transmittance);
                    throw;
                }
            }

            public void Dispose()
            {
                MaxRenderResources.UnbindTexture("_AerialPerspectiveLUT", Scattering);
                MaxRenderResources.UnbindTexture("_AerialTransmittanceLUT", Transmittance);
                MaxRenderResources.Destroy(ref Sky);
                MaxRenderResources.Destroy(ref Scattering);
                MaxRenderResources.Destroy(ref Transmittance);
                AerialTargets = null;
                SkyValid = AerialValid = false;
            }
        }

        private static RenderTexture CreateLut(int width, int height, string name)
        {
            var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            if (!target.Create())
            {
                MaxRenderResources.Destroy(ref target);
                throw new InvalidOperationException("Cannot allocate " + name);
            }
            return target;
        }

        public MaxProceduralSkyboxPass(AtmosphereSettings setting)
        {
            m_setting = setting;
            try
            {
                m_skyboxMat = new Material(setting.ProceduralSkyboxShader);
                m_transmittanceMat = new Material(setting.TransmittanceShader);
                m_multiscatteringMat = new Material(setting.MultiscatteringShader);
                m_skyviewMat = new Material(setting.SkyviewShader);
                m_environmentSkyviewMat = new Material(setting.SkyviewShader);
                m_aerialPerspectiveLUTMat = new Material(setting.AerialPerspectiveLUTShader);
                m_transmittanceLUT = CreateLut(256, 64, "TransmittanceLUT");
                m_multiscatteringLUT = CreateLut(32, 32, "MultiscatteringLUT");
#if UNITY_EDITOR
                UnityEditor.EditorApplication.projectChanged += InvalidateCache;
#endif
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void InvalidateCache()
        {
            m_transmittanceValid = m_multiscatteringValid = false;
            m_environmentValid = false;
            foreach (CameraLuts cache in m_cameraLuts.Values) cache.SkyValid = cache.AerialValid = false;
        }

        public void ReleaseCamera(int cameraId)
        {
            if (!m_cameraLuts.TryGetValue(cameraId, out CameraLuts cache)) return;
            // A material may still retain the last camera's textures after its output disappears.
            m_skyboxMat.SetTexture(ShaderProperties._SkyviewLUT, null);
            cache.Dispose();
            m_cameraLuts.Remove(cameraId);
        }

        public void PruneDestroyedCameras()
        {
            // Editor views may arrive in separate Render calls. Absence from this
            // call's camera list does not mean that a camera/cache is no longer used.
            m_unusedCameraLuts.Clear();
            foreach (var entry in m_cameraLuts)
                if (entry.Value.Owner == null) m_unusedCameraLuts.Add(entry.Key);
            foreach (int id in m_unusedCameraLuts) ReleaseCamera(id);
        }

        private void Update(CommandBuffer cmd, Camera camera, Vector4 lightDirection, bool skyEnabled, bool fogEnabled, float environmentHeight)
        {
            var medium = new MediumKey(m_setting);
            bool transmittanceDirty = !m_transmittanceValid || !medium.Equals(m_mediumKey) || !m_transmittanceLUT.IsCreated();
            if (transmittanceDirty)
            {
                if (!m_transmittanceLUT.IsCreated() && !m_transmittanceLUT.Create())
                    throw new InvalidOperationException("Cannot recreate TransmittanceLUT");
                BakeTransmittanceLUT(cmd);
                m_mediumKey = medium;
                m_transmittanceValid = true;
            }
            if (transmittanceDirty || !m_multiscatteringValid ||
                !m_mieAnisotropy.Equals(m_setting._MieAnisotropy) || !m_multiscatteringLUT.IsCreated())
            {
                if (!m_multiscatteringLUT.IsCreated() && !m_multiscatteringLUT.Create())
                    throw new InvalidOperationException("Cannot recreate MultiscatteringLUT");
                BakeMultiscatteringLUT(cmd);
                m_mieAnisotropy = m_setting._MieAnisotropy;
                m_multiscatteringValid = true;
                ++m_mediumRevision;
            }

            float environmentY = m_setting._SeaLevel + Mathf.Max(1.0f, environmentHeight);
            var environmentKey = GetViewKey(Mathf.Max(environmentY - m_setting._SeaLevel, 1.0f), lightDirection);
            if (m_environmentSkyviewLUT == null || !m_environmentSkyviewLUT.IsCreated())
            {
                MaxRenderResources.Destroy(ref m_environmentSkyviewLUT);
                m_environmentSkyviewLUT = CreateLut(256, 128, "EnvironmentSkyviewLUT");
                // Longitude is periodic; latitude remains clamped at the poles.
                m_environmentSkyviewLUT.wrapModeU = TextureWrapMode.Repeat;
                m_environmentValid = false;
            }
            if (!m_environmentValid || !environmentKey.Equals(m_environmentKey))
            {
                BakeSkyviewLUT(cmd, m_environmentSkyviewMat, environmentY, m_environmentSkyviewLUT);
                m_environmentKey = environmentKey;
                m_environmentValid = true;
                ++EnvironmentRevision;
            }
            if (!skyEnabled && !fogEnabled) return;

            int cameraId = camera.GetInstanceID();
            if (!m_cameraLuts.TryGetValue(cameraId, out CameraLuts cache))
            {
                cache = new CameraLuts { Owner = camera };
                m_cameraLuts.Add(cameraId, cache);
            }
            var viewKey = GetViewKey(Mathf.Max(camera.transform.position.y - m_setting._SeaLevel, 1.0f), lightDirection);
            if (skyEnabled)
            {
                cache.EnsureSky();
                if (!cache.SkyValid || !viewKey.Equals(cache.SkyKey))
                {
                    BakeSkyviewLUT(cmd, m_skyviewMat, camera.transform.position.y, cache.Sky);
                    cache.SkyKey = viewKey;
                    cache.SkyValid = true;
                }
                m_skyboxMat.SetTexture(ShaderProperties._TransmittanceLUT, m_transmittanceLUT);
                m_skyboxMat.SetTexture(ShaderProperties._SkyviewLUT, cache.Sky);
                SetupAtmosphereParameters(m_skyboxMat);
            }
            if (fogEnabled)
            {
                cache.EnsureAerial();
                Matrix4x4 projection = GL.GetGPUProjectionMatrix(CameraViewport.GetProjection(camera), true);
                Matrix4x4 cameraToWorld = camera.worldToCameraMatrix.inverse;
                if (!cache.AerialValid || !viewKey.Equals(cache.AerialKey) ||
                    !projection.Equals(cache.Projection) || !cameraToWorld.Equals(cache.CameraToWorld) ||
                    !cache.Distance.Equals(m_setting._AerialPerspectiveDistance) || cache.Orthographic != camera.orthographic)
                {
                    BakeAerialPerspectiveLUT(cmd, camera, cache, projection, cameraToWorld);
                    cache.AerialKey = viewKey;
                    cache.Projection = projection;
                    cache.CameraToWorld = cameraToWorld;
                    cache.Distance = m_setting._AerialPerspectiveDistance;
                    cache.Orthographic = camera.orthographic;
                    cache.AerialValid = true;
                }
                // Rebind on every camera, even on a cache hit: other cameras use different textures.
                cmd.SetGlobalTexture(ShaderProperties._AerialPerspectiveLUT, cache.Scattering);
                cmd.SetGlobalTexture(ShaderProperties._AerialTransmittanceLUT, cache.Transmittance);
                cmd.SetGlobalVector(ShaderProperties._AerialPerspectiveParams,
                    new Vector4(m_setting._AerialPerspectiveDistance, 32, 32, 32));
            }
        }

        private ViewKey GetViewKey(float altitude, Vector4 direction)
        {
            return new ViewKey { MediumRevision = m_mediumRevision, Altitude = altitude,
                SunColor = m_sun.LinearColor, SunDirection = direction };
        }

        public void PrepareAtmosphere(ScriptableRenderContext context, Camera camera, SunLightData sun, float environmentHeight)
        {
            m_sun = sun;
            CommandBuffer cmd = CommandBufferPool.Get("Atmosphere Scattering");
            try
            {
                bool fogEnabled = m_setting.EnableAerialPerspective && m_setting._AerialPerspectiveDistance > 0.0f;
                bool skyEnabled = camera.clearFlags == CameraClearFlags.Skybox;
                cmd.SetGlobalFloat(ShaderProperties._AerialPerspectiveEnabled, fogEnabled ? 1.0f : 0.0f);
#if UNITY_EDITOR
                // Never cache Unity's async compilation placeholder shader into data LUTs.
                UnityEditor.ShaderUtil.SetAsyncCompilation(cmd, false);
#endif
                Update(cmd, camera, sun.AtmosphereDirection, skyEnabled, fogEnabled, environmentHeight);
                // Surface sunlight uses the same optical depth as the sky. These
                // globals are needed even with fog disabled or a solid background.
                cmd.SetGlobalFloat(ShaderProperties._SeaLevel, m_setting._SeaLevel);
                cmd.SetGlobalFloat(ShaderProperties._PlanetRadius, m_setting._PlanetRadius);
                cmd.SetGlobalFloat(ShaderProperties._AtmosphereHeight, m_setting._AtmosphereHeight);
                cmd.SetGlobalTexture(ShaderProperties._TransmittanceLUT, m_transmittanceLUT);
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.RestoreAsyncCompilation(cmd);
#endif
                context.ExecuteCommandBuffer(cmd);
            }
            catch
            {
                // A partial/failed recording must not turn unrendered textures into cache hits.
                InvalidateCache();
                throw;
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }
            context.SetupCameraProperties(camera);
            ShaderBindings.SetPerCameraShaderVariables(context, camera, true);
        }

        public void DrawSkybox(ScriptableRenderContext context, Camera cam, CommandBuffer cmd,
            RenderTargetIdentifier target, RenderTargetIdentifier depth, Rect viewport)
        {
            cmd.name = "Procedural Skybox";
            cmd.SetRenderTarget(target, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                depth, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            cmd.SetViewport(viewport);
            cmd.BeginSample("Procedural Skybox");
            cmd.DrawProcedural(Matrix4x4.identity, m_skyboxMat, 0, MeshTopology.Triangles, 3);
            cmd.EndSample("Procedural Skybox");
            context.ExecuteCommandBuffer(cmd);
        }

        private void BakeTransmittanceLUT(CommandBuffer cmd)
        {
            SetupAtmosphereParameters(m_transmittanceMat);
            cmd.Blit(null, m_transmittanceLUT, m_transmittanceMat);
        }

        private void BakeMultiscatteringLUT(CommandBuffer cmd)
        {
            SetupAtmosphereParameters(m_multiscatteringMat);
            m_multiscatteringMat.SetTexture(ShaderProperties._TransmittanceLUT, m_transmittanceLUT);
            cmd.Blit(null, m_multiscatteringLUT, m_multiscatteringMat);
        }

        private void BakeSkyviewLUT(CommandBuffer cmd, Material material, float observerY, RenderTexture target)
        {
            SetupAtmosphereParameters(material);
            material.SetFloat("_AtmosphereCameraY", observerY);
            material.SetTexture(ShaderProperties._TransmittanceLUT, m_transmittanceLUT);
            material.SetTexture(ShaderProperties._MultiscatteringLUT, m_multiscatteringLUT);
            cmd.Blit(null, target, material);
        }

        private void BakeAerialPerspectiveLUT(CommandBuffer cmd, Camera camera, CameraLuts cache,
            Matrix4x4 projection, Matrix4x4 cameraToWorld)
        {
            SetupAtmosphereParameters(m_aerialPerspectiveLUTMat);
            m_aerialPerspectiveLUTMat.SetMatrix(ShaderProperties._AerialInverseProjection, projection.inverse);
            m_aerialPerspectiveLUTMat.SetMatrix(ShaderProperties._AerialCameraToWorld, cameraToWorld);
            Vector3 position = camera.transform.position;
            m_aerialPerspectiveLUTMat.SetVector(ShaderProperties._AerialCameraPosition,
                new Vector4(position.x, position.y, position.z, camera.orthographic ? 1.0f : 0.0f));
            m_aerialPerspectiveLUTMat.SetVector(ShaderProperties._AerialPerspectiveParams,
                new Vector4(m_setting._AerialPerspectiveDistance, 32, 32, 32));
            m_aerialPerspectiveLUTMat.SetTexture(ShaderProperties._TransmittanceLUT, m_transmittanceLUT);
            m_aerialPerspectiveLUTMat.SetTexture(ShaderProperties._MultiscatteringLUT, m_multiscatteringLUT);
            cmd.SetRenderTarget(cache.AerialTargets, new RenderTargetIdentifier(BuiltinRenderTextureType.None));
            cmd.SetViewport(new Rect(0, 0, 32 * 32, 32));
            cmd.BeginSample("Aerial Perspective LUT");
            cmd.DrawProcedural(Matrix4x4.identity, m_aerialPerspectiveLUTMat, 0, MeshTopology.Triangles, 3);
            cmd.EndSample("Aerial Perspective LUT");
        }

        private void SetupAtmosphereParameters(Material mat)
        {
            mat.SetFloat(ShaderProperties._SeaLevel, m_setting._SeaLevel);
            mat.SetFloat(ShaderProperties._PlanetRadius, m_setting._PlanetRadius);
            mat.SetFloat(ShaderProperties._AtmosphereHeight, m_setting._AtmosphereHeight);
            mat.SetColor(ShaderProperties._SolarIrradiance, m_sun.LinearColor);
            mat.SetVector(ShaderProperties._AtmosphereSunDirection, m_sun.AtmosphereDirection);
            mat.SetFloat(ShaderProperties._SunDiskAngle, m_setting._SunDiskAngle);
            mat.SetFloat(ShaderProperties._RayleighScatteringScale, m_setting._RayleighScatteringScale);
            mat.SetFloat(ShaderProperties._RayleighScatteringScalarHeight, m_setting._RayleighScatteringScalarHeight);
            mat.SetFloat(ShaderProperties._MieScatteringScale, m_setting._MieScatteringScale);
            mat.SetFloat(ShaderProperties._MieAnisotropy, m_setting._MieAnisotropy);
            mat.SetFloat(ShaderProperties._MieScatteringScalarHeight, m_setting._MieScatteringScalarHeight);
            mat.SetFloat(ShaderProperties._OzoneAbsorptionScale, m_setting._OzoneAbsorptionScale);
            mat.SetFloat(ShaderProperties._OzoneLevelCenterHeight, m_setting._OzoneLevelCenterHeight);
            mat.SetFloat(ShaderProperties._OzoneLevelWidth, m_setting._OzoneLevelWidth);
        }

        public void Dispose()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.projectChanged -= InvalidateCache;
#endif
            foreach (CameraLuts cache in m_cameraLuts.Values) cache.Dispose();
            m_cameraLuts.Clear();
            MaxRenderResources.Destroy(ref m_skyboxMat);
            MaxRenderResources.Destroy(ref m_transmittanceMat);
            MaxRenderResources.Destroy(ref m_multiscatteringMat);
            MaxRenderResources.Destroy(ref m_skyviewMat);
            MaxRenderResources.Destroy(ref m_environmentSkyviewMat);
            MaxRenderResources.Destroy(ref m_environmentSkyviewLUT);
            MaxRenderResources.Destroy(ref m_aerialPerspectiveLUTMat);
            MaxRenderResources.UnbindTexture("_TransmittanceLUT", m_transmittanceLUT);
            MaxRenderResources.Destroy(ref m_transmittanceLUT);
            MaxRenderResources.Destroy(ref m_multiscatteringLUT);
        }


        private static class ShaderProperties
        {
            public static readonly int _SeaLevel = Shader.PropertyToID("_SeaLevel");
            public static readonly int _PlanetRadius = Shader.PropertyToID("_PlanetRadius");
            public static readonly int _AtmosphereHeight = Shader.PropertyToID("_AtmosphereHeight");
            public static readonly int _SolarIrradiance = Shader.PropertyToID("_SolarIrradiance");
            public static readonly int _AtmosphereSunDirection = Shader.PropertyToID("_AtmosphereSunDirection");
            public static readonly int _SunDiskAngle = Shader.PropertyToID("_SunDiskAngle");
            public static readonly int _RayleighScatteringScale = Shader.PropertyToID("_RayleighScatteringScale");
            public static readonly int _RayleighScatteringScalarHeight = Shader.PropertyToID("_RayleighScatteringScalarHeight");
            public static readonly int _MieScatteringScale = Shader.PropertyToID("_MieScatteringScale");
            public static readonly int _MieAnisotropy = Shader.PropertyToID("_MieAnisotropy");
            public static readonly int _MieScatteringScalarHeight = Shader.PropertyToID("_MieScatteringScalarHeight");
            public static readonly int _OzoneAbsorptionScale = Shader.PropertyToID("_OzoneAbsorptionScale");
            public static readonly int _OzoneLevelCenterHeight = Shader.PropertyToID("_OzoneLevelCenterHeight");
            public static readonly int _OzoneLevelWidth = Shader.PropertyToID("_OzoneLevelWidth");

            public static readonly int _AerialPerspectiveEnabled = Shader.PropertyToID("_AerialPerspectiveEnabled");
            public static readonly int _AerialPerspectiveParams = Shader.PropertyToID("_AerialPerspectiveParams");
            public static readonly int _AerialInverseProjection = Shader.PropertyToID("_AerialInverseProjection");
            public static readonly int _AerialCameraToWorld = Shader.PropertyToID("_AerialCameraToWorld");
            public static readonly int _AerialCameraPosition = Shader.PropertyToID("_AerialCameraPosition");
            public static readonly int _AerialPerspectiveLUT = Shader.PropertyToID("_AerialPerspectiveLUT");
            public static readonly int _AerialTransmittanceLUT = Shader.PropertyToID("_AerialTransmittanceLUT");

            // LUTs
            public static readonly int _TransmittanceLUT = Shader.PropertyToID("_TransmittanceLUT");
            public static readonly int _MultiscatteringLUT = Shader.PropertyToID("_MultiscatteringLUT");
            public static readonly int _SkyviewLUT = Shader.PropertyToID("_SkyviewLUT");
        }

        [System.Serializable]
        public class AtmosphereSettings
        {
            [SerializeField] public float _SeaLevel = 0.0f;
            [SerializeField] public float _PlanetRadius = 6360000.0f;
            [SerializeField] public float _AtmosphereHeight = 60000.0f;
            [Tooltip("Visible solar angular radius in degrees. Disk radiance is derived from the shared solar illuminance.")]
            [SerializeField] public float _SunDiskAngle = 1.0f;
            [SerializeField] public float _RayleighScatteringScale = 1.0f;
            [SerializeField] public float _RayleighScatteringScalarHeight = 8000.0f;
            [SerializeField] public float _MieScatteringScale = 1.0f;
            [SerializeField] public float _MieAnisotropy = 0.8f;
            [SerializeField] public float _MieScatteringScalarHeight = 1200.0f;
            [SerializeField] public float _OzoneAbsorptionScale = 1.0f;
            [SerializeField] public float _OzoneLevelCenterHeight = 25000.0f;
            [SerializeField] public float _OzoneLevelWidth = 15000.0f;

            public bool EnableAerialPerspective = true;
            [Min(0.0f)] [SerializeField] public float _AerialPerspectiveDistance = 32000.0f;

            [SerializeField]
            private Shader m_proceduralSkyboxShader;
            public Shader ProceduralSkyboxShader
            {
                get
                {
                    return m_proceduralSkyboxShader;
                }
            }

            [SerializeField]
            private Shader m_transmittanceShader;
            public Shader TransmittanceShader
            {
                get
                {
                    return m_transmittanceShader;
                }
            }

            [SerializeField]
            private Shader m_multiscatteringShader;
            public Shader MultiscatteringShader
            {
                get
                {
                    return m_multiscatteringShader;
                }
            }

            [SerializeField]
            private Shader m_skyviewShader;
            public Shader SkyviewShader
            {
                get
                {
                    return m_skyviewShader;
                }
            }

            [SerializeField]
            private Shader m_aerialPerspectiveLUTShader;
            public Shader AerialPerspectiveLUTShader
            {
                get
                {
                    return m_aerialPerspectiveLUTShader;
                }
            }


        }
    }
}
