using MaxSRP;
using System;
using System.IO;
using UnityEngine;

namespace MaxTerrain
{
    // Comparison 保留历史对照；Authoring 编辑当前对象；Published 只读本地烘焙快照。
    public enum MeshTerrainWorkflowMode { Comparison, Authoring, Published }

    // 对照场景管理公共数据、联动机位和显示选项。绘制从两侧各自的相机入口执行。
    [DisallowMultipleComponent]
    public sealed class MeshComparison : MonoBehaviour
    {
        [Header("Local terrain demo workflow")]
        public MeshTerrainWorkflowMode workflowMode;
        public MeshTerrainPublication publication;
        [Tooltip("历史对照场景可单独启用；制作/发布场景自动启用实时覆盖。")]
        public bool liveOverlays;
        public bool UsesLiveOverlays => liveOverlays || workflowMode != MeshTerrainWorkflowMode.Comparison;
        public MeshWorldVtOverlaySet EffectiveOverlaySet => workflowMode == MeshTerrainWorkflowMode.Published
            ? (publication ? publication.overlays : null) : worldVtOverlays;
        public MeshMaterialSet EffectiveMaterials => workflowMode == MeshTerrainWorkflowMode.Published && publication
            ? publication.surfaceMaterials : surfaceMaterials;
        public string EffectiveControlDirectory => workflowMode == MeshTerrainWorkflowMode.Published && publication
            ? publication.controlRelativeDirectory : controlRelativeDirectory;
        public Camera directCamera, deltaCamera;
        public Shader geometryShader, directShader;
        public bool surfaceEnabled;
        public bool hexTiling = true;
        public MeshMaterialSet surfaceMaterials;
        public string controlRelativeDirectory = "DirectDeltaControls16LandscapeV5";
        [Range(0, 11)] public int surfaceDebug;
        public Light sun;
        [Header("Delta single-page RVT")]
        public bool singlePageRvt;
        public Shader singlePageShader, singlePageProducerShader;
        public Vector2 singlePageOrigin = new Vector2(8128, 7456);
        [Range(4, 128)] public float singlePageWorldSize = 32;
        public int singlePageResolution = 2048;
        [Range(0, 2)] public int singlePageMode = 1;
        [Header("Delta world RVT + SVT")]
        public bool worldVirtualTexture;
        public Shader worldVtConsumerShader;
        public string worldVtArchive = MeshWorldVtArchive.DefaultRelativeDirectory;
        [Range(1, 4)] public int worldVtPageBudget = 4;
        [Range(0, 3)] public int worldVtMode = 1;
        [Range(-4, 6)] public float worldVtMipBias;
        public MeshVtMaxAnisotropy worldVtMaxAnisotropy = MeshVtMaxAnisotropy.X4;
        [Tooltip("静态道路及旧版覆盖资产。场景贴花由MeshWorldVtDecal独立对象提供；修改后重烘SVT并重新Play。")]
        public MeshWorldVtOverlaySet worldVtOverlays;

        public string datasetRelativeDirectory = "DirectDeltaMaladetas10km";
        [Range(0, 4)] public int debugMode;
        public int shot;
        [Range(0, 15)] public int referenceMaterial;

        public MeshHeightSource Source { get; private set; }
        public string Error { get; private set; }
        public bool IsReady => direct != null && delta != null;
        public string DatasetDirectory => Path.Combine(Application.streamingAssetsPath,
            workflowMode == MeshTerrainWorkflowMode.Published && publication ? publication.datasetRelativeDirectory : datasetRelativeDirectory);
        public Shader ResolveDirectShader() => directShader ? directShader : Shader.Find("MaxTerrain/DirectReference");

        DirectDeltaTerrain direct;
        DeltaTerrain delta;
        MeshCdlod forest;
        MeshMaterialResident materials;
        bool moving, started;
        float route;
        GUIStyle heading, label, referenceLabel;

        void OnEnable()
        {
            if (started && Application.isPlaying) Initialize();
        }

