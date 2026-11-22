using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MaxTerrain.Editor
{
    /// <summary>Builds the static overlay authoring example without changing the accepted scene.</summary>
    public static class MeshWorldVtOverlayScene
    {
        public const string ScenePath = "Assets/Samples/MaxTerrain/Scenes/DirectDeltaWorldVtOverlays10km.unity";
        public const string SetPath = "Assets/Samples/MaxTerrain/Data/WorldVtStaticOverlays.asset";
        public const string MaskPath = "Assets/Samples/MaxTerrain/Data/WorldVtArrowMask.asset";
        public const string ArchiveDirectory = "DirectDeltaWorldSvt04";
        const string ReferenceScene = "Assets/Samples/MaxTerrain/Scenes/DirectDeltaWorldVt10km.unity";
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Build static VT authoring outside Play, bake its archive, then enter Play.");
            Texture2D mask = AssetDatabase.LoadAssetAtPath<Texture2D>(MaskPath);
            if (!mask)
            {
                const int side = 128;
                mask = new Texture2D(side, side, TextureFormat.R8, true, true)
                {
                    name = "World VT arrow opacity mask", filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp, anisoLevel = 1
                };
                var pixels = new byte[side * side];
                // A small local control mask, not another PBR source or full-world texture.
                for (int y = 0; y < side; y++) for (int x = 0; x < side; x++)
                {
                    int covered = 0;
                    for (int sy = 0; sy < 4; sy++) for (int sx = 0; sx < 4; sx++)
                    {
                        float u = (x + (sx + .5f) / 4) / side * 2 - 1;
                        float v = (y + (sy + .5f) / 4) / side * 2 - 1;
                        bool stem = Mathf.Abs(u) < .18f && v > -.8f && v < .24f;
                        bool head = v >= .05f && v < .9f && Mathf.Abs(u) < (.9f - v) * .9f;
                        if (stem || head) covered++;
                    }
                    pixels[y * side + x] = (byte)Mathf.RoundToInt(covered * 255f / 16);
                }
                mask.SetPixelData(pixels, 0);
                mask.Apply(true, false);
                AssetDatabase.CreateAsset(mask, MaskPath);
            }

            MeshWorldVtOverlaySet set = AssetDatabase.LoadAssetAtPath<MeshWorldVtOverlaySet>(SetPath);
            bool createdSet = !set;
            if (!set)
            {
                set = ScriptableObject.CreateInstance<MeshWorldVtOverlaySet>();
                set.name = "Static road and arrow VT overlays";
                set.writerShader = Shader.Find("Hidden/DirectDeltaMesh/WorldVtOverlay");
                set.repackShader = Shader.Find("Hidden/DirectDeltaMesh/WorldVtOverlayRepack");
                set.roads = new[] { new MeshWorldVtOverlaySet.Road
                {
                    stableId = "demo-road", order = 0, materialId = 3,
                    tint = new Color(.55f, .48f, .39f, 1), opacity = 1,
                    width = 10, feather = .8f, uvMetres = new Vector2(3, 3),
                    centreLine = new[] { new Vector2(112, 548), new Vector2(155, 570),
                        new Vector2(180, 606), new Vector2(203, 655), new Vector2(240, 701),
                        new Vector2(312, 745), new Vector2(385, 810) }
                } };
                set.decals = new[] { new MeshWorldVtOverlaySet.Decal
                {
                    stableId = "demo-road-arrow", order = 100, materialId = 3,
                    // Reuse the corrected compressed material pair; tint creates a painted mark.
                    tint = new Color(5, 4.5f, 3.2f, 1), opacity = .95f,
                    position = new Vector3(180, 625, 606), rotationEuler = new Vector3(0, 24, 0),
                    size = new Vector3(18, 100, 26), feather = .35f,
                    uvMetres = new Vector2(3, 3), maskTexture = mask,
                    channels = MeshWorldVtOverlayChannels.Albedo
                } };
                AssetDatabase.CreateAsset(set, SetPath);
            }
            if (!AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath))
            {
                MeshSceneArchive.Restore(ReferenceScene);
                if (!AssetDatabase.CopyAsset(ReferenceScene, ScenePath))
                    throw new InvalidOperationException("Could not copy the accepted world VT scene.");
            }
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            bool wasLoaded = scene.IsValid() && scene.isLoaded;
            if (!wasLoaded) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            MeshWorldVtOverlaySet migrationSet = null;
            MeshWorldVtOverlaySet.Decal[] legacyBeforeMigration = null;
            bool migrationSaved = false;
            try
            {
                MeshComparison rig = null;
                foreach (GameObject go in scene.GetRootGameObjects())
                    if ((rig = go.GetComponentInChildren<MeshComparison>(true)) != null) break;
                if (!rig) throw new InvalidOperationException("The overlay comparison scene has no MeshComparison.");
                // Rebuilding a previously edited scene keeps its chosen road asset and
                // existing scene decals. Only a brand-new demo gets a sampled initial Y.
                if (rig.worldVtOverlays) { set = rig.worldVtOverlays; createdSet = false; }
                if (createdSet)
                {
                    using (var source = new MeshHeightSource(rig.DatasetDirectory))
                        foreach (var decal in set.decals)
                            decal.position.y = source.SampleMeters(decal.position.x, decal.position.z, 0);
                }

                // Clearing legacy authoring must not remove decals from another map.
                // A shared source is copied for this scene before migrating its records.
                if (set.decals == null) throw new InvalidOperationException("The legacy decal array is null.");
                if (set.decals.Length > 0 && SharedWithAnotherSceneOrRig(set, scene, rig))
                {
                    var copy = UnityEngine.Object.Instantiate(set);
                    copy.name = set.name + " - " + Path.GetFileNameWithoutExtension(ScenePath);
                    string copyPath = AssetDatabase.GenerateUniqueAssetPath(
                        Path.GetDirectoryName(SetPath).Replace('\\', '/') + "/" + copy.name + ".asset");
                    AssetDatabase.CreateAsset(copy, copyPath);
                    set = copy;
                }

                migrationSet = set;
                legacyBeforeMigration = set.decals;
                MigrateDecalsToScene(set, scene, rig);
                rig.worldVtOverlays = set;
                rig.worldVtArchive = ArchiveDirectory;
                rig.worldVirtualTexture = true;
                rig.worldVtMode = 1;
                rig.worldVtMaxAnisotropy = MeshVtMaxAnisotropy.X4;
                rig.shot = 0;
                rig.name = "DirectDelta reference vs Delta - static VT overlays";
                EditorUtility.SetDirty(rig);
                if (!EditorSceneManager.SaveScene(scene))
                    throw new InvalidOperationException("The overlay scene could not be saved.");
                // Save only this tool's asset; do not save unrelated dirty authoring.
                AssetDatabase.SaveAssetIfDirty(set);
                migrationSaved = true;
            }
            catch
            {
                // If either save fails, retain the legacy records in memory as well as
                // on disk. The ID-based migration can safely resume on the next run.
                if (migrationSet && !migrationSaved)
                {
                    migrationSet.decals = legacyBeforeMigration;
                    EditorUtility.SetDirty(migrationSet);
                }
                throw;
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                if (!wasLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

        static void MigrateDecalsToScene(MeshWorldVtOverlaySet set, Scene scene, MeshComparison rig)
        {
            var existing = new Dictionary<string, MeshWorldVtDecal>(StringComparer.Ordinal);
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (MeshWorldVtDecal decal in root.GetComponentsInChildren<MeshWorldVtDecal>(true))
                {
                    if (decal.targetTerrain != rig) continue;
                    if (string.IsNullOrWhiteSpace(decal.stableId) || existing.ContainsKey(decal.stableId))
                        throw new InvalidOperationException("Scene decals need unique stable IDs for their terrain.");
                    existing.Add(decal.stableId, decal);
                }

            // Validate legacy input before creating any objects. A rerun after migration
            // has no legacy records and therefore never recreates a deleted/disabled mark.
            using (var legacy = set.CreateSnapshot()) { }
            var oldDecals = set.decals;
            var created = new List<GameObject>();
            try
            {
                foreach (MeshWorldVtOverlaySet.Decal data in oldDecals)
                {
                    // A matching scene object owns its Transform and parameters already;
                    // preserve these even if the object is currently inactive or disabled.
                    if (existing.ContainsKey(data.stableId)) continue;
                    string objectName = data.stableId == "demo-road-arrow"
                        ? "VT Decal - Road Arrow" : "VT Decal - " + data.stableId;
                    var go = new GameObject(objectName);
                    SceneManager.MoveGameObjectToScene(go, scene);
                    created.Add(go);
                    var component = go.AddComponent<MeshWorldVtDecal>();
                    component.ConfigureFrom(data, rig);
                    existing.Add(data.stableId, component);
                }

                // Exactly one source owns each decal: scene objects replace the old array.
                set.decals = Array.Empty<MeshWorldVtOverlaySet.Decal>();
                MeshWorldVtDecal[] active = rig.CollectWorldVtDecals();
                using (var snapshot = MeshWorldVtOverlaySet.CreateSnapshot(set, rig.CollectWorldVtDecals))
                    if (snapshot.Count != set.roads.Length + active.Length || snapshot.DecalCount != active.Length)
                        throw new InvalidOperationException("The combined road/scene-decal snapshot is incomplete.");
                EditorUtility.SetDirty(set);
            }
            catch
            {
                set.decals = oldDecals;
                foreach (GameObject go in created) UnityEngine.Object.DestroyImmediate(go);
                throw;
            }
        }

        static bool SharedWithAnotherSceneOrRig(MeshWorldVtOverlaySet set, Scene current, MeshComparison owner)
        {
            // Include loaded unsaved references and a second rig in this same scene.
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene loaded = SceneManager.GetSceneAt(i);
                if (!loaded.isLoaded) continue;
                foreach (GameObject root in loaded.GetRootGameObjects())
                    foreach (MeshComparison other in root.GetComponentsInChildren<MeshComparison>(true))
                        if (other != owner && other.worldVtOverlays == set) return true;
            }
            string path = AssetDatabase.GetAssetPath(set);
            if (string.IsNullOrEmpty(path)) return true;
            foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
            {
                string otherPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(otherPath, current.path, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string dependency in AssetDatabase.GetDependencies(otherPath, true))
                    if (string.Equals(dependency, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static void BuildBatch()
        {
            int code = 1;
            try { Build(); code = 0; }
            catch (Exception exception) { Debug.LogException(exception); }
            finally { EditorApplication.Exit(code); }
        }
    }
}
