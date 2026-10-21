using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    [Serializable]
    public sealed class TemporalAASettings
    {
        public bool Enabled = true;
        [Range(0.0f, 0.98f)] public float HistoryWeight = 0.94f;
        [Range(0.5f, 2.0f)] public float VarianceGamma = 1.25f;
    }

    // Native-resolution resolve: Salvi variance clipping, YCoCg, Catmull-Rom history.
    // See Documentation/TemporalAA.md for the shared raster/motion/history contract.
    public sealed class MaxTemporalAAPass : IDisposable
    {
#if UNITY_EDITOR
        public static Action<CommandBuffer, Camera, RenderTexture, RenderTexture, RenderTexture> EditorResolveCapture;
#endif
        private readonly Material m_material;
        private readonly Dictionary<int, CameraHistory> m_histories = new Dictionary<int, CameraHistory>();
        private readonly List<int> m_dead = new List<int>();
        public sealed class CameraHistory : IDisposable
        {
            public readonly Camera Camera;
            public readonly RenderTexture[] Colors = new RenderTexture[2];
            public readonly RenderTexture[] Depths = new RenderTexture[2];
            public RenderTexture Motion;
            public Matrix4x4 Projection, ViewProjection, PreviousVP, PreviousView, PreviousProjection;
            public Vector2 Jitter, PreviousJitter;
            public Vector3 Position;
            public Quaternion Rotation;
            public Rect Viewport;
            public int Index, Sample, Width, Height, SettleFrames;
            public int DestinationId;
            public Vector4 SunDirection;
            public Color SunColor;
            public double LastRenderTime;
            public bool Valid;
            public DepthTextureMode AddedDepthModes;
            public CameraHistory(Camera camera) { Camera = camera; }
            public void Dispose()
            {
                for (int i = 0; i < 2; ++i)
                {
                    MaxRenderResources.Destroy(ref Colors[i]);
                    MaxRenderResources.Destroy(ref Depths[i]);
                }
                MaxRenderResources.Destroy(ref Motion);
                if (Camera != null) Camera.depthTextureMode &= ~AddedDepthModes;
                AddedDepthModes = 0;
                Valid = false;
            }
        }

        public MaxTemporalAAPass(Shader shader)
        {
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("MaxSRP requires the serialized Hidden/MaxSRP/TemporalAA shader.");
            m_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        public CameraHistory Begin(Camera camera, TemporalAASettings settings, bool exclusiveOutput, SunLightData sun = default)
        {
            // A multi-projection camera stack cannot share a single camera's motion/depth.
            bool eligible = settings.Enabled && exclusiveOutput && !camera.stereoEnabled &&
                (camera.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView) &&
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
            if (!TargetsValid(state, size))
            {
                state.Dispose();
                state.Width = size.x; state.Height = size.y;
                try
                {
                    for (int i = 0; i < 2; ++i)
                    {
                        state.Colors[i] = Create(size, RenderTextureFormat.ARGBHalf, "TAA History " + i, FilterMode.Bilinear);
                        state.Depths[i] = Create(size, RenderTextureFormat.RGFloat, "TAA Depth Count " + i, FilterMode.Point);
                    }
                    state.Motion = Create(size, RenderTextureFormat.ARGBHalf, "Motion Vectors", FilterMode.Point);
                }
                catch { state.Dispose(); throw; }
            }
            Matrix4x4 projection = CameraViewport.GetProjection(camera);
            state.AddedDepthModes |= (DepthTextureMode.MotionVectors | DepthTextureMode.Depth) & ~camera.depthTextureMode;
            camera.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;
            Rect viewport = CameraViewport.GetViewport(camera);
            int destinationId = camera.targetTexture != null ? camera.targetTexture.GetInstanceID() : -camera.targetDisplay - 1;
            // Long render gaps, cuts, projection/viewport changes and lost native textures invalidate history.
            // Slow view motion and final-output exposure changes do not.
            if (Time.realtimeSinceStartupAsDouble - state.LastRenderTime > 2 ||
                state.Viewport != viewport || MatrixChanged(projection, state.PreviousProjection, 0.00001f) ||
                state.DestinationId != destinationId ||
                Vector3.Distance(camera.transform.position, state.Position) > Mathf.Max(2.0f, camera.nearClipPlane * 20) ||
                Quaternion.Angle(camera.transform.rotation, state.Rotation) > 45)
                state.Valid = false;
            if (!state.Valid) state.Sample = 0;
            state.Jitter = new Vector2((Halton((state.Sample & 7) + 1, 2) - 0.5f) / size.x,
                (Halton((state.Sample & 7) + 1, 3) - 0.5f) / size.y);
            Matrix4x4 gpu = GL.GetGPUProjectionMatrix(projection, true);
            state.ViewProjection = gpu * camera.worldToCameraMatrix;
            if (!state.Valid || MatrixChanged(state.ViewProjection, state.PreviousVP, 0.00001f) ||
                state.SunDirection != sun.Direction || state.SunColor != sun.LinearColor)
                state.SettleFrames = 32;
            state.SunDirection = sun.Direction;
            state.SunColor = sun.LinearColor;
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
            state.Viewport = viewport;
            state.DestinationId = destinationId;
            return state;
        }

        public static void Bind(ScriptableRenderContext context, Camera camera, CameraHistory state)
        {
            ShaderBindings.SetPerCameraShaderVariables(context, camera, true, state == null ? (Matrix4x4?)null : state.Projection);
            var cmd = CommandBufferPool.Get("Temporal Camera Contract");
            try
            {
                cmd.SetGlobalVector("_MaxTaaJitter", state == null ? Vector4.zero :
                    new Vector4(state.Jitter.x, state.Jitter.y, state.PreviousJitter.x, state.PreviousJitter.y));
                cmd.SetGlobalVector("_MaxTemporalPhase", state == null ? Vector4.zero :
                    new Vector4(1, Mathf.Repeat((state.Sample % 1024 + 0.5f) * 0.754877666f, 1),
                        Mathf.Repeat((state.Sample % 1024 + 0.5f) * 0.569840296f, 1), 0));
                if (state != null)
                {
                    cmd.SetGlobalMatrix("_MaxUnjitteredVP", state.ViewProjection);
                    cmd.SetGlobalMatrix("_MaxPreviousVP", state.PreviousVP);
                    cmd.SetGlobalMatrix("_MaxPreviousView", state.PreviousView);
                }
                context.ExecuteCommandBuffer(cmd);
            }
            finally { CommandBufferPool.Release(cmd); }
        }

        public void DrawMotion(ScriptableRenderContext context, Camera camera, CameraHistory state,
            RenderTexture depth, ref CullingResults culling)
        {
            if (state == null) return;
            camera.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;
            var cmd = CommandBufferPool.Get("Object Motion Vectors");
            try
            {
                cmd.SetRenderTarget(state.Motion, depth);
                cmd.SetViewport(new Rect(0, 0, state.Width, state.Height));
                // Native Camera-only renderers can omit the object pass. Zero means
                // camera fallback, gated by the GBuffer material capability marker.
                cmd.ClearRenderTarget(false, true, Color.clear);
                context.ExecuteCommandBuffer(cmd);
            }
            finally { CommandBufferPool.Release(cmd); }
            var drawing = new DrawingSettings(new ShaderTagId("MotionVectors"),
                new SortingSettings(camera) { criteria = SortingCriteria.CommonOpaque });
            drawing.perObjectData = PerObjectData.MotionVectors;
            drawing.enableDynamicBatching = false;
            var filtering = new FilteringSettings(RenderQueueRange.opaque);
            context.DrawRenderers(culling, ref drawing, ref filtering);
        }

        public void Resolve(ScriptableRenderContext context, Camera camera, CameraHistory state,
            RenderTexture color, RenderTexture depth, TemporalAASettings settings)
        {
            if (state == null) return;
            int next = 1 - state.Index;
            var cmd = CommandBufferPool.Get("Temporal AA");
            try
            {
                cmd.SetGlobalTexture("_TaaSource", color);
                cmd.SetGlobalTexture("_TaaDepth", depth);
                cmd.SetGlobalTexture("_TaaMotion", state.Motion);
                cmd.SetGlobalTexture("_TaaHistory", state.Colors[state.Index]);
                cmd.SetGlobalTexture("_TaaPreviousDepth", state.Depths[state.Index]);
                Rect v = state.Viewport;
                float sourceY = SystemInfo.graphicsUVStartsAtTop ? 1 - v.yMax / color.height : v.y / color.height;
                cmd.SetGlobalVector("_TaaSourceRect", new Vector4(v.width / color.width, v.height / color.height,
                    v.x / color.width, sourceY));
                cmd.SetGlobalVector("_TaaSize", new Vector4(state.Width, state.Height, 1f / state.Width, 1f / state.Height));
                cmd.SetGlobalVector("_TaaSettings", new Vector4(state.Valid ? 1 : 0,
                    Mathf.Clamp(settings.HistoryWeight, 0, 0.98f), Mathf.Clamp(settings.VarianceGamma, 0.5f, 2),
                    camera.orthographic && Quaternion.Angle(camera.transform.rotation, state.Rotation) > 0.001f ? 1 : 0));
                cmd.SetRenderTarget(new RenderTargetIdentifier[] { state.Colors[next], state.Depths[next] },
                    new RenderTargetIdentifier(BuiltinRenderTextureType.None));
                cmd.SetViewport(new Rect(0, 0, state.Width, state.Height));
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.SetAsyncCompilation(cmd, false);
#endif
                cmd.DrawProcedural(Matrix4x4.identity, m_material, 0, MeshTopology.Triangles, 3);
#if UNITY_EDITOR
                EditorResolveCapture?.Invoke(cmd, camera, color, state.Colors[next], state.Motion);
#endif
                cmd.SetGlobalTexture("_TaaResolved", state.Colors[next]);
                cmd.SetRenderTarget(color);
                cmd.SetViewport(v);
                cmd.DrawProcedural(Matrix4x4.identity, m_material, 1, MeshTopology.Triangles, 3);
#if UNITY_EDITOR
                UnityEditor.ShaderUtil.RestoreAsyncCompilation(cmd);
#endif
                context.ExecuteCommandBuffer(cmd);
            }
            finally { CommandBufferPool.Release(cmd); }
            state.Index = next;
            state.Valid = true;
            state.PreviousVP = state.ViewProjection;
            state.PreviousView = camera.worldToCameraMatrix;
            state.PreviousProjection = CameraViewport.GetProjection(camera);
            state.PreviousJitter = state.Jitter;
            state.Position = camera.transform.position;
            state.Rotation = camera.transform.rotation;
            state.LastRenderTime = Time.realtimeSinceStartupAsDouble;
            ++state.Sample;
            state.SettleFrames = Mathf.Max(0, state.SettleFrames - 1);
#if UNITY_EDITOR
            // Let an idle Scene view converge, without requiring focus or a second TAA.
            if (!Application.isPlaying && camera.cameraType == CameraType.SceneView && state.SettleFrames > 0)
            {
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                UnityEditor.SceneView.RepaintAll();
            }
#endif
        }

        public void ResetHistory(Camera camera)
        {
            if (camera != null && m_histories.TryGetValue(camera.GetInstanceID(), out CameraHistory state)) state.Valid = false;
        }
        public void PruneDestroyedCameras()
        {
            m_dead.Clear();
            foreach (var entry in m_histories) if (entry.Value.Camera == null) m_dead.Add(entry.Key);
            foreach (int id in m_dead) { m_histories[id].Dispose(); m_histories.Remove(id); }
        }
        private static bool TargetsValid(CameraHistory state, Vector2Int size)
        {
            if (state.Width != size.x || state.Height != size.y || state.Motion == null || !state.Motion.IsCreated()) return false;
            for (int i = 0; i < 2; ++i)
                if (state.Colors[i] == null || !state.Colors[i].IsCreated() || state.Depths[i] == null || !state.Depths[i].IsCreated()) return false;
            return true;
        }
        private static RenderTexture Create(Vector2Int size, RenderTextureFormat format, string name, FilterMode filter)
        {
            var rt = new RenderTexture(size.x, size.y, 0, format, RenderTextureReadWrite.Linear)
            { name = name, filterMode = filter, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            if (!rt.Create()) { MaxRenderResources.Destroy(ref rt); throw new InvalidOperationException("Unable to create " + name); }
            return rt;
        }
        private static bool MatrixChanged(Matrix4x4 a, Matrix4x4 b, float tolerance)
        {
            for (int i = 0; i < 16; ++i) if (Mathf.Abs(a[i] - b[i]) > tolerance) return true;
            return false;
        }
        private static float Halton(int index, int radix)
        {
            float value = 0, fraction = 1;
            while (index > 0) { fraction /= radix; value += fraction * (index % radix); index /= radix; }
            return value;
        }
        public void Dispose()
        {
            foreach (CameraHistory state in m_histories.Values) state.Dispose();
            m_histories.Clear();
            CoreUtils.Destroy(m_material);
        }
    }
}