        void Start()
        {
            started = true;
            Initialize();
        }

        void OnDisable() => Release();

        void Initialize()
        {
            Release();
            Error = null;
            try
            {
                // 发布场景只读导出的独立资产，不收集美术场景中的可编辑 GameObject。
                if (workflowMode == MeshTerrainWorkflowMode.Published)
                {
                    if (!publication) throw new InvalidOperationException("Select a baked terrain publication.");
                    publication.Validate();
                    datasetRelativeDirectory = publication.datasetRelativeDirectory;
                    controlRelativeDirectory = publication.controlRelativeDirectory;
                    surfaceMaterials = publication.surfaceMaterials;
                    hexTiling = publication.hexTiling;
                    worldVtArchive = publication.archiveRelativeDirectory;
                    worldVirtualTexture = true;
                    surfaceEnabled = true;
                }
                if (workflowMode == MeshTerrainWorkflowMode.Authoring) worldVirtualTexture = false;
                Shader directTerrainShader = ResolveDirectShader();
                if (!directCamera || !deltaCamera || directCamera == deltaCamera || !geometryShader || !directTerrainShader)
                    throw new InvalidOperationException("The comparison requires two distinct cameras and two terrain shaders.");

                Source = new MeshHeightSource(DatasetDirectory);
                forest = CreateGeometry(delta: true);
                string controls = Path.Combine(Application.streamingAssetsPath, controlRelativeDirectory);
                if (surfaceEnabled)
                {
                    if (!surfaceMaterials) throw new InvalidOperationException("The shaded comparison requires its compressed sixteen-material set.");
                    MeshControlBake.ValidateMaterialContract(controls, surfaceMaterials);
                    materials = new MeshMaterialResident(surfaceMaterials);
                }

                // 静态节点、patch、源高度读取器和压缩材质可复用。选区和渲染资源由各侧持有。
                direct = new DirectDeltaTerrain(directCamera, directTerrainShader,
                    forest.CreateSelection(legacyRanges: surfaceEnabled), Source, materials, controls, sun);
                if (surfaceEnabled && UsesLiveOverlays)
                    direct.ConfigureOverlays(EffectiveOverlaySet, CollectWorldVtDecals,
                        workflowMode == MeshTerrainWorkflowMode.Authoring, CollectWorldVtRoads,
                        immutable: workflowMode == MeshTerrainWorkflowMode.Published);
                delta = CreateDeltaRenderer();

                SetShot(shot);
                if (surfaceEnabled && singlePageRvt && !worldVirtualTexture) FocusSinglePage();
                ApplyDisplaySettings();
            }
            catch (Exception exception)
            {
                Release();
                Error = exception.Message;
                Debug.LogException(exception, this);
            }
        }


