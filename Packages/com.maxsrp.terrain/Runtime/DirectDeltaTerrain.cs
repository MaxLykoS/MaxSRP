using MaxSRP;
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxTerrain
{
    // DirectDelta renders the resident control map and source materials directly.
    // It owns its camera selection/height/material; the host SRP schedules every draw.
    internal sealed class DirectDeltaTerrain : IDisposable, IMaxRenderParticipant, IMaxRenderDiagnosticParticipant
    {
        readonly Camera camera;
        readonly MeshHeightSource source;
        readonly MeshMaterialResident sourceMaterials;
        readonly Light sun;
        readonly MaxTerrainGeometryPasses passes;
        Material material;
        MeshSurfaceOverlays overlays;
        bool authoringPreview;
        public string OverlayError { get; private set; }
        int debugMode, surfaceDebug;
        bool hexTiling;

        public bool UsesMaterialDiagnostics => debugMode != 0 || surfaceDebug != 0 || sourceMaterials == null;

        public MeshCdlod Geometry { get; }
        public MeshHeightClipmap Height { get; private set; }
        public MeshResidentControlMap Controls { get; private set; }
        public int DrawCount { get; private set; }

        public DirectDeltaTerrain(Camera camera, Shader shader, MeshCdlod geometry, MeshHeightSource source,
            MeshMaterialResident sourceMaterials, string controlDirectory, Light sun)
        {
            this.camera = camera;
            this.source = source;
            this.sourceMaterials = sourceMaterials;
            this.sun = sun;
            Geometry = geometry;
            passes = new MaxTerrainGeometryPasses(Geometry, legacyRanges: true);
            try
            {
                Height = new MeshHeightClipmap(source);
                material = new Material(shader)
                {
                    name = "DirectDelta - height clipmap mesh",
                    enableInstancing = true,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (sourceMaterials != null)
                {
                    Controls = new MeshResidentControlMap(controlDirectory);
                    material.EnableKeyword("DD_SURFACE_ON");
                }
                MaxRenderBridge.Register(this);
            }
            catch { Dispose(); throw; }
        }

        public void SetDisplay(int geometryMode, int surfaceMode, bool useHex)
        {
            if (debugMode != geometryMode || surfaceDebug != surfaceMode || hexTiling != useHex)
                MaxRenderBridge.InvalidateTemporalHistory(camera);
            debugMode = geometryMode;
            surfaceDebug = surfaceMode;
            hexTiling = useHex;
        }

        public void ConfigureOverlays(MeshWorldVtOverlaySet set, Func<MeshWorldVtDecal[]> staticDecals,
            bool authoringPreview = false, Func<MeshWorldVtRoad[]> roadProvider = null, bool immutable = false)
        {
            overlays?.Dispose();
            overlays = new MeshSurfaceOverlays(set, staticDecals, roadProvider, immutable);
            this.authoringPreview = authoringPreview;
        }

        public void ResetHeight()
        {
            var replacement = new MeshHeightClipmap(source);
            Height.Dispose();
            Height = replacement;
            passes.Invalidate();
            MaxRenderBridge.InvalidateShadows(camera);
        }

        public bool RendersCamera(Camera currentCamera) => currentCamera == camera;

        public void PrepareCamera(ScriptableRenderContext context, Camera currentCamera)
        {
            if (currentCamera != camera) return;
            Vector3 observer = camera.transform.position;
            passes.Prepare(camera);
            Height.UpdateCenter(new Vector2(observer.x, observer.z));
            Geometry.Select(observer, MaxTerrainGeometryPasses.CullingPlanes(camera));
            Geometry.ValidateClipCoverage(Height.Origins);
            Height.Bind(material);
            material.SetVector("_MeshObserver", Geometry.ObserverParameters(observer));
            material.SetFloat("_MeshHeightScale", source.Metadata.heightScale);
            material.SetInt("_MeshDebug", debugMode);
            BindSurface();
            // Direct baseline 在光照前直接浮点合成；不经过 VT stage/压缩量化。
            try { overlays?.Bind(material); OverlayError = null; }
            catch (Exception error) when (authoringPreview && (error is ArgumentException ||
                error is InvalidOperationException || error is System.IO.InvalidDataException))
            {
                material.DisableKeyword("DD_LIVE_OVERLAYS");
                OverlayError = error.Message;
            }

        }

        void BindSurface()
        {
            if (Controls == null) return;
            sourceMaterials.Bind(material);
            Controls.Bind(material);
            material.SetInt("_DDSurfaceDebug", surfaceDebug);
            material.SetFloat("_DDHexTiling", hexTiling ? 1 : 0);
            if (!sun) return;
            material.SetVector("_DDLightDirection", -sun.transform.forward);
            Color color = sun.color.linear * sun.intensity;
            material.SetColor("_DDLightColor", new Color(color.r, color.g, color.b, 1));
        }

        public void RecordGBuffer(CommandBuffer commands, Camera currentCamera)
        {
            DrawCount = passes.Record(commands, material);
        }

        public void RecordMotion(CommandBuffer commands, Camera currentCamera) => passes.RecordMotion(commands, material);
        public void RecordShadow(CommandBuffer commands, Camera currentCamera, Matrix4x4 view,
            Matrix4x4 projection, Plane[] planes) => passes.RecordShadow(commands, material, camera, view, projection, planes);
        public void EndCamera(ScriptableRenderContext context, Camera currentCamera) => passes.Commit(camera);

        public void Dispose()
        {
            MaxRenderBridge.Unregister(this);
            MaxRenderBridge.InvalidateShadows(camera);
            passes.Dispose();
            Controls?.Dispose();
            overlays?.Dispose();
            Height?.Dispose();
            Geometry.Dispose();
            MeshResidentHeight.Destroy(material);
        }
    }
}
