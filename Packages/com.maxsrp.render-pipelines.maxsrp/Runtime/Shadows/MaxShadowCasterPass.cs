using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Unity.Profiling;

namespace MaxSRP
{
    public class MaxShadowCasterPass : MaxRenderPass
    {
        public const int MaxAtlasSize = 4096;
        public const int TileBorder = 2;
        internal const float PcfRadiusTexels = 3f;
        private sealed class CameraShadows
        {
            public readonly Camera Camera;
            public readonly CascadeProjection[] FarProjections = new CascadeProjection[2];
            public RenderTexture Atlas;
            public Light Source;
            // Zero means the whole atlas needs to be refreshed.
            public int NextFarCascade;

            public CameraShadows(Camera camera) => Camera = camera;
        }

        private readonly struct CascadeProjection
        {
            private readonly Matrix4x4 m_viewProjection;
            private readonly float m_halfDepth;
            private readonly float m_width;

            public CascadeProjection(Matrix4x4 viewProjection, float halfDepth, float width)
            {
                m_viewProjection = viewProjection;
                m_halfDepth = halfDepth;
                m_width = width;
            }

            public void Bind(CommandBuffer cmd, int cascade)
            {
                cmd.SetGlobalMatrix(MaxShadowShaderProperties.ViewProjection[cascade], m_viewProjection);
                cmd.SetGlobalFloat(MaxShadowShaderProperties.DepthHalfRange[cascade], m_halfDepth);
                cmd.SetGlobalFloat(MaxShadowShaderProperties.OrthoWidth[cascade], m_width);
            }
        }

        private readonly Dictionary<int, CameraShadows> m_cameras = new Dictionary<int, CameraShadows>();
        private readonly List<int> m_retired = new List<int>();
        private readonly RectInt[] m_tileRects = new RectInt[4];
        private Cascade m_cascade;
        private Camera m_shadowCamera;
        private Material m_tileClear;
        private readonly CascadeSettings m_settings;
        private readonly int m_cascadeCount;
        private readonly ShaderTagId m_shaderTagID = new ShaderTagId("ShadowCaster");
        private readonly MaxShadowCasterCullingVolume m_casterVolume = new MaxShadowCasterCullingVolume();
        private readonly Plane[] m_terrainCasterPlanes = new Plane[6];
        private readonly Vector3[] m_receiverWorldCorners = new Vector3[8];
        private static readonly ProfilerMarker s_volumeMarker = new ProfilerMarker("MaxSRP.Shadow.CasterVolume");
        private static readonly ProfilerMarker s_cullMarker = new ProfilerMarker("MaxSRP.Shadow.Cull");
        private static readonly ProfilerMarker s_drawMarker = new ProfilerMarker("MaxSRP.Shadow.DrawRenderers");
        private static readonly ProfilerMarker s_submitMarker = new ProfilerMarker("MaxSRP.Shadow.Submit");
        private static readonly ProfilerMarker s_terrainMarker = new ProfilerMarker("MaxSRP.Shadow.RecordTerrain");
        public MaxShadowCasterPass(CascadeSettings settings, Shader tileClearShader) : this(settings, tileClearShader, 4) { }

        internal MaxShadowCasterPass(CascadeSettings settings, Shader tileClearShader, int cascadeCount)
        {
            m_settings = settings;
            m_cascadeCount = cascadeCount;
            try
            {
                m_cascade = new Cascade(settings, cascadeCount);
                if (tileClearShader && tileClearShader.isSupported)
                    m_tileClear = new Material(tileClearShader) { hideFlags = HideFlags.HideAndDontSave };
            }
            catch { Dispose(); throw; }
        }

        internal static void ResetReceiverState(ScriptableRenderContext context)
        {
            using var commands = new PooledCommandBuffer("Reset Main Light Shadows");
            commands.Buffer.SetGlobalFloat(MaxShadowShaderProperties.Available, 0.0f);
            context.ExecuteCommandBuffer(commands.Buffer);
        }