        DeltaTerrain CreateDeltaRenderer()
        {
            var result = new DeltaTerrain(deltaCamera, geometryShader,
                forest.CreateSelection(legacyRanges: false), Source, materials,
                Path.Combine(Application.streamingAssetsPath, EffectiveControlDirectory), sun);
            try
            {
                var set = EffectiveOverlaySet;
                if (surfaceEnabled && UsesLiveOverlays)
                    result.ConfigureOverlays(set, CollectWorldVtDecals, CollectWorldVtRoads,
                        immutable: workflowMode == MeshTerrainWorkflowMode.Published);
                if (surfaceEnabled && worldVirtualTexture)
                {
                    string archiveName = worldVtArchive;
                    if (string.IsNullOrWhiteSpace(archiveName) || archiveName == "DirectDeltaWorldSvt01" || archiveName == "DirectDeltaWorldSvt02")
                        archiveName = MeshWorldVtArchive.DefaultRelativeDirectory;
                    result.ConfigureWorldVt(worldVtConsumerShader, singlePageProducerShader,
                        Path.Combine(Application.streamingAssetsPath, archiveName), set,
                        CollectWorldVtDecals, CollectWorldVtRoads,
                        immutableOverlayInputs: workflowMode == MeshTerrainWorkflowMode.Published);
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        /// <summary>烘焙完成后显式重新载入；旧实例和旧 IO 在 Initialize 的 Release 中一同释放。</summary>
        public void Reinitialize()
        {
            if (Application.isPlaying && isActiveAndEnabled) Initialize();
        }

        void Update()
        {
            if (!Application.isPlaying || !IsReady) return;
            if (surfaceEnabled && workflowMode != MeshTerrainWorkflowMode.Published && Input.GetKeyDown(KeyCode.H))
                hexTiling = !hexTiling;
            if (surfaceEnabled && worldVirtualTexture) UpdateWorldVtControls();
            else if (surfaceEnabled && singlePageRvt)
            {
                if (Input.GetKeyDown(KeyCode.V)) singlePageMode = singlePageMode == 0 ? 1 : 0;
                if (Input.GetKeyDown(KeyCode.B)) singlePageMode = singlePageMode == 2 ? 1 : 2;
                if (Input.GetKeyDown(KeyCode.G)) FocusSinglePage();
                if (Input.GetKeyDown(KeyCode.U)) delta.RequestSinglePageRebuild();
            }
            int shotCount = surfaceEnabled ? 9 : 6;
            for (int i = 0; i < shotCount; i++) if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i))) SetShot(i);
            if (surfaceEnabled && Input.GetKeyDown(KeyCode.LeftBracket)) SetReferenceMaterial(referenceMaterial - 1);
            if (surfaceEnabled && Input.GetKeyDown(KeyCode.RightBracket)) SetReferenceMaterial(referenceMaterial + 1);
            bool geometryKeys = !surfaceEnabled || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            for (int i = 0; i < 12; ++i)
            {
                if (!Input.GetKeyDown((KeyCode)((int)KeyCode.F1 + i))) continue;
                if (!geometryKeys || i >= 5)
                {
                    debugMode = 0;
                    surfaceDebug = surfaceEnabled && i < 11 ? i + 1 : 0;
                }
                else
                {
                    debugMode = i;
                    if (i == 0) surfaceDebug = 0;
                }
            }
            if (Input.GetKeyDown(KeyCode.R)) ResetClipmap();
            if (Input.GetKeyDown(KeyCode.P)) { moving = !moving; route = 0; }
            if (moving)
            {
                route += Time.unscaledDeltaTime;
                float x = 1200 + Mathf.PingPong(route * 90, 7600);
                float z = 3500 + Mathf.Sin(route * .045f) * 1200;
                var position = new Vector3(x, Source.SampleMeters(x, z, 0) + 65, z);
                directCamera.transform.SetPositionAndRotation(position, Quaternion.Euler(13, 35, 0));
            }
            else if (Input.GetMouseButton(1))
            {
                Vector3 angles = directCamera.transform.eulerAngles;
                float pitch = angles.x > 180 ? angles.x - 360 : angles.x;
                directCamera.transform.rotation = Quaternion.Euler(Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * 2, -88, 88), angles.y + Input.GetAxis("Mouse X") * 2, 0);
                var velocity = new Vector3(Input.GetAxisRaw("Horizontal"), 0, Input.GetAxisRaw("Vertical"));
                Vector3 step = directCamera.transform.TransformDirection(velocity);
                step += Vector3.up * (Input.GetKey(KeyCode.E) ? 1 : Input.GetKey(KeyCode.C) ? -1 : 0);
                directCamera.transform.position += step * (Input.GetKey(KeyCode.LeftShift) ? 700 : 130) * Time.unscaledDeltaTime;
            }
            Sync();
            ApplyDisplaySettings();
        }

