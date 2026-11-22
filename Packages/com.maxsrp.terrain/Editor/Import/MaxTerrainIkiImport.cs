using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MaxTerrain.Editor
{
    // Offline asset preparation. Rendering and material evaluation stay in the
    // existing MaxTerrain paths; this tool only installs their source data.
    public static class MaxTerrainIkiImport
    {
        public const string MaterialPath = "Assets/Samples/MaxTerrain/Materials/IkiSurface50.asset";
        public const string HeightDirectory = "DirectDeltaIki10240m";
        public const string ControlDirectory = "DirectDeltaControls64Iki10240V1";
        public const string MaterialDirectory = "DirectDeltaSurface64IkiV1";
        const string OverlayPath = "Assets/Samples/MaxTerrain/Data/IkiAuthoringOverlays.asset";

        [Serializable] sealed class SourceMaterial
        {
            public int id;
            public string name, ahMipsPath, nraoMipsPath;
            public float[] uvPerMetre, parameters;
        }
        [Serializable] sealed class SourceManifest
        {
            public int resolution;
            public string sourceFingerprint, layerOrder;
            public SourceMaterial[] materials;
        }
        [Serializable] sealed class ReferencePoint { public int id; public float x, z; }
        [Serializable] sealed class ReferencePoints { public ReferencePoint[] locations; }
        [Serializable] sealed class ImportReport
        {
            public bool passed;
            public string error, stage, input, materialAsset;
            public int materialCount, maximumMaterials = MeshMaterialSet.Capacity;
            public double elapsedSeconds;
        }

        static string Input => Path.GetFullPath(Environment.GetEnvironmentVariable("MAX_TERRAIN_IKI_INPUT") ??
            "D:/CodexTemp/MaxTerrainIkiImport");
        static string Output => Path.GetFullPath(Environment.GetEnvironmentVariable("MAX_TERRAIN_IKI_OUTPUT") ??
            "OfflineBaseline/MaxTerrain/IkiImport");

        [MenuItem("Tools/MaxTerrain/Iki/Install prepared Iki assets")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Install outside Play.");
            Directory.CreateDirectory(Output);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var report = new ImportReport { input = Input, materialAsset = MaterialPath };
            try
            {
                report.stage = "source validation"; Save(report);
                InstallDirectory(HeightDirectory);
                InstallDirectory(ControlDirectory);
                using (var heights = new MeshHeightSource(Path.Combine(Application.streamingAssetsPath, HeightDirectory)))
                    if (heights.Metadata.recipe != "iki-2p56-1m-v1") throw new InvalidDataException("Unexpected Iki height recipe.");
                report.stage = "material compression"; Save(report);
                MeshMaterialSet set = BuildMaterials();
                report.materialCount = set.Count;
                MeshControlBake.ValidateMaterialContract(Path.Combine(Application.streamingAssetsPath, ControlDirectory), set);
                report.stage = "scene assets"; Save(report);
                ConfigureScenes(set);
                AssetDatabase.SaveAssets();
                report.passed = true;
                report.stage = "installed";
            }
            catch (Exception exception) { report.error = exception.ToString(); throw; }
            finally { report.elapsedSeconds = watch.Elapsed.TotalSeconds; Save(report); }
        }

        static void Save(ImportReport report) => File.WriteAllText(Path.Combine(Output, "install.json"), JsonUtility.ToJson(report, true));

        static void InstallDirectory(string name)
        {
            string source = Path.Combine(Input, name), target = Path.Combine(Application.streamingAssetsPath, name);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }

        static MeshMaterialSet BuildMaterials()
        {
            string manifestPath = Path.Combine(Input, "materials.json");
            var manifest = JsonUtility.FromJson<SourceManifest>(File.ReadAllText(manifestPath));
            if (manifest == null || manifest.materials == null || manifest.materials.Length != 50 || manifest.resolution != 1024 ||
                manifest.layerOrder != "iki-physical-palette-v1") throw new InvalidDataException("Invalid prepared Iki materials.");
            string fingerprint = MeshMaterialResident.Sha256(File.ReadAllBytes(manifestPath));
            MeshMaterialSet existing = AssetDatabase.LoadAssetAtPath<MeshMaterialSet>(MaterialPath);
            if (existing && existing.sourceFingerprint == fingerprint)
            {
                existing.Validate(); MeshMaterialBuilder.ValidatePayloads(existing);
                LoadReferenceLocations(existing);
                EditorUtility.SetDirty(existing); AssetDatabase.SaveAssetIfDirty(existing);
                return existing;
            }
            MeshMaterialSet set = ScriptableObject.CreateInstance<MeshMaterialSet>();
            set.name = "Iki - physical palette (50 / 64)";
            set.resolution = manifest.resolution;
            set.relativeDirectory = MaterialDirectory;
            set.sourceLicense = "Local Ghost of Tsushima DIRECTOR'S CUT source assets";
            set.sourceFingerprint = fingerprint;
            set.layerOrder = manifest.layerOrder;
            int count = manifest.materials.Length;
            set.names = new string[count]; set.uvPerMetre = new Vector4[count]; set.parameters = new Vector4[count];
            foreach (SourceMaterial material in manifest.materials)
            {
                if (material.id < 0 || material.id >= count || set.names[material.id] != null ||
                    material.uvPerMetre == null || material.uvPerMetre.Length != 4)
                    throw new InvalidDataException("Invalid or duplicate physical material ID.");
                set.names[material.id] = material.name;
                set.uvPerMetre[material.id] = new Vector4(material.uvPerMetre[0], material.uvPerMetre[1], material.uvPerMetre[2], material.uvPerMetre[3]);
                set.parameters[material.id] = new Vector4(1, set.resolution, set.MipCount - 1, 1);
            }
            LoadReferenceLocations(set);
            string directory = Path.Combine(Application.streamingAssetsPath, MaterialDirectory);
            Directory.CreateDirectory(directory);
            int variantIndex = 0;
            foreach (TextureFormat format in new[] { TextureFormat.BC7, TextureFormat.ASTC_6x6 })
            {
                string suffix = format == TextureFormat.BC7 ? "bc7" : "astc6x6";
                string ahName = "albedo-height." + suffix + ".bin", nraoName = "normal-rough-ao." + suffix + ".bin";
                string ahFile = Path.Combine(directory, ahName), nraoFile = Path.Combine(directory, nraoName);
                using (var ah = File.Create(ahFile)) using (var nrao = File.Create(nraoFile))
                    foreach (SourceMaterial material in manifest.materials.OrderBy(item => item.id))
                    {
                        CompressChain(Path.Combine(Input, material.ahMipsPath), set.resolution, true, format, ah);
                        CompressChain(Path.Combine(Input, material.nraoMipsPath), set.resolution, false, format, nrao);
                        File.WriteAllText(Path.Combine(Output, "compression-progress.txt"), format + " " + (material.id + 1) + "/" + count);
                    }
                long expected = MeshMaterialSet.ArrayBytes(set.resolution, format, count);
                if (new FileInfo(ahFile).Length != expected || new FileInfo(nraoFile).Length != expected)
                    throw new InvalidDataException("Compressed Iki array length differs from its layer count.");
                set.variants[variantIndex++] = new MeshMaterialSet.Variant
                {
                    format = format, arrayBytes = expected, albedoHeightFile = ahName, normalRoughAoFile = nraoName,
                    albedoHeightSha256 = MeshMaterialResident.Sha256(File.ReadAllBytes(ahFile)),
                    normalRoughAoSha256 = MeshMaterialResident.Sha256(File.ReadAllBytes(nraoFile))
                };
            }
            set.Validate(); MeshMaterialBuilder.ValidatePayloads(set);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            if (existing) { EditorUtility.CopySerialized(set, existing); Object.DestroyImmediate(set); set = existing; }
            else AssetDatabase.CreateAsset(set, MaterialPath);
            EditorUtility.SetDirty(set); AssetDatabase.SaveAssetIfDirty(set);
            return set;
        }

        static void CompressChain(string path, int resolution, bool srgb, TextureFormat format, Stream output)
        {
            var texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, true, !srgb);
            try
            {
                texture.LoadRawTextureData(File.ReadAllBytes(path)); texture.Apply(false, false);
                EditorUtility.CompressTexture(texture, format, TextureCompressionQuality.Normal);
                if (texture.format != format || texture.mipmapCount != MeshMaterialSet.CountMips(resolution))
                    throw new InvalidDataException("Compressor changed the requested format or mip count.");
                for (int mip = 0; mip < texture.mipmapCount; mip++)
                {
                    byte[] bytes = texture.GetPixelData<byte>(mip).ToArray();
                    if (bytes.Length != MeshMaterialSet.MipBytes(Math.Max(1, resolution >> mip), format))
                        throw new InvalidDataException("Unexpected compressed mip length.");
                    output.Write(bytes, 0, bytes.Length);
                }
            }
            finally { Object.DestroyImmediate(texture); }
        }

        static void LoadReferenceLocations(MeshMaterialSet set)
        {
            string path = Path.Combine(Input, "reference-locations.json");
            if (!File.Exists(path)) return;
            set.referenceLocations = Enumerable.Repeat(new Vector2(5000, 5000), set.Count).ToArray();
            foreach (ReferencePoint point in JsonUtility.FromJson<ReferencePoints>(File.ReadAllText(path)).locations)
                if (point.id >= 0 && point.id < set.Count) set.referenceLocations[point.id] = new Vector2(point.x, point.z);
        }

        static void ConfigureScenes(MeshMaterialSet materials)
        {
            var overlays = AssetDatabase.LoadAssetAtPath<MeshWorldVtOverlaySet>(OverlayPath);
            if (!overlays)
            {
                overlays = ScriptableObject.CreateInstance<MeshWorldVtOverlaySet>();
                overlays.name = "Iki authoring overlays";
                AssetDatabase.CreateAsset(overlays, OverlayPath);
            }
            Scene active = SceneManager.GetActiveScene();
            foreach (string path in new[] { MeshTerrainWorkflow.AuthoringScene, MeshTerrainWorkflow.PublishedScene })
            {
                Scene scene = SceneManager.GetSceneByPath(path);
                bool close = !scene.isLoaded;
                if (close) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                try
                {
                    MeshComparison rig = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<MeshComparison>(true)).Single();
                    rig.surfaceMaterials = materials; rig.datasetRelativeDirectory = HeightDirectory;
                    rig.controlRelativeDirectory = ControlDirectory; rig.surfaceEnabled = true; rig.hexTiling = true;
                    rig.referenceMaterial = 0; rig.shot = 1;
                    if (path == MeshTerrainWorkflow.AuthoringScene)
                    {
                        rig.worldVtOverlays = overlays;
                        var obsolete = new HashSet<GameObject>();
                        foreach (GameObject root in scene.GetRootGameObjects())
                        {
                            foreach (MeshWorldVtRoad road in root.GetComponentsInChildren<MeshWorldVtRoad>(true))
                                if (road.targetTerrain == rig) obsolete.Add(road.gameObject);
                            foreach (MeshWorldVtDecal decal in root.GetComponentsInChildren<MeshWorldVtDecal>(true))
                                if (decal.targetTerrain == rig) obsolete.Add(decal.gameObject);
                        }
                        foreach (GameObject item in obsolete) if (item) Object.DestroyImmediate(item);
                    }
                    EditorUtility.SetDirty(rig); EditorSceneManager.MarkSceneDirty(scene);
                    if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save " + path);
                }
                finally { if (close) EditorSceneManager.CloseScene(scene, true); }
            }
            if (active.IsValid() && active.isLoaded) SceneManager.SetActiveScene(active);
        }

        public static void InstallBatch()
        {
            int result = 1;
            try { Install(); result = 0; }
            catch (Exception exception) { Debug.LogException(exception); }
            finally { EditorApplication.Exit(result); }
        }

        public static void PrepareMaterialsBatch()
        {
            int result = 1;
            try { Directory.CreateDirectory(Output); BuildMaterials(); result = 0; }
            catch (Exception exception) { Debug.LogException(exception); }
            finally { EditorApplication.Exit(result); }
        }

        public static void PublishBatch()
        {
            int result = 1;
            try
            {
                Publish();
                result = 0;
            }
            catch (Exception exception) { Debug.LogException(exception); }
            finally { EditorApplication.Exit(result); }
        }

        public static void InstallAndPublishBatch()
        {
            int result = 1;
            try { Install(); Publish(); result = 0; }
            catch (Exception exception) { Debug.LogException(exception); }
            finally { EditorApplication.Exit(result); }
        }

        static void Publish()
        {
            EditorSceneManager.OpenScene(MeshTerrainWorkflow.AuthoringScene);
            MeshComparison rig = Object.FindObjectOfType<MeshComparison>();
            MeshTerrainWorkflow.Publish(rig, false);
            MeshTerrainPublication publication = AssetDatabase.LoadAssetAtPath<MeshTerrainPublication>(MeshTerrainWorkflow.PublicationPath);
            Scene scene = EditorSceneManager.OpenScene(MeshTerrainWorkflow.PublishedScene);
            MeshComparison published = Object.FindObjectOfType<MeshComparison>();
            published.publication = publication; published.worldVtArchive = publication.archiveRelativeDirectory;
            EditorUtility.SetDirty(published); EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        }
    }
}
