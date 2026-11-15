using MaxSRP;
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxTerrain
{
    // Delta 相机独立拥有可见选区、两张 Clipmap、主绘制与 VT 反馈。
    // MeshComparison 只传入相机和公共源资产，运行时渲染不经过 DirectDelta。
    internal sealed class DeltaTerrain : IDisposable, IMaxRenderParticipant, IMaxRenderDiagnosticParticipant
    {
        readonly Camera camera;
        readonly Shader fallbackShader;
        readonly MeshHeightSource source;
        readonly MeshMaterialResident sourceMaterials;
        readonly string controlDirectory;
        readonly Light sun;
        Material material;
        MeshSurfaceOverlays overlays;
        readonly MaxTerrainGeometryPasses passes;
        Shader producerShader;
        string archiveDirectory, worldVtFailure;
        MeshWorldVtOverlaySet worldOverlays;
        Func<MeshWorldVtDecal[]> sceneDecals;
        Func<MeshWorldVtRoad[]> sceneRoads;
        bool immutableOverlayInputs;
        int previousVtFrame = int.MinValue, feedbackFrame = int.MinValue;
        uint feedbackSample;
        int maxAnisotropy = 4;
        bool mainFeedbackActive;
        int debugMode, surfaceDebug, worldMode = 1;
        bool hexTiling, singlePageFailed, singlePageRebuildRequested;

        public bool UsesMaterialDiagnostics => debugMode != 0 || surfaceDebug != 0 || sourceMaterials == null || (archiveDirectory != null && worldMode >= 2);

        public MeshCdlod Geometry { get; }
        public MeshHeightClipmap Height { get; private set; }
        public MeshControlMap Controls { get; private set; }
        public MeshWorldVirtualTexture WorldVt { get; private set; }
        public string WorldVtError => worldVtFailure ?? WorldVt?.Error;
        public int DrawCount { get; private set; }
        public int PageBudget { private get; set; }
        public float MipBias { private get; set; }
        public int MaxAnisotropy
        {
            get => maxAnisotropy;
            set
            {
                value = MeshWorldVirtualTexture.NormalizeMaxAnisotropy(value);
                if (maxAnisotropy == value) return;
                InvalidateFeedback();
                maxAnisotropy = value;
            }
        }
        internal int FeedbackDivisor { get; set; } = 8;
        public int WorldMode
        {
            set
            {
                if ((worldMode == 0) != (value == 0)) InvalidateFeedback();
                if (worldMode != value) MaxRenderBridge.InvalidateTemporalHistory(camera);
                worldMode = value;
            }
        }

        public MeshSinglePageRvt SinglePage { get; private set; }
        public string SinglePageError { get; private set; }
        public bool SinglePageEnabled { private get; set; }
        public Shader SinglePageShader { private get; set; }
        public Vector2 SinglePageOrigin { private get; set; }
        public float SinglePageWorldSize { private get; set; }
        public int SinglePageResolution { private get; set; }
        public int SinglePageMode { private get; set; }

        public DeltaTerrain(Camera camera, Shader fallbackShader, MeshCdlod geometry, MeshHeightSource source,
            MeshMaterialResident sourceMaterials, string controlDirectory, Light sun)
        {
            this.camera = camera;
            this.fallbackShader = fallbackShader;
            this.source = source;
            this.sourceMaterials = sourceMaterials;
            this.controlDirectory = controlDirectory;
            this.sun = sun;
            Geometry = geometry;
            passes = new MaxTerrainGeometryPasses(Geometry, legacyRanges: false);
            try
            {
                Height = new MeshHeightClipmap(source);
                material = new Material(fallbackShader)
                {
                    name = "Delta - height clipmap mesh",
                    enableInstancing = true,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (sourceMaterials != null)
                {
                    Controls = new MeshControlMap(controlDirectory);
                    material.EnableKeyword("DD_SURFACE_ON");
                }
                MaxRenderBridge.Register(this);
            }
            catch { Dispose(); throw; }
        }

        public void ConfigureWorldVt(Shader consumer, Shader producer, string directory,
            MeshWorldVtOverlaySet overlays = null, Func<MeshWorldVtDecal[]> sceneDecalProvider = null,
            Func<MeshWorldVtRoad[]> sceneRoadProvider = null, bool immutableOverlayInputs = false)
        {
            archiveDirectory = directory;
            worldOverlays = overlays;
            sceneDecals = sceneDecalProvider;
            sceneRoads = sceneRoadProvider;
            this.immutableOverlayInputs = immutableOverlayInputs;
            producerShader = producer;
            Shader shader = consumer ? consumer : Shader.Find("MaxTerrain/WorldVirtualTerrain");
            if (shader && shader.isSupported) SetTerrainShader(shader);
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
            Func<MeshWorldVtRoad[]> roadProvider = null, bool immutable = false)
        {
            overlays?.Dispose();
            overlays = new MeshSurfaceOverlays(set, staticDecals, roadProvider, immutable);
        }

        public void ResetHeight()
        {
            var replacement = new MeshHeightClipmap(source);
            Height.Dispose();
            Height = replacement;
            passes.Invalidate();
            MaxRenderBridge.InvalidateShadows(camera);
        }

        // 右侧每帧入口。先准备显示几何，随后处理旧反馈和页生产，
        // 本帧GBuffer/反馈由MaxSRP显式提交，主相机间的资源与反馈保持独立。
        public bool RendersCamera(Camera currentCamera) => currentCamera == camera;

        public void PrepareCamera(ScriptableRenderContext context, Camera currentCamera)
        {
            if (currentCamera != camera) return;
            int frame = Time.frameCount;
            bool useWorldVt = archiveDirectory != null;
            if (!useWorldVt) PrepareSinglePageShader();
            PrepareVisibleTerrain();

            if (useWorldVt) AdvanceWorldVt(frame);
            else UpdateSinglePage();

            // 本相机独立绑定静态输入，供直接着色/缺页回退使用。
            // VT 缓存已经包含静态装饰，缓存命中不再执行实时覆盖合成。
            overlays?.Bind(material);

        }

        public void RecordGBuffer(CommandBuffer commands, Camera currentCamera)
        {
            material.DisableKeyword("DD_WORLD_VT_MAIN_FEEDBACK");
            mainFeedbackActive = false;
            if (archiveDirectory != null) BeginMainFeedback(commands, Time.frameCount);
            DrawCount = RecordPatches(commands, material);
            if (mainFeedbackActive) commands.ClearRandomWriteTargets();
        }

        public void RecordMotion(CommandBuffer commands, Camera currentCamera) => passes.RecordMotion(commands, material);
        public void RecordShadow(CommandBuffer commands, Camera currentCamera, Matrix4x4 view,
            Matrix4x4 projection, Plane[] planes) => passes.RecordShadow(commands, material, camera, view, projection, planes);

        void BeginMainFeedback(CommandBuffer commands, int frame)
        {
            if (WorldVt == null || worldMode == 0 || feedbackFrame == frame) return;
            if (SystemInfo.supportedRandomWriteTargetCount < 5)
            {
                FailWorldVt(new NotSupportedException("Main-pass VT feedback requires a pixel shader UAV at slot 4 after the four GBuffer targets."));
                return;
            }
            int divisor = FeedbackDivisor;
            int x = divisor / 2, y = divisor / 2;
            if (feedbackSample != 0)
            {
                uint phase = feedbackSample - 1;
                x = y = 0;
                for (int step = divisor / 2; step > 0; step /= 2)
                {
                    x += (int)((phase >> 1) & 1) * step;
                    y += (int)(phase & 1) * step;
                    phase >>= 2;
                }
            }
            try
            {
                mainFeedbackActive = WorldVt.BeginMainFeedback(commands, material, divisor, x, y);
                if (!mainFeedbackActive) return;
                material.EnableKeyword("DD_WORLD_VT_MAIN_FEEDBACK");
                feedbackFrame = frame;
            }
            catch (Exception exception) { FailWorldVt(exception); }
        }

        public void EndCamera(ScriptableRenderContext context, Camera currentCamera)
        {
            if (currentCamera != camera) return;
            passes.Commit(camera);
            if (!mainFeedbackActive) return;
            mainFeedbackActive = false;
            try
            {
                WorldVt?.SubmitMainFeedback(context);
                feedbackSample++;
            }
            catch (Exception exception) { FailWorldVt(exception); }
        }

        void PrepareVisibleTerrain()
        {
            Vector3 observer = camera.transform.position;
            passes.Prepare(camera);
            var center = new Vector2(observer.x, observer.z);
            Height.UpdateCenter(center);
            Controls?.UpdateCenter(center);
            Geometry.Select(observer, MaxTerrainGeometryPasses.CullingPlanes(camera));
            Geometry.ValidateClipCoverage(Height.Origins);
            BindGeometry(material);
            material.SetInt("_MeshDebug", debugMode);
            BindSurface();
        }

        void BindGeometry(Material target)
        {
            Height.Bind(target);
            target.SetVector("_MeshObserver", Geometry.ObserverParameters(camera.transform.position));
            target.SetFloat("_MeshHeightScale", source.Metadata.heightScale);
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

        void AdvanceWorldVt(int frame)
        {
            if (worldVtFailure != null) return;
            try
            {
                // 延迟到 Delta 首次渲染。只有左相机开启时不会创建页池。
                if (WorldVt == null) CreateWorldVt();
                if (previousVtFrame != int.MinValue && (long)frame - previousVtFrame > 1)
                    InvalidateFeedback(); // 相机停用期间，旧读回可能已过期。
                previousVtFrame = frame;

                // V 切到直接着色时仍回收已提交的读回，但不消费页预算。
                int budget = worldMode == 0 ? 0 : PageBudget;
                WorldVt.Advance(material, budget, frame);
                WorldVt.Bind(material, worldMode, MipBias, MaxAnisotropy);
            }
            catch (Exception exception) { FailWorldVt(exception); }
        }

        void CreateWorldVt()
        {
            if (material.shader == fallbackShader)
                throw new InvalidOperationException("The world VT terrain shader is unavailable.");
            WorldVt = new MeshWorldVirtualTexture(source, controlDirectory, sourceMaterials, material,
                archiveDirectory, producerShader, worldOverlays, sceneDecals, sceneRoads, immutableOverlayInputs);
        }

        int RecordPatches(CommandBuffer commands, Material drawMaterial) => passes.Record(commands, drawMaterial);


        public void InvalidateFeedback()
        {
            feedbackSample = 0;
            mainFeedbackActive = false;
            WorldVt?.InvalidateFeedback();
        }

        void FailWorldVt(Exception exception)
        {
            ReleaseWorldVt();
            worldVtFailure = exception.Message;
            material.SetFloat("_DDVtReady", 0);
            Debug.LogWarning("Delta world VT is using direct shading: " + exception.Message);
        }

        void ReleaseWorldVt()
        {
            WorldVt?.Dispose();
            WorldVt = null;
        }

        // 历史单页对照仍由 Delta 自己渲染。世界 VT 走上方独立的多页流程。
        void PrepareSinglePageShader()
        {
            if (!SinglePageEnabled || sourceMaterials == null || singlePageFailed) return;
            Shader shader = SinglePageShader ? SinglePageShader : Shader.Find("MaxTerrain/SinglePageTerrain");
            if (shader && shader.isSupported) SetTerrainShader(shader);
            else FailSinglePage(new InvalidOperationException("Single-page terrain shader is missing or unsupported."));
        }

        void SetTerrainShader(Shader shader)
        {
            if (material.shader == shader) return;
            var settings = new Material(material) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                material.shader = shader;
                material.CopyPropertiesFromMaterial(settings);
                material.enableInstancing = true;
                if (sourceMaterials != null) material.EnableKeyword("DD_SURFACE_ON");
            }
            finally { MeshResidentHeight.Destroy(settings); }
        }

        void UpdateSinglePage()
        {
            if (!SinglePageEnabled || sourceMaterials == null)
            {
                SinglePage?.Bind(material, 0);
                return;
            }
            if (singlePageFailed) return;
            try
            {
                if (SinglePage == null)
                    SinglePage = new MeshSinglePageRvt(source, controlDirectory, sourceMaterials, material);
                if (singlePageRebuildRequested || SinglePage.NeedsRebuild(SinglePageOrigin, SinglePageWorldSize, SinglePageResolution, material))
                {
                    SinglePage.Rebuild(SinglePageOrigin, SinglePageWorldSize, SinglePageResolution, material);
                    singlePageRebuildRequested = false;
                }
                SinglePage.Bind(material, SinglePageMode);
                SinglePageError = null;
            }
            catch (Exception exception) { FailSinglePage(exception); }
        }

        void FailSinglePage(Exception exception)
        {
            SinglePageError = exception.Message;
            singlePageFailed = true;
            material.SetVector("_DDRvtInfo", Vector4.zero);
            Debug.LogException(exception);
        }

        public void RequestSinglePageRebuild()
        {
            singlePageFailed = false;
            singlePageRebuildRequested = true;
        }

        public void Dispose()
        {
            MaxRenderBridge.Unregister(this);
            MaxRenderBridge.InvalidateShadows(camera);
            passes.Dispose();
            ReleaseWorldVt();
            SinglePage?.Dispose();
            Controls?.Dispose();
            overlays?.Dispose();
            Height?.Dispose();
            Geometry.Dispose();
            MeshResidentHeight.Destroy(material);
        }
    }
}
