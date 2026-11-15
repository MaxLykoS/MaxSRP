using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxTerrain
{
    // Per-camera geometry history. The SRP owns targets and matrices; this helper
    // only submits the same immutable heightfield with current/previous CDLOD morph.
    internal sealed class MaxTerrainGeometryPasses : IDisposable
    {
        const int BatchSize = 500;
        readonly MeshCdlod geometry, shadowGeometry;
        readonly Dictionary<Vector4, MeshCdlod.Selection> previous = new Dictionary<Vector4, MeshCdlod.Selection>();
        readonly Matrix4x4[] matrices = new Matrix4x4[BatchSize];
        readonly Vector4[] patches = new Vector4[BatchSize], parameters = new Vector4[BatchSize], oldParameters = new Vector4[BatchSize];
        readonly float[] valid = new float[BatchSize];
        readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        Vector4 previousObserver, preparedObserver;
        int previousFrame = int.MinValue;
        bool historyValid;

        public MaxTerrainGeometryPasses(MeshCdlod geometry, bool legacyRanges)
        {
            this.geometry = geometry;
            shadowGeometry = geometry.CreateSelection(legacyRanges);
            for (int i = 0; i < matrices.Length; ++i) matrices[i] = Matrix4x4.identity;
        }

        public static Plane[] CullingPlanes(Camera camera)
        {
            // Expand the unjittered frustum by one pixel without mutating Camera.
            Matrix4x4 projection = camera.projectionMatrix;
            Rect viewport = camera.targetTexture
                ? new Rect(0, 0, camera.targetTexture.width, camera.targetTexture.height) : camera.pixelRect;
            // Match the host CameraViewport contract, including offscreen Game
            // cameras. The terrain assembly does not depend on host internals.
            if (camera.cameraType == CameraType.Game && viewport.width > 0 && viewport.height > 0)
                projection.m00 = projection.m11 * viewport.height / viewport.width;
            float width = Math.Max(viewport.width, 1), height = Math.Max(viewport.height, 1);
            float x = width / (width + 2), y = height / (height + 2);
            for (int column = 0; column < 4; ++column) { projection[0,column] *= x; projection[1,column] *= y; }
            return GeometryUtility.CalculateFrustumPlanes(projection * camera.worldToCameraMatrix);
        }

        // Remember the observer used to build THIS draw, not a transform another
        // callback might change after rendering (camera cuts/followers/debug routes).
        public void Prepare(Camera camera) => preparedObserver = geometry.ObserverParameters(camera.transform.position);

        public void Invalidate() { previous.Clear(); historyValid = false; previousFrame = int.MinValue; }

        public int Record(CommandBuffer commands, Material material, string passName = null, MeshCdlod selection = null)
        {
            int pass = passName == null ? 0 : material.FindPass(passName);
            if (pass < 0) throw new InvalidOperationException("Missing MaxTerrain shader pass: " + passName);
            MeshCdlod selected = selection ?? geometry;
            material.SetVector("_MeshPreviousObserver", previousObserver);
            bool hasPrevious = historyValid && (long)Time.frameCount - previousFrame <= 1;
            int draws = 0;
            for (int start = 0; start < selected.Selections.Count; start += BatchSize)
            {
                int count = Math.Min(BatchSize, selected.Selections.Count - start);
                for (int i = 0; i < count; ++i)
                {
                    MeshCdlod.Selection current = selected.Selections[start + i];
                    patches[i] = current.Patch; parameters[i] = current.Parameters;
                    MeshCdlod.Selection old = current;
                    bool matched = hasPrevious && previous.TryGetValue(current.Patch, out old);
                    if (!matched) old = current;
                    oldParameters[i] = old.Parameters;
                    // New patches / changing quarter topology reject history. A zero
                    // velocity is never substituted for unknown previous geometry.
                    valid[i] = matched && old.Mask == current.Mask ? 1 : 0;
                }
                properties.Clear();
                properties.SetVectorArray("_MeshPatch", patches);
                properties.SetVectorArray("_MeshParams", parameters);
                properties.SetVectorArray("_MeshPreviousParams", oldParameters);
                properties.SetFloatArray("_MeshMotionValidity", valid);
                commands.DrawMeshInstanced(selected.Patch, 0, material, pass, matrices, count, properties);
                draws++;
            }
            return draws;
        }

        public void RecordMotion(CommandBuffer commands, Material material) => Record(commands, material, "MaxTerrainMotion");

        public void RecordShadow(CommandBuffer commands, Material material, Camera camera,
            Matrix4x4 view, Matrix4x4 gpuProjection, Plane[] planes)
        {
            // Light-frustum selection includes casters outside the visible camera.
            // Morph distance still uses the viewer, matching the visible surface.
            shadowGeometry.Select(camera.transform.position, planes);
            commands.SetGlobalMatrix("_MaxTerrainShadowVP", gpuProjection * view);
            Record(commands, material, "MaxTerrainShadow", shadowGeometry);
        }

        public void Commit(Camera camera)
        {
            previous.Clear();
            foreach (var selected in geometry.Selections) previous[selected.Patch] = selected;
            previousObserver = preparedObserver;
            previousFrame = Time.frameCount; historyValid = true;
        }

        public void Dispose() { previous.Clear(); shadowGeometry.Dispose(); }
    }
}
