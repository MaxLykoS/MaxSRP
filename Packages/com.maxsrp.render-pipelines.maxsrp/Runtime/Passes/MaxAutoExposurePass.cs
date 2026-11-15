using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    // UE 5.8 reference: PostProcessEyeAdaptation.cpp, PostProcessHistogramCommon.ush
    // and PostProcessMobile.usf. Histories contain exposure scalars, never scene colour.
    public sealed class MaxAutoExposurePass : IDisposable
    {
        private const int CurveSize = 64;
        private const float CurveMinEV = -10f, CurveMaxEV = 20f;
        private readonly ComputeShader m_compute;
        private readonly int m_clear, m_histogram, m_basic, m_resolve;
        private readonly Dictionary<int, CameraState> m_cameras = new Dictionary<int, CameraState>();
        private readonly List<int> m_destroyed = new List<int>();

        private sealed class CameraState : IDisposable
        {
            internal readonly Camera Camera;
            internal readonly RenderTexture[] Exposure = new RenderTexture[2];
            internal ComputeBuffer Statistics;
            internal Texture2D Curve;
            internal ExposureSettings Settings;
            internal ExposureMode Mode;
            internal int CurveHash, ParametersHash, Index, Width, Height, Destination, Scene;
            internal Rect Viewport;
            internal Matrix4x4 Projection;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal double LastTime, LastWorldTime, SettleUntil;
            internal bool Valid;

            internal CameraState(Camera camera) { Camera = camera; }
            public void Dispose()
            {
                for (int i = 0; i < Exposure.Length; ++i) MaxRenderResources.Destroy(ref Exposure[i]);
                MaxRenderResources.Destroy(ref Curve);
                Statistics?.Release(); Statistics = null;
                Valid = false;
            }
        }

        public MaxAutoExposurePass(ComputeShader shader)
        {
            m_compute = shader;
            if (m_compute == null || !SystemInfo.supportsComputeShaders) return;
            m_clear = m_compute.FindKernel("ClearExposureStatistics");
            m_histogram = m_compute.FindKernel("MeterHistogram");
            m_basic = m_compute.FindKernel("MeterBasic");
            m_resolve = m_compute.FindKernel("ResolveExposure");
        }

        // Null means the caller must use its manual/fixed exposure fallback. Meter only
        // the camera's viewport in the original fixed-scale scene HDR, before gizmos/bloom.
        public Texture Execute(ScriptableRenderContext context, Camera camera, RenderTexture sceneHDR,
            Rect viewport, ExposureSettings settings, bool reset = false)
        {
            CommandBuffer cmd = CommandBufferPool.Get("Auto Exposure");
            try
            {
                Texture result = Record(cmd, camera, sceneHDR, viewport, settings, reset);
                context.ExecuteCommandBuffer(cmd);
                return result;
            }
            finally { CommandBufferPool.Release(cmd); }
        }

        // Records the production work, also usable by numerical GPU validation.
        // Submit a camera's commands before releasing/reconfiguring its resources.
        public Texture Record(CommandBuffer cmd, Camera camera, RenderTexture sceneHDR,
            Rect viewport, ExposureSettings settings, bool reset = false)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (camera == null) return null;
            if (settings == null || settings.Mode == ExposureMode.Manual || sceneHDR == null ||
                m_compute == null || !SystemInfo.supportsComputeShaders ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) ||
                !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBFloat) ||
                (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView))
            {
                ReleaseCamera(camera);
                return null;
            }

            int width = Mathf.Clamp(Mathf.RoundToInt(viewport.width), 1, sceneHDR.width);
            int height = Mathf.Clamp(Mathf.RoundToInt(viewport.height), 1, sceneHDR.height);
            int x = Mathf.Clamp(Mathf.RoundToInt(viewport.x), 0, sceneHDR.width - width);
            int y = Mathf.Clamp(Mathf.RoundToInt(viewport.y), 0, sceneHDR.height - height);
            Rect meteringViewport = new Rect(x, y, width, height);
            int id = camera.GetInstanceID();
            if (!m_cameras.TryGetValue(id, out CameraState state))
                m_cameras.Add(id, state = new CameraState(camera));
            if (!TargetsValid(state))
            {
                state.Dispose();
                try
                {
                    state.Statistics = new ComputeBuffer(64, sizeof(uint)) { name = "MaxSRP Exposure Statistics" };
                    for (int i = 0; i < 2; ++i)
                    {
                        state.Exposure[i] = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBFloat,
                            RenderTextureReadWrite.Linear)
                        {
                            name = "MaxSRP Exposure " + i, hideFlags = HideFlags.HideAndDontSave,
                            enableRandomWrite = true, filterMode = FilterMode.Point,
                            wrapMode = TextureWrapMode.Clamp
                        };
                        if (!state.Exposure[i].Create())
                            throw new InvalidOperationException("Could not create MaxSRP auto exposure history.");
                    }
                }
                catch { state.Dispose(); m_cameras.Remove(id); throw; }
            }

            double now = Time.realtimeSinceStartupAsDouble;
            double worldTime = Application.isPlaying ? Time.timeAsDouble : now;
            int destination = camera.targetTexture != null ? camera.targetTexture.GetInstanceID() : -camera.targetDisplay - 1;
            Matrix4x4 projection = CameraViewport.GetProjection(camera);
            bool moved = state.Position != camera.transform.position || state.Rotation != camera.transform.rotation;
            bool invalid = reset || !state.Valid || now - state.LastTime > 2 || state.LastTime > now ||
                state.Settings != settings || state.Width != sceneHDR.width || state.Height != sceneHDR.height ||
                state.Viewport != meteringViewport || state.Destination != destination ||
                state.Scene != camera.gameObject.scene.handle || MatrixChanged(projection, state.Projection) ||
                state.Mode != settings.Mode ||
                Vector3.Distance(camera.transform.position, state.Position) > Mathf.Max(2f, camera.nearClipPlane * 20f) ||
                Quaternion.Angle(camera.transform.rotation, state.Rotation) > 45f;

            int parametersHash = SettingsHash(settings);
            double settlingTime = SettlingTime(settings);
            if (invalid || moved || state.ParametersHash != parametersHash) state.SettleUntil = now + settlingTime;
            // A profile/metering-method switch changes the statistics representation.
            // Bounds and compensation edits otherwise adapt continuously as in UE.
            if (state.Settings != null && state.Mode != settings.Mode) invalid = true;
            int previousCurveHash = state.CurveHash;
            UpdateCurve(state, settings.CompensationCurve);
            if (state.CurveHash != previousCurveHash) state.SettleUntil = now + settlingTime;

            float dt = state.Valid ? Mathf.Clamp((float)(worldTime - state.LastWorldTime), 0f, 0.25f) : 0f;
            float histogramMin = Finite(settings.HistogramMinEV100, -10f);
            float histogramMax = Mathf.Max(histogramMin + 1f, Finite(settings.HistogramMaxEV100, 20f));
            histogramMin = Mathf.Clamp(histogramMin, -24f, 32f);
            histogramMax = Mathf.Clamp(histogramMax, histogramMin + 1f, 33f);
            float minEV = Mathf.Clamp(Finite(settings.MinEV100, -10f), -24f, 32f);
            float maxEV = Mathf.Clamp(Finite(settings.MaxEV100, 20f), minEV, 32f);
            float high = Mathf.Clamp(Finite(settings.HighPercent, 90f), 1f, 99f) * 0.01f;
            float low = Mathf.Clamp(Finite(settings.LowPercent, 10f), 1f, high * 100f) * 0.01f;
            int divider = settings.Mode == ExposureMode.Basic ? 4 : 2;
            int meterWidth = (width + divider - 1) / divider, meterHeight = (height + divider - 1) / divider;
            // Integer sums stay below 2^30 at every supported render size. Per-group
            // quantization follows UE's fixed-point mobile reduction; no float atomics.
            float weightScale = Mathf.Min(4096f, 1073741824f / ((float)meterWidth * meterHeight));
            int output = 1 - state.Index;
            cmd.SetComputeVectorParam(m_compute, "_ExposureSourceSize", new Vector4(sceneHDR.width, sceneHDR.height, 1f / sceneHDR.width, 1f / sceneHDR.height));
            cmd.SetComputeVectorParam(m_compute, "_ExposureViewport", new Vector4(x, y, width, height));
            cmd.SetComputeVectorParam(m_compute, "_ExposureMeterSize", new Vector4(meterWidth, meterHeight, 1f / meterWidth, 1f / meterHeight));
            cmd.SetComputeVectorParam(m_compute, "_ExposureHistogram", new Vector4(histogramMin, histogramMax, low, high));
            cmd.SetComputeVectorParam(m_compute, "_ExposureAdaptation", new Vector4(minEV, maxEV,
                Mathf.Clamp(Finite(settings.SpeedUp, 3f), 0f, 100f), Mathf.Clamp(Finite(settings.SpeedDown, 1f), 0f, 100f)));
            cmd.SetComputeVectorParam(m_compute, "_ExposureControl", new Vector4(dt, invalid || minEV == maxEV ? 1f : 0f,
                Mathf.Clamp(Finite(settings.CompensationEV, 0f), -16f, 16f), weightScale));
            cmd.SetComputeIntParam(m_compute, "_ExposureBasic", settings.Mode == ExposureMode.Basic ? 1 : 0);
            cmd.SetComputeFloatParam(m_compute, "_ExposureHDRScale", LightPhotometry.HDRScale);
            cmd.SetComputeBufferParam(m_compute, m_clear, "_ExposureStatistics", state.Statistics);
            cmd.DispatchCompute(m_compute, m_clear, 1, 1, 1);
            int meter = settings.Mode == ExposureMode.Basic ? m_basic : m_histogram;
            cmd.SetComputeTextureParam(m_compute, meter, "_ExposureSceneColor", sceneHDR);
            cmd.SetComputeTextureParam(m_compute, meter, "_ExposureMeteringMask", settings.MeteringMask != null ? settings.MeteringMask : Texture2D.whiteTexture);
            cmd.SetComputeBufferParam(m_compute, meter, "_ExposureStatistics", state.Statistics);
            cmd.DispatchCompute(m_compute, meter, (meterWidth + 7) / 8, (meterHeight + 7) / 8, 1);
            cmd.SetComputeBufferParam(m_compute, m_resolve, "_ExposureStatistics", state.Statistics);
            cmd.SetComputeTextureParam(m_compute, m_resolve, "_ExposurePrevious", state.Exposure[state.Index]);
            cmd.SetComputeTextureParam(m_compute, m_resolve, "_ExposureResult", state.Exposure[output]);
            cmd.SetComputeTextureParam(m_compute, m_resolve, "_ExposureCompensationCurve", state.Curve);
            cmd.DispatchCompute(m_compute, m_resolve, 1, 1, 1);

            state.Index = output; state.Valid = true; state.Settings = settings; state.Mode = settings.Mode;
            state.Width = sceneHDR.width; state.Height = sceneHDR.height; state.Viewport = meteringViewport;
            state.Destination = destination; state.Scene = camera.gameObject.scene.handle;
            state.Position = camera.transform.position; state.Rotation = camera.transform.rotation;
            state.Projection = projection; state.LastTime = now; state.LastWorldTime = worldTime;
            state.ParametersHash = parametersHash;
