using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MaxSRP.EditorTools
{
    /// <summary>Explicit, asynchronous full-Bistro bake. Never starts from an asset import callback.</summary>
    [InitializeOnLoad]
    public static class MaxBistroLightmapBaker
    {
        public const string ScenePath = MaxBistroSceneBuilder.ScenePath;
        public const string OutputFolder = "Assets/Scenes/BistroTest/Lighting";
        internal const string BakeFolder = "Assets/LocalGIBakes/Bistro";
        public const string SettingsPath = OutputFolder + "/BistroLightingSettings.lighting";
        private const string ReportFolder = "OfflineBaseline/BistroLightmap";
        private const string PendingKey = "MaxSRP.BistroLightmap.Pending";
        private const string PlacementPath = OutputFolder + "/PracticalLightPlacement.json";
        private const float BakeTexelsPerUnit = 4.0f;
        private static double s_lastStatus;
        private static string s_backupFolder;
        private static bool s_nativeFinishing;

        [Serializable]
        public sealed class Report
        {
            public string utc;
            public string phase;
            public bool valid;
            public bool isRunning;
            public float progress;
            public int renderers;
            public int excludedRenderers;
            public int meshes;
            public int practicalLights;
            public int materials;
            public int missingUV2;
            public int invalidUV2;
            public int overlappingUV2;
            public int uvMarginModels;
            public int uvMarginMismatches;
            public float minimumUvLightmapResolution;
            public float minimumUvObjectScale;
            public string[] uvMarginSettings;
            public string uvOverlapCheck = "64x64 continuous-UV triangle-interior screening only; this does not validate native atlas texel/filter-footprint overlap or chart padding.";
            public int nativeUvOverlapRenderers = -1;
            public int nativeUvOverlapStopThreshold;
            public string[] nativeUvOverlapNames;
            public int lightmapCount;
            public bool lightUnitFixturePassed;
            public int shaderErrors;
            public int srpBatcherCompatibilityCode;
            public double worldSurfaceArea;
            public double estimatedSurfaceTexels;
            public double estimatedNativeRectTexels;
            public double estimatedAtlasPackingTexels;
            public int estimatedNativeAtlasLowerBound;
            public double largestEstimatedNativeTile;
            public string uvLayoutScheme = "Source-cached per-component xatlas; nominal 4 texels/m; 2-pixel chart padding with bilinear-footprint validation; packedSide from the external cache manifest.";
            public string uvLayoutDescription;
            public string texelBudgetDefinition = "SurfaceTexels=worldArea*density^2; nativeScale=manifestPackedSide/sqrt(instanceWorldArea*density^2), so NativeRectTexels=sum(manifestPackedSide^2); PackingTexels also allows ceil(side)+4 renderer padding. Actual native ST and atlas dimensions require a post-bake audit; atlas count is only an area lower bound.";
            public string[] errors;
            public string[] warnings;
            public string settings = "ProgressiveCPU; Directional; 4 texels/m; 2048 atlas; 512 indirect samples; 4 bounces; Mixed BakedIndirect; no baked sky/sun/emission";
            public string unitConvention = "Bake candela/1000; decoded E/pi multiplied by 0.01 to pipeline HDRScale=100000";
        }

        [Serializable] private sealed class SavedPlacements { public int version = 0; public SavedPlacement[] lights = Array.Empty<SavedPlacement>(); }
        [Serializable] private sealed class SavedPlacement { public string name = ""; public float[] original = Array.Empty<float>(); public float[] target = Array.Empty<float>(); }

        // Explicitly replay saved absolute positions. Rebuilding the scene reproduces the correction;
        // already corrected lights are unchanged, and user-adjusted lights are never overwritten.
        public static int ApplySavedPracticalPlacements(Scene scene)
        {
            if (!scene.IsValid() || (!string.IsNullOrEmpty(scene.path) && scene.path != ScenePath))
                throw new InvalidOperationException("Apply Bistro placements only to its test scene or its new unsaved builder scene.");
            if (!File.Exists(PlacementPath)) return 0;
            SavedPlacements saved = JsonUtility.FromJson<SavedPlacements>(File.ReadAllText(PlacementPath));
            if (saved == null || saved.version != 1 || saved.lights == null)
                throw new InvalidOperationException("Invalid saved Bistro practical light placements.");
            var lights = new Dictionary<string, Light>(StringComparer.Ordinal);
            foreach (Light light in Components<Light>(scene))
                if (light.name.StartsWith("Practical ", StringComparison.Ordinal)) lights.Add(light.name, light);
            var pending = new List<KeyValuePair<Light, Vector3>>();
            foreach (SavedPlacement item in saved.lights)
            {
                if (item.original == null || item.target == null || item.original.Length != 3 || item.target.Length != 3)
                    throw new InvalidOperationException("Invalid saved placement vector: " + item.name);
                if (!lights.TryGetValue(item.name, out Light light))
                    throw new InvalidOperationException("Saved Bistro light not found: " + item.name);
                var source = new Vector3(item.original[0], item.original[1], item.original[2]);
                var target = new Vector3(item.target[0], item.target[1], item.target[2]);
                if (!float.IsFinite(target.x) || !float.IsFinite(target.y) || !float.IsFinite(target.z))
                    throw new InvalidOperationException("Nonfinite saved light position: " + item.name);
                if ((light.transform.position - target).sqrMagnitude <= 1e-6f) continue;
                if ((light.transform.position - source).sqrMagnitude <= 1e-6f)
                    pending.Add(new KeyValuePair<Light, Vector3>(light, target));
                else Debug.LogWarning("Preserving manually adjusted Bistro light: " + light.name);
            }
            foreach (var item in pending)
            {
                item.Key.transform.position = item.Value;
                PrefabUtility.RecordPrefabInstancePropertyModifications(item.Key.transform);
                EditorUtility.SetDirty(item.Key.transform);
            }
            if (pending.Count > 0) EditorSceneManager.MarkSceneDirty(scene);
            return pending.Count;
        }

        static MaxBistroLightmapBaker()
        {
            Lightmapping.bakeCompleted += OnNativeBakeCompleted;
            EditorApplication.update += Poll;
            MaxBistroSkyVisibilityBaker.Completed += OnSkyVisibilityCompleted;
        }

        [MenuItem("MaxSRP/Bistro/Lightmap/1 Prepare Full Bistro")]
        public static void PrepareMenu() { Debug.Log(Prepare()); }
        [MenuItem("MaxSRP/Bistro/Lightmap/2 Preflight")]
        public static void PreflightMenu() { Debug.Log(Preflight()); }
        [MenuItem("MaxSRP/Bistro/Lightmap/3 Start Full Bake")]
        public static void BakeMenu() { Debug.Log(StartFullBake()); }
        [MenuItem("MaxSRP/Bistro/Lightmap/Status")]
        public static void StatusMenu() { Debug.Log(Status()); }
        [MenuItem("MaxSRP/Bistro/Lightmap/Cancel")]
        public static void Cancel()
        {
            // Clear ownership first: a native cancellation may invoke completion callbacks.
            SessionState.SetBool(PendingKey, false);
            if (Lightmapping.isRunning) Lightmapping.Cancel();
            if (MaxBistroSkyVisibilityBaker.IsRunning) MaxBistroSkyVisibilityBaker.Cancel();
            Write(new Report { phase = "cancelled", errors = new[] { "Bistro lighting bake was cancelled; existing assets were retained." } }, "status.json");
        }

        public static string Prepare()
        {
            RejectTinySceneForFullBake();
            if (Lightmapping.isRunning || MaxBistroSkyVisibilityBaker.IsRunning)
                throw new InvalidOperationException("A Bistro lighting bake is already running.");
            Write(new Report
            {
                phase = "preparing-uv", valid = false, isRunning = false,
                warnings = new[] { "Validating the xatlas source-cache/manifest and importing cached UV2; model reimport can take several minutes. Offline UV generation and the native lightmap bake require explicit separate steps." }
            }, "status.json");
            try
            {
                Report report = JsonUtility.FromJson<Report>(PrepareScene());
                report.phase = report.valid ? "prepared" : "prepare-failed";
                report.isRunning = false;
                Write(report, "status.json");
                return JsonUtility.ToJson(report, true);
            }
            catch (Exception exception)
            {
                Write(new Report { phase = "prepare-failed", valid = false, isRunning = false,
                    errors = new[] { exception.ToString() } }, "status.json");
                throw;
            }
        }

        private static string PrepareScene()
        {
            Scene scene = RequireScene(true);
            s_backupFolder = Path.Combine(ReportFolder, "PreparationBackup", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            BackupAsset(ScenePath);
            BackupAsset(SettingsPath);
            MaxBakedLightingData.RestoreSourceMaterials(scene);
            EnsureFolder(OutputFolder);
            EnsureUV2(scene);
            // Importing a model can replace native Mesh objects: resolve all scene components again.
            scene = SceneManager.GetSceneByPath(ScenePath);
            ApplySavedPracticalPlacements(scene);
            var materials = new HashSet<Material>();
            foreach (MeshRenderer renderer in Components<MeshRenderer>(scene))
            {
                if (!renderer.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh) continue;
                bool visible = IsVisibleReceiver(renderer);
                StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(renderer.gameObject);
                if (visible) flags |= StaticEditorFlags.ContributeGI;
                else flags &= ~StaticEditorFlags.ContributeGI;
                GameObjectUtility.SetStaticEditorFlags(renderer.gameObject, flags);
                renderer.receiveGI = ReceiveGI.Lightmaps;
                renderer.scaleInLightmap = visible ? MaxBistroLightmapUV.GetScale(renderer) : 0;
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (!material || !materials.Add(material)) continue;
                    BackupAsset(AssetDatabase.GetAssetPath(material));
                    material.doubleSidedGI = material.HasProperty("_Cull") && material.GetFloat("_Cull") == (float)CullMode.Off;
                    material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                    // The native lightmapper extracts alpha-tested occlusion from the material's
                    // main texture, cutoff and render type/queue, independently of Meta rasterization.
                    bool cutout = material.HasProperty("_Cutoff") && material.GetFloat("_Cutoff") > 0;
                    material.renderQueue = (int)(cutout ? RenderQueue.AlphaTest : RenderQueue.Geometry);
                    material.SetOverrideTag("RenderType", cutout ? "TransparentCutout" : "Opaque");
                    if (material.HasProperty("_MainTex") && material.HasProperty("_BaseColorMap"))
                    {
                        material.SetTexture("_MainTex", material.GetTexture("_BaseColorMap"));
                        material.SetTextureScale("_MainTex", material.GetTextureScale("_BaseColorMap"));
                        material.SetTextureOffset("_MainTex", material.GetTextureOffset("_BaseColorMap"));
                    }
                    if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
                    EditorUtility.SetDirty(material);
                }
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
            foreach (Light light in Components<Light>(scene))
            {
                bool practical = IsPractical(light);
                light.lightmapBakeType = practical ? LightmapBakeType.Mixed : LightmapBakeType.Realtime;
                if (practical) light.bounceIntensity = 1;
                PrefabUtility.RecordPrefabInstancePropertyModifications(light);
            }
            LightingSettings settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(SettingsPath);
            if (!settings)
            {
                settings = new LightingSettings { name = "Bistro Indirect Artificial Lighting" };
                AssetDatabase.CreateAsset(settings, SettingsPath);
            }
            settings.autoGenerate = false;
            settings.bakedGI = true;
            settings.realtimeGI = false;
            settings.realtimeEnvironmentLighting = false;
            settings.mixedBakeMode = MixedLightingMode.IndirectOnly;
            settings.directionalityMode = LightmapsMode.CombinedDirectional;
            settings.lightmapper = LightingSettings.Lightmapper.ProgressiveCPU;
            settings.lightmapResolution = BakeTexelsPerUnit;
            settings.lightmapMaxSize = 2048;
            settings.lightmapPadding = 4;
            settings.directSampleCount = 64;
            settings.indirectSampleCount = 512;
            settings.environmentSampleCount = 32;
            settings.minBounces = 2;
            settings.maxBounces = 4;
            settings.sampling = LightingSettings.Sampling.Fixed;
            settings.prioritizeView = false;
            settings.filteringMode = LightingSettings.FilterMode.Auto;
            settings.lightmapCompression = LightmapCompression.None;
            settings.ao = false;
            settings.albedoBoost = 1;
            settings.indirectScale = 1;
            Lightmapping.lightingSettings = settings;
            LightmapSettings.lightmapsMode = LightmapsMode.CombinedDirectional;
            // MaxSRP's program sky/IBL owns environmental illumination. Unity's bake environment
            // must be black, including the editor's default sky, to avoid baking a second sky.
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.ambientIntensity = 0;
            RenderSettings.skybox = null;
            EditorUtility.SetDirty(settings);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            MaxLightmapping.Setup();
            return Preflight();
        }

        public static string Preflight()
        {
            Scene scene = RequireScene(false);
            Report report = BuildReport(scene);
            Write(report, "preflight.json");
            return JsonUtility.ToJson(report, true);
        }

        public static string StartFullBake()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Wait for Unity import/compilation before starting the bake.");
            if (Lightmapping.isRunning || MaxBistroSkyVisibilityBaker.IsRunning)
                throw new InvalidOperationException("A lighting bake is already running.");
            Scene scene = RequireScene(false);
            MaxBakedLightingData.RestoreSourceMaterials(scene);
            Report report = BuildReport(scene);
            Write(report, "preflight.json");
            if (!report.valid) throw new InvalidOperationException("Bistro preflight failed: " + string.Join("; ", report.errors));
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            SessionState.SetBool(PendingKey, true);
            report.phase = "starting-native-lightmap";
            Write(report, "status.json");
            bool accepted = Lightmapping.BakeAsync();
            if (!accepted)
            {
                SessionState.SetBool(PendingKey, false);
                report.phase = "native-bake-rejected";
                report.valid = false;
                report.errors = new[] { "Unity Lightmapping.BakeAsync returned false." };
            }
            else
            {
                report.phase = "native-lightmap";
                report.isRunning = Lightmapping.isRunning;
                report.progress = Lightmapping.buildProgress;
            }
            Write(report, "status.json");
            return JsonUtility.ToJson(report, true);
        }

        public static string Status()
        {
            string path = Path.Combine(ReportFolder, "status.json");
            Report report = File.Exists(path) ? JsonUtility.FromJson<Report>(File.ReadAllText(path)) : new Report { phase = "not-started" };
            report.isRunning = Lightmapping.isRunning || MaxBistroSkyVisibilityBaker.IsRunning;
            report.progress = Lightmapping.isRunning ? Lightmapping.buildProgress : report.progress;
            report.utc = DateTime.UtcNow.ToString("o");
            return JsonUtility.ToJson(report, true);
        }

        private static Report BuildReport(Scene scene)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var report = new Report { phase = "preflight", utc = DateTime.UtcNow.ToString("o") };
            var meshes = new HashSet<Mesh>();
            var materials = new HashSet<Material>();
            var shaders = new HashSet<Shader>();
            bool uvRecipeValid = true;
            report.uvMarginModels = AssetImporter.GetAtPath(MaxBistroLightmapUV.ModelPath) is ModelImporter ? 1 : 0;
            report.minimumUvLightmapResolution = BakeTexelsPerUnit;
            // Compatibility fields only: xatlas supplies the packed side in its cache
            // manifest; ModelImporter Calculate/local/lossy scale does not size charts.
            report.minimumUvObjectScale = 0;
            try
            {
                if (!Mathf.Approximately(MaxBistroLightmapUV.Density, BakeTexelsPerUnit))
                    throw new InvalidOperationException("Cached xatlas UV density differs from the native bake contract.");
                MaxBistroLightmapUV.Validate(scene);
                report.uvLayoutDescription = MaxBistroLightmapUV.Describe(scene);
            }
            catch (Exception exception)
            {
                uvRecipeValid = false;
                ++report.uvMarginMismatches;
                errors.Add("Lightmap xatlas source-cache/recipe validation failed: " + exception.Message);
            }
            report.uvMarginSettings = new[] { report.uvLayoutScheme, "Recipe=" + MaxBistroLightmapUV.RecipePath,
                report.uvLayoutDescription ?? "Source-cache/recipe validation failed; capture sources and generate the offline cache before Prepare." };
            foreach (MeshRenderer renderer in Components<MeshRenderer>(scene))
            {
                if (!renderer.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh) continue;
                if (!IsVisibleReceiver(renderer)) { ++report.excludedRenderers; continue; }
                ++report.renderers;
                if ((GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & StaticEditorFlags.ContributeGI) == 0)
                    errors.Add("Renderer does not contribute GI: " + renderer.name);
                if (renderer.receiveGI != ReceiveGI.Lightmaps || renderer.scaleInLightmap <= 0)
                    errors.Add("Renderer cannot receive a lightmap: " + renderer.name);
                Mesh mesh = filter.sharedMesh;
                if (meshes.Add(mesh))
                {
                    UvCheck check = CheckUV2(mesh);
                    if (!check.present) ++report.missingUV2;
                    else if (!check.valid) ++report.invalidUV2;
                    if (check.overlapFraction > 0.01f) ++report.overlappingUV2;
                }
                double worldArea = SurfaceArea(mesh, renderer.localToWorldMatrix);
                report.worldSurfaceArea += worldArea;
                if (uvRecipeValid)
                {
                    try
                    {
                        float requiredScale = MaxBistroLightmapUV.GetScale(renderer);
                        if (!float.IsFinite(requiredScale) || requiredScale <= 0 ||
                            !float.IsFinite(renderer.scaleInLightmap) ||
                            !Mathf.Approximately(renderer.scaleInLightmap, requiredScale))
                        {
                            ++report.uvMarginMismatches;
                            errors.Add("Renderer Scale In Lightmap differs from its xatlas manifest packed-side budget; run Prepare: " + renderer.name);
                        }
                        if (!float.IsFinite(requiredScale) || requiredScale <= 0)
                            throw new InvalidOperationException("Nonfinite/nonpositive native UV scale.");
                        double rectangle = worldArea * BakeTexelsPerUnit * BakeTexelsPerUnit * requiredScale * requiredScale;
                        double paddedSide = Math.Ceiling(Math.Sqrt(rectangle)) + 4;
                        if (!double.IsFinite(rectangle) || rectangle <= 0 || paddedSide > 2048)
                            errors.Add("Cached xatlas lightmap tile cannot fit a 2048 atlas with renderer padding: " + renderer.name +
                                "; padded side=" + paddedSide.ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
                        else
                        {
                            report.estimatedNativeRectTexels += rectangle;
                            report.estimatedAtlasPackingTexels += paddedSide * paddedSide;
                            report.largestEstimatedNativeTile = Math.Max(report.largestEstimatedNativeTile, paddedSide);
                        }
                    }
                    catch (Exception exception)
                    {
                        errors.Add("Cached xatlas native-scale budget failed for " + renderer.name + ": " + exception.Message);
                    }
                }
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (!material || !materials.Add(material)) continue;
                    if (shaders.Add(material.shader))
                    {
                        foreach (var message in ShaderUtil.GetShaderMessages(material.shader))
                            if (message.severity.ToString() == "Error")
                            { ++report.shaderErrors; errors.Add("Shader error " + material.shader.name + ": " + message.message); }
                        var compatibility = typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityCode",
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (compatibility != null)
                        {
                            int code = (int)compatibility.Invoke(null, new object[] { material.shader, 0 });
                            report.srpBatcherCompatibilityCode = Mathf.Max(report.srpBatcherCompatibilityCode, code);
                            if (code != 0) errors.Add("SRP Batcher incompatibility for " + material.shader.name + ": " + code);
                        }
                    }
                    if (material.FindPass("META") < 0 && material.FindPass("Meta") < 0)
                        errors.Add("Missing Meta pass: " + material.name);
                    if (material.globalIlluminationFlags != MaterialGlobalIlluminationFlags.EmissiveIsBlack)
                        errors.Add("Unexpected emissive GI: " + material.name);
                    bool doubleSided = material.HasProperty("_Cull") && material.GetFloat("_Cull") == 0;
                    if (material.doubleSidedGI != doubleSided)
                        errors.Add("GI backface mismatch: " + material.name);
                    bool cutout = material.HasProperty("_Cutoff") && material.GetFloat("_Cutoff") > 0;
                    if (cutout && (material.renderQueue < (int)RenderQueue.AlphaTest || material.GetTag("RenderType", false) != "TransparentCutout"))
                        errors.Add("Native alpha-cutout extraction is not configured: " + material.name);
                    if (material.HasProperty("_MainTex") && material.HasProperty("_BaseColorMap") &&
                        material.GetTexture("_MainTex") != material.GetTexture("_BaseColorMap"))
                        errors.Add("Native baker main texture alias differs from Meta/runtime: " + material.name);
                }
            }
            report.meshes = meshes.Count;
            report.materials = materials.Count;
            if (report.renderers < 1000) errors.Add("Expected complete Bistro exterior, found only " + report.renderers + " visible mesh renderers.");
            if (report.missingUV2 > 0 || report.invalidUV2 > 0)
                errors.Add("Missing/invalid UV2 meshes: " + report.missingUV2 + "/" + report.invalidUV2);
            if (report.overlappingUV2 > 0)
                warnings.Add("Coarse UV interior-overlap screening flagged " + report.overlappingUV2 + " meshes. Resolve against the exact imported-UV overlap/footprint audit; this screening does not replace cache validation.");
            warnings.Add("The coarse UV check cannot certify chart separation. Require the post-import continuous-overlap and bilinear-footprint audit, then inspect native post-bake overlap diagnostics. LightingSettings padding separates renderer tiles only.");
            foreach (Light light in Components<Light>(scene))
            {
                if (IsPractical(light))
                {
                    ++report.practicalLights;
                    if (light.type != UnityEngine.LightType.Point || light.lightmapBakeType != LightmapBakeType.Mixed)
                        errors.Add("Practical must be a Mixed point light: " + light.name);
                    if (!float.IsFinite(light.intensity) || light.intensity <= 0 || !float.IsFinite(light.range) || light.range <= 0)
                        errors.Add("Nonfinite or invalid light intensity/range: " + light.name);
                    if (light.bounceIntensity != 1) errors.Add("Unexpected bounce multiplier: " + light.name);
                }
                else if (light.lightmapBakeType != LightmapBakeType.Realtime)
                    errors.Add("Sun/stress/imported light would enter bake: " + light.name);
            }
            if (report.practicalLights != 53) errors.Add("Expected 53 practical lights; got " + report.practicalLights);
            report.lightUnitFixturePassed = ValidateLightUnits(scene, errors);
            if (PlayerSettings.colorSpace != ColorSpace.Linear) errors.Add("Lightmap decoding requires Linear color space.");
            if (!MaxLightmapping.IsConfigured) errors.Add("MaxSRP physical GI light delegate is not configured.");
            if (!Lightmapping.TryGetLightingSettings(out LightingSettings settings) || !settings)
                errors.Add("No scene LightingSettings.");
            else if (!settings.bakedGI || settings.realtimeGI || settings.mixedBakeMode != MixedLightingMode.IndirectOnly ||
                settings.directionalityMode != LightmapsMode.CombinedDirectional || settings.lightmapMaxSize != 2048 ||
                settings.lightmapResolution != BakeTexelsPerUnit || settings.indirectSampleCount < 512 || settings.maxBounces != 4 || settings.ao ||
                settings.albedoBoost != 1 || settings.indirectScale != 1 || settings.autoGenerate)
                errors.Add("LightingSettings differ from the reviewed bake contract.");
            if (RenderSettings.ambientMode != AmbientMode.Flat || RenderSettings.ambientLight.maxColorComponent != 0 ||
                RenderSettings.ambientIntensity != 0 || RenderSettings.skybox)
                errors.Add("Unity baked environment must be black without a skybox.");
            warnings.Add("Native inverse-square baking uses Unity range falloff; it is not an exact match for MaxSRP's analytic distance/range attenuation near the range boundary.");
            warnings.Add("Moving receivers and solar bounce are deferred to Volume GI; local realtime direct-light visibility is unchanged.");
            report.estimatedSurfaceTexels = report.worldSurfaceArea * BakeTexelsPerUnit * BakeTexelsPerUnit;
            report.estimatedNativeAtlasLowerBound = (int)Math.Ceiling(report.estimatedAtlasPackingTexels / (2048.0 * 2048.0));
            warnings.Add("The external xatlas manifest defines packed sides; Prepare does not run the offline generator or start a bake. Require an actual imported-UV audit before Bake and an actual native ST/texel-density audit after packing. The atlas estimate is an area lower bound, not a packing guarantee.");
            report.errors = errors.ToArray();
            report.warnings = warnings.ToArray();
            report.valid = errors.Count == 0;
            report.isRunning = Lightmapping.isRunning;
            return report;
        }

        private static void EnsureUV2(Scene scene)
        {
            BackupAsset(MaxBistroLightmapUV.RecipePath);
            BackupAsset(MaxBistroLightmapUV.ModelPath + ".meta");
            MaxBistroLightmapUV.Ensure(scene);
        }

        private struct UvCheck { public bool present, valid; public float overlapFraction; }

        private static UvCheck CheckUV2(Mesh mesh)
        {
            var result = new UvCheck { present = mesh.HasVertexAttribute(VertexAttribute.TexCoord1), valid = true };
            if (!result.present) { result.valid = false; return result; }
            using (Mesh.MeshDataArray dataArray = MeshUtility.AcquireReadOnlyMeshData(mesh))
            using (var uvs = new NativeArray<Vector2>(mesh.vertexCount, Allocator.Temp))
            {
                Mesh.MeshData data = dataArray[0];
                data.GetUVs(1, uvs);
                for (int i = 0; i < uvs.Length; ++i)
                {
                    Vector2 uv = uvs[i];
                    if (!float.IsFinite(uv.x) || !float.IsFinite(uv.y) || uv.x < -0.001f || uv.y < -0.001f || uv.x > 1.001f || uv.y > 1.001f)
                        result.valid = false;
                }
                const int size = 64;
                var occupied = new bool[size * size];
                int covered = 0, overlaps = 0, nondegenerate = 0;
                for (int submesh = 0; submesh < data.subMeshCount; ++submesh)
                {
                    SubMeshDescriptor descriptor = data.GetSubMesh(submesh);
                    if (descriptor.topology != MeshTopology.Triangles) continue;
                    using (var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp))
                    {
                        data.GetIndices(indices, submesh, true);
                        for (int triangle = 0; triangle + 2 < indices.Length; triangle += 3)
                        {
                            Vector2 a = uvs[indices[triangle]], b = uvs[indices[triangle + 1]], c = uvs[indices[triangle + 2]];
                            float area = Cross(b - a, c - a);
                            if (Mathf.Abs(area) < 1e-12f) continue;
                            ++nondegenerate;
                            int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) * size), 0, size - 1);
                            int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) * size), 0, size - 1);
                            int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) * size), 0, size - 1);
                            int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) * size), 0, size - 1);
                            for (int y = minY; y <= maxY; ++y)
                            for (int x = minX; x <= maxX; ++x)
                            {
                                Vector2 p = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);
                                float u = Cross(b - p, c - p) / area, v = Cross(c - p, a - p) / area;
                                if (u <= 0.001f || v <= 0.001f || 1 - u - v <= 0.001f) continue;
                                int pixel = y * size + x;
                                ++covered;
                                if (occupied[pixel]) ++overlaps;
                                occupied[pixel] = true;
                            }
                        }
                    }
                }
                if (nondegenerate == 0) result.valid = false;
                result.overlapFraction = covered > 0 ? (float)overlaps / covered : 0;
            }
            return result;
        }

        private static double SurfaceArea(Mesh mesh, Matrix4x4 transform)
        {
            double area = 0;
            using (Mesh.MeshDataArray dataArray = MeshUtility.AcquireReadOnlyMeshData(mesh))
            using (var positions = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Temp))
            {
                Mesh.MeshData data = dataArray[0];
                data.GetVertices(positions);
                for (int submesh = 0; submesh < data.subMeshCount; ++submesh)
                {
                    SubMeshDescriptor descriptor = data.GetSubMesh(submesh);
                    if (descriptor.topology != MeshTopology.Triangles) continue;
                    using (var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp))
                    {
                        data.GetIndices(indices, submesh, true);
                        for (int triangle = 0; triangle + 2 < indices.Length; triangle += 3)
                        {
                            Vector3 a = positions[indices[triangle]], b = positions[indices[triangle + 1]], c = positions[indices[triangle + 2]];
                            area += Vector3.Cross(transform.MultiplyVector(b - a), transform.MultiplyVector(c - a)).magnitude * 0.5;
                        }
                    }
                }
            }
            return area;
        }

        private static void Poll()
        {
            // delayCall can wait for an Inspector update while the Editor is in
            // the background. Bake completion must also progress unattended.
            if (s_nativeFinishing && !Lightmapping.isRunning &&
                !EditorApplication.isCompiling && !EditorApplication.isUpdating)
            {
                FinishNativeBake();
                return;
            }
            if (!SessionState.GetBool(PendingKey, false) || EditorApplication.timeSinceStartup - s_lastStatus < 3) return;
            s_lastStatus = EditorApplication.timeSinceStartup;
            string path = Path.Combine(ReportFolder, "status.json");
            if (!File.Exists(path)) return;
            Report report = JsonUtility.FromJson<Report>(File.ReadAllText(path));
            report.isRunning = Lightmapping.isRunning || MaxBistroSkyVisibilityBaker.IsRunning;
            report.progress = Lightmapping.isRunning ? Lightmapping.buildProgress : report.progress;
            if (Lightmapping.isRunning) report.phase = "native-lightmap";
            if (MaxBistroSkyVisibilityBaker.IsRunning) report.phase = "sky-visibility";
            if (!report.isRunning && !s_nativeFinishing && report.phase == "native-lightmap")
            {
                SessionState.SetBool(PendingKey, false);
                report.valid = false;
                report.phase = "native-stopped-or-failed";
                report.errors = new[] { "Native bake stopped without a successful completion callback. Inspect the Unity console; no sky bake was launched." };
            }
            Write(report, "status.json");
        }

        private static void OnNativeBakeCompleted()
        {
            if (!SessionState.GetBool(PendingKey, false)) return;
            s_nativeFinishing = true;
        }

        private static void FinishNativeBake()
        {
            try
            {
                Scene scene = RequireScene(false);
                LightmapData[] maps = LightmapSettings.lightmaps;
                if (maps == null || maps.Length == 0 || !Lightmapping.lightingDataAsset)
                    throw new InvalidOperationException("Native bake completed without persisted lightmaps/LightingDataAsset.");
                for (int i = 0; i < maps.Length; ++i)
                    if (!maps[i].lightmapColor || !maps[i].lightmapDir)
                        throw new InvalidOperationException("Directional color/direction pair missing at atlas " + i);
                MoveNativeAssets(maps);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
                string previousReportPath = Path.Combine(ReportFolder, "status.json");
                if (!File.Exists(previousReportPath)) previousReportPath = Path.Combine(ReportFolder, "preflight.json");
                if (!File.Exists(previousReportPath))
                    throw new InvalidOperationException("Native bake completed without its preflight/status report.");
                Report report = JsonUtility.FromJson<Report>(File.ReadAllText(previousReportPath));
                if (report == null || !report.valid || !report.lightUnitFixturePassed)
                    throw new InvalidOperationException("Native bake preflight/status report is invalid or lacks the physical-light unit verification.");
                report.phase = "native-complete-starting-sky-visibility";
                report.lightmapCount = maps.Length;
                report.isRunning = false;
                report.errors = Array.Empty<string>();
                InspectNativeUvOverlap(scene, report);
                Write(report, "native_validation.json");
                if (!report.valid)
                {
                    SessionState.SetBool(PendingKey, false);
                    report.phase = "native-complete-uv-overlap";
                    Write(report, "status.json");
                    Debug.LogError(string.Join("; ", report.errors));
                    return;
                }
                Write(report, "status.json");
                MaxBistroSkyVisibilityBaker.Begin(scene);
            }
            catch (Exception exception)
            {
                SessionState.SetBool(PendingKey, false);
                Write(new Report { phase = "failed-after-native", errors = new[] { exception.ToString() } }, "status.json");
                Debug.LogException(exception);
            }
            finally { s_nativeFinishing = false; }
        }

        private static void InspectNativeUvOverlap(Scene scene, Report report)
        {
            var query = typeof(Lightmapping).GetMethod("HasUVOverlaps",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null, new[] { typeof(Renderer) }, null);
            if (query == null)
                throw new MissingMethodException("Unity native Lightmapping.HasUVOverlaps(Renderer) is required for the Bistro post-bake audit.");
            var names = new List<string>();
            int receivers = 0;
            foreach (MeshRenderer renderer in Components<MeshRenderer>(scene))
            {
                if (!IsVisibleReceiver(renderer) || renderer.scaleInLightmap <= 0 ||
                    (GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & StaticEditorFlags.ContributeGI) == 0 ||
                    !renderer.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh) continue;
                ++receivers;
                if ((bool)query.Invoke(null, new object[] { renderer })) names.Add(renderer.name);
            }
            report.renderers = receivers;
            report.nativeUvOverlapRenderers = names.Count;
            report.nativeUvOverlapNames = names.ToArray();
            // A few tiny/sliver charts can trigger native diagnostics; this count alone
            // cannot measure their affected area. Widespread overlaps must not pass as
            // a finished bake merely because its textures exist and contain finite RGB.
            report.nativeUvOverlapStopThreshold = Mathf.Max(8, Mathf.CeilToInt(receivers * 0.05f));
            if (names.Count > 0)
            {
                var warnings = new List<string>(report.warnings ?? Array.Empty<string>());
                warnings.Add("Native atlas UV/filter-footprint overlap was reported for " + names.Count + "/" + receivers +
                    " receivers. This differs from continuous-UV intersection; inspect affected charts. Automatic continuation stops above " +
                    report.nativeUvOverlapStopThreshold + " receivers.");
                report.warnings = warnings.ToArray();
            }
            if (names.Count > report.nativeUvOverlapStopThreshold)
            {
                report.valid = false;
                report.errors = new[] { "Widespread native lightmap UV/filter-footprint overlap: " + names.Count + "/" + receivers +
                    ". Native assets were preserved; sky visibility and completion were withheld pending UV-margin inspection." };
            }
        }

        private static void MoveNativeAssets(LightmapData[] maps)
        {
            EnsureFolder(BakeFolder + "/Lightmaps");
            var assets = new HashSet<Object> { Lightmapping.lightingDataAsset };
            foreach (LightmapData map in maps)
            {
                assets.Add(map.lightmapColor);
                assets.Add(map.lightmapDir);
                if (map.shadowMask) assets.Add(map.shadowMask);
            }
            foreach (Object asset in assets)
            {
                string path = AssetDatabase.GetAssetPath(asset);
                if (string.IsNullOrEmpty(path) || path.StartsWith(BakeFolder + "/", StringComparison.Ordinal)) continue;
                if (!path.StartsWith("Assets/Scenes/BistroTest/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing to relocate a bake dependency outside BistroTest: " + path);
                string destination = AssetDatabase.GenerateUniqueAssetPath(BakeFolder + "/Lightmaps/" + Path.GetFileName(path));
                string error = AssetDatabase.MoveAsset(path, destination);
                if (!string.IsNullOrEmpty(error)) throw new IOException(error);
            }
        }

        private static void OnSkyVisibilityCompleted(bool success, string message)
        {
            if (!SessionState.GetBool(PendingKey, false)) return;
            SessionState.SetBool(PendingKey, false);
            string nativeReportPath = Path.Combine(ReportFolder, "native_validation.json");
            Report report = File.Exists(nativeReportPath)
                ? JsonUtility.FromJson<Report>(File.ReadAllText(nativeReportPath)) : new Report();
            report.phase = success ? "completed" : "sky-visibility-failed";
            report.valid = success && report.valid;
            report.progress = success ? 1 : 0;
            report.lightmapCount = LightmapSettings.lightmaps.Length;
            report.errors = success ? Array.Empty<string>() : new[] { message };
            Write(report, "status.json");
        }

        private static Scene RequireScene(bool open)
        {
            RejectTinySceneForFullBake();
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Bake Bistro in Edit Mode.");
            if (SceneManager.sceneCount == 1 && SceneManager.GetActiveScene().path == ScenePath) return SceneManager.GetActiveScene();
            if (!open) throw new InvalidOperationException("Only BistroTest must be loaded to prevent baking other scenes.");
            for (int i = 0; i < SceneManager.sceneCount; ++i)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save modified scene before opening BistroTest.");
            return EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        private static void RejectTinySceneForFullBake()
        {
        }

        private static bool IsPractical(Light light)
        {
            if (!light.isActiveAndEnabled || light.type != UnityEngine.LightType.Point) return false;
            for (Transform parent = light.transform.parent; parent; parent = parent.parent)
                if (parent.name == "Practical Lights (Showcase)") return true;
            return false;
        }

        private static bool IsVisibleReceiver(MeshRenderer renderer)
        {
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) return false;
            foreach (Material material in renderer.sharedMaterials)
                if (material && (!material.HasProperty("_Cutoff") || material.GetFloat("_Cutoff") <= 1)) return true;
            return false;
        }

        private static IEnumerable<T> Components<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (T component in root.GetComponentsInChildren<T>(true)) yield return component;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static void Write(Report report, string file)
        {
            Directory.CreateDirectory(ReportFolder);
            report.utc = DateTime.UtcNow.ToString("o");
            File.WriteAllText(Path.Combine(ReportFolder, file), JsonUtility.ToJson(report, true));
        }

        private static void BackupAsset(string path)
        {
            if (string.IsNullOrEmpty(s_backupFolder) || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string destination = Path.Combine(s_backupFolder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(path, destination, false);
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static bool ValidateLightUnits(Scene scene, List<string> errors)
        {
            var lights = new List<Light>(Components<Light>(scene));
            using (var output = new NativeArray<UnityEngine.Experimental.GlobalIllumination.LightDataGI>(lights.Count, Allocator.Temp))
            {
                MaxLightmapping.ExtractLights(lights.ToArray(), output);
                for (int i = 0; i < lights.Count; ++i)
                {
                    Light light = lights[i];
                    var data = output[i];
                    if (!IsPractical(light))
                    {
                        if (data.mode != UnityEngine.Experimental.GlobalIllumination.LightMode.Unknown)
                        { errors.Add("Excluded light was exported for baking: " + light.name); return false; }
                        continue;
                    }
                    Color expected = LightPhotometry.PointColor(light);
                    Vector3 actual = new Vector3(data.color.red, data.color.green, data.color.blue) *
                        (data.color.intensity * MaxLightmapping.LightmapToPipeline);
                    Vector3 reference = new Vector3(expected.r, expected.g, expected.b);
                    float tolerance = Mathf.Max(1e-8f, reference.magnitude * 1e-5f);
                    if ((actual - reference).magnitude > tolerance || data.shadow == 0 ||
                        data.falloff != UnityEngine.Experimental.GlobalIllumination.FalloffType.InverseSquared ||
                        data.mode != UnityEngine.Experimental.GlobalIllumination.LightMode.Mixed)
                    { errors.Add("Physical unit/falloff/visibility fixture failed: " + light.name); return false; }
                }
            }
            return true;
        }
    }
}