        void ApplyDisplaySettings()
        {
            // Published 的材质算法属于烘焙配方；运行时只允许切换诊断，
            // 不用一个预览快捷键把新 RVT 算法与旧 SVT 归档混在一起。
            bool effectiveHex = workflowMode == MeshTerrainWorkflowMode.Published && publication
                ? publication.hexTiling : hexTiling;
            direct.SetDisplay(debugMode, surfaceDebug, effectiveHex);
            delta.SetDisplay(debugMode, surfaceDebug, effectiveHex);
            delta.WorldMode = worldVtMode;
            delta.PageBudget = worldVtPageBudget;
            delta.MipBias = worldVtMipBias;
            delta.MaxAnisotropy = (int)worldVtMaxAnisotropy;
            delta.SinglePageEnabled = singlePageRvt;
            delta.SinglePageShader = singlePageShader;
            delta.SinglePageOrigin = singlePageOrigin;
            delta.SinglePageWorldSize = singlePageWorldSize;
            delta.SinglePageResolution = singlePageResolution;
            delta.SinglePageMode = singlePageMode;
        }

        public void Sync()
        {
            if (!directCamera || !deltaCamera) return;
            deltaCamera.transform.SetPositionAndRotation(directCamera.transform.position, directCamera.transform.rotation);
            deltaCamera.fieldOfView = directCamera.fieldOfView;
            deltaCamera.nearClipPlane = directCamera.nearClipPlane;
            deltaCamera.farClipPlane = directCamera.farClipPlane;
        }
        // 明确地形归属，避免另一场景或另一地形的贴花进入当前VT生产。
        public MeshWorldVtDecal[] CollectWorldVtDecals() => workflowMode == MeshTerrainWorkflowMode.Published
            ? Array.Empty<MeshWorldVtDecal>() : MeshWorldVtDecal.Collect(this);
        // 发布场景已持有冻结道路记录，不再读取制作场景中的可拖动对象。
        public MeshWorldVtRoad[] CollectWorldVtRoads() => workflowMode == MeshTerrainWorkflowMode.Published
            ? Array.Empty<MeshWorldVtRoad>() : MeshWorldVtRoad.Collect(this);

        public void SetShot(int value)
        {
            if (!directCamera) return;
            shot = Mathf.Clamp(value, 0, surfaceEnabled ? 8 : 5); moving = false;
            MeshWorldVtDecal[] decals = shot == 0 ? CollectWorldVtDecals() : Array.Empty<MeshWorldVtDecal>();
            MeshWorldVtOverlaySet fixedSet = EffectiveOverlaySet;
            bool legacyDecal = fixedSet && fixedSet.decals != null && fixedSet.decals.Length > 0;
            if (shot == 0 && Source != null && (decals.Length > 0 || legacyDecal))
            {
                // Only the new overlay scene changes shot 1; the accepted reference scene stays intact.
                Vector3 mark = decals.Length > 0 ? decals[0].transform.position : fixedSet.decals[0].position;
                Vector3 target = Above(mark.x, mark.z, 0);
                directCamera.transform.position = Above(mark.x - 8, mark.z - 32, 22);
                directCamera.transform.LookAt(target);
            }
            else if (surfaceEnabled && shot == 0 && Source != null && Source.Metadata.recipe == MeshLandscapeHeight.Recipe)
            {
                Vector3 target = Above(1792, 1024, 0);
                directCamera.transform.position = target + new Vector3(0, 10, -6);
                directCamera.transform.LookAt(target);
            }
            else if (surfaceEnabled && shot == 7)
            {
                float center = MeshControlLandscape.SwatchOrigin + MeshControlLandscape.SwatchCellSize * MeshControlLandscape.SwatchCount * .5f;
                directCamera.transform.SetPositionAndRotation(Above(center, center, 650), Quaternion.Euler(90, 0, 0));
            }
            else if (surfaceEnabled && shot == 8)
            {
                Vector2 center = MeshControlLandscape.SwatchCenter(referenceMaterial);
                Vector3 target = Above(center.x, center.y, 0);
                directCamera.transform.position = target + new Vector3(0, 9, -7);
                directCamera.transform.LookAt(target);
            }
            else if (shot == 6)
            {
                Vector3 position = Source != null
                    ? Above(221.6581f, 5159.819f, 40) : new Vector3(221.6581f, 169.3483f, 5159.819f);
                directCamera.transform.SetPositionAndRotation(position,
                    Quaternion.Euler(78.105f, -28.775f, 0));
            }
            else
            {
                Vector3 position, target;
                switch (shot)
                {
                    case 1: position = Above(5000, 4400, 45); target = Above(5200, 4700, 5); break;
                    case 2: position = Above(7900, 6600, 100); target = Above(7200, 6500, 0); break;
                    case 3: position = new Vector3(5000, 10500, -1200); target = new Vector3(5000, 250, 5000); break;
                    case 4: position = Above(9960, 5100, 35); target = Above(9350, 5300, 0); break;
                    case 5: position = Above(1023.9f, 2047.9f, 30); target = Above(1300, 2400, 0); break;
                    default: position = Above(1200, 1400, 55); target = Above(1600, 1850, 0); break;
                }
                directCamera.transform.position = position; directCamera.transform.LookAt(target);
            }
            Sync();
            delta?.InvalidateFeedback();
            // Explicit cuts retire the host's shared TAA/exposure history even
            // when the move is below its automatic discontinuity threshold.
            MaxRenderBridge.InvalidateTemporalHistory(directCamera);
            MaxRenderBridge.InvalidateTemporalHistory(deltaCamera);
        }
        public void SetReferenceMaterial(int id)
        {
            referenceMaterial = (id % MeshControlContract.MaterialCount + MeshControlContract.MaterialCount) % MeshControlContract.MaterialCount;
            SetShot(8);
        }
        Vector3 Above(float x, float z, float offset) => new Vector3(x, (Source == null ? 200 : Source.SampleMeters(x, z, 0)) + offset, z);
        void ResetClipmap()
        {
            direct.ResetHeight();
            delta.ResetHeight();
        }

