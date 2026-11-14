using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Profiling;

namespace MaxSRP
{
    public class MaxShadowCasterPass : IDisposable
    {
        public const int MaxAtlasSize = 4096;
        public const int TileBorder = 2;
        private sealed class CameraCache
        {
            public Camera Camera;
            public RenderTexture Atlas;
            public readonly MaxShadowReusePolicy Policy = new MaxShadowReusePolicy();
            public readonly string[] Reasons = new string[4];
            public Matrix4x4 Projection;
            public Vector2Int Size;
            public Rect Viewport;
            public RenderTexture Output;
            public Light Sun;
            public int CullingMask, SceneHandle;
            public bool Initialized, HasKey, ReuseEnabled;
            public long Sequence;
            public double LastSeen;
        }
        private readonly Dictionary<int, CameraCache> m_cameras = new Dictionary<int, CameraCache>();
        private readonly List<int> m_retired = new List<int>();
        private RenderTexture m_shadowAtlas;
        private readonly RectInt[] m_tileRects = new RectInt[4];
        private Cascade m_cascade;
        private Camera m_shadowCamera;
        private Material m_tileClear;
        private readonly MaxProceduralSkyboxPass.AtmosphereSettings m_atmosphereSettings;
        private readonly CascadeSettings m_settings;
        private readonly ShaderTagId m_shaderTagID = new ShaderTagId("ShadowCaster");
        private readonly MaxShadowCasterCullingVolume m_casterVolume = new MaxShadowCasterCullingVolume();
        private readonly Vector3[] m_receiverWorldCorners = new Vector3[8];
        private static readonly ProfilerMarker s_executeMarker = new ProfilerMarker("MaxSRP.Shadow.Execute");
        private static readonly ProfilerMarker s_volumeMarker = new ProfilerMarker("MaxSRP.Shadow.CasterVolume");
        private static readonly ProfilerMarker s_cullMarker = new ProfilerMarker("MaxSRP.Shadow.Cull");
        private static readonly ProfilerMarker s_drawMarker = new ProfilerMarker("MaxSRP.Shadow.DrawRenderers");
        private static readonly ProfilerMarker s_submitMarker = new ProfilerMarker("MaxSRP.Shadow.Submit");
#if UNITY_EDITOR
        public static bool? ReuseOverride;
        private static readonly HashSet<MaxShadowCasterPass> s_livePasses = new HashSet<MaxShadowCasterPass>();
        public static void InvalidateCaches()
        {
            foreach (MaxShadowCasterPass pass in s_livePasses) pass.Invalidate(null);
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        }
        public static string ReuseStatus
        {
            get
            {
                foreach (MaxShadowCasterPass pass in s_livePasses)
                {
                    Camera camera = Camera.main;
                    CameraCache state;
                    if (!camera || !pass.m_cameras.TryGetValue(camera.GetInstanceID(), out state)) continue;
                    var text = new System.Text.StringBuilder("Near 0/1: real-time; far 2/3: ");
                    for (int i = 2; i < 4; ++i)
                    {
                        var c = state.Policy.GetCascade(i);
                        text.Append('C').Append(i).Append(" age=").Append(c.AgeFrames)
                            .Append(" error=").Append(c.EstimatedErrorTexels.ToString("F3"))
                            .Append(" texel ").Append(state.Reasons[i]).Append(i == 2 ? "; " : "");
                    }
                    return text.ToString();
                }
                return "No main-camera shadow sample yet.";
            }
        }
        static MaxShadowCasterPass()
        {
            UnityEditor.EditorApplication.hierarchyChanged += InvalidateCaches;
            UnityEditor.EditorApplication.projectChanged += InvalidateCaches;
            UnityEditor.Undo.undoRedoPerformed += InvalidateCaches;
            UnityEditor.ObjectChangeEvents.changesPublished += OnObjectChanges;
        }
        private static void OnObjectChanges(ref UnityEditor.ObjectChangeEventStream changes)
        {
            if (changes.length > 0) InvalidateCaches();
        }
        public static bool CaptureDiagnostics, CaptureGeometry;
        public static event Action<ShadowCpuSample> CpuSampleRecorded;
        public static RenderTexture DiagnosticAtlas { get; private set; }
        public static readonly Matrix4x4[] DiagnosticShadowViews = new Matrix4x4[4];
        public static readonly Vector3[][] DiagnosticReceiverCorners = {
            new Vector3[8], new Vector3[8], new Vector3[8], new Vector3[8] };
        public static readonly float[] DiagnosticPadding = new float[4];
        public static readonly RectInt[] DiagnosticTiles = new RectInt[4];
        public struct ShadowCpuSample
        {
            public int CameraId;
            public bool CacheEnabled;
            public double TotalMilliseconds, SetupMilliseconds, VolumeMilliseconds;
            public double CullMilliseconds, DrawMilliseconds, SubmitMilliseconds;
            public int UpdatedCascades, ReusedCascades, FallbackCascadeCount;
        }
        private ShadowCpuSample m_cpuSample;
        private static double ElapsedMilliseconds(long start) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - start) * (1000.0 / System.Diagnostics.Stopwatch.Frequency);
#endif
        public MaxShadowCasterPass(CascadeSettings settings, MaxProceduralSkyboxPass.AtmosphereSettings atmosphere, Shader tileClearShader)
        {
            m_settings = settings;
            m_atmosphereSettings = atmosphere;
            try
            {
                m_cascade = new Cascade(settings);
                if (tileClearShader && tileClearShader.isSupported)
                    m_tileClear = new Material(tileClearShader) { hideFlags = HideFlags.HideAndDontSave };
#if UNITY_EDITOR
                s_livePasses.Add(this);
#endif
            }
            catch { Dispose(); throw; }
        }
        public void Invalidate(Camera camera)
        {
            foreach (CameraCache state in m_cameras.Values)
                if (!camera || state.Camera == camera)
                {
                    state.Policy.InvalidateAll("explicit invalidation");
                    state.Initialized = false;
                }
        }
        private CameraCache PrepareCamera(Camera camera, SunLightData sun, bool reuse, double now)
        {
            m_retired.Clear();
            foreach (var item in m_cameras)
                if (!item.Value.Camera || now - item.Value.LastSeen > 120.0) m_retired.Add(item.Key);
            foreach (int key in m_retired)
            {
                CameraCache retired = m_cameras[key];
                MaxRenderResources.UnbindTexture("_MainShadowAtlas", retired.Atlas);
                MaxRenderResources.Destroy(ref retired.Atlas);
                m_cameras.Remove(key);
            }
            int id = camera.GetInstanceID();
            if (!m_cameras.TryGetValue(id, out CameraCache state))
            {
                state = new CameraCache { Camera = camera };
                m_cameras.Add(id, state);
            }
            Matrix4x4 projection = CameraViewport.GetProjection(camera);
            Vector2Int size = CameraViewport.GetRenderSize(camera);
            Rect viewport = CameraViewport.GetViewport(camera);
            if (!state.HasKey || state.Camera != camera || state.Projection != projection || state.Size != size ||
                state.Viewport != viewport || state.Output != camera.targetTexture || state.Sun != sun.Source ||
                state.CullingMask != camera.cullingMask || state.SceneHandle != camera.gameObject.scene.handle ||
                state.ReuseEnabled != reuse || now - state.LastSeen > 2.0)
            {
                state.Policy.InvalidateAll("camera/output/light/mode changed");
                state.Initialized = false;
            }
            state.Camera = camera; state.Projection = projection; state.Size = size;
            state.Viewport = viewport; state.Output = camera.targetTexture; state.Sun = sun.Source;
            state.CullingMask = camera.cullingMask; state.SceneHandle = camera.gameObject.scene.handle;
            state.ReuseEnabled = reuse; state.LastSeen = now; state.HasKey = true; ++state.Sequence;
            return state;
        }
        private void EnsureShadowAtlas(CameraCache state)
        {
            int resolution = Mathf.Min(MaxAtlasSize, Mathf.ClosestPowerOfTwo(SystemInfo.maxTextureSize));
            if (resolution > SystemInfo.maxTextureSize) resolution /= 2;
            if (!state.Atlas || state.Atlas.width != resolution || !state.Atlas.IsCreated())
            {
                var descriptor = new RenderTextureDescriptor(resolution, resolution, RenderTextureFormat.Depth, 24)
                    { shadowSamplingMode = ShadowSamplingMode.CompareDepths, msaaSamples = 1, sRGB = false };
                var replacement = new RenderTexture(descriptor) {
                    name = "Directional Shadow Atlas - " + state.Camera.GetInstanceID(),
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave };
                if (!replacement.Create()) { MaxRenderResources.Destroy(ref replacement); throw new InvalidOperationException("Cannot allocate directional shadow atlas"); }
                MaxRenderResources.UnbindTexture("_MainShadowAtlas", state.Atlas);
                MaxRenderResources.Destroy(ref state.Atlas);
                state.Atlas = replacement;
                state.Policy.InvalidateAll("atlas recreated"); state.Initialized = false;
            }
            m_shadowAtlas = state.Atlas;
            int cellSize = resolution / 2;
            for (int i = 0; i < 4; ++i)
            {
                int requested = (int)m_settings.GetCascade(i).MapResolution;
                int tileSize = Mathf.Min(requested >= 2048 ? 2048 : requested >= 1024 ? 1024 : 512, cellSize);
                var rect = new RectInt((i % 2) * cellSize + TileBorder, (i / 2) * cellSize + TileBorder,
                    tileSize - 2 * TileBorder, tileSize - 2 * TileBorder);
                m_tileRects[i] = rect;
                Shader.SetGlobalVector("_ShadowAtlasTile" + i, new Vector4((float)rect.width / resolution,
                    (float)rect.height / resolution, (float)rect.x / resolution, (float)rect.y / resolution));
                Shader.SetGlobalFloat("_ShadowResolution" + i, rect.width);
            }
            Shader.SetGlobalTexture("_MainShadowAtlas", m_shadowAtlas);
            Shader.SetGlobalInteger("_PcssCascadeCount", Mathf.Clamp(m_settings.PcssCascadeCount, 0, 4));
        }
        public bool Execute(ScriptableRenderContext context, Camera camera, SunLightData sun)
        {
#if UNITY_EDITOR
            bool capture = CaptureDiagnostics;
            long start = capture ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (capture) m_cpuSample = new ShadowCpuSample { CameraId = camera.GetInstanceID() };
#endif
            bool rendered;
            using (s_executeMarker.Auto()) rendered = ExecuteInternal(context, camera, sun);
#if UNITY_EDITOR
            if (capture && rendered)
            {
                m_cpuSample.TotalMilliseconds = ElapsedMilliseconds(start);
                m_cpuSample.SetupMilliseconds = m_cpuSample.TotalMilliseconds - m_cpuSample.CullMilliseconds -
                    m_cpuSample.DrawMilliseconds - m_cpuSample.SubmitMilliseconds;
                DiagnosticAtlas = m_shadowAtlas;
                CpuSampleRecorded?.Invoke(m_cpuSample);
            }
#endif
            return rendered;
        }
        private bool ExecuteInternal(ScriptableRenderContext context, Camera camera, SunLightData sun)
        {
            if (!sun.CastsShadows) { Invalidate(camera); return false; }
            bool reuse = m_settings.ShortTermReuse;
#if UNITY_EDITOR
            reuse = ReuseOverride ?? reuse;
#endif
            reuse = reuse && m_tileClear && (camera.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView);
            double now = Time.realtimeSinceStartupAsDouble;
            CameraCache state = PrepareCamera(camera, sun, reuse, now);
            EnsureShadowAtlas(state);
#if UNITY_EDITOR
            if (CaptureDiagnostics) m_cpuSample.CacheEnabled = reuse;
#endif
            float angularRadius = Mathf.Clamp(m_atmosphereSettings._SunDiskAngle, 0.001f, 89.0f) * Mathf.Deg2Rad;
            Shader.SetGlobalFloat("_ShadowLightTanAngularRadius", Mathf.Tan(angularRadius));
            Vector3 lightDir = -(Vector3)sun.Direction;
            if (!m_cascade.UpdateCascade(camera, ref lightDir)) return false;
            if (!m_shadowCamera)
            {
                var shadowObject = new GameObject("MaxSRP Shadow Camera") { hideFlags = HideFlags.HideAndDontSave };
                m_shadowCamera = shadowObject.AddComponent<Camera>();
            }
            m_shadowCamera.CopyFrom(camera);
            m_shadowCamera.enabled = false; m_shadowCamera.targetTexture = null; m_shadowCamera.rect = new Rect(0, 0, 1, 1);
            m_shadowCamera.orthographic = true;
            m_shadowCamera.ResetProjectionMatrix(); m_shadowCamera.ResetWorldToCameraMatrix(); m_shadowCamera.ResetCullingMatrix();
            bool fullClear = !reuse || !state.Initialized;
            if (fullClear)
            {
                CommandBuffer clear = CommandBufferPool.Get("Shadow Atlas Clear");
                try
                {
                    clear.SetRenderTarget(m_shadowAtlas);
                    clear.SetViewport(new Rect(0, 0, m_shadowAtlas.width, m_shadowAtlas.height));
                    clear.ClearRenderTarget(true, false, Color.clear);
                    context.ExecuteCommandBuffer(clear);
                }
                finally { CommandBufferPool.Release(clear); }
                state.Initialized = true;
                state.Policy.InvalidateAll("full atlas clear");
            }
            for (int i = 0; i < 4; ++i)
            {
                RectInt tile = m_tileRects[i];
                int resolution = tile.width;
                if (i == 0) Shader.SetGlobalFloat("_ShadowTexelResolution", resolution);
                float halfDepth = m_cascade.ConfigCameraToShadowSpace(m_shadowCamera, lightDir, i, m_cascade.m_OrthoDistance, resolution);
                Matrix4x4 view = m_shadowCamera.worldToCameraMatrix;
                Matrix4x4 vp = GL.GetGPUProjectionMatrix(m_shadowCamera.projectionMatrix, true) * view;
                float width = m_cascade.m_OrthoWidths[i];
                ShadowSettings settings = m_settings.GetCascade(i);
                float worldTexel = width / resolution;
                float kernel = i < Mathf.Clamp(m_settings.PcssCascadeCount, 0, 4) ? Mathf.Max(0, settings.MaxPenumbraRadius) : 0;
                float support = kernel + 2.0f * Mathf.Sqrt(2.0f) * worldTexel;
                m_cascade.GetReceiverWorldCorners(camera, i, m_receiverWorldCorners);
                string reason = i < 2 ? "near: real-time" : "A: real-time";
                int maxFrames = i == 2 ? m_settings.Cascade2MaxAgeFrames : m_settings.Cascade3MaxAgeFrames;
                bool canReuse = reuse && i >= 2 && Mathf.Abs(sun.Direction.y) >= 0.05f &&
                    state.Policy.CanReuse(i, m_receiverWorldCorners, (Vector3)sun.Direction, state.Sequence,
                        Mathf.Max(2, maxFrames),
                        Mathf.Max(0.01f, m_settings.ReuseMaxErrorTexels), support, Mathf.Abs(settings.ShadingPointNormalBias), resolution, out reason);
                if (canReuse)
                {
                    var cached = state.Policy.GetCascade(i);
                    vp = cached.VP; view = cached.View; width = cached.Width; halfDepth = cached.HalfDepthRange;
#if UNITY_EDITOR
                    if (CaptureDiagnostics) ++m_cpuSample.ReusedCascades;
#endif
                }
                Shader.SetGlobalMatrix("_ShadowVPMatrix" + i, vp);
                Shader.SetGlobalFloat("_ShadowDepthHalfRange" + i, halfDepth);
                Shader.SetGlobalFloat("_OrthoWidth" + i, width);
                state.Reasons[i] = reason;
#if UNITY_EDITOR
                if (CaptureGeometry)
                {
                    DiagnosticShadowViews[i] = view;
                    Array.Copy(m_receiverWorldCorners, DiagnosticReceiverCorners[i], 8);
                    DiagnosticPadding[i] = support + Mathf.Abs(settings.ShadingPointNormalBias);
                    DiagnosticTiles[i] = tile;
                }
#endif
                if (canReuse) continue;
                CommandBuffer cmd = CommandBufferPool.Get("ShadowMap");
                try
                {
                    context.SetupCameraProperties(m_shadowCamera);
                    cmd.SetGlobalMatrix("_ShadowCasterVP", vp);
                    cmd.SetRenderTarget(m_shadowAtlas);
                    if (!fullClear)
                    {
                        // SV_Depth drawing is viewport-bounded; a native depth clear may erase every tile.
                        cmd.SetViewport(new Rect(tile.x - TileBorder, tile.y - TileBorder,
                            tile.width + 2 * TileBorder, tile.height + 2 * TileBorder));
                        cmd.DrawProcedural(Matrix4x4.identity, m_tileClear, 0, MeshTopology.Triangles, 3);
                    }
                    cmd.SetViewport(new Rect(tile.x, tile.y, tile.width, tile.height));
                    context.ExecuteCommandBuffer(cmd);
                    if (!m_shadowCamera.TryGetCullingParameters(out var parameters)) { state.Policy.Invalidate(i, "culling unavailable"); continue; }
#if UNITY_EDITOR
                    long volumeStart = CaptureDiagnostics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
#endif
                    float guard = reuse && i >= 2 ? Mathf.Clamp(m_settings.ReuseGuardBandTexels, 2, 128) * worldTexel : 0;
                    using (s_volumeMarker.Auto())
                        if (!m_casterVolume.TryApply(ref parameters, m_receiverWorldCorners, view,
                            support + Mathf.Abs(settings.ShadingPointNormalBias) + guard))
                        {
#if UNITY_EDITOR
                            if (CaptureDiagnostics) ++m_cpuSample.FallbackCascadeCount;
#endif
                        }
#if UNITY_EDITOR
                    if (CaptureDiagnostics) m_cpuSample.VolumeMilliseconds += ElapsedMilliseconds(volumeStart);
                    long cullStart = CaptureDiagnostics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
#endif
                    CullingResults results;
                    using (s_cullMarker.Auto()) results = context.Cull(ref parameters);
#if UNITY_EDITOR
                    if (CaptureDiagnostics) m_cpuSample.CullMilliseconds += ElapsedMilliseconds(cullStart);
                    long drawStart = CaptureDiagnostics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
#endif
                    var sorting = new SortingSettings(m_shadowCamera);
                    var drawing = new DrawingSettings(m_shaderTagID, sorting);
                    var filtering = FilteringSettings.defaultValue;
                    using (s_drawMarker.Auto()) context.DrawRenderers(results, ref drawing, ref filtering);
#if UNITY_EDITOR
                    if (CaptureDiagnostics) m_cpuSample.DrawMilliseconds += ElapsedMilliseconds(drawStart);
                    long submitStart = CaptureDiagnostics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
#endif
                    using (s_submitMarker.Auto()) context.Submit();
#if UNITY_EDITOR
                    if (CaptureDiagnostics) { m_cpuSample.SubmitMilliseconds += ElapsedMilliseconds(submitStart); ++m_cpuSample.UpdatedCascades; }
#endif
                    state.Policy.Record(i, vp, view, width, halfDepth, (Vector3)sun.Direction,
                        m_receiverWorldCorners, guard, support, Mathf.Abs(settings.ShadingPointNormalBias), state.Sequence);
                }
                finally { CommandBufferPool.Release(cmd); }
            }
            return true;
        }
        public void Dispose()
        {
#if UNITY_EDITOR
            if (DiagnosticAtlas == m_shadowAtlas) DiagnosticAtlas = null;
            s_livePasses.Remove(this);
#endif
            if (m_shadowCamera)
            {
                GameObject shadowObject = m_shadowCamera.gameObject; m_shadowCamera = null;
                MaxRenderResources.Destroy(ref shadowObject);
            }
            foreach (CameraCache state in m_cameras.Values)
            {
                MaxRenderResources.UnbindTexture("_MainShadowAtlas", state.Atlas);
                MaxRenderResources.Destroy(ref state.Atlas);
            }
            m_cameras.Clear(); m_shadowAtlas = null;
            MaxRenderResources.Destroy(ref m_tileClear);
        }
    }

    public class Cascade
    {
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

        public Cascade(CascadeSettings settings)
        { 
            this.m_cascadeSettings = settings;

            m_OrthoDistance = settings.ShadowDistance;

            Shader.SetGlobalFloat(ShaderProperties._OrthoDistance, m_OrthoDistance);
        }

        public bool UpdateCascade(Camera mainCamera, ref Vector3 lightDir)
        {
            // ShadowDistance is the maximum camera-forward depth, capped by the camera far plane.
            m_OrthoDistance = m_cascadeSettings.ShadowDistance;
            Shader.SetGlobalFloat(ShaderProperties._OrthoDistance, m_OrthoDistance);
            float shadowNear = mainCamera.nearClipPlane;
            float shadowFar = Mathf.Min(mainCamera.farClipPlane, m_OrthoDistance);
            float span = shadowFar - shadowNear;
            Shader.SetGlobalVector(ShaderProperties._CascadeDepthRange,
                new Vector4(shadowNear, shadowFar, span > 0 ? 1.0f / span : 0.0f, 0.0f));
            if (span <= 0) return false;

            // UE-style geometric slice lengths: 1, exponent, exponent^2, ...
            float exponent = Mathf.Clamp(m_cascadeSettings.DistributionExponent, 1.0f, 4.0f);
            float total = 0.0f;
            float weight = 1.0f;
            for (int i = 0; i < 4; ++i) { total += weight; weight *= exponent; }
            float cumulative = 0.0f;
            weight = 1.0f;
            for (int i = 0; i < 4; ++i)
            {
                cumulative += weight / total;
                m_splitEnds[i] = cumulative;
                weight *= exponent;
            }
            m_splitEnds[3] = 1.0f;
            Shader.SetGlobalFloat(ShaderProperties._Split0, m_splitEnds[0]);
            Shader.SetGlobalFloat(ShaderProperties._Split1, m_splitEnds[1]);
            Shader.SetGlobalFloat(ShaderProperties._Split2, m_splitEnds[2]);
            Shader.SetGlobalFloat(ShaderProperties._Split3, m_splitEnds[3]);
            float blend0 = CascadeTransitionWidth(0.0f, m_splitEnds[0], m_splitEnds[1], m_cascadeSettings.CascadeBlendFraction);
            float blend1 = CascadeTransitionWidth(m_splitEnds[0], m_splitEnds[1], m_splitEnds[2], m_cascadeSettings.CascadeBlendFraction);
            float blend2 = CascadeTransitionWidth(m_splitEnds[1], m_splitEnds[2], 1.0f, m_cascadeSettings.CascadeBlendFraction);
            m_receiverStarts[0] = 0.0f;
            m_receiverStarts[1] = m_splitEnds[0] - blend0;
            m_receiverStarts[2] = m_splitEnds[1] - blend1;
            m_receiverStarts[3] = m_splitEnds[2] - blend2;
            m_receiverCornersReady = false;
            Shader.SetGlobalVector("_CascadeBlendWidths", new Vector4(blend0, blend1, blend2, 0.0f));
            float fadeWidth = span * (1.0f - m_splitEnds[2]) * Mathf.Clamp(m_cascadeSettings.DistanceFadeFraction, 0.0f, 0.5f);
            Shader.SetGlobalVector("_ShadowDistanceFade", new Vector4(shadowFar - fadeWidth, shadowFar, 0.0f, 0.0f));
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
            m_cascadeSpheres[3] = StableCascadeSphere(m_f3Near, m_f3Far, cameraToWorld);

            m_cascadeSettings.Set();
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
            Vector4 sphere = m_cascadeSpheres[cascade];
            Vector3 center = new Vector3(sphere.x, sphere.y, sphere.z);
            float diameter = 2.0f * sphere.w;
            // Include kernel support and normal offset before snapping the center.
            ShadowSettings settings = m_cascadeSettings.GetCascade(cascade);
            float padding = Mathf.Max(settings.MaxPenumbraRadius, 0.0f) + Mathf.Abs(settings.ShadingPointNormalBias);
            float len = (Mathf.Max(0.001f, diameter) + 2.0f * padding) * resolution / (resolution - 2.0f);
            m_OrthoWidths[cascade] = len;
            float disPerPix = len / resolution;

            Matrix4x4 toShadowViewInv = Matrix4x4.TRS(Vector3.zero, LightRotation(lightDir), Vector3.one);
            Matrix4x4 toShadowView = toShadowViewInv.inverse;
            if (cascade == 0)
            {
                Shader.SetGlobalMatrix("_ShadowNoiseWorldToLight", toShadowView);
                // Shadow texels are too coarse as random-rotation cells at close range.
                // Use subpixel cells in close views so a quad does not reuse the same random set.
                // This changes sampling phase density, not the stable shadow projection.
                Shader.SetGlobalFloat("_ShadowNoiseWorldTexelSize", disPerPix / 128.0f);
            }

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
