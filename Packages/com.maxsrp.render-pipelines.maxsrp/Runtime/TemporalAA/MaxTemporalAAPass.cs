using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace MaxSRP
{
    public enum TemporalAAJitterSequence { Halton8 = 0, Halton16 = 1 }
    public enum TemporalAAQuality { PlatformDefault = 0, High = 1, Low = 2 }

    [Serializable]
    public sealed class TemporalAASettings
    {
        public bool Enabled = true;
        [FormerlySerializedAs("Algorithm")]
        [Tooltip("Platform Default uses Gen4 High on PC and Gen4 Low on Mobile.")]
        public TemporalAAQuality Quality;

        [Header("History")]
        [Range(0.0f, 1.0f), Tooltip("Base current-frame contribution for Gen4 High and Low before motion, luminance, and history validity are considered.")]
        public float Gen4CurrentFrameWeight = 0.04f;

        [Header("Projection Jitter")]
        [Tooltip("Number of subpixel positions in the native Gen4 jitter sequence before it repeats.")]
        public TemporalAAJitterSequence JitterSequence = TemporalAAJitterSequence.Halton8;
        [Range(0.0f, 1.0f), Tooltip("Scale of the native Gen4 jitter offsets. Zero disables projection jitter. One uses the reference half-pixel window.")]
        public float JitterSpread = 1.0f;

        // Retain the public and serialized fields used by existing integrations.
        [HideInInspector]
        public float DepthToleranceMeters = 0.01f;
        [HideInInspector]
        public float DepthToleranceRelative = 0.005f;

        [Header("Output")]
        [Range(0.0f, 1.0f), Tooltip("Sharpen the resolved output before post-processing. The color history retains the unsharpened result.")]
        public float Sharpness = 0.0f;

        internal TemporalAAQuality GetQuality(MaxRenderPlatform platform)
        {
            if (Quality == TemporalAAQuality.High || Quality == TemporalAAQuality.Low)
                return Quality;
            return platform == MaxRenderPlatform.Mobile ? TemporalAAQuality.Low : TemporalAAQuality.High;
        }

    }

    // Shared native-resolution TAA resolve for MaxSRP's HDR and motion buffers.
    // See Documentation/TemporalAA.md for the shared raster/motion/history contract.
    public sealed class MaxTemporalAAPass : MaxRenderPass
    {
#if UNITY_EDITOR
        public static Action<CommandBuffer, Camera, RenderTexture, RenderTexture, RenderTexture> EditorResolveCapture;
#endif
        private static readonly GlobalKeyword s_velocityUInt = GlobalKeyword.Create("MAX_TAA_VELOCITY_UINT");
        private readonly Material m_material;
        private readonly RenderTextureFormat m_metadataFormat;
        private readonly GraphicsFormat m_motionFormat;
        private readonly bool m_canPackHistory;
        private readonly bool m_usesDynamicHistory;
        private readonly bool m_quantizeHistory;
        private readonly FilterMode m_historyFilter;
        private Dictionary<int, CameraHistory> m_histories = new Dictionary<int, CameraHistory>();
        private readonly List<int> m_dead = new List<int>();
        public sealed class CameraHistory : IDisposable
        {
            public readonly Camera Camera;
            public readonly RenderTexture[] Colors = new RenderTexture[2];
            public readonly RenderTexture[] Metadata = new RenderTexture[2];
            public readonly RenderTargetIdentifier[] ResolveTargets = new RenderTargetIdentifier[2];
            public RenderTexture Motion;
            internal bool PreserveAlpha, DynamicInColor;
            public Matrix4x4 Projection, ViewProjection, PreviousVP, PreviousView;
            internal Matrix4x4 UnjitteredProjection, PreviousUnjitteredProjection, ClipToPreviousClip;
            public Vector2 Jitter, PreviousJitter;
            public Vector3 Position;
            public Quaternion Rotation;
            public int Index, Sample, Width, Height;
            internal uint FrameIndex;
            internal int JitterIndex, NextJitterIndex;
            internal bool CameraCutPending;
            public int CloudRevision;
            public bool Valid;
            public DepthTextureMode AddedDepthModes;
            public CameraHistory(Camera camera) { Camera = camera; }
            public void Dispose()
            {
                for (int i = 0; i < 2; ++i)
                {
                    MaxRenderResources.UnbindTexture("_SceneColorHDR", Colors[i]);
                    MaxRenderResources.Destroy(ref Colors[i]);
                    MaxRenderResources.Destroy(ref Metadata[i]);
                }
                MaxRenderResources.Destroy(ref Motion);
                if (Camera != null) Camera.depthTextureMode &= ~AddedDepthModes;
                AddedDepthModes = 0;
                Valid = false;
            }
        }

        public MaxTemporalAAPass(Shader shader, TemporalAAQuality quality = TemporalAAQuality.High,
            MaxRenderPlatform platform = MaxRenderPlatform.PC)
        {
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("MaxSRP requires the serialized Hidden/MaxSRP/TemporalAA shader.");
            m_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            m_usesDynamicHistory = quality != TemporalAAQuality.Low;
            m_material.SetKeyword(new LocalKeyword(shader, "MAX_TAA_GEN4_LOW"), !m_usesDynamicHistory);
            m_metadataFormat = SystemInfo.IsFormatSupported(GraphicsFormat.R16G16_SFloat, FormatUsage.Render) &&
                SystemInfo.IsFormatSupported(GraphicsFormat.R16G16_SFloat, FormatUsage.Linear)
                ? RenderTextureFormat.RGHalf : RenderTextureFormat.ARGBHalf;
            m_canPackHistory = MaxRenderResources.SupportsPackedHdrTarget();
            GraphicsDeviceType api = SystemInfo.graphicsDeviceType;
            if (CanRenderAndSample(GraphicsFormat.R16G16_UNorm))
                m_motionFormat = GraphicsFormat.R16G16_UNorm;
            else if (api == GraphicsDeviceType.OpenGLES3 && Application.platform == RuntimePlatform.Android)
                // UE 5.8.4 VelocityRendering.cpp uses core RG16UI on Android GLES.
                m_motionFormat = GraphicsFormat.R16G16_UInt;
            else
                m_motionFormat = CanRenderAndSample(GraphicsFormat.R32G32_SFloat)
                    ? GraphicsFormat.R32G32_SFloat : GraphicsFormat.R16G16B16A16_SFloat;
            // Keep the legacy texture filter on APIs without verified inline samplers.
            m_historyFilter = api == GraphicsDeviceType.Direct3D11 || api == GraphicsDeviceType.Direct3D12 ||
                api == GraphicsDeviceType.Vulkan || api == GraphicsDeviceType.Metal ? FilterMode.Point : FilterMode.Bilinear;
            // UE Gen4 MainCS applies stochastic quantization. Android/iOS default to MainPS.
            m_quantizeHistory = platform == MaxRenderPlatform.PC;
        }

        public CameraHistory Begin(Camera camera, TemporalAASettings settings)
        {
            bool eligible = Application.isPlaying && settings.Enabled &&
                camera.cameraType == CameraType.Game && !camera.stereoEnabled &&
                (camera.clearFlags == CameraClearFlags.Skybox || camera.clearFlags == CameraClearFlags.Color);
            int id = camera.GetInstanceID();
            if (!eligible)
            {
                if (m_histories.TryGetValue(id, out CameraHistory disabled))
                {
                    disabled.Dispose();
                    m_histories.Remove(id);
                }
                return null;
            }
            if (!m_histories.TryGetValue(id, out CameraHistory state))
                m_histories.Add(id, state = new CameraHistory(camera));
            Vector2Int size = CameraViewport.GetRenderSize(camera);
            bool preserveAlpha = MaxRenderResources.RequiresSceneAlpha(camera);
            bool dynamicInColor = m_usesDynamicHistory && !preserveAlpha;
            RenderTextureFormat historyFormat = !m_usesDynamicHistory && !preserveAlpha && m_canPackHistory
                ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.ARGBHalf;
            if (!TargetsValid(state, size, historyFormat, preserveAlpha, dynamicInColor))
            {
                state.Dispose();
                state.Width = size.x; state.Height = size.y;
                state.PreserveAlpha = preserveAlpha;
                state.DynamicInColor = dynamicInColor;
                try
                {
                    for (int i = 0; i < 2; ++i)
                        // History reconstruction uses its own explicit linear sampler.
                        state.Colors[i] = Create(size, historyFormat, "TAA History " + i, m_historyFilter);
                    state.Motion = CreateMotionTarget(size, m_motionFormat, "Motion Vectors");
                }
                catch { state.Dispose(); throw; }
            }
            Matrix4x4 projection = CameraViewport.GetProjection(camera);
            state.AddedDepthModes |= (DepthTextureMode.MotionVectors | DepthTextureMode.Depth) & ~camera.depthTextureMode;
            camera.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;
            // UE 5.8 SceneVisibility.cpp discards previous view info for large camera movement.
            if (Vector3.Distance(camera.transform.position, state.Position) > Mathf.Max(2.0f, camera.nearClipPlane * 20) ||
                Quaternion.Angle(camera.transform.rotation, state.Rotation) > 45)
                state.Valid = false;
            if (!state.Valid) state.Sample = 0;
            // UE advances the view-state index before sampling, and only a camera cut
            // or sequence wrap restarts it. AO still gets MaxSRP's convergence count.
            int sampleCount = settings.JitterSequence == TemporalAAJitterSequence.Halton16 ? 16 : 8;
            state.NextJitterIndex = state.JitterIndex + 1;
            if (state.CameraCutPending || state.NextJitterIndex >= sampleCount)
                state.NextJitterIndex = 0;
            state.Jitter = GetProjectionJitter(camera.orthographic, state.NextJitterIndex, size, settings.JitterSpread);
            Matrix4x4 gpu = GL.GetGPUProjectionMatrix(projection, true);
            Matrix4x4 view = camera.worldToCameraMatrix;
            state.UnjitteredProjection = gpu;
            state.ViewProjection = gpu * view;
            state.ClipToPreviousClip = Matrix4x4.identity;
            if (state.Valid)
            {
                // UE SceneView.cpp builds ClipToPrevClip with translated views.
                // Use a common origin near the camera before inverting either VP.
                Vector3 origin = camera.transform.position;
                Matrix4x4 relativeView = TranslateViewOrigin(view, origin);
                Matrix4x4 relativePreviousView = TranslateViewOrigin(state.PreviousView, origin);
                state.ClipToPreviousClip = state.PreviousUnjitteredProjection * relativePreviousView *
                    (gpu * relativeView).inverse;
            }
            // Jitter is defined in texture UV (top-left origin on D3D), not camera NDC.
            float y = SystemInfo.graphicsUVStartsAtTop ? -state.Jitter.y : state.Jitter.y;
            Matrix4x4 jittered = gpu;
            jittered.SetRow(0, gpu.GetRow(0) + 2 * state.Jitter.x * gpu.GetRow(3));
            jittered.SetRow(1, gpu.GetRow(1) + 2 * y * gpu.GetRow(3));
            state.Projection = jittered;
            if (!state.Valid)
            {
                state.PreviousVP = state.ViewProjection;
                state.PreviousView = camera.worldToCameraMatrix;
                state.PreviousJitter = state.Jitter;
            }
            return state;
        }

        public static void Bind(ScriptableRenderContext context, Camera camera, CameraHistory state)
        {
            ShaderBindings.SetPerCameraShaderVariables(context, camera, true, state == null ? (Matrix4x4?)null : state.Projection);
            using (var commands = new PooledCommandBuffer("Temporal Camera Contract"))
            {
                CommandBuffer cmd = commands.Buffer;
                cmd.SetGlobalVector("_MaxTaaJitter", state == null ? Vector4.zero :
                    new Vector4(state.Jitter.x, state.Jitter.y, state.PreviousJitter.x, state.PreviousJitter.y));
                // Procedural terrain implements its previous CDLOD deformation
                // itself, but shares this camera's projection/history lifecycle.
                Matrix4x4 terrainVP = state == null
                    ? GL.GetGPUProjectionMatrix(CameraViewport.GetProjection(camera), true) * camera.worldToCameraMatrix
                    : state.ViewProjection;
                cmd.SetGlobalMatrix("_MaxTerrainCurrentNonJitteredVP", terrainVP);
                cmd.SetGlobalMatrix("_MaxTerrainPreviousNonJitteredVP", state == null ? terrainVP : state.PreviousVP);
                cmd.SetGlobalMatrix("_MaxTerrainPreviousView", state == null ? camera.worldToCameraMatrix : state.PreviousView);
                cmd.SetGlobalFloat("_MaxTerrainMotionValid", state != null && state.Valid ? 1.0f : 0.0f);
                if (state != null)
                {
                    cmd.SetGlobalMatrix("_MaxUnjitteredVP", state.ViewProjection);
                    cmd.SetGlobalMatrix("_MaxPreviousVP", state.PreviousVP);
                    cmd.SetGlobalMatrix("_MaxPreviousView", state.PreviousView);
                }
                context.ExecuteCommandBuffer(cmd);
            }
        }

        public void DrawMotion(ScriptableRenderContext context, Camera camera, CameraHistory state,
            RenderTexture depth, ref CullingResults culling)
        {
            if (state == null) return;
            camera.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;
            using var motion = new GpuPassScope(context, "Object Motion Vectors");
            CommandBuffer setup = motion.Buffer;
            bool useUInt = state.Motion.graphicsFormat == GraphicsFormat.R16G16_UInt;
            if (useUInt) setup.EnableKeyword(s_velocityUInt);
            setup.SetRenderTarget(state.Motion, depth);
            setup.SetGlobalFloat("_MaxVelocityGamma", m_quantizeHistory ? 1f : 0f);
            setup.SetViewport(new Rect(0, 0, state.Width, state.Height));
            // Native Camera-only renderers can omit the object pass. Zero means
            // camera fallback, gated by the GBuffer material capability marker.
            setup.ClearRenderTarget(false, true, Color.clear);
            motion.Enqueue();
            var drawing = new DrawingSettings(new ShaderTagId("MotionVectors"),
                new SortingSettings(camera) { criteria = SortingCriteria.CommonOpaque });
            drawing.perObjectData = PerObjectData.MotionVectors;
            drawing.enableDynamicBatching = false;
            var filtering = new FilteringSettings(RenderQueueRange.opaque);
            try
            {
                context.DrawRenderers(culling, ref drawing, ref filtering);
                if (MaxRenderBridge.HasParticipants(camera))
                {
                    using var commands = new PooledCommandBuffer("MaxTerrain Motion Vectors");
                    CommandBuffer cmd = commands.Buffer;
                    // Keep the jittered geometry raster and current GDepth;
                    // terrain's motion pass uses ZTest Equal and no depth write.
                    cmd.SetRenderTarget(state.Motion, depth);
                    cmd.SetViewport(new Rect(0, 0, state.Width, state.Height));
                    MaxRenderBridge.RecordMotion(cmd, camera);
                    context.ExecuteCommandBuffer(cmd);
                }
            }
            finally
            {
                if (useUInt)
                {
                    motion.Buffer.DisableKeyword(s_velocityUInt);
                    motion.Enqueue();
                }
            }
        }

        public void Resolve(ScriptableRenderContext context, Camera camera, CameraHistory state,
            RenderTexture color, RenderTexture depth, TemporalAASettings settings, int cloudRevision = 0,
            TemporalAADebugView debugView = TemporalAADebugView.Off,
            GraphicsBuffer exposure = null, float exposureMultiplier = 1f) =>
            ResolveOutput(context, camera, state, color, depth, settings, true, cloudRevision, debugView,
                exposure, exposureMultiplier);

        internal RenderTexture ResolveOutput(ScriptableRenderContext context, Camera camera, CameraHistory state,
            RenderTexture color, RenderTexture depth, TemporalAASettings settings, bool requiresSceneColor,
            int cloudRevision, TemporalAADebugView debugView, GraphicsBuffer exposure, float exposureMultiplier)
        {
            if (state == null) return color;
            int next = 1 - state.Index;
            RenderTexture resolved = state.Colors[next];
            bool showDebug = debugView != TemporalAADebugView.Off;
            float sharpness = Mathf.Clamp01(settings.Sharpness);
            // UE Gen4 Main exposes its resolved history directly to later passes.
            bool copyToScene = requiresSceneColor || showDebug || sharpness > 0f ||
                color.filterMode != resolved.filterMode;
            UpdateMetadataTarget(state, next, showDebug);
            using (var commands = new PooledCommandBuffer("Temporal AA"))
            {
                CommandBuffer cmd = commands.Buffer;
                cmd.SetGlobalFloat("_TaaGen4CurrentFrameWeight", Mathf.Clamp01(settings.Gen4CurrentFrameWeight));
                cmd.SetGlobalTexture("_TaaSource", color);
                cmd.SetGlobalTexture("_TaaDepth", depth);
                cmd.SetGlobalTexture("_TaaMotion", state.Motion);
                cmd.SetGlobalFloat("_MaxVelocityGamma", m_quantizeHistory ? 1f : 0f);
                cmd.SetGlobalFloat("_TaaSkyHistoryInvalid", state.CloudRevision != cloudRevision ? 1f : 0f);
                cmd.SetGlobalTexture("_TaaHistory", state.Colors[state.Index]);
                cmd.SetGlobalFloat("_TaaManualExposure", exposureMultiplier);
                // 中间颜色和历史都覆盖相机自己的完整缓冲。
                Rect renderViewport = new Rect(0, 0, state.Width, state.Height);
                cmd.SetGlobalVector("_TaaSize", new Vector4(state.Width, state.Height, 1f / state.Width, 1f / state.Height));
                bool orthographicSkyMoved = camera.orthographic &&
                    Quaternion.Angle(camera.transform.rotation, state.Rotation) > 0.001f;
                cmd.SetGlobalVector("_TaaSettings", new Vector4(state.Valid ? 1f : 0f,
                    orthographicSkyMoved ? 1f : 0f, state.PreserveAlpha ? 1f : 0f,
                    state.DynamicInColor ? 1f : 0f));
                cmd.SetGlobalMatrix("_TaaClipToPreviousClip", state.ClipToPreviousClip);
                cmd.SetGlobalInteger("_TaaFrameIndex", (int)((state.FrameIndex + 1) & 7));
                // UE Quantization.ush: 6/6/5 mantissa bits for packed RGB, 10 for FP16.
                Vector4 quantizationError = resolved.format == RenderTextureFormat.RGB111110Float
                    ? new Vector4(1f / 64f, 1f / 64f, 1f / 32f, 0f)
                    : new Vector4(1f / 1024f, 1f / 1024f, 1f / 1024f, 0f);
                cmd.SetGlobalVector("_TaaHistoryQuantizationError", m_quantizeHistory ? quantizationError : Vector4.zero);
                int resolvePass = 2;
                if (state.Metadata[next] != null)
                {
                    state.ResolveTargets[0] = resolved;
                    state.ResolveTargets[1] = state.Metadata[next];
                    // A concrete, depthless RT keeps D3D11's MRT binding valid.
                    cmd.SetRenderTarget(state.ResolveTargets, resolved);
                    resolvePass = 0;
                }
                else
                    cmd.SetRenderTarget(resolved);
                cmd.SetViewport(renderViewport);
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.SetAsyncCompilation(cmd, false);
#endif
                bool useUInt = state.Motion.graphicsFormat == GraphicsFormat.R16G16_UInt;
                if (useUInt) cmd.EnableKeyword(s_velocityUInt);
                MaxAutoExposurePass.BindForDraw(cmd, exposure);
                cmd.DrawProcedural(Matrix4x4.identity, m_material, resolvePass, MeshTopology.Triangles, 3);
                if (exposure != null) MaxAutoExposurePass.BindForDraw(cmd, null);
                if (useUInt) cmd.DisableKeyword(s_velocityUInt);
#if UNITY_EDITOR
                EditorResolveCapture?.Invoke(cmd, camera, color, resolved, state.Motion);
#endif
                if (copyToScene)
                {
                    cmd.SetGlobalTexture("_TaaResolved", resolved);
                    cmd.SetGlobalFloat("_TaaSharpness", sharpness);
                    cmd.SetGlobalInteger("_TaaDebugView", (int)debugView);
                    if (showDebug)
                        cmd.SetGlobalTexture("_TaaResolvedMetadata", state.Metadata[next]);
                    cmd.SetRenderTarget(color);
                    cmd.SetViewport(renderViewport);
                    cmd.DrawProcedural(Matrix4x4.identity, m_material, 1, MeshTopology.Triangles, 3);
                }
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.RestoreAsyncCompilation(cmd);
#endif
                context.ExecuteCommandBuffer(cmd);
            }
            state.Index = next;
            state.CloudRevision = cloudRevision;
            state.Valid = true;
            state.PreviousVP = state.ViewProjection;
            state.PreviousView = camera.worldToCameraMatrix;
            state.PreviousUnjitteredProjection = state.UnjitteredProjection;
            state.PreviousJitter = state.Jitter;
            state.Position = camera.transform.position;
            state.Rotation = camera.transform.rotation;
            ++state.Sample;
            ++state.FrameIndex;
            state.JitterIndex = state.NextJitterIndex;
            state.CameraCutPending = false;
            return copyToScene ? color : resolved;
        }

        private void UpdateMetadataTarget(CameraHistory state, int index, bool showDebug)
        {
            if (!showDebug)
            {
                MaxRenderResources.Destroy(ref state.Metadata[index]);
                state.ResolveTargets[1] = default;
                return;
            }

            RenderTextureFormat format = m_metadataFormat;
            RenderTexture metadata = state.Metadata[index];
            if (metadata != null && metadata.IsCreated() && metadata.format == format) return;
            RenderTexture replacement = Create(new Vector2Int(state.Width, state.Height), format,
                "TAA History Metadata " + index, FilterMode.Bilinear);
            MaxRenderResources.Destroy(ref state.Metadata[index]);
            state.Metadata[index] = replacement;
        }

        public void ResetHistory(Camera camera)
        {
            if (camera != null && m_histories.TryGetValue(camera.GetInstanceID(), out CameraHistory state))
            {
                state.Valid = false;
                state.CameraCutPending = true;
            }
        }

        // Pass construction can change independently of the camera's history buffers.
        internal void TransferHistoriesTo(MaxTemporalAAPass replacement)
        {
            var empty = replacement.m_histories;
            replacement.m_histories = m_histories;
            m_histories = empty;
        }

        public override void PruneDestroyedCameras()
        {
            m_dead.Clear();
            foreach (var entry in m_histories) if (entry.Value.Camera == null) m_dead.Add(entry.Key);
            foreach (int id in m_dead) { m_histories[id].Dispose(); m_histories.Remove(id); }
        }
        private bool TargetsValid(CameraHistory state, Vector2Int size, RenderTextureFormat historyFormat,
            bool preserveAlpha, bool dynamicInColor)
        {
            if (state.Width != size.x || state.Height != size.y || state.PreserveAlpha != preserveAlpha ||
                state.DynamicInColor != dynamicInColor || state.Motion == null || !state.Motion.IsCreated() ||
                state.Motion.graphicsFormat != m_motionFormat)
                return false;
            for (int i = 0; i < 2; ++i)
                if (state.Colors[i] == null || !state.Colors[i].IsCreated() || state.Colors[i].format != historyFormat ||
                    (state.Metadata[i] != null && !state.Metadata[i].IsCreated())) return false;
            return true;
        }
        private static bool CanRenderAndSample(GraphicsFormat format) =>
            SystemInfo.IsFormatSupported(format, FormatUsage.Render) &&
            SystemInfo.IsFormatSupported(format, FormatUsage.Sample);

        private static RenderTexture CreateMotionTarget(Vector2Int size, GraphicsFormat format, string name)
        {
            var rt = new RenderTexture(new RenderTextureDescriptor(size.x, size.y, format, 0))
            { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            if (!rt.Create() || rt.graphicsFormat != format)
            {
                MaxRenderResources.Destroy(ref rt);
                throw new InvalidOperationException($"Unable to create {name} with format {format}.");
            }
            return rt;
        }
        private static RenderTexture Create(Vector2Int size, RenderTextureFormat format, string name, FilterMode filter)
        {
            var rt = new RenderTexture(size.x, size.y, 0, format, RenderTextureReadWrite.Linear)
            { name = name, filterMode = filter, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            if (!rt.Create()) { MaxRenderResources.Destroy(ref rt); throw new InvalidOperationException("Unable to create " + name); }
            return rt;
        }
        private static Matrix4x4 TranslateViewOrigin(Matrix4x4 view, Vector3 origin)
        {
            for (int row = 0; row < 4; ++row)
            {
                Vector4 transform = view.GetRow(row);
                view[row, 3] = (float)(transform.w + (double)transform.x * origin.x +
                    (double)transform.y * origin.y + (double)transform.z * origin.z);
            }
            return view;
        }

        private static Vector2 GetProjectionJitter(bool orthographic, int sampleIndex, Vector2Int size, float spread)
        {
            // UE 5.8 SceneVisibility.cpp: native 8/16-sample Gen4 uses a windowed
            // Gaussian for perspective cameras and zero jitter for orthographic cameras.
            if (orthographic) return Vector2.zero;
            int index = sampleIndex + 1;
            float u = Halton(index, 2);
            float theta = 2f * Mathf.PI * Halton(index, 3);
            const float sigma = 0.47f; // r.TemporalAAFilterSize = 1
            const float window = 0.5f / sigma;
            float inWindow = Mathf.Exp(-0.5f * window * window);
            float radius = sigma * Mathf.Sqrt(-2f * Mathf.Log((1f - u) * inWindow + u));
            // UE sample Y is measured downward. Convert it to this backend's texture UV.
            float sampleX = radius * Mathf.Cos(theta);
            float sampleY = radius * Mathf.Sin(theta);
            float textureY = SystemInfo.graphicsUVStartsAtTop ? sampleY : -sampleY;
            return Mathf.Clamp01(spread) * new Vector2(sampleX / size.x, textureY / size.y);
        }

        private static float Halton(int index, int radix)
        {
            float value = 0;
            float inverseRadix = 1f / radix;
            float fraction = inverseRadix;
            while (index > 0)
            {
                value += fraction * (index % radix);
                index /= radix;
                fraction *= inverseRadix;
            }
            return value;
        }
        internal void ClearHistories()
        {
            foreach (CameraHistory state in m_histories.Values) state.Dispose();
            m_histories.Clear();
        }

        protected override void ReleaseResources()
        {
            ClearHistories();
            CoreUtils.Destroy(m_material);
        }
    }
}