        public MeshCdlod CreateGeometry(bool delta) =>
            new MeshCdlod(DatasetDirectory, useBudgetRanges: surfaceEnabled && delta);

        public void FocusSinglePage()
        {
            if (Source == null || !directCamera) return;
            Vector2 center = singlePageOrigin + Vector2.one * (singlePageWorldSize * .5f);
            Vector3 target = Above(center.x, center.y, 0);
            float dx = Source.SampleMeters(center.x + 1, center.y, 0) - Source.SampleMeters(center.x - 1, center.y, 0);
            float dz = Source.SampleMeters(center.x, center.y + 1, 0) - Source.SampleMeters(center.x, center.y - 1, 0);
            Vector3 downhill = new Vector3(-dx, 0, -dz);
            if (downhill.sqrMagnitude < .01f) downhill = Vector3.back;
            directCamera.transform.position = target + downhill.normalized * (singlePageWorldSize * 2) + Vector3.up * (singlePageWorldSize * .45f);
            directCamera.transform.LookAt(target);
            moving = false;
            Sync();
            delta?.InvalidateFeedback();
            // Explicit cuts retire the host's shared TAA/exposure history even
            // when the move is below its automatic discontinuity threshold.
            MaxRenderBridge.InvalidateTemporalHistory(directCamera);
            MaxRenderBridge.InvalidateTemporalHistory(deltaCamera);
        }

        void UpdateWorldVtControls()
        {
            if (Input.GetKeyDown(KeyCode.V)) worldVtMode = worldVtMode == 0 ? 1 : 0;
            if (Input.GetKeyDown(KeyCode.B)) worldVtMode = worldVtMode == 2 ? 1 : 2;
            if (Input.GetKeyDown(KeyCode.N)) worldVtMode = worldVtMode == 3 ? 1 : 3;
            if (Input.GetKeyDown(KeyCode.G)) FocusSinglePage();
            if (Input.GetKeyDown(KeyCode.J)) worldVtPageBudget = worldVtPageBudget == 1 ? 4 : 1;
        }