#if UNITY_EDITOR
            if (!Application.isPlaying && camera.cameraType == CameraType.SceneView && now < state.SettleUntil)
            {
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                UnityEditor.SceneView.RepaintAll();
            }
#endif
            return state.Exposure[state.Index];
        }

        private static bool TargetsValid(CameraState state) => state.Statistics != null && state.Statistics.IsValid() &&
            state.Exposure[0] != null && state.Exposure[0].IsCreated() && state.Exposure[1] != null && state.Exposure[1].IsCreated();

        private static float Finite(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
        private static double SettlingTime(ExposureSettings settings)
        {
            float speed = Mathf.Min(Finite(settings.SpeedUp, 0f), Finite(settings.SpeedDown, 0f));
            if (speed <= 0f) return 0;
            // Worst-case linear traversal plus the final exponential tail. Editor
            // repaints are bounded, and a frozen adaptation never requests them.
            float span = Mathf.Abs(Finite(settings.MaxEV100, 20f) - Finite(settings.MinEV100, -10f));
            return Mathf.Clamp((span + 8f) / speed, 0f, 120f);
        }
        private static bool MatrixChanged(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; ++i) if (Mathf.Abs(a[i] - b[i]) > 0.00001f) return true;
            return false;
        }

        private static int SettingsHash(ExposureSettings settings)
        {
            unchecked
            {
                int hash = (int)settings.Mode;
                hash = hash * 31 + settings.MinEV100.GetHashCode(); hash = hash * 31 + settings.MaxEV100.GetHashCode();
                hash = hash * 31 + settings.CompensationEV.GetHashCode(); hash = hash * 31 + settings.SpeedUp.GetHashCode();
                hash = hash * 31 + settings.SpeedDown.GetHashCode(); hash = hash * 31 + settings.LowPercent.GetHashCode();
                hash = hash * 31 + settings.HighPercent.GetHashCode(); hash = hash * 31 + settings.HistogramMinEV100.GetHashCode();
                hash = hash * 31 + settings.HistogramMaxEV100.GetHashCode();
                return hash * 31 + (settings.MeteringMask != null ? settings.MeteringMask.GetInstanceID() : 0);
            }
        }

        private static void UpdateCurve(CameraState state, AnimationCurve curve)
        {
            int hash = 17;
            unchecked
            {
                if (curve != null)
                {
                    hash = hash * 31 + (int)curve.preWrapMode; hash = hash * 31 + (int)curve.postWrapMode;
                    for (int i = 0; i < curve.length; ++i)
                    {
                        Keyframe key = curve[i]; hash = hash * 31 + key.time.GetHashCode(); hash = hash * 31 + key.value.GetHashCode();
                        hash = hash * 31 + key.inTangent.GetHashCode(); hash = hash * 31 + key.outTangent.GetHashCode();
                        hash = hash * 31 + key.inWeight.GetHashCode(); hash = hash * 31 + key.outWeight.GetHashCode();
                        hash = hash * 31 + (int)key.weightedMode;
                    }
                }
            }
            if (state.Curve != null && state.CurveHash == hash) return;
            if (state.Curve == null)
                state.Curve = new Texture2D(CurveSize, 1, TextureFormat.RFloat, false, true)
                {
                    name = "MaxSRP Exposure Compensation Curve", hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
                };
            var pixels = new float[CurveSize];
            for (int i = 0; i < CurveSize; ++i)
            {
                float ev = Mathf.Lerp(CurveMinEV, CurveMaxEV, i / (CurveSize - 1f));
                float compensation = curve != null && curve.length > 0 ? Mathf.Clamp(Finite(curve.Evaluate(ev), 0f), -16f, 16f) : 0f;
                pixels[i] = Mathf.Pow(2f, compensation);
            }
            state.Curve.SetPixelData(pixels, 0); state.Curve.Apply(false, false); state.CurveHash = hash;
        }

        public void ResetHistory(Camera camera)
        {
            if (camera != null && m_cameras.TryGetValue(camera.GetInstanceID(), out CameraState state)) state.Valid = false;
        }

        public void ReleaseCamera(Camera camera)
        {
            if (camera == null) return;
            int id = camera.GetInstanceID();
            if (!m_cameras.TryGetValue(id, out CameraState state)) return;
            state.Dispose(); m_cameras.Remove(id);
        }

        public void PruneDestroyedCameras()
        {
            m_destroyed.Clear();
            foreach (var entry in m_cameras) if (entry.Value.Camera == null) m_destroyed.Add(entry.Key);
            foreach (int id in m_destroyed) { m_cameras[id].Dispose(); m_cameras.Remove(id); }
        }

        public void Dispose()
        {
            foreach (var entry in m_cameras) entry.Value.Dispose();
            m_cameras.Clear();
        }
    }
}
