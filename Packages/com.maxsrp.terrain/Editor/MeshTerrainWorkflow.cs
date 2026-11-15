using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MaxTerrain.Editor
{
    /// <summary>
    /// 求职 Demo 的本地工作流：Direct 制作预览 → 手动烘焙 → Delta 读取固定结果。
    /// 文件都在同一工程内；不引入 Bundle、下载、增量发布或运行中热替换。
    /// </summary>
    public static class MeshTerrainWorkflow
    {
        public const string AuthoringScene = "Assets/Samples/MaxTerrain/Scenes/DirectDeltaAuthoring10km.unity";
        public const string PublishedScene = "Assets/Samples/MaxTerrain/Scenes/DeltaPublished10km.unity";
        public const string PublicationPath = "Assets/Samples/MaxTerrain/Data/TerrainPublication.asset";
        public const string AuthoringSetPath = "Assets/Samples/MaxTerrain/Data/TerrainAuthoringOverlays.asset";
        const string PublishedFolder = "Assets/Samples/MaxTerrain/Data/Published";
        const string OutputRoot = "DirectDeltaWorldSvt05";

        [MenuItem("Tools/MaxTerrain/Workflow/1 - Configure authoring and published scenes")]
        public static void BuildScenes()
        {
            RequireEdit();
            EnsureFolder(PublishedFolder);
            bool newAuthoring = !AssetDatabase.LoadAssetAtPath<SceneAsset>(AuthoringScene);
            // MaxTerrain ships the two active workflow scenes directly. Historical
            // Built-in checkpoints are not hidden dependencies of this project.
            if (newAuthoring)
                throw new FileNotFoundException("Restore the imported MaxTerrain authoring scene before configuring the workflow.", AuthoringScene);
            Scene previous = SceneManager.GetActiveScene();
            Scene authoring = OpenAdditive(AuthoringScene, out bool closeAuthoring);
            try
            {
                MeshComparison rig = FindRig(authoring);
                // 仅首次建立副本；重跑工具保留美术已经修改的道路和贴花 Transform。
                if (newAuthoring || !rig.worldVtOverlays)
                {
                    var set = AssetDatabase.LoadAssetAtPath<MeshWorldVtOverlaySet>(AuthoringSetPath);
                    if (!set)
                    {
                        set = rig.worldVtOverlays ? Object.Instantiate(rig.worldVtOverlays) : ScriptableObject.CreateInstance<MeshWorldVtOverlaySet>();
                        set.name = "Terrain authoring roads";
                        AssetDatabase.CreateAsset(set, AuthoringSetPath);
                    }
                    rig.worldVtOverlays = set;
                }
                rig.workflowMode = MeshTerrainWorkflowMode.Authoring;
                rig.worldVirtualTexture = false;
                rig.singlePageRvt = false;
                rig.surfaceEnabled = true;
                rig.name = "DirectDelta - high fidelity authoring";
                rig.directCamera.enabled = true;
                rig.directCamera.rect = new Rect(0, 0, 1, 1);
                rig.deltaCamera.enabled = false;
                rig.deltaCamera.gameObject.SetActive(false);

                var publication = AssetDatabase.LoadAssetAtPath<MeshTerrainPublication>(PublicationPath);
                if (!publication)
                    throw new InvalidOperationException("The terrain publication is missing. Bake the authoring scene to create a new publication.");
                rig.publication = publication;
                // 初次建立固定04检查点后再改变作者道路。新的矩形仍需手动烘焙；
                // 不把旧SVT误称为新道路的缓存，也不覆盖已有出版版本。
                MigrateDemoRoadToRectangle(rig);
                EditorUtility.SetDirty(rig);
                Save(authoring);
                if (rig.worldVtOverlays) AssetDatabase.SaveAssetIfDirty(rig.worldVtOverlays);

                bool newPublished = !AssetDatabase.LoadAssetAtPath<SceneAsset>(PublishedScene);
                if (newPublished && !AssetDatabase.CopyAsset(AuthoringScene, PublishedScene))
                    throw new InvalidOperationException("Could not create the Delta publication validation scene.");
                Scene published = OpenAdditive(PublishedScene, out bool closePublished);
                try
                {
                    MeshComparison runtime = FindRig(published);
                    runtime.workflowMode = MeshTerrainWorkflowMode.Published;
                    runtime.publication = publication;
                    runtime.worldVirtualTexture = true;
                    runtime.singlePageRvt = false;
                    runtime.surfaceEnabled = true;
                    runtime.worldVtMode = 1;
                    runtime.worldVtArchive = publication.archiveRelativeDirectory;
                    runtime.worldVtOverlays = null;
                    runtime.name = "DirectDelta baseline vs Delta - published terrain";
                    runtime.directCamera.enabled = true;
                    runtime.directCamera.rect = new Rect(0, 0, .5f, 1);
                    runtime.deltaCamera.gameObject.SetActive(true);
                    runtime.deltaCamera.enabled = true;
                    runtime.deltaCamera.rect = new Rect(.5f, 0, .5f, 1);
                    // 发布场景不持有静态作者对象；近景 RVT 使用 publication 内的固定记录。
                    foreach (GameObject root in published.GetRootGameObjects())
                        foreach (MeshWorldVtDecal decal in root.GetComponentsInChildren<MeshWorldVtDecal>(true))
                            if (decal.targetTerrain == runtime)
                                RemoveAuthorComponent(decal);
                    foreach (GameObject root in published.GetRootGameObjects())
                        foreach (MeshWorldVtRoad road in root.GetComponentsInChildren<MeshWorldVtRoad>(true))
                            if (road.targetTerrain == runtime)
                                RemoveAuthorComponent(road);
                    EditorUtility.SetDirty(runtime);
                    Save(published);
                }
                finally { if (closePublished) EditorSceneManager.CloseScene(published, true); }
                AssetDatabase.SaveAssetIfDirty(publication);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                if (closeAuthoring) EditorSceneManager.CloseScene(authoring, true);
            }
            AssetDatabase.Refresh();
        }

        [MenuItem("Tools/MaxTerrain/Workflow/2 - Open DirectDelta authoring")]
        public static void OpenAuthoring()
        {
            RequireEdit();
            if (!AssetDatabase.LoadAssetAtPath<SceneAsset>(AuthoringScene)) BuildScenes();
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(AuthoringScene);
        }

        [MenuItem("Tools/MaxTerrain/Workflow/3 - Bake local terrain")]
        public static void BakeActiveAuthoring()
        {
            RequireEdit();
            MeshComparison rig = FindRig(SceneManager.GetActiveScene());
            if (rig.workflowMode != MeshTerrainWorkflowMode.Authoring)
                throw new InvalidOperationException("Open the DirectDelta authoring scene before baking.");
            Publish(rig, true);
        }

        [MenuItem("Tools/MaxTerrain/Workflow/4 - Open Delta publication validation")]
        public static void OpenPublished()
        {
            RequireEdit();
            if (!AssetDatabase.LoadAssetAtPath<SceneAsset>(PublishedScene)) BuildScenes();
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(PublishedScene);
        }

        /// <summary>
        /// 每次写入新的版本目录和固定数据资产，所有校验完成后才切换唯一 publication。
        /// 失败/取消保留旧引用，未完成的版本保留供排错，不自动递归清理目录。
        /// </summary>
        public static MeshTerrainPublication Publish(MeshComparison rig, bool showProgress)
        {
            RequireEdit();
            if (!rig || rig.workflowMode != MeshTerrainWorkflowMode.Authoring)
                throw new InvalidOperationException("Only an authoring terrain can publish local data.");
            EnsureFolder(PublishedFolder);
            string version = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            MeshWorldVtOverlaySet frozen = Freeze(rig, version, out MeshMaterialSet materials, out string overlayHash);
            string relative = OutputRoot + "/" + version;
            MeshTerrainPublication candidate = ScriptableObject.CreateInstance<MeshTerrainPublication>();
            try
            {
                // 固定材质参数与 Hex 开关也必须与描述一起冻结，不能一边烘焙一边读作者参数。
                Fill(candidate, rig, frozen, materials, relative, version, null, overlayHash, null, null);
                var result = MeshWorldVtBake.BakeLocal(rig, frozen, Path.Combine(Application.streamingAssetsPath, relative),
                    showProgress, materials, candidate.hexTiling);
                // 改的是作者数据时，不能把旧快照发布成“当前地图”；原 publication 仍保留。
                using (var current = MeshWorldVtOverlaySet.CreateSnapshot(rig.worldVtOverlays,
                    rig.CollectWorldVtDecals, rig.CollectWorldVtRoads))
                    if (current.ContentHash != overlayHash || rig.hexTiling != candidate.hexTiling ||
                        JsonUtility.ToJson(rig.surfaceMaterials) != JsonUtility.ToJson(materials) ||
                        rig.datasetRelativeDirectory != candidate.datasetRelativeDirectory ||
                        rig.controlRelativeDirectory != candidate.controlRelativeDirectory)
                        throw new InvalidDataException("Authoring inputs changed during baking. The previous publication remains active.");
                candidate.recipeIdentity = result.recipe;
                candidate.rawPayloadSha256 = result.rawPayloadSha256;
                candidate.bc3PayloadSha256 = result.bc3PayloadSha256;
                candidate.Validate();
                MeshTerrainPublication target = AssetDatabase.LoadAssetAtPath<MeshTerrainPublication>(PublicationPath);
                if (!target)
                {
                    target = ScriptableObject.CreateInstance<MeshTerrainPublication>();
                    AssetDatabase.CreateAsset(target, PublicationPath);
                }
                // 只有这里修改运行场景所引用的小入口资产。固定记录、贴图和归档已全部就绪。
                EditorUtility.CopySerialized(candidate, target);
                EditorUtility.SetDirty(target);
                AssetDatabase.SaveAssetIfDirty(target);
                rig.publication = target;
                EditorUtility.SetDirty(rig);
                Save(rig.gameObject.scene);
                Debug.Log($"Published local terrain {version}: {result.rawPages} RGBA8 pages, {result.compressedPages} BC3 pages. Reopen the validation scene to use it.", target);
                return target;
            }
            finally { Object.DestroyImmediate(candidate); AssetDatabase.Refresh(); }
        }

        static MeshTerrainPublication SeedCurrentArchive(MeshComparison rig)
        {
            // 第一次建场景时复用现有 04 页数据；仍创建独立的静态记录/遮罩/材质参数，
            // 并实际比较 producer 配方，避免把已经改过的作者对象配上旧 SVT。
            string directory = Path.Combine(Application.streamingAssetsPath, MeshWorldVtOverlayScene.ArchiveDirectory);
            var raw = ReadManifest(directory);
            var bc3 = ReadManifest(Path.Combine(directory, MeshWorldVtArchive.Bc3Subdirectory));
            MeshWorldVtOverlaySet frozen = Freeze(rig, "seed04", out MeshMaterialSet materials, out string hash);
            var settings = new Material(rig.geometryShader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                settings.SetFloat("_DDHexTiling", rig.hexTiling ? 1 : 0);
                using (var source = new MeshHeightSource(rig.DatasetDirectory))
                using (var resident = new MeshMaterialResident(materials))
                using (var producer = new MeshWorldVtProducer(source,
                    Path.Combine(Application.streamingAssetsPath, rig.controlRelativeDirectory), resident, rig.singlePageProducerShader, frozen))
                    if (producer.GetRecipeIdentity(settings) != raw.recipe || raw.recipe != bc3.recipe)
                        throw new InvalidDataException("Existing 04 SVT differs from the authoring contents. Bake local terrain before validation.");
            }
            finally { Object.DestroyImmediate(settings); }
            var publication = ScriptableObject.CreateInstance<MeshTerrainPublication>();
            Fill(publication, rig, frozen, materials, MeshWorldVtOverlayScene.ArchiveDirectory,
                "seed04", raw.recipe, hash, raw.payloadSha256, bc3.payloadSha256);
            publication.Validate();
            AssetDatabase.CreateAsset(publication, PublicationPath);
            return publication;
        }

        static MeshWorldVtArchive.Manifest ReadManifest(string path) =>
            JsonUtility.FromJson<MeshWorldVtArchive.Manifest>(File.ReadAllText(Path.Combine(path, MeshWorldVtArchive.ManifestName)));

        static void Fill(MeshTerrainPublication publication, MeshComparison rig, MeshWorldVtOverlaySet overlays,
            MeshMaterialSet materials, string archive, string version, string recipe, string hash, string raw, string compressed)
        {
            publication.name = "Local terrain publication";
            publication.overlays = overlays;
            publication.surfaceMaterials = materials;
            publication.datasetRelativeDirectory = rig.datasetRelativeDirectory;
            publication.controlRelativeDirectory = rig.controlRelativeDirectory;
            publication.archiveRelativeDirectory = archive;
            publication.hexTiling = rig.hexTiling;
            publication.versionId = version;
            publication.recipeIdentity = recipe;
            publication.overlayContentHash = hash;
            publication.rawPayloadSha256 = raw;
            publication.bc3PayloadSha256 = compressed;
            publication.authoringScenePath = rig.gameObject.scene.path;
        }

        static MeshWorldVtOverlaySet Freeze(MeshComparison rig, string version, out MeshMaterialSet materialCopy, out string hash)
        {
            var set = rig.worldVtOverlays ? Object.Instantiate(rig.worldVtOverlays) : ScriptableObject.CreateInstance<MeshWorldVtOverlaySet>();
            set.name = "Fixed terrain overlays - " + version;
            // 场景道路只是作者入口；发布后转换为已有两点/三角带契约，运行场景
            // 不再查找可编辑Road对象。保留资产中未迁移的历史道路以兼容旧检查点。
            var roads = new List<MeshWorldVtOverlaySet.Road>(set.roads ?? Array.Empty<MeshWorldVtOverlaySet.Road>());
            foreach (MeshWorldVtRoad component in rig.CollectWorldVtRoads()) roads.Add(component.Capture());
            set.roads = roads.ToArray();
            var decals = new List<MeshWorldVtOverlaySet.Decal>(set.decals ?? Array.Empty<MeshWorldVtOverlaySet.Decal>());
            foreach (MeshWorldVtDecal component in rig.CollectWorldVtDecals()) decals.Add(component.Capture());
            set.decals = decals.ToArray();
            // 先验证所有输入，避免对非法作者数据创建半成品资产。
            using (var snapshot = set.CreateSnapshot()) hash = snapshot.ContentHash;
            string path = AssetDatabase.GenerateUniqueAssetPath(PublishedFolder + "/Terrain-" + version + ".asset");
            AssetDatabase.CreateAsset(set, path);
            var masks = new Dictionary<Texture2D, Texture2D>();
            foreach (MeshWorldVtOverlaySet.Entry entry in Entries(set))
            {
                if (!entry.maskTexture) continue;
                if (!masks.TryGetValue(entry.maskTexture, out Texture2D copy))
                {
                    copy = Object.Instantiate(entry.maskTexture);
                    copy.name = "Fixed opacity - " + entry.maskTexture.name;
                    AssetDatabase.AddObjectToAsset(copy, set);
                    masks.Add(entry.maskTexture, copy);
                }
                entry.maskTexture = copy;
            }
            materialCopy = Object.Instantiate(rig.surfaceMaterials);
            materialCopy.name = "Fixed Surface16 material parameters";
            materialCopy.Validate();
            if (JsonUtility.ToJson(materialCopy) != JsonUtility.ToJson(rig.surfaceMaterials))
                throw new InvalidDataException("Freezing the Surface16 metadata changed its material parameters.");
            AssetDatabase.AddObjectToAsset(materialCopy, set);
            EditorUtility.SetDirty(set);
            EditorUtility.SetDirty(materialCopy);
            AssetDatabase.SaveAssetIfDirty(set);
            using (var snapshot = set.CreateSnapshot())
                if (snapshot.ContentHash != hash)
                    throw new InvalidDataException("Freezing opacity textures changed the static terrain inputs.");
            return set;
        }

        static IEnumerable<MeshWorldVtOverlaySet.Entry> Entries(MeshWorldVtOverlaySet set)
        {
            foreach (var road in set.roads) yield return road;
            foreach (var decal in set.decals) yield return decal;
        }

        /// <summary>
        /// 将未编辑的旧demo-road替换为一块可以直接拖动的10×120米矩形。
        /// 仅建立作者对象并标脏；BuildScenes负责先保存场景，再保存去掉旧记录的资产。
        /// 返回新建对象数。已存在对象（包括禁用对象）保持原Transform及材质参数。
        /// </summary>
        public static int MigrateDemoRoadToRectangle(MeshComparison rig)
        {
            RequireEdit();
            if (!rig || rig.workflowMode != MeshTerrainWorkflowMode.Authoring)
                throw new InvalidOperationException("Rectangle roads are edited in the authoring scene.");
            MeshWorldVtOverlaySet set = rig.worldVtOverlays;
            if (!set || set.roads == null) return 0;
            MeshWorldVtRoad existing = null;
            foreach (GameObject root in rig.gameObject.scene.GetRootGameObjects())
                foreach (MeshWorldVtRoad road in root.GetComponentsInChildren<MeshWorldVtRoad>(true))
                    if (road.targetTerrain == rig && road.stableId == "demo-road")
                    {
                        if (existing) throw new InvalidDataException("The demo terrain has duplicate rectangle-road IDs.");
                        existing = road;
                    }

            int legacyIndex = Array.FindIndex(set.roads, road => road != null && road.stableId == "demo-road");
            if (legacyIndex < 0) return 0; // 用户已删除/迁移，不重新生成已删的作者对象。
            if (Array.FindIndex(set.roads, legacyIndex + 1, road => road != null && road.stableId == "demo-road") >= 0)
                throw new InvalidDataException("The demo asset has duplicate road IDs.");
            MeshWorldVtOverlaySet.Road legacy = set.roads[legacyIndex];
            if (!IsOriginalDemoRoad(legacy))
            {
                // 不能为了换编辑入口，把用户画过的弯路近似成另一条路。保留原始
                // 记录；用户可新建矩形后自行删旧路，程序不会偷偷丢失任何编辑内容。
                Debug.LogWarning("The legacy demo road has been edited and was preserved. Create a Rectangle Road to replace it manually.", rig);
                return 0;
            }

            GameObject created = null;
            MeshWorldVtOverlaySet.Road[] before = set.roads;
            try
            {
                if (!existing)
                {
                    created = new GameObject("Road - Rectangle");
                    SceneManager.MoveGameObjectToScene(created, rig.gameObject.scene);
                    MeshWorldVtRoad road = created.AddComponent<MeshWorldVtRoad>();
                    road.targetTerrain = rig;
                    road.stableId = legacy.stableId;
                    road.order = legacy.order;
                    road.materialId = legacy.materialId;
                    road.tint = legacy.tint;
                    road.opacity = legacy.opacity;
                    road.uvMetres = legacy.uvMetres;
                    road.uvOffset = legacy.uvOffset;
                    road.maskTexture = legacy.maskTexture;
                    road.maskChannel = legacy.maskChannel;
                    road.maskUv = legacy.maskUv;
                    road.channels = legacy.channels;
                    road.feather = legacy.feather;
                    float y;
                    using (var source = new MeshHeightSource(rig.DatasetDirectory))
                        y = source.SampleMeters(180, 606, 0);
                    road.transform.SetPositionAndRotation(new Vector3(180, y, 606), Quaternion.Euler(0, 24, 0));
                    road.transform.localScale = new Vector3(10, 1, 120);
                    road.Capture();
                }

                // 现存矩形有用户编辑时也保留。若上次先保存场景后资产保存失败，
                // 重跑仅清掉那条确定未编辑的旧记录，避免同一ID画两次。
                var retained = new List<MeshWorldVtOverlaySet.Road>(set.roads);
                retained.RemoveAt(legacyIndex);
                set.roads = retained.ToArray();
                using (MeshWorldVtOverlaySet.CreateSnapshot(set, rig.CollectWorldVtDecals, rig.CollectWorldVtRoads)) { }
                EditorUtility.SetDirty(set);
                EditorSceneManager.MarkSceneDirty(rig.gameObject.scene);
                return created ? 1 : 0;
            }
            catch
            {
                set.roads = before;
                if (created) Object.DestroyImmediate(created);
                throw;
            }
        }

        static bool IsOriginalDemoRoad(MeshWorldVtOverlaySet.Road road)
        {
            Vector2[] points = { new Vector2(112, 548), new Vector2(155, 570), new Vector2(180, 606),
                new Vector2(203, 655), new Vector2(240, 701), new Vector2(312, 745), new Vector2(385, 810) };
            if (road.order != 0 || road.materialId != 3 || !road.tint.Equals(new Color(.55f, .48f, .39f, 1)) ||
                road.opacity != 1 || road.uvMetres != new Vector2(3, 3) || road.uvOffset != Vector2.zero ||
                road.maskTexture || road.maskChannel != 0 || road.maskUv != new Vector4(1, 1, 0, 0) ||
                road.channels != MeshWorldVtOverlayChannels.Full || road.width != 10 || road.feather != .8f ||
                road.centreLine == null || road.centreLine.Length != points.Length) return false;
            for (int i = 0; i < points.Length; i++) if (road.centreLine[i] != points[i]) return false;
            return true;
        }

        static void RemoveAuthorComponent(Component component)
        {
            // 作者对象通常是Transform+单组件的独立GO。若用户把其他组件/子对象
            // 放在它上面，只移除作者组件，保留不属于此次导出的场景内容。
            GameObject owner = component.gameObject;
            Component[] components = owner.GetComponents<Component>();
            if (owner.transform.childCount == 0 && components.Length == 2)
                Object.DestroyImmediate(owner);
            else Object.DestroyImmediate(component);
        }

        static MeshComparison FindRig(Scene scene)
        {
            MeshComparison found = null;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (MeshComparison rig in root.GetComponentsInChildren<MeshComparison>(true))
                {
                    if (found) throw new InvalidOperationException("A workflow scene must contain exactly one terrain comparison.");
                    found = rig;
                }
            if (!found) throw new InvalidOperationException("This scene has no DirectDelta terrain.");
            return found;
        }

        static Scene OpenAdditive(string path, out bool opened)
        {
            Scene scene = SceneManager.GetSceneByPath(path);
            opened = !scene.IsValid() || !scene.isLoaded;
            return opened ? EditorSceneManager.OpenScene(path, OpenSceneMode.Additive) : scene;
        }

        static void Save(Scene scene)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save the terrain workflow scene: " + scene.path);
        }

        static void RequireEdit()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Edit or bake terrain outside Play; reopen the runtime scene after publishing.");
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        public static void BuildBatch()
        {
            int exit = 1;
            try { BuildScenes(); exit = 0; }
            catch (Exception exception) { Debug.LogException(exception); }
            finally { EditorApplication.Exit(exit); }
        }


        public static void PublishBatch()
        {
            int exit = 1;
            try
            {
                RequireEdit();
                EditorSceneManager.OpenScene(AuthoringScene);
                Publish(FindRig(SceneManager.GetActiveScene()), false);
                exit = 0;
            }
            catch (Exception exception) { Debug.LogException(exception); }
            finally { EditorApplication.Exit(exit); }
        }
    }

    [CustomEditor(typeof(MeshTerrainPublication))]
    public sealed class MeshTerrainPublicationEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("这是已烘焙的固定地图入口。请在 DirectDelta 制作场景编辑，烘焙成功后工具会更新此处。", MessageType.Info);
            using (new EditorGUI.DisabledScope(true)) DrawDefaultInspector();
            if (GUILayout.Button("打开 DirectDelta 制作场景")) MeshTerrainWorkflow.OpenAuthoring();
            if (GUILayout.Button("打开 Delta 运行验证场景")) MeshTerrainWorkflow.OpenPublished();
        }
    }

    [CustomEditor(typeof(MeshComparison))]
    public sealed class MeshTerrainWorkflowInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var rig = (MeshComparison)target;
            if (rig.workflowMode == MeshTerrainWorkflowMode.Authoring)
            {
                EditorGUILayout.HelpBox("DirectDelta 高保真制作：道路和静态贴花可直接编辑。烘焙成功后，Delta 读取这次固定结果。", MessageType.Info);
                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button("烘焙地形并更新本地运行数据")) MeshTerrainWorkflow.Publish(rig, true);
                    if (GUILayout.Button("打开 Delta 运行验证场景")) MeshTerrainWorkflow.OpenPublished();
                }
            }
            else if (rig.workflowMode == MeshTerrainWorkflowMode.Published)
            {
                EditorGUILayout.HelpBox("当前场景读取固定烘焙结果。修改道路或贴花请返回 DirectDelta 制作场景，再重新烘焙。", MessageType.Info);
                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                    if (GUILayout.Button("打开 DirectDelta 制作场景")) MeshTerrainWorkflow.OpenAuthoring();
            }
            DrawDefaultInspector();
        }
    }
}
