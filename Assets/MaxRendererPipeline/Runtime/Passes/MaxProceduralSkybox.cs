using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace MaxSRP
{
    public class MaxProceduralSkyboxPass : IDisposable
    {
        private Material m_skyboxMat, m_transmittanceMat, m_multiscatteringMat, m_skyviewMat, m_aerialPerspectiveLUTMat;
        private readonly ComputeShader m_aerialCompute;
        private readonly int m_aerialKernel;
        private RenderTexture m_transmittanceLUT, m_multiscatteringLUT;
        private Material m_environmentSkyviewMat;
        private RenderTexture m_environmentSkyviewLUT;
        private bool m_environmentValid;
        private ViewKey m_environmentKey;
        private SunLightData m_sun;
        public RenderTexture EnvironmentSky => m_environmentSkyviewLUT;
        public RenderTexture TransmittanceLUT => m_transmittanceLUT;
        public float EnvironmentViewHeight { get; private set; }
        public Vector4 SkyViewSizeAndInvSize
        {
            get
            {
                Vector2Int size = SkyViewSize(MaxAtmosphereConfig.ResolvePlatform(m_configuration));
                return new Vector4(size.x, size.y, 1.0f / size.x, 1.0f / size.y);
            }
        }
        public int EnvironmentRevision { get; private set; }
        private readonly Dictionary<int, CameraLuts> m_cameraLuts = new Dictionary<int, CameraLuts>();
        private readonly List<int> m_unusedCameraLuts = new List<int>();
        private readonly AtmosphereSettings m_setting;
        private readonly MaxAtmosphereConfig m_configuration;
        private Color m_groundAlbedo;
        private AtmospherePlatform m_platform;
        // Previously phase and feedback used separate local keywords. A single
        // platform variant now keeps phase, closure and numerical budgets paired.
        internal const string MobileKeyword = "MAX_ATMOSPHERE_MOBILE";
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
            public RenderTexture Sky, Scattering;
            public bool SkyValid, AerialValid;
            public ViewKey SkyKey, AerialKey;
            public Matrix4x4 Projection, CameraToWorld;
            public float Distance;
            public float StartDepth;
            public bool Orthographic;
            public Vector3 SkyRight, SkyForward;

            public void EnsureSky(Vector2Int size, GraphicsFormat format)
            {
                if (Sky != null && Sky.IsCreated() && Sky.width == size.x && Sky.height == size.y &&
                    Sky.graphicsFormat == format) return;
                MaxRenderResources.Destroy(ref Sky);
                SkyValid = false;
                Sky = CreateLut(size.x, size.y, "SkyviewLUT", format);
            }

            public void EnsureAerial(int depth)
            {
                if (Scattering != null && Scattering.IsCreated() &&
                    Scattering.dimension == TextureDimension.Tex3D && Scattering.volumeDepth == depth &&
                    Scattering.width == 32 && Scattering.height == 32) return;
                MaxRenderResources.UnbindTexture("_AerialPerspectiveLUT", Scattering);
                MaxRenderResources.Destroy(ref Scattering);
                AerialValid = false;
                try
                {
                    // Legacy AP used separate RGB scattering/transmission volumes
                    // (earlier two RGBA32F 2D atlases). Pack mean transmission in
                    // alpha now, matching UE's single filtered volume query.
                    Scattering = CreateLut(32, 32, "AerialPerspectiveLUT", GraphicsFormat.R16G16B16A16_SFloat, depth);
                }
                catch
                {
                    MaxRenderResources.Destroy(ref Scattering);
                    throw;
                }
            }

            public void Dispose()
            {
                MaxRenderResources.UnbindTexture("_AerialPerspectiveLUT", Scattering);
                MaxRenderResources.Destroy(ref Sky);
                MaxRenderResources.Destroy(ref Scattering);
                SkyValid = AerialValid = false;
            }
        }

        // Legacy MaxSRP used 256x128 world-lat/long SkyView for both platforms
        // and 32x32x32 AP stored as two RGBA32F 1024x32 textures. The current
        // presets use UE-style compact formats and native 3D AP volumes.
        private static Vector2Int SkyViewSize(AtmospherePlatform platform) =>
            platform == AtmospherePlatform.Mobile ? new Vector2Int(96, 50) : new Vector2Int(192, 104);

        private static int AerialDepth(AtmospherePlatform platform) =>
            platform == AtmospherePlatform.Mobile ? 4 : 32;

        private static GraphicsFormat SkyLutFormat(AtmospherePlatform platform)
        {
            GraphicsFormat packed = GraphicsFormat.B10G11R11_UFloatPack32;
            return platform == AtmospherePlatform.PC &&
                SystemInfo.IsFormatSupported(packed, FormatUsage.Render) &&
                SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float)
                ? packed : GraphicsFormat.R16G16B16A16_SFloat;
        }

        private static GraphicsFormat TransmittanceFormat(AtmospherePlatform platform) =>
            platform == AtmospherePlatform.Mobile ? GraphicsFormat.R8G8B8A8_UNorm : SkyLutFormat(platform);

        private static RenderTexture CreateLut(int width, int height, string name, GraphicsFormat format, int volumeDepth = 1)
        {
            var descriptor = new RenderTextureDescriptor(width, height)
            {
                graphicsFormat = format, depthBufferBits = 0, msaaSamples = 1,
                dimension = volumeDepth > 1 ? TextureDimension.Tex3D : TextureDimension.Tex2D,
                volumeDepth = volumeDepth, useMipMap = false, autoGenerateMips = false,
                enableRandomWrite = volumeDepth > 1
            };
            var target = new RenderTexture(descriptor)
            { name = name, hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            if (!target.Create())
            {
                MaxRenderResources.Destroy(ref target);
                throw new InvalidOperationException("Cannot allocate " + name);
            }
            return target;
        }

        private static bool EnsureLut(ref RenderTexture target, int width, int height, string name, GraphicsFormat format)
        {
            // Unity's destroyed native object compares equal to null even while its
            // managed wrapper remains. Never call IsCreated/Create on that wrapper.
            if (target == null || target.width != width || target.height != height || target.graphicsFormat != format)
            {
                MaxRenderResources.Destroy(ref target);
                target = CreateLut(width, height, name, format);
                return true;
            }
            if (target.IsCreated()) return false;
            if (!target.Create()) throw new InvalidOperationException("Cannot recreate " + name);
            return true;
        }

        public MaxProceduralSkyboxPass(AtmosphereSettings setting, MaxAtmosphereConfig configuration, ComputeShader aerialCompute)
        {
            m_setting = setting;
            m_configuration = configuration;
            try
            {
                m_skyboxMat = new Material(setting.ProceduralSkyboxShader) { hideFlags = HideFlags.HideAndDontSave };
                m_transmittanceMat = new Material(setting.TransmittanceShader) { hideFlags = HideFlags.HideAndDontSave };
                m_multiscatteringMat = new Material(setting.MultiscatteringShader) { hideFlags = HideFlags.HideAndDontSave };
                m_skyviewMat = new Material(setting.SkyviewShader) { hideFlags = HideFlags.HideAndDontSave };
                m_environmentSkyviewMat = new Material(setting.SkyviewShader) { hideFlags = HideFlags.HideAndDontSave };
                m_aerialPerspectiveLUTMat = new Material(setting.AerialPerspectiveLUTShader) { hideFlags = HideFlags.HideAndDontSave };
                m_aerialCompute = aerialCompute;
                if (m_aerialCompute == null) throw new InvalidOperationException("AP volume compute shader is missing.");
                m_aerialKernel = m_aerialCompute.FindKernel("BuildAerialPerspective");
                AtmospherePlatform platform = MaxAtmosphereConfig.ResolvePlatform(m_configuration);
                m_transmittanceLUT = CreateLut(256, 64, "TransmittanceLUT", TransmittanceFormat(platform));
                m_multiscatteringLUT = CreateLut(32, 32, "MultiscatteringLUT", SkyLutFormat(platform));
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
            AtmospherePlatform platform = MaxAtmosphereConfig.ResolvePlatform(m_configuration);
            bool platformDirty = m_platform != platform;
            Vector2Int skySize = SkyViewSize(platform);
            int aerialDepth = AerialDepth(platform);
            GraphicsFormat skyFormat = SkyLutFormat(platform);
            bool transmittanceRestored = EnsureLut(ref m_transmittanceLUT, 256, 64, "TransmittanceLUT", TransmittanceFormat(platform));
            bool multiscatteringRestored = EnsureLut(ref m_multiscatteringLUT, 32, 32, "MultiscatteringLUT", skyFormat);
            bool transmittanceDirty = transmittanceRestored || !m_transmittanceValid ||
                !medium.Equals(m_mediumKey) || platformDirty;
            if (transmittanceDirty)
            {
                BakeTransmittanceLUT(cmd);
                m_mediumKey = medium;
                m_transmittanceValid = true;
            }
            Color groundAlbedo = MaxAtmosphereConfig.ResolveGroundAlbedo(m_configuration);
            bool phaseDirty = platformDirty || !m_mieAnisotropy.Equals(m_setting._MieAnisotropy);
            bool multiscatteringDirty = transmittanceDirty || multiscatteringRestored ||
                !m_multiscatteringValid || !m_groundAlbedo.Equals(groundAlbedo) || platformDirty;
            if (multiscatteringDirty)
            {
                BakeMultiscatteringLUT(cmd);
                m_groundAlbedo = groundAlbedo;
                m_multiscatteringValid = true;
            }
            if (multiscatteringDirty || phaseDirty)
            {
                // Multi's feedback/closure depends on PC/Mobile, but its isotropic
                // source stays independent of g. Both changes invalidate SkyView/AP.
                m_mieAnisotropy = m_setting._MieAnisotropy;
                m_platform = platform;
                ++m_mediumRevision;
            }

            float environmentY = m_setting._SeaLevel + Mathf.Max(0.001f, environmentHeight * AtmosphereSettings.MetresToKilometres);
            EnvironmentViewHeight = m_setting._PlanetRadius + Mathf.Max(0.001f, environmentY - m_setting._SeaLevel);
            cmd.SetGlobalVector(ShaderProperties._AtmosphereSkyViewSizeAndInvSize, SkyViewSizeAndInvSize);
            cmd.SetGlobalFloat(ShaderProperties._AtmosphereEnvironmentSkyViewHeight, EnvironmentViewHeight);
            var environmentKey = GetViewKey(Mathf.Max(environmentY - m_setting._SeaLevel, 0.001f), lightDirection);
            if (EnsureLut(ref m_environmentSkyviewLUT, skySize.x, skySize.y, "EnvironmentSkyviewLUT", skyFormat))
            {
                // UE unit/sub-UV mapping stores its seam at the border texels;
                // unlike the legacy periodic lat/long map, both axes clamp.
                m_environmentValid = false;
            }
            if (!m_environmentValid || !environmentKey.Equals(m_environmentKey))
            {
                BakeSkyviewLUT(cmd, m_environmentSkyviewMat, environmentY, m_environmentSkyviewLUT,
                    Vector3.right, Vector3.forward);
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
            var viewKey = GetViewKey(Mathf.Max(camera.transform.position.y * AtmosphereSettings.MetresToKilometres - m_setting._SeaLevel, 0.001f), lightDirection);
            if (skyEnabled)
            {
                cache.EnsureSky(skySize, skyFormat);
                GetSkyViewBasis(camera, out Vector3 skyRight, out Vector3 skyForward);
                if (!cache.SkyValid || !viewKey.Equals(cache.SkyKey) ||
                    !cache.SkyRight.Equals(skyRight) || !cache.SkyForward.Equals(skyForward))
                {
                    BakeSkyviewLUT(cmd, m_skyviewMat, camera.transform.position.y * AtmosphereSettings.MetresToKilometres,
                        cache.Sky, skyRight, skyForward);
                    cache.SkyKey = viewKey;
                    cache.SkyRight = skyRight;
                    cache.SkyForward = skyForward;
                    cache.SkyValid = true;
                }
                m_skyboxMat.SetTexture(ShaderProperties._TransmittanceLUT, m_transmittanceLUT);
                m_skyboxMat.SetTexture(ShaderProperties._SkyviewLUT, cache.Sky);
                SetupAtmosphereParameters(m_skyboxMat);
                SetupSkyViewBasis(m_skyboxMat, skyRight, skyForward);
            }
            if (fogEnabled)
            {
                cache.EnsureAerial(aerialDepth);
                Matrix4x4 projection = GL.GetGPUProjectionMatrix(CameraViewport.GetProjection(camera), true);
                Matrix4x4 cameraToWorld = camera.worldToCameraMatrix.inverse;
                float startDepth = Mathf.Max(camera.nearClipPlane,
                    m_setting._AerialPerspectiveStartDepth * AtmosphereSettings.KilometresToMetres);
                if (!cache.AerialValid || !viewKey.Equals(cache.AerialKey) ||
                    !projection.Equals(cache.Projection) || !cameraToWorld.Equals(cache.CameraToWorld) ||
                    !cache.Distance.Equals(m_setting._AerialPerspectiveDistance) || !cache.StartDepth.Equals(startDepth) ||
                    cache.Orthographic != camera.orthographic)
                {
                    BakeAerialPerspectiveLUT(cmd, camera, cache, projection, cameraToWorld, startDepth);
                    cache.AerialKey = viewKey;
                    cache.Projection = projection;
                    cache.CameraToWorld = cameraToWorld;
                    cache.Distance = m_setting._AerialPerspectiveDistance;
                    cache.StartDepth = startDepth;
                    cache.Orthographic = camera.orthographic;
                    cache.AerialValid = true;
                }
                // Rebind on every camera, even on a cache hit: other cameras use different textures.
                cmd.SetGlobalTexture(ShaderProperties._AerialPerspectiveLUT, cache.Scattering);
                cmd.SetGlobalVector(ShaderProperties._AerialPerspectiveParams,
                    new Vector4(m_setting._AerialPerspectiveDistance * AtmosphereSettings.KilometresToMetres, 32, 32, aerialDepth));
                cmd.SetGlobalFloat(ShaderProperties._AerialPerspectiveStartDepth, startDepth);
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
            SetupPlatformVariant(m_transmittanceMat);
            SetupAtmosphereParameters(m_transmittanceMat);
            cmd.Blit(null, m_transmittanceLUT, m_transmittanceMat);
        }

        private void BakeMultiscatteringLUT(CommandBuffer cmd)
        {
            SetupPlatformVariant(m_multiscatteringMat);
            SetupAtmosphereParameters(m_multiscatteringMat);
            m_multiscatteringMat.SetTexture(ShaderProperties._TransmittanceLUT, m_transmittanceLUT);
            cmd.Blit(null, m_multiscatteringLUT, m_multiscatteringMat);
        }

        private void BakeSkyviewLUT(CommandBuffer cmd, Material material, float observerY, RenderTexture target,
            Vector3 skyRight, Vector3 skyForward)
        {
            SetupPlatformVariant(material);
            SetupAtmosphereParameters(material);
            SetupSkyViewBasis(material, skyRight, skyForward);
            material.SetFloat("_AtmosphereCameraY", observerY);
            material.SetTexture(ShaderProperties._TransmittanceLUT, m_transmittanceLUT);
            material.SetTexture(ShaderProperties._MultiscatteringLUT, m_multiscatteringLUT);
            cmd.Blit(null, target, material);
        }

        private void BakeAerialPerspectiveLUT(CommandBuffer cmd, Camera camera, CameraLuts cache,
            Matrix4x4 projection, Matrix4x4 cameraToWorld, float startDepth)
        {
            bool mobile = MaxAtmosphereConfig.ResolvePlatform(m_configuration) == AtmospherePlatform.Mobile;
            if (mobile) m_aerialCompute.EnableKeyword(MobileKeyword);
            else m_aerialCompute.DisableKeyword(MobileKeyword);
            SetupAerialComputeParameters(cmd);
            cmd.SetComputeMatrixParam(m_aerialCompute, ShaderProperties._AerialInverseProjection, projection.inverse);
            cmd.SetComputeMatrixParam(m_aerialCompute, ShaderProperties._AerialCameraToWorld, cameraToWorld);
            Vector3 position = camera.transform.position * AtmosphereSettings.MetresToKilometres;
            cmd.SetComputeVectorParam(m_aerialCompute, ShaderProperties._AerialCameraPosition,
                new Vector4(position.x, position.y, position.z, camera.orthographic ? 1.0f : 0.0f));
            cmd.SetComputeVectorParam(m_aerialCompute, ShaderProperties._AerialPerspectiveParams,
                new Vector4(m_setting._AerialPerspectiveDistance * AtmosphereSettings.KilometresToMetres, 32, 32, cache.Scattering.volumeDepth));
            cmd.SetComputeFloatParam(m_aerialCompute, ShaderProperties._AerialPerspectiveStartDepth, startDepth);
            cmd.SetComputeFloatParam(m_aerialCompute, ShaderProperties._AtmosphereAerialSampleCountPerSlice, mobile ? 1.0f : 2.0f);
            cmd.SetComputeTextureParam(m_aerialCompute, m_aerialKernel, ShaderProperties._TransmittanceLUT, m_transmittanceLUT);
            cmd.SetComputeTextureParam(m_aerialCompute, m_aerialKernel, ShaderProperties._MultiscatteringLUT, m_multiscatteringLUT);
            cmd.SetComputeTextureParam(m_aerialCompute, m_aerialKernel, "_AerialScatteringOutput", cache.Scattering);
            cmd.BeginSample("Aerial Perspective LUT");
            // Native 3D UAV writes avoid per-slice render-target state. The old
            // fragment producer remains as a reference to reconstruct MRT draws.
            cmd.DispatchCompute(m_aerialCompute, m_aerialKernel, 4, 4, cache.Scattering.volumeDepth);
            cmd.EndSample("Aerial Perspective LUT");
        }

        private void SetupAerialComputeParameters(CommandBuffer cmd)
        {
            cmd.SetComputeVectorParam(m_aerialCompute, ShaderProperties._GroundAlbedo, MaxAtmosphereConfig.ResolveGroundAlbedo(m_configuration));
            cmd.SetComputeFloatParam(m_aerialCompute, ShaderProperties._SeaLevel, m_setting._SeaLevel);
            cmd.SetComputeFloatParam(m_aerialCompute, ShaderProperties._PlanetRadius, m_setting._PlanetRadius);
            cmd.SetComputeFloatParam(m_aerialCompute, ShaderProperties._AtmosphereHeight, m_setting._AtmosphereHeight);
            cmd.SetComputeVectorParam(m_aerialCompute, ShaderProperties._SolarIrradiance, m_sun.LinearColor);
            cmd.SetComputeVectorParam(m_aerialCompute, ShaderProperties._AtmosphereSunDirection, m_sun.AtmosphereDirection);
            cmd.SetComputeFloatParam(m_aerialCompute, ShaderProperties._RayleighScatteringScale, m_setting._RayleighScatteringScale);
            cmd.SetComputeFloatParam(m_aerialCompute, ShaderProperties._RayleighScatteringScalarHeight, m_setting._RayleighScatteringScalarHeight);
            cmd.SetComputeFloatParam(m_aerialCompute, ShaderProperties._MieScatteringScale, m_setting._MieScatteringScale);
            cmd.SetComputeFloatParam(m_aerialCompute, ShaderProperties._MieAnisotropy, m_setting._MieAnisotropy);
            cmd.SetComputeFloatParam(m_aerialCompute, ShaderProperties._MieScatteringScalarHeight, m_setting._MieScatteringScalarHeight);
            cmd.SetComputeFloatParam(m_aerialCompute, ShaderProperties._OzoneAbsorptionScale, m_setting._OzoneAbsorptionScale);
            cmd.SetComputeFloatParam(m_aerialCompute, ShaderProperties._OzoneLevelCenterHeight, m_setting._OzoneLevelCenterHeight);
            cmd.SetComputeFloatParam(m_aerialCompute, ShaderProperties._OzoneLevelWidth, m_setting._OzoneLevelWidth);
        }

        private void SetupPlatformVariant(Material material)
        {
            // Retain exactly one paired platform variant on each LUT generator.
            // Trans/Multi budgets, phase and closure switch together at runtime.
            material.SetKeyword(new LocalKeyword(material.shader, MobileKeyword),
                MaxAtmosphereConfig.ResolvePlatform(m_configuration) == AtmospherePlatform.Mobile);
        }

        private static void GetSkyViewBasis(Camera camera, out Vector3 basisX, out Vector3 basisZ)
        {
            // UE uses the view's horizontal forward/left plane. Our helper's
            // local y is up, so its x/z columns carry those two tangent vectors.
            // Near a vertical view choose a stable tangent basis, as UE does.
            Vector3 forward = camera.transform.forward;
            basisX = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.999f
                ? Vector3.right : Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
            basisZ = Vector3.Cross(basisX, Vector3.up).normalized;
        }

        private void SetupSkyViewBasis(Material material, Vector3 basisX, Vector3 basisZ)
        {
            material.SetVector(ShaderProperties._AtmosphereSkyViewRight, basisX);
            material.SetVector(ShaderProperties._AtmosphereSkyViewForward, basisZ);
        }

        private void SetupAtmosphereParameters(Material mat)
        {
            mat.SetVector(ShaderProperties._AtmosphereSkyViewSizeAndInvSize, SkyViewSizeAndInvSize);
            mat.SetFloat(ShaderProperties._AtmosphereEnvironmentSkyViewHeight, EnvironmentViewHeight);
            mat.SetColor(ShaderProperties._GroundAlbedo, MaxAtmosphereConfig.ResolveGroundAlbedo(m_configuration));
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
            public static readonly int _AtmosphereSkyViewSizeAndInvSize = Shader.PropertyToID("_AtmosphereSkyViewSizeAndInvSize");
            public static readonly int _AtmosphereEnvironmentSkyViewHeight = Shader.PropertyToID("_AtmosphereEnvironmentSkyViewHeight");
            public static readonly int _AtmosphereSkyViewRight = Shader.PropertyToID("_AtmosphereSkyViewRight");
            public static readonly int _AtmosphereSkyViewForward = Shader.PropertyToID("_AtmosphereSkyViewForward");
            public static readonly int _AtmosphereAerialSampleCountPerSlice = Shader.PropertyToID("_AtmosphereAerialSampleCountPerSlice");
            public static readonly int _AerialPerspectiveStartDepth = Shader.PropertyToID("_AerialPerspectiveStartDepth");
            public static readonly int _GroundAlbedo = Shader.PropertyToID("_GroundAlbedo");
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

            // LUTs
            public static readonly int _TransmittanceLUT = Shader.PropertyToID("_TransmittanceLUT");
            public static readonly int _MultiscatteringLUT = Shader.PropertyToID("_MultiscatteringLUT");
            public static readonly int _SkyviewLUT = Shader.PropertyToID("_SkyviewLUT");
        }

        [System.Serializable]
        public class AtmosphereSettings
        {
            public const float MetresToKilometres = 0.001f;
            public const float KilometresToMetres = 1000f;
            // Serialized manifests before this version used metres.
            [HideInInspector] public int LengthUnitVersion = 1;
            [Tooltip("Length in kilometres (km).")] [SerializeField] public float _SeaLevel = 0.0f;
            [Tooltip("Length in kilometres (km).")] [SerializeField] public float _PlanetRadius = 6360.0f;
            [Tooltip("Length in kilometres (km).")] [SerializeField] public float _AtmosphereHeight = 60.0f;
            [Tooltip("Visible solar angular radius in degrees. Disk radiance is derived from the shared solar illuminance.")]
            [SerializeField] public float _SunDiskAngle = 0.26785f;
            [SerializeField] public float _RayleighScatteringScale = 1.0f;
            [Tooltip("Length in kilometres (km).")] [SerializeField] public float _RayleighScatteringScalarHeight = 8.0f;
            [SerializeField] public float _MieScatteringScale = 1.0f;
            [SerializeField] public float _MieAnisotropy = 0.8f;
            [Tooltip("Length in kilometres (km).")] [SerializeField] public float _MieScatteringScalarHeight = 1.2f;
            [SerializeField] public float _OzoneAbsorptionScale = 1.0f;
            [Tooltip("Length in kilometres (km).")] [SerializeField] public float _OzoneLevelCenterHeight = 25.0f;
            [Tooltip("Length in kilometres (km).")] [SerializeField] public float _OzoneLevelWidth = 15.0f;

            public bool EnableAerialPerspective = true;
            [Min(0.0f)] [Tooltip("AP volume depth in kilometres (km), measured after the start distance.")] [SerializeField] public float _AerialPerspectiveDistance = 96.0f;
            [Min(0.0f)] [Tooltip("AP start distance in kilometres (km); clamped to the camera near plane.")] [SerializeField] public float _AerialPerspectiveStartDepth = 0.1f;

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