        void DrawWorldVtStatus()
        {
            string status = "AVT + RVT/SVT | V cache | B source | N mip gap | G cliff | J budget";
            status += "\nProjection: stochastic single-plane";
            MeshWorldVirtualTexture vt = delta.WorldVt;
            if (vt != null)
            {
                status += "\nResident: " + vt.ResidentRvtCount + " RVT / " + vt.ResidentSvtCount + " SVT | requests " +
                    vt.RequestedCount + " | published " + vt.PagesPublishedThisFrame + "/" + worldVtPageBudget;
                status += vt.PinnedReady ? " | coarse coverage ready" : " | loading world coverage";
                status += "\nPool " + (vt.GpuBytes / 1000000f).ToString("F2") + " MB / " + MeshWorldVirtualTexture.Slots +
                    " slots | AVT regions " + vt.AdaptiveTable.ActiveCount + " | mip0 419 texels/m";
                status += "\nFeedback MainPass /" + delta.FeedbackDivisor + " | AF" + delta.MaxAnisotropy + " | " + MeshWorldVirtualTexture.FeedbackReadbackBytes / 1024 +
                    " KiB/readback | unique " + vt.LatestFeedbackUniqueKeys + " | deferred frames " + vt.FeedbackTruncatedFrames + " | insert failures " + vt.FeedbackOverflowFrames;
            }
            if (!string.IsNullOrEmpty(delta.WorldVtError)) status += "\n" + delta.WorldVtError;
            float half = Screen.width * .5f;
            GUI.Box(new Rect(half, 48, half, 122), GUIContent.none);
            GUI.Label(new Rect(half + 8, 50, half - 16, 118), status, label);
        }