        public void Invalidate(Camera camera)
        {
            foreach (CameraShadows state in m_cameras.Values)
                if (!camera || state.Camera == camera)
                    state.NextFarCascade = 0;
        }
        // 在渲染批次开始时回收已销毁相机的阴影 atlas，即使当前无投影光源。
        public override void PruneDestroyedCameras()
        {
            m_retired.Clear();
            foreach (var item in m_cameras)
                if (item.Value.Camera == null) m_retired.Add(item.Key);
            foreach (int key in m_retired)
            {
                CameraShadows retired = m_cameras[key];
                MaxRenderResources.UnbindTexture(MaxShadowShaderProperties.Atlas, retired.Atlas);
                MaxRenderResources.Destroy(ref retired.Atlas);
                m_cameras.Remove(key);
            }
        }

        private CameraShadows GetCameraShadows(Camera camera)
        {
            int id = camera.GetInstanceID();
            if (!m_cameras.TryGetValue(id, out CameraShadows state))
            {
                state = new CameraShadows(camera);
                m_cameras.Add(id, state);
            }
            return state;
        }
        private void EnsureShadowAtlas(CameraShadows state, CommandBuffer receiverBindings)
        {
            int resolution = Mathf.Min(MaxAtlasSize, Mathf.ClosestPowerOfTwo(SystemInfo.maxTextureSize));
            if (resolution > SystemInfo.maxTextureSize) resolution /= 2;
            if (!state.Atlas || state.Atlas.width != resolution || !state.Atlas.IsCreated())
            {
                var descriptor = new RenderTextureDescriptor(resolution, resolution, GraphicsFormat.None, 0)
                {
                    depthStencilFormat = GraphicsFormat.D16_UNorm,
                    shadowSamplingMode = ShadowSamplingMode.RawDepth,
                    msaaSamples = 1
                };
                var replacement = new RenderTexture(descriptor) {
                    name = "Directional Shadow Atlas - " + state.Camera.GetInstanceID(),
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point,
                    hideFlags = HideFlags.HideAndDontSave };
                if (!replacement.Create()) { MaxRenderResources.Destroy(ref replacement); throw new InvalidOperationException("Cannot allocate directional shadow atlas"); }
                MaxRenderResources.UnbindTexture(MaxShadowShaderProperties.Atlas, state.Atlas);
                MaxRenderResources.Destroy(ref state.Atlas);
                state.Atlas = replacement;
                state.NextFarCascade = 0;
            }
            int cellSize = resolution / 2;
            // Three Mobile tiles still fit a 2x2 layout within the 4096 limit.
            for (int i = 0; i < m_cascadeCount; ++i)
            {
                int requested = (int)m_settings.GetCascade(i).MapResolution;
                int tileSize = Mathf.Min(requested >= 2048 ? 2048 : requested >= 1024 ? 1024 : 512, cellSize);
                var rect = new RectInt((i % 2) * cellSize + TileBorder, (i / 2) * cellSize + TileBorder,
                    tileSize - 2 * TileBorder, tileSize - 2 * TileBorder);
                m_tileRects[i] = rect;
                receiverBindings.SetGlobalVector(MaxShadowShaderProperties.AtlasTile[i], new Vector4((float)rect.width / resolution,
                    (float)rect.height / resolution, (float)rect.x / resolution, (float)rect.y / resolution));
                receiverBindings.SetGlobalFloat(MaxShadowShaderProperties.Resolution[i], rect.width);
            }
        }
        public bool Execute(ScriptableRenderContext context, Camera camera, SunLightData sun)
        {
            if (!sun.CastsShadows) { Invalidate(camera); return false; }
            CameraShadows state = GetCameraShadows(camera);
            if (state.Source != sun.Source)
            {
                state.Source = sun.Source;
                state.NextFarCascade = 0;
            }
            // Publish the receiver data only after every required cascade is ready.
            using var receiverCommands = new PooledCommandBuffer("Bind Main Light Shadows");
            CommandBuffer receiverBindings = receiverCommands.Buffer;
            EnsureShadowAtlas(state, receiverBindings);
            RenderTexture atlas = state.Atlas;
            int farCascade = state.NextFarCascade;
            // A failed shadow render must restart with a complete atlas.
            state.NextFarCascade = 0;
            Vector3 lightDir = -(Vector3)sun.Direction;
            if (!m_cascade.UpdateCascade(camera, receiverBindings)) return false;
            if (!m_shadowCamera)
            {
                var shadowObject = new GameObject("MaxSRP Shadow Camera") { hideFlags = HideFlags.HideAndDontSave };
                m_shadowCamera = shadowObject.AddComponent<Camera>();
            }
            m_shadowCamera.CopyFrom(camera);
            m_shadowCamera.enabled = false; m_shadowCamera.targetTexture = null; m_shadowCamera.rect = new Rect(0, 0, 1, 1);
            m_shadowCamera.orthographic = true;
            m_shadowCamera.ResetProjectionMatrix(); m_shadowCamera.ResetWorldToCameraMatrix(); m_shadowCamera.ResetCullingMatrix();
            bool fullClear = farCascade == 0 || !m_tileClear;
            if (fullClear)
            {
                using var commands = new PooledCommandBuffer("Shadow Atlas Clear");
                CommandBuffer clear = commands.Buffer;
                clear.SetRenderTarget(atlas);
                clear.SetViewport(new Rect(0, 0, atlas.width, atlas.height));
                clear.ClearRenderTarget(true, false, Color.clear);
                context.ExecuteCommandBuffer(clear);
            }
            for (int i = 0; i < m_cascadeCount; ++i)
            {
                if (!fullClear && i >= 2 && i != farCascade)
                {
                    state.FarProjections[i - 2].Bind(receiverBindings, i);
                    continue;
                }

                RectInt tile = m_tileRects[i];
                int resolution = tile.width;
                float halfDepth = m_cascade.ConfigCameraToShadowSpace(m_shadowCamera, lightDir, i, m_cascade.m_OrthoDistance, resolution);
                Matrix4x4 view = m_shadowCamera.worldToCameraMatrix;
                Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(m_shadowCamera.projectionMatrix, true);
                Matrix4x4 vp = gpuProjection * view;
                float width = m_cascade.m_OrthoWidths[i];
                ShadowSettings settings = m_settings.GetCascade(i);
                float worldTexel = width / resolution;
                float support = PcfRadiusTexels * Mathf.Sqrt(2.0f) * worldTexel;
                m_cascade.GetReceiverWorldCorners(camera, i, m_receiverWorldCorners);
                var projection = new CascadeProjection(vp, halfDepth, width);
                projection.Bind(receiverBindings, i);
                if (!m_shadowCamera.TryGetCullingParameters(out var parameters)) return false;
                using (var commands = new GpuPassScope(context, "ShadowMap"))
                {
                    CommandBuffer cmd = commands.Buffer;
                    context.SetupCameraProperties(m_shadowCamera);
                    cmd.SetGlobalMatrix("_ShadowCasterVP", vp);
                    cmd.SetRenderTarget(atlas, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                    if (!fullClear)
                    {
                        // SV_Depth drawing is viewport-bounded; a native depth clear may erase every tile.
                        cmd.SetViewport(new Rect(tile.x - TileBorder, tile.y - TileBorder,
                            tile.width + 2 * TileBorder, tile.height + 2 * TileBorder));
                        cmd.DrawProcedural(Matrix4x4.identity, m_tileClear, 0, MeshTopology.Triangles, 3);
                    }
                    cmd.SetViewport(new Rect(tile.x, tile.y, tile.width, tile.height));
                    commands.Enqueue();
                    // If tightening fails, TryApply leaves Unity's conservative caster volume in place.
                    using (s_volumeMarker.Auto())
                        m_casterVolume.TryApply(ref parameters, m_receiverWorldCorners, view,
                            support + Mathf.Abs(settings.ShadingPointNormalBias));
                    CullingResults results;
                    using (s_cullMarker.Auto()) results = context.Cull(ref parameters);
                    var sorting = new SortingSettings(m_shadowCamera);
                    var drawing = new DrawingSettings(m_shaderTagID, sorting);
                    var filtering = FilteringSettings.defaultValue;
                    using (s_drawMarker.Auto()) context.DrawRenderers(results, ref drawing, ref filtering);
                    using (s_terrainMarker.Auto())
                    if (MaxRenderBridge.HasParticipants(camera))
                    {
                        // A DrawMeshInstanced terrain is not present in Unity's
                        // culling results. Select its casters against this actual
                        // light volume, including off-camera terrain, and append
                        // them only when this cascade really refreshes.
                        for (int plane = 0; plane < m_terrainCasterPlanes.Length; ++plane)
                            m_terrainCasterPlanes[plane] = parameters.GetCullingPlane(plane);
                        cmd.SetRenderTarget(atlas, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                        cmd.SetViewport(new Rect(tile.x, tile.y, tile.width, tile.height));
                        cmd.SetGlobalMatrix("_ShadowCasterVP", vp);
                        MaxRenderBridge.RecordShadow(cmd, camera, view, gpuProjection, m_terrainCasterPlanes);
                        commands.Enqueue();
                    }
                }
                using (s_submitMarker.Auto()) context.Submit();
                if (i >= 2) state.FarProjections[i - 2] = projection;
            }
            receiverBindings.SetGlobalTexture(MaxShadowShaderProperties.Atlas, atlas, RenderTextureSubElement.Depth);
            receiverBindings.SetGlobalFloat(MaxShadowShaderProperties.Available, 1.0f);
            context.ExecuteCommandBuffer(receiverBindings);
            state.NextFarCascade = m_cascadeCount == 4 && farCascade == 2 ? 3 : 2;
            return true;
        }
        protected sealed override void ReleaseResources()
        {
            if (m_shadowCamera)
            {
                GameObject shadowObject = m_shadowCamera.gameObject; m_shadowCamera = null;
                MaxRenderResources.Destroy(ref shadowObject);
            }
            foreach (CameraShadows state in m_cameras.Values)
            {
                MaxRenderResources.UnbindTexture(MaxShadowShaderProperties.Atlas, state.Atlas);
                MaxRenderResources.Destroy(ref state.Atlas);
            }
            m_cameras.Clear();
            MaxRenderResources.Destroy(ref m_tileClear);
        }
    }

    public class Cascade
    {
        private static readonly ProfilerMarker s_frustumMarker = new ProfilerMarker("MaxSRP.Shadow.CascadeFrustum");
        private static readonly ProfilerMarker s_projectionMarker = new ProfilerMarker("MaxSRP.Shadow.CascadeProjection");
        private readonly float[] m_splitEnds = new float[4];
        public float[] m_OrthoWidths = new float[4];
        public float m_OrthoDistance = 500;

        // 主相机视锥体
        private Vector3[] m_farCorners = new Vector3[4];
        private Vector3[] m_nearCorners = new Vector3[4];

        // 主相机划分四个视锥体
        private Vector3[] m_f0Near = new Vector3[4], m_f0Far = new Vector3[4];
        private Vector3[] m_f1Near = new Vector3[4], m_f1Far = new Vector3[4];
        private Vector3[] m_f2Near = new Vector3[4], m_f2Far = new Vector3[4];
        private Vector3[] m_f3Near = new Vector3[4], m_f3Far = new Vector3[4];

        // 主相机视锥体包围盒
        private readonly Vector4[] m_cascadeSpheres = new Vector4[4];
        private readonly float[] m_receiverStarts = new float[4];
        private readonly Vector3[] m_receiverNear = new Vector3[4], m_receiverFar = new Vector3[4];
        private bool m_receiverCornersReady;
        private Matrix4x4 m_receiverCameraToWorld;

        private CascadeSettings m_cascadeSettings;
        private readonly int m_cascadeCount;

        public Cascade(CascadeSettings settings) : this(settings, 4) { }

        internal Cascade(CascadeSettings settings, int cascadeCount)
        { 
            this.m_cascadeSettings = settings;
            m_cascadeCount = cascadeCount;

            m_OrthoDistance = settings.ShadowDistance;

            Shader.SetGlobalFloat(ShaderProperties._OrthoDistance, m_OrthoDistance);
        }

        public bool UpdateCascade(Camera mainCamera, ref Vector3 lightDir)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Bind Shadow Cascade Parameters");
            try
            {
                bool valid = UpdateCascade(mainCamera, cmd);
                Graphics.ExecuteCommandBuffer(cmd);
                return valid;
            }
            finally { CommandBufferPool.Release(cmd); }
        }

        internal bool UpdateCascade(Camera mainCamera, CommandBuffer receiverBindings)
        {
            using var profile = s_frustumMarker.Auto();
            // ShadowDistance is the maximum camera-forward depth, capped by the camera far plane.
            m_OrthoDistance = m_cascadeSettings.ShadowDistance;
            receiverBindings.SetGlobalFloat(ShaderProperties._OrthoDistance, m_OrthoDistance);
            float shadowNear = mainCamera.nearClipPlane;
            float shadowFar = Mathf.Min(mainCamera.farClipPlane, m_OrthoDistance);
            float span = shadowFar - shadowNear;
            receiverBindings.SetGlobalVector(ShaderProperties._CascadeDepthRange,
                new Vector4(shadowNear, shadowFar, span > 0 ? 1.0f / span : 0.0f, 0.0f));
            if (span <= 0) return false;

            // UE-style geometric slice lengths: 1, exponent, exponent^2, ...
            float exponent = Mathf.Clamp(m_cascadeSettings.DistributionExponent, 1.0f, 4.0f);
            float total = 0.0f;
            float weight = 1.0f;
            for (int i = 0; i < m_cascadeCount; ++i) { total += weight; weight *= exponent; }
            float cumulative = 0.0f;
            weight = 1.0f;
            for (int i = 0; i < m_cascadeCount; ++i)
            {
                cumulative += weight / total;
                m_splitEnds[i] = cumulative;
                weight *= exponent;
            }
            // Unused boundaries are outside all receiver depth tests. The
            // zero-width transition at the last boundary prevents sampling C3.
            for (int i = m_cascadeCount - 1; i < m_splitEnds.Length; ++i)
                m_splitEnds[i] = 1.0f;
            receiverBindings.SetGlobalFloat(ShaderProperties._Split0, m_splitEnds[0]);
            receiverBindings.SetGlobalFloat(ShaderProperties._Split1, m_splitEnds[1]);
            receiverBindings.SetGlobalFloat(ShaderProperties._Split2, m_splitEnds[2]);
            receiverBindings.SetGlobalFloat(ShaderProperties._Split3, m_splitEnds[3]);
            float blend0 = CascadeTransitionWidth(0.0f, m_splitEnds[0], m_splitEnds[1], m_cascadeSettings.CascadeBlendFraction);
            float blend1 = CascadeTransitionWidth(m_splitEnds[0], m_splitEnds[1], m_splitEnds[2], m_cascadeSettings.CascadeBlendFraction);
            float blend2 = CascadeTransitionWidth(m_splitEnds[1], m_splitEnds[2], 1.0f, m_cascadeSettings.CascadeBlendFraction);
            m_receiverStarts[0] = 0.0f;
            m_receiverStarts[1] = m_splitEnds[0] - blend0;
            m_receiverStarts[2] = m_splitEnds[1] - blend1;
            m_receiverStarts[3] = m_splitEnds[2] - blend2;
            m_receiverCornersReady = false;
            receiverBindings.SetGlobalVector("_CascadeBlendWidths", new Vector4(blend0, blend1, blend2, 0.0f));
            float fadeWidth = span * (1.0f - m_splitEnds[m_cascadeCount - 2]) *
                Mathf.Clamp(m_cascadeSettings.DistanceFadeFraction, 0.0f, 0.5f);
            receiverBindings.SetGlobalVector("_ShadowDistanceFade", new Vector4(shadowFar - fadeWidth, shadowFar, 0.0f, 0.0f));
            // 获取主相机视锥体
            FillFrustumPlaneCornersView(mainCamera, shadowNear, m_nearCorners);
            FillFrustumPlaneCornersView(mainCamera, shadowFar, m_farCorners);

            // 按照比例划分相机视锥体
            for (int i = 0; i < 4; i++)
            {
                Vector3 dir = m_farCorners[i] - m_nearCorners[i];

                m_f0Near[i] = m_nearCorners[i];
                m_f0Far[i] = m_nearCorners[i] + dir * m_splitEnds[0];

                m_f1Near[i] = m_nearCorners[i] + dir * (m_splitEnds[0] - blend0);
                m_f1Far[i] = m_nearCorners[i] + dir * m_splitEnds[1];

                m_f2Near[i] = m_nearCorners[i] + dir * (m_splitEnds[1] - blend1);
                m_f2Far[i] = m_nearCorners[i] + dir * m_splitEnds[2];

                m_f3Near[i] = m_nearCorners[i] + dir * (m_splitEnds[2] - blend2);
                m_f3Far[i] = m_farCorners[i];
            }

            // 计算旋转不变的级联包围球
            // Fit in view space: camera rotation/translation cannot change the radius.
            Matrix4x4 cameraToWorld = mainCamera.worldToCameraMatrix.inverse;
            m_receiverCameraToWorld = cameraToWorld;
            m_cascadeSpheres[0] = StableCascadeSphere(m_f0Near, m_f0Far, cameraToWorld);
            m_cascadeSpheres[1] = StableCascadeSphere(m_f1Near, m_f1Far, cameraToWorld);
            m_cascadeSpheres[2] = StableCascadeSphere(m_f2Near, m_f2Far, cameraToWorld);
            if (m_cascadeCount == 4)
                m_cascadeSpheres[3] = StableCascadeSphere(m_f3Near, m_f3Far, cameraToWorld);

            m_cascadeSettings.Set(receiverBindings);
            return true;
        }

        private static Quaternion LightRotation(Vector3 lightDir)
        {
            Vector3 up = Mathf.Abs(lightDir.y) > 0.99f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(lightDir, up);
        }

        public static float CascadeTransitionWidth(float leftStart, float boundary, float rightEnd, float fraction)
        {
            return Mathf.Max(0.0f, Mathf.Min(boundary - leftStart, rightEnd - boundary)) * Mathf.Clamp(fraction, 0.0f, 0.3f);
        }
        // Intersect each actual projection ray with the requested view-depth plane.
        // This supports Scene View orthographic/custom projections and custom view
        // matrices, without assuming that the Transform exactly matches the view.
        private static void FillFrustumPlaneCorners(Camera camera, float depth, Vector3[] corners)
        {
            FillFrustumPlaneCornersView(camera, depth, corners);
            Matrix4x4 cameraToWorld = camera.worldToCameraMatrix.inverse;
            for (int i = 0; i < 4; ++i) corners[i] = cameraToWorld.MultiplyPoint(corners[i]);
        }

        private static void FillFrustumPlaneCornersView(Camera camera, float depth, Vector3[] corners)
        {
            Matrix4x4 inverseProjection = CameraViewport.GetProjection(camera).inverse;
            FillFrustumPlaneCornersView(inverseProjection, depth, corners);
        }

        private static void FillFrustumPlaneCornersView(Matrix4x4 inverseProjection, float depth, Vector3[] corners)
        {
            for (int i = 0; i < 4; ++i)
            {
                float x = i < 2 ? -1.0f : 1.0f;
                float y = i == 0 || i == 3 ? -1.0f : 1.0f;
                // Unity's CPU projection uses -1..1 depth. Use two finite points;
                // depth zero also works for a projection with an infinite far plane.
                Vector3 a = inverseProjection.MultiplyPoint(new Vector3(x, y, -1.0f));
                Vector3 b = inverseProjection.MultiplyPoint(new Vector3(x, y, 0.0f));
                float t = (-depth - a.z) / (b.z - a.z);
                corners[i] = a + (b - a) * t;
            }
        }

        // Expanded receiver corners only affect candidate culling, never the stable raster projection.
        public void GetReceiverWorldCorners(Camera mainCamera, int cascade, Vector3[] corners)
        {
            if (!m_receiverCornersReady)
            {
                Matrix4x4 projection = CameraViewport.GetProjection(mainCamera);
                Vector2Int size = CameraViewport.GetRenderSize(mainCamera);
                projection.SetRow(0, projection.GetRow(0) * (size.x / (size.x + 2.0f)));
                projection.SetRow(1, projection.GetRow(1) * (size.y / (size.y + 2.0f)));
                Matrix4x4 inverse = projection.inverse;
                FillFrustumPlaneCornersView(inverse, mainCamera.nearClipPlane, m_receiverNear);
                FillFrustumPlaneCornersView(inverse, Mathf.Min(mainCamera.farClipPlane, m_OrthoDistance), m_receiverFar);
                m_receiverCornersReady = true;
            }
            for (int j = 0; j < 4; ++j)
            {
                Vector3 delta = m_receiverFar[j] - m_receiverNear[j];
                corners[j] = m_receiverCameraToWorld.MultiplyPoint3x4(m_receiverNear[j] + delta * m_receiverStarts[cascade]);
                corners[j + 4] = m_receiverCameraToWorld.MultiplyPoint3x4(m_receiverNear[j] + delta * m_splitEnds[cascade]);
            }
        }

        // 齐次坐标矩阵乘法变换
        Vector3 matTransform(Matrix4x4 m, Vector3 v, float w)
        {
            Vector4 v4 = new Vector4(v.x, v.y, v.z, w);
            v4 = m * v4;
            return new Vector3(v4.x, v4.y, v4.z);
        }

        // 从相机空间分段计算球半径，仅把球心转换到世界空间。
        private static Vector4 StableCascadeSphere(Vector3[] nearCorners, Vector3[] farCorners, Matrix4x4 cameraToWorld)
        {
            Vector3 center = Vector3.zero;
            for (int i = 0; i < 4; ++i) center += nearCorners[i] + farCorners[i];
            center *= 0.125f;
            float radius = 0.0f;
            for (int i = 0; i < 4; ++i)
            {
                radius = Mathf.Max(radius, Vector3.Distance(center, nearCorners[i]));
                radius = Mathf.Max(radius, Vector3.Distance(center, farCorners[i]));
            }
            Vector3 worldCenter = cameraToWorld.MultiplyPoint(center);
            return new Vector4(worldCenter.x, worldCenter.y, worldCenter.z, radius);
        }

        // Rotation-invariant extent plus a world-anchored texel grid.
        public float ConfigCameraToShadowSpace(Camera camera, Vector3 lightDir, int cascade, float distance, float resolution)
        {
            using var profile = s_projectionMarker.Auto();
            Vector4 sphere = m_cascadeSpheres[cascade];
            Vector3 center = new Vector3(sphere.x, sphere.y, sphere.z);
            float diameter = 2.0f * sphere.w;
            // Include kernel support and normal offset before snapping the center.
            ShadowSettings settings = m_cascadeSettings.GetCascade(cascade);
            float normalPadding = Mathf.Abs(settings.ShadingPointNormalBias);
            // Reserve the box footprint and up to one texel of snapping per axis.
            float paddingTexels = MaxShadowCasterPass.PcfRadiusTexels + 1.0f;
            float len = (Mathf.Max(0.001f, diameter) + 2.0f * normalPadding) *
                resolution / (resolution - 2.0f * paddingTexels);
            m_OrthoWidths[cascade] = len;
            float disPerPix = len / resolution;

            Matrix4x4 toShadowViewInv = Matrix4x4.TRS(Vector3.zero, LightRotation(lightDir), Vector3.one);
            Matrix4x4 toShadowView = toShadowViewInv.inverse;

            // 相机坐标旋转到光源坐标系下取整
            center = matTransform(toShadowView, center, 1.0f);
            for (int i = 0; i < 3; i++)
                center[i] = Mathf.Floor(center[i] / disPerPix) * disPerPix;
            center = matTransform(toShadowViewInv, center, 1.0f);

            // 配置相机
            camera.transform.rotation = LightRotation(lightDir);
            camera.transform.position = center;
            // Fit light-space depth too: wide/orthographic Scene views can have
            // a larger depth extent than the camera-forward shadow distance.
            float halfDepthRange = Mathf.Max(distance, sphere.w + disPerPix + Mathf.Abs(settings.ShadingPointNormalBias));
            camera.nearClipPlane = -halfDepthRange;
            camera.farClipPlane = halfDepthRange;
            camera.aspect = 1.0f;
            camera.orthographicSize = len * 0.5f;
            return halfDepthRange;
        }

        private class ShaderProperties
        {
            public static readonly int _OrthoDistance = Shader.PropertyToID("_OrthoDistance");
            public static readonly int _CascadeDepthRange = Shader.PropertyToID("_CascadeDepthRange");
            public static readonly int _Split0 = Shader.PropertyToID("_Split0");
            public static readonly int _Split1 = Shader.PropertyToID("_Split1");
            public static readonly int _Split2 = Shader.PropertyToID("_Split2");
            public static readonly int _Split3 = Shader.PropertyToID("_Split3");
        }
    }
}
