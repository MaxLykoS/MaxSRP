using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MaxSRP.EditorTools
{
    // Source payloads and generated texture payloads stay in the ignored dependency tree.
    // Materials, configuration and the scene itself are project-authored assets.
    public static class MaxBistroSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/BistroTest/BistroTest.unity";
        private const string SourcePath = "Assets/ThirdParty/Bistro/BistroExterior.fbx";
        private const string TextureDirectory = "Assets/ThirdParty/Bistro/ConvertedTextures/";
        private const string MaterialDirectory = "Assets/Scenes/BistroTest/Materials";
        private const string MainPipelinePath = "Assets/MaxRendererPipeline/Max Renderer Pipeline Asset.asset";
        private static readonly Color WarmWhite = new Color(1.0f, 0.88f, 0.72f, 1.0f);

        [MenuItem("MaxSRP/Bistro/Build Separate Test Scene")]
        private static void BuildMenu() { Debug.Log(Build()); }

        public static string Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Build the Bistro test scene in Edit Mode.");
            for (int i = 0; i < SceneManager.sceneCount; ++i)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                    throw new InvalidOperationException("Save the modified scene before building Bistro: " + scene.path);
            }

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePath);
            var shader = Shader.Find("MaxSRP/Bistro GBuffer");
            var mainPipeline = AssetDatabase.LoadAssetAtPath<MaxRendererPipelineAsset>(MainPipelinePath);
            if (!source || !shader || !mainPipeline)
                throw new InvalidOperationException("Bistro source, Bistro GBuffer shader, or the main MaxSRP asset is missing. Restore/import the local dependency first.");
            if (!AssetDatabase.IsValidFolder(TextureDirectory.TrimEnd('/')))
                throw new InvalidOperationException("Run the Bistro texture conversion before building the scene.");

            EnsureFolder("Assets/Scenes/BistroTest");
            EnsureFolder(MaterialDirectory);
            var materials = BuildMaterials(source, shader);
            AssetDatabase.SaveAssets();
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            bool saved = false;
            try
            {
                Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var environment = new GameObject("Bistro Test Environment");
                var selection = environment.AddComponent<MaxSceneExposure>();
                selection.CameraEV100 = 10.5f;
                selection.ExposureEV = 0;

                var geometry = (GameObject)PrefabUtility.InstantiatePrefab(source, scene);
                geometry.name = "Bistro Exterior (Local Source)";
                // ModelImporter has already applied FBX units and the authored root scale.
                // An extra centimetre conversion here would shrink the whole street twice.
                Vector3 importedScale = geometry.transform.localScale;
                var renderers = geometry.GetComponentsInChildren<MeshRenderer>(true);
                foreach (MeshRenderer renderer in renderers)
                {
                    Material[] replacement = renderer.sharedMaterials;
                    for (int slot = 0; slot < replacement.Length; ++slot)
                    {
                        if (!replacement[slot]) continue;
                        if (!materials.TryGetValue(replacement[slot].name, out Material material))
                            throw new InvalidOperationException("Unmapped Bistro material: " + replacement[slot].name);
                        replacement[slot] = material;
                    }
                    renderer.sharedMaterials = replacement;
                    renderer.shadowCastingMode = ShadowCastingMode.On;
                    renderer.receiveShadows = true;
                    renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                }

                Camera[] importedCameras = geometry.GetComponentsInChildren<Camera>(true);
                Camera sourceCamera = importedCameras.Length > 0 ? importedCameras[0] : null;
                Bounds bounds = GetBounds(renderers);
                Camera camera = CreateCamera(sourceCamera, bounds);
                foreach (Camera importedCamera in importedCameras)
                {
                    importedCamera.enabled = false;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(importedCamera);
                    foreach (AudioListener listener in importedCamera.GetComponents<AudioListener>())
                    {
                        listener.enabled = false;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(listener);
                    }
                }
                foreach (Light importedLight in geometry.GetComponentsInChildren<Light>(true))
                {
                    importedLight.enabled = false;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(importedLight);
                }

                Light sun = CreateSun(environment.transform);
                RenderSettings.sun = sun;
                RenderSettings.fog = false; // Aerial perspective comes from MaxSRP's atmosphere.
                var practical = new GameObject("Practical Lights (Showcase)");
                practical.transform.SetParent(environment.transform, false);
                int practicalCount = CreatePracticalLights(renderers, practical.transform);
                var stress = new GameObject("Stress Tests (Enable One Child Only)");
                stress.transform.SetParent(environment.transform, false);
                foreach (int count in new[] { 128, 256, 512 })
                    CreateStressGroup(stress.transform, count, bounds, camera);

                if (!EditorSceneManager.SaveScene(scene, ScenePath))
                    throw new IOException("Unity could not save " + ScenePath);
                saved = true;
                Selection.activeGameObject = camera.gameObject;
                if (SceneView.lastActiveSceneView)
                {
                    SceneView.lastActiveSceneView.LookAtDirect(camera.transform.position + camera.transform.forward * 12,
                        camera.transform.rotation, 12);
                }
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();

                var report = new StringBuilder();
                report.AppendLine("Bistro test scene built: " + ScenePath);
                report.AppendLine("Local FBX dependency: " + SourcePath);
                report.AppendLine("Materials: " + materials.Count + "; mesh renderers: " + renderers.Length);
                report.AppendLine("Imported root scale preserved: " + Precise(importedScale));
                report.AppendLine("World bounds center: " + Precise(bounds.center) + "; size: " + Precise(bounds.size));
                report.AppendLine("Camera position: " + Precise(camera.transform.position) + "; Euler: " + Precise(camera.transform.eulerAngles));
                report.AppendLine("Active practical point lights: " + practicalCount + "; inactive stress sets: 128 / 256 / 512");
                report.AppendLine("Sun: 30000 lux; physical point units: lumens; exposure: EV100 10.5");
                report.AppendLine("Glass transmission is unsupported: glass-only submeshes are clipped from GBuffer, motion and shadows.");
                report.AppendLine("Local lights do not cast shadows; emissive materials are not GI emitters.");
                return report.ToString();
            }
            finally
            {
                if (!saved) EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
            }
        }

        private static Dictionary<string, Material> BuildMaterials(GameObject source, Shader shader)
        {
            var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
            foreach (Renderer renderer in source.GetComponentsInChildren<Renderer>(true))
            foreach (Material sourceMaterial in renderer.sharedMaterials)
            {
                if (!sourceMaterial || materials.ContainsKey(sourceMaterial.name)) continue;
                string name = sourceMaterial.name;
                string stem = name.Replace(".DoubleSided", "");
                string path = MaterialDirectory + "/" + SafeFileName(name) + ".mat";
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!material)
                {
                    material = new Material(shader) { name = name };
                    AssetDatabase.CreateAsset(material, path);
                }
                material.shader = shader;
                material.name = name;
                Texture2D baseColor = Texture(stem, "BaseColor");
                Texture2D mask = Texture(stem, "Specular");
                Texture2D normal = Texture(stem, "Normal");
                if (!baseColor || !normal)
                    throw new InvalidOperationException("Missing converted textures for Bistro material: " + stem);
                if (!mask) mask = FallbackMask();
                Texture2D emission = Texture(stem, "Emissive");
                string lower = stem.ToLowerInvariant();
                bool doubleSided = name.EndsWith(".DoubleSided", StringComparison.OrdinalIgnoreCase);
                bool foliage = lower.StartsWith("foliage_") && !lower.Contains("trunk");
                bool glass = lower.Contains("glass") && !lower.Contains("masked");
                bool emissive = emission || lower.Contains("emissive") || lower.Contains("light_bulb") || lower.Contains("support_bulb");
                material.SetTexture("_BaseColorMap", baseColor);
                material.SetTexture("_MaskMap", mask);
                material.SetTexture("_NormalMap", normal);
                material.SetTexture("_EmissionMap", emission ? emission : baseColor);
                material.SetFloat("_EmissionNits", emissive && !glass ? (lower.Contains("stringlights") ? 800 : 2200) : 0);
                material.SetFloat("_Cutoff", glass ? 1.1f : (doubleSided || foliage || lower.Contains("masked") ? 0.5f : 0));
                material.SetFloat("_Cull", doubleSided || foliage ? (float)CullMode.Off : (float)CullMode.Back);
                material.enableInstancing = true;
                EditorUtility.SetDirty(material);
                materials.Add(name, material);
            }
            return materials;
        }

        private static Camera CreateCamera(Camera source, Bounds bounds)
        {
            var gameObject = new GameObject("Main Camera");
            gameObject.tag = "MainCamera";
            var camera = gameObject.AddComponent<Camera>();
            if (source) gameObject.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            else
            {
                gameObject.transform.position = new Vector3(bounds.center.x, 2, bounds.min.z + bounds.size.z * 0.2f);
                gameObject.transform.LookAt(new Vector3(bounds.center.x, 2, bounds.center.z));
            }
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 250;
            camera.fieldOfView = 60;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.allowHDR = true;
            camera.allowMSAA = false;
            gameObject.AddComponent<AudioListener>();
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type controller = assembly.GetType("UnityTemplateProjects.SimpleCameraController");
                if (controller == null) continue;
                gameObject.AddComponent(controller);
                break;
            }
            return camera;
        }

        private static Light CreateSun(Transform parent)
        {
            var gameObject = new GameObject("Sun (30000 lux)");
            gameObject.transform.SetParent(parent, false);
            gameObject.transform.rotation = Quaternion.Euler(8, 140, 0);
            var sun = gameObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 30000;
            sun.color = Color.white;
            sun.shadows = LightShadows.Soft;
            return sun;
        }

        private static int CreatePracticalLights(MeshRenderer[] renderers, Transform parent)
        {
            var placements = new List<Vector3>();
            foreach (MeshRenderer renderer in renderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                Material[] materials = renderer.sharedMaterials;
                for (int slot = 0; slot < materials.Length; ++slot)
                {
                    Material material = materials[slot];
                    if (!material || material.GetFloat("_EmissionNits") <= 0 || material.GetFloat("_Cutoff") > 1) continue;
                    string name = material.name.ToLowerInvariant();
                    // String bulb strips span entire streets; one light at their combined center
                    // is misleading. The stress groups exercise many lights independently.
                    if (name.Contains("stringlights")) continue;
                    Bounds worldBounds = renderer.bounds;
                    if (filter && filter.sharedMesh && slot < filter.sharedMesh.subMeshCount)
                        worldBounds = TransformBounds(renderer.transform.localToWorldMatrix, filter.sharedMesh.GetSubMesh(slot).bounds);
                    if (worldBounds.size.magnitude > 8 || !IsFinite(worldBounds.center)) continue;
                    Vector3 position = worldBounds.center;
                    bool duplicate = false;
                    foreach (Vector3 placed in placements)
                        if ((placed - position).sqrMagnitude < 1) { duplicate = true; break; }
                    if (duplicate) continue;
                    bool street = name.Contains("streetlight") || name.Contains("lantern");
                    CreatePoint(parent, "Practical " + (placements.Count + 1).ToString("D3") + " - " + material.name,
                        position, street ? 6000 : 3000, street ? 9 : 7, WarmWhite);
                    placements.Add(position);
                }
            }
            return placements.Count;
        }

        private static void CreateStressGroup(Transform parent, int count, Bounds bounds, Camera camera)
        {
            var group = new GameObject("Stress " + count + " Point Lights (Extra)");
            group.transform.SetParent(parent, false);
            group.SetActive(false);
            int width = count >= 256 ? 16 : 8;
            int depth = count / (width * 2);
            float floor = Mathf.Max(0, camera.transform.position.y - 2.5f);
            // Fixed spatial distribution makes counts repeatable and independent of frame rate.
            for (int i = 0; i < count; ++i)
            {
                int x = i % width;
                int z = (i / width) % depth;
                int level = i / (width * depth);
                var position = new Vector3(
                    Mathf.Lerp(bounds.min.x + 5, bounds.max.x - 5, (x + 0.5f) / width),
                    floor + 2.2f + level * 4.5f,
                    Mathf.Lerp(bounds.min.z + 5, bounds.max.z - 5, (z + 0.5f) / depth));
                Color color = i % 3 == 0 ? new Color(0.72f, 0.84f, 1) : WarmWhite;
                CreatePoint(group.transform, "Stress Point " + (i + 1).ToString("D3"), position, 1800, 8, color);
            }
        }

        private static void CreatePoint(Transform parent, string name, Vector3 position, float lumens, float range, Color color)
        {
            var gameObject = new GameObject(name);
            gameObject.transform.SetParent(parent, false);
            gameObject.transform.position = position;
            var light = gameObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.lightmapBakeType = LightmapBakeType.Realtime;
            light.intensity = lumens;
            light.range = range;
            light.color = color;
            light.shadows = LightShadows.None;
            gameObject.AddComponent<MaxPhysicalLight>().PointUnit = PointLightUnit.Lumen;
        }

        private static Bounds GetBounds(MeshRenderer[] renderers)
        {
            bool first = true;
            Bounds bounds = default;
            foreach (MeshRenderer renderer in renderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (first) { bounds = renderer.bounds; first = false; }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (first) throw new InvalidOperationException("The Bistro model contains no active mesh renderers.");
            return bounds;
        }

        private static Bounds TransformBounds(Matrix4x4 matrix, Bounds local)
        {
            Vector3 x = matrix.MultiplyVector(new Vector3(local.extents.x, 0, 0));
            Vector3 y = matrix.MultiplyVector(new Vector3(0, local.extents.y, 0));
            Vector3 z = matrix.MultiplyVector(new Vector3(0, 0, local.extents.z));
            Vector3 extents = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(matrix.MultiplyPoint3x4(local.center), extents * 2);
        }

        private static bool IsFinite(Vector3 value) => !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z) &&
            !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);

        private static Texture2D Texture(string stem, string suffix) =>
            AssetDatabase.LoadAssetAtPath<Texture2D>(TextureDirectory + stem + "_" + suffix + ".png");

        private static Texture2D FallbackMask()
        {
            const string directory = "Assets/ThirdParty/Bistro/Generated";
            // The importer recognizes _Specular as a linear mask rather than sRGB.
            const string path = directory + "/Fallback_Specular.png";
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture) return texture;
            EnsureFolder(directory);
            var fallback = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            try
            {
                fallback.SetPixel(0, 0, new Color(1, 0.6f, 0, 1));
                fallback.Apply(false, false);
                File.WriteAllBytes(path, fallback.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(fallback); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (!texture) throw new IOException("Could not import the fallback Bistro mask texture.");
            return texture;
        }

        private static string SafeFileName(string name)
        {
            foreach (char character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
            return name;
        }

        private static string Precise(Vector3 value) => string.Format(CultureInfo.InvariantCulture,
            "({0:R}, {1:R}, {2:R})", value.x, value.y, value.z);

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int split = path.LastIndexOf('/');
            string parent = path.Substring(0, split);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(split + 1));
        }
    }
}