        void OnGUI()
        {
            if (heading == null)
            {
                heading = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.UpperCenter, normal = { textColor = Color.white } };
                label = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = Color.white } };
                referenceLabel = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter, wordWrap = true, normal = { textColor = Color.white } };
            }
            GUI.Box(new Rect(0, 0, Screen.width, 46), GUIContent.none);
            if (workflowMode == MeshTerrainWorkflowMode.Authoring)
            {
                GUI.Label(new Rect(0, 10, Screen.width, 35), "DirectDelta | AUTHORING | floating-point surface baseline", heading);
                GUI.Box(new Rect(0, Screen.height - 74, Screen.width, 74), GUIContent.none);
                GUI.Label(new Rect(12, Screen.height - 68, Screen.width - 24, 66),
                    !string.IsNullOrEmpty(Error) ? Error : !string.IsNullOrEmpty(direct?.OverlayError) ? direct.OverlayError :
                    "Edit static roads / decals in Scene View. Bake local terrain to update the published scene.\n" +
                    "RMB + WASD: fly | F1-F5: material channels", label);
                return;
            }
            GUI.Label(new Rect(0, 10, Screen.width * .5f, 35), "DirectDelta | 10 x 10 km | HEIGHT CLIPMAP | CDLOD", heading);
            GUI.Label(new Rect(Screen.width * .5f, 10, Screen.width * .5f, 35), "Delta | 10 x 10 km | HEIGHT CLIPMAP | CDLOD", heading);
            GUI.Box(new Rect(0, Screen.height - 144, Screen.width, 144), GUIContent.none);
            if (!string.IsNullOrEmpty(Error)) { GUI.Label(new Rect(12, Screen.height - 100, Screen.width - 24, 90), Error, label); return; }
            if (!IsReady) { GUI.Label(new Rect(12, Screen.height - 100, Screen.width - 24, 90), "Press Play. If height data is absent: Tools > DirectDelta Mesh > Build data and comparison scene.", label); return; }
            Vector3 p = directCamera.transform.position;
            string stats = (surfaceEnabled ? "1-9" : "1-6") + " views | RMB + WASD fly, E/C up/down, Shift fast | P move | R reset both height clipmaps\n" +
                (surfaceEnabled ? "F1 Albedo | F2 Height | F3 Normal | F4 Roughness | F5 AO | F6 ID | F7 Weight\n" :
                    "F1 surface | F2 CDLOD | F3 morph | F4 height level | F5 grid\n") +
                "Height GPU payload: DirectDelta " + (direct.Height.GpuBytes / 1048576f).ToString("F2") + " MiB | Delta " + (delta.Height.GpuBytes / 1048576f).ToString("F2") + " MiB; source CPU cache " + (Source.CpuCacheBytes / 1048576f).ToString("F2") + " MiB\n" +
                "Patches / draws: DirectDelta " + direct.Geometry.Selections.Count + " / " + direct.DrawCount +
                " | Delta " + delta.Geometry.Selections.Count + " / " + delta.DrawCount + " | pos " + p.ToString("F1");
            if (materials != null)
                stats += "\nShared materials: 16 | " + materials.Format + " " + surfaceMaterials.resolution + "px full mips " + (materials.GpuBytes/1048576f).ToString("F2") + " MiB | IDs: Direct " + (direct.Controls.GpuBytes/1048576f).ToString("F2") + " MiB resident / Delta " + (delta.Controls.GpuBytes/1048576f).ToString("F2") + " MiB Clipmap | " + (worldVirtualTexture ? "World RVT + SVT" : singlePageRvt ? "Single-page RVT / DTA+SVT off" : "DTA/RVT/SVT off") +
                    "\n" + (workflowMode == MeshTerrainWorkflowMode.Published ? "Baked Hex " : "H Hex ") +
                    (hexTiling ? "ON" : "OFF") + " | 8 Swatches | 9 Closeup | [ / ] Material " + referenceMaterial + ": " + surfaceMaterials.names[referenceMaterial] + " | F12 Lit";
            GUI.Label(new Rect(12, Screen.height - 136, Screen.width - 24, 136), stats, label);
            if (surfaceEnabled && worldVirtualTexture) DrawWorldVtStatus();
            else if (surfaceEnabled && singlePageRvt)
            {
                string pageStatus = "Delta RVT | V cache " + (singlePageMode == 0 ? "OFF" : "ON") +
                    " | B coverage | G focus | U rebuild";
                MeshSinglePageRvt page = delta.SinglePage;
                if (page != null)
                    pageStatus += "\nPage " + page.Origin + " / " + page.WorldSize + "m | generations " +
                        page.GenerationCount + " | page GPU " + (page.GpuBytes / 1048576f).ToString("F2") + " MiB";
                pageStatus += "\nFixed 1m page sources | outside page: direct shading";
                if (!string.IsNullOrEmpty(delta.SinglePageError)) pageStatus += "\nRVT error: " + delta.SinglePageError;
                GUI.Box(new Rect(Screen.width * .5f, 48, Screen.width * .5f, 68), GUIContent.none);
                GUI.Label(new Rect(Screen.width * .5f + 8, 50, Screen.width * .5f - 16, 64), pageStatus, label);
            }
            if (surfaceEnabled && shot == 7 && surfaceMaterials)
            {
                DrawReferenceLabels(directCamera);
                DrawReferenceLabels(deltaCamera);
            }
        }
        void DrawReferenceLabels(Camera camera)
        {
            if (!camera) return;
            for (int id = 0; id < MeshControlContract.MaterialCount; ++id)
            {
                Vector2 center = MeshControlLandscape.SwatchCenter(id);
                Vector3 point = camera.WorldToScreenPoint(Above(center.x, center.y, .5f));
                if (point.z <= 0) continue;
                Rect box = new Rect(point.x - 66, Screen.height - point.y - 21, 132, 42);
                Color previous = GUI.color;
                GUI.color = new Color(0, 0, 0, .65f); GUI.DrawTexture(box, Texture2D.whiteTexture);
                GUI.color = previous;
                if (GUI.Button(box, id + "  " + surfaceMaterials.names[id], referenceLabel)) SetReferenceMaterial(id);
            }
        }
        void Release()
        {
            direct?.Dispose();
            direct = null;
            delta?.Dispose();
            delta = null;
            materials?.Dispose();
            materials = null;
            forest?.Dispose();
            forest = null;
            Source?.Dispose();
            Source = null;
        }
    }
}
