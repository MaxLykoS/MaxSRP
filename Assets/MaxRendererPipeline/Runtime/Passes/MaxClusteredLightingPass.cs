using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    [Serializable]
    public sealed class ClusteredLightingSettings
    {
        [Tooltip("Cull local point lights into a per-camera 3D grid. Disabled uses the same full light buffer as a reference.")]
        public bool Enabled = true;
        [Range(16, 128)] public int TileSize = 64;
        [Range(8, 64)] public int DepthSlices = 32;
        [Range(8, 128), Tooltip("Stored indices per cluster. Overflow falls back to all lights; it never drops light contribution.")]
        public int MaxLightsPerCluster = 64;
    }

    // This grid depends only on the unjittered view frustum and light bounds.
    // It does not sample GBuffer/depth or introduce a temporal history.
    public sealed class MaxClusteredLightingPass : IDisposable
    {
        private const long MaxGridBytes = 32L * 1024 * 1024;
        private readonly ComputeShader m_compute;
        private readonly int m_prepareKernel, m_buildKernel;
        private readonly ComputeBuffer m_dummy;
        private readonly Dictionary<int, CameraLightResources> m_cameras = new Dictionary<int, CameraLightResources>();
        private readonly List<int> m_destroyed = new List<int>();

        public MaxClusteredLightingPass(ComputeShader compute)
        {
            m_compute = compute;
            if (m_compute != null && SystemInfo.supportsComputeShaders)
            {
                m_prepareKernel = m_compute.FindKernel("PrepareLights");
                m_buildKernel = m_compute.FindKernel("BuildClusters");
            }
            m_dummy = new ComputeBuffer(1, sizeof(uint));
            m_dummy.SetData(new uint[] { 0 });
        }

        public CameraLightResources Prepare(ScriptableRenderContext context, Camera camera,
            int width, int height, MaxLightConfigurator lights, ClusteredLightingSettings settings)
        {
            int id = camera.GetInstanceID();
            if (!m_cameras.TryGetValue(id, out CameraLightResources resources))
            {
                resources = new CameraLightResources(camera, m_dummy);
                m_cameras.Add(id, resources);
            }
            int count = lights.PointLightCount;
            resources.EnsureLights(Mathf.Max(1, count));
            resources.LightCount = count;
            resources.Enabled = false;
            CommandBuffer cmd = CommandBufferPool.Get("Clustered Light Culling");
            try
            {
                // Record the upload in rendering order, including repeated renders of a camera.
                if (count > 0) cmd.SetBufferData(resources.Lights, lights.PointLights, 0, 0, count);
                int tile = Mathf.Clamp(settings.TileSize, 16, 128);
                int nx = (width + tile - 1) / tile, ny = (height + tile - 1) / tile;
                int nz = Mathf.Clamp(settings.DepthSlices, 8, 64);
                int capacity = Mathf.Clamp(settings.MaxLightsPerCluster, 8, 128);
                long cells = (long)nx * ny * nz;
                bool supported = settings.Enabled && count > 0 && m_compute != null && SystemInfo.supportsComputeShaders;
                float near = camera.orthographic ? camera.nearClipPlane : Mathf.Max(0.0001f, camera.nearClipPlane);
                float far = camera.farClipPlane;
                Matrix4x4 projection = CameraViewport.GetProjection(camera);
                Matrix4x4 inverse = projection.inverse;
                Matrix4x4 view = camera.worldToCameraMatrix;
                // Oversized or invalid grids use complete per-pixel evaluation instead of truncating lights.
                if (supported && cells * (capacity + 1) * sizeof(uint) <= MaxGridBytes &&
                    IsFinite(near) && IsFinite(far) && far > near && IsFinite(projection) && IsFinite(inverse) && IsRigidView(view))
                {
                    resources.EnsureGrid((int)cells, capacity);
                    resources.Grid = new Vector4(nx, ny, nz, capacity);
                    resources.Screen = new Vector4(width, height, tile, 0);
                    resources.Depth = new Vector4(near, far, camera.orthographic ? 1 : 0, 0);
                    resources.View = view;
                    resources.Projection = projection;
                    cmd.SetComputeIntParam(m_compute, "_MaxPointLightCount", count);
                    cmd.SetComputeVectorParam(m_compute, "_MaxClusterGrid", resources.Grid);
                    cmd.SetComputeVectorParam(m_compute, "_MaxClusterScreen", resources.Screen);
                    cmd.SetComputeVectorParam(m_compute, "_MaxClusterDepth", resources.Depth);
                    cmd.SetComputeMatrixParam(m_compute, "_MaxClusterWorldToView", resources.View);
                    cmd.SetComputeMatrixParam(m_compute, "_MaxClusterProjection", projection);
                    cmd.SetComputeMatrixParam(m_compute, "_MaxClusterInvProjection", inverse);
                    cmd.SetComputeBufferParam(m_compute, m_prepareKernel, "_MaxPointLights", resources.Lights);
                    cmd.SetComputeBufferParam(m_compute, m_prepareKernel, "_MaxClusterViewLights", resources.ViewLights);
                    cmd.DispatchCompute(m_compute, m_prepareKernel, (count + 63) / 64, 1, 1);
                    cmd.SetComputeBufferParam(m_compute, m_buildKernel, "_MaxClusterViewLights", resources.ViewLights);
                    cmd.SetComputeBufferParam(m_compute, m_buildKernel, "_MaxClusterCounts", resources.Counts);
                    cmd.SetComputeBufferParam(m_compute, m_buildKernel, "_MaxClusterLightIndices", resources.Indices);
                    cmd.DispatchCompute(m_compute, m_buildKernel, (nx + 3) / 4, (ny + 3) / 4, (nz + 3) / 4);
                    resources.Enabled = true;
                }
                context.ExecuteCommandBuffer(cmd);
            }
            finally { CommandBufferPool.Release(cmd); }
            return resources;
        }

        private static bool IsFinite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        private static bool IsFinite(Matrix4x4 matrix)
        {
            for (int i = 0; i < 16; ++i) if (!IsFinite(matrix[i])) return false;
            return true;
        }

        private static bool IsRigidView(Matrix4x4 matrix)
        {
            // Sphere radii remain in world units. Scaled/sheared custom views
            // must use full lighting rather than underestimating their bounds.
            if (!IsFinite(matrix) || Mathf.Abs(matrix.m30) + Mathf.Abs(matrix.m31) +
                Mathf.Abs(matrix.m32) + Mathf.Abs(matrix.m33 - 1) > 1e-4f) return false;
            Vector3 x = matrix.GetRow(0), y = matrix.GetRow(1), z = matrix.GetRow(2);
            return Mathf.Abs(x.sqrMagnitude - 1) < 1e-4f && Mathf.Abs(y.sqrMagnitude - 1) < 1e-4f &&
                Mathf.Abs(z.sqrMagnitude - 1) < 1e-4f && Mathf.Abs(Vector3.Dot(x, y)) < 1e-4f &&
                Mathf.Abs(Vector3.Dot(x, z)) < 1e-4f && Mathf.Abs(Vector3.Dot(y, z)) < 1e-4f;
        }

        public void PruneDestroyedCameras()
        {
            m_destroyed.Clear();
            foreach (var item in m_cameras) if (item.Value.Camera == null) m_destroyed.Add(item.Key);
            foreach (int id in m_destroyed) { m_cameras[id].Dispose(); m_cameras.Remove(id); }
        }

        public void Dispose()
        {
            foreach (var item in m_cameras) item.Value.Dispose();
            m_cameras.Clear();
            if (m_dummy != null && m_dummy.IsValid()) m_dummy.Release();
        }

        public sealed class CameraLightResources : IDisposable
        {
            public readonly Camera Camera;
            internal ComputeBuffer Lights, ViewLights, Counts, Indices;
            internal int LightCount;
            internal bool Enabled;
            internal Vector4 Grid, Screen, Depth;
            internal Matrix4x4 View, Projection;
            private readonly ComputeBuffer m_dummy;
            private int m_lightCapacity, m_cellCount, m_capacity;

            internal CameraLightResources(Camera camera, ComputeBuffer dummy) { Camera = camera; m_dummy = dummy; }

            internal void EnsureLights(int count)
            {
                if (Lights != null && Lights.IsValid() && ViewLights != null && ViewLights.IsValid() && count <= m_lightCapacity) return;
                Release(ref Lights); Release(ref ViewLights);
                m_lightCapacity = Mathf.NextPowerOfTwo(count);
                Lights = new ComputeBuffer(m_lightCapacity, 32) { name = "MaxSRP Point Lights" };
                ViewLights = new ComputeBuffer(m_lightCapacity, 16) { name = "MaxSRP View Light Bounds" };
            }

            internal void EnsureGrid(int cells, int capacity)
            {
                if (Counts != null && Counts.IsValid() && Indices != null && Indices.IsValid() && cells == m_cellCount && capacity == m_capacity) return;
                Release(ref Counts); Release(ref Indices);
                Counts = new ComputeBuffer(cells, sizeof(uint)) { name = "MaxSRP Cluster Counts" };
                Indices = new ComputeBuffer(cells * capacity, sizeof(uint)) { name = "MaxSRP Cluster Light Indices" };
                m_cellCount = cells; m_capacity = capacity;
            }

            internal void Bind(MaterialPropertyBlock block)
            {
                block.SetFloat("_MaxUsePointLightBuffer", 1);
                block.SetInteger("_MaxPointLightCount", LightCount);
                block.SetBuffer("_MaxPointLights", Lights);
                block.SetFloat("_MaxClusterEnabled", Enabled ? 1 : 0);
                block.SetVector("_MaxClusterGrid", Grid);
                block.SetVector("_MaxClusterScreen", Screen);
                block.SetVector("_MaxClusterDepth", Depth);
                block.SetMatrix("_MaxClusterWorldToView", View);
                block.SetMatrix("_MaxClusterProjection", Projection);
                block.SetBuffer("_MaxClusterCounts", Counts != null && Counts.IsValid() ? Counts : m_dummy);
                block.SetBuffer("_MaxClusterLightIndices", Indices != null && Indices.IsValid() ? Indices : m_dummy);
            }

            private static void Release(ref ComputeBuffer buffer) { buffer?.Release(); buffer = null; }
            public void Dispose() { Release(ref Lights); Release(ref ViewLights); Release(ref Counts); Release(ref Indices); }
        }
    }
}
