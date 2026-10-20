using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP.EditorDiagnostics
{
    internal static class MaxHelmetBaselineDiagnostics
    {
        private const int Size = 768;
        private static readonly string Output = Path.GetFullPath(Path.Combine(Application.dataPath,
            "../OfflineBaseline/HelmetOnly"));
        private static double started;

        [Serializable] private sealed class Report
        {
            public string capturedUtc, sourceScene, sourceMaterial, atmosphereJson, iblSettingsJson, brdfSourceSHA256;
            public int width = Size, height = Size;
            public Vector3 cameraPosition, cameraTarget;
            public Quaternion cameraRotation;
            public float verticalFov, cameraEV100, exposureEV, exposureMultiplier, hdrScale = LightPhotometry.HDRScale;
            public float sunIntensity;
            public Vector4 sunDirection;
            public Color solarIrradiance;
            public string[] modes = { "combined", "direct", "ibl", "ibl_diffuse", "ibl_specular", "direct_unshadowed" };
            public string note = "Independent preview scene; helmet only; one solar light; no points/fog; untouched source scene. ReadPixels bottom-left RGBA float32; raw HDR before exposure.";
        }

        [InitializeOnLoadMethod]
        private static void OnLoad()
        {
            if (File.Exists(Path.Combine(Output, "request.txt"))) Begin();
        }

        [MenuItem("MaxSRP/Diagnostics/Capture Helmet Only Baseline")]
        private static void Begin()
        {
            Directory.CreateDirectory(Output);
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update -= WaitForPipeline;
            EditorApplication.update += WaitForPipeline;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        private static void WaitForPipeline()
        {
            if (EditorApplication.isCompiling || EditorApplication.timeSinceStartup - started < 3) return;
            var pipeline = RenderPipelineManager.currentPipeline as MaxRenderPipeline;
            if (pipeline == null)
            {
                if (EditorApplication.timeSinceStartup - started > 90)
                {
                    File.WriteAllText(Path.Combine(Output, "error.txt"), "No active MaxSRP pipeline after 90 seconds.");
                    EditorApplication.update -= WaitForPipeline;
                }
                return;
            }
            EditorApplication.update -= WaitForPipeline;
            try
            {
                Capture(pipeline);
                File.Delete(Path.Combine(Output, "request.txt"));
                File.Delete(Path.Combine(Output, "error.txt"));
                Debug.Log("MaxSRP helmet-only baseline saved to " + Output);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(Output, "error.txt"), error.ToString());
                Debug.LogException(error);
            }
        }

        private static object Field(object instance, string name)
        {
            return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic).GetValue(instance);
        }

        private static void Capture(MaxRenderPipeline pipeline)
        {
            Renderer source = null;
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
                if (renderer.name == "node_damagedHelmet_-6514") { source = renderer; break; }
            if (source == null) throw new InvalidOperationException("Active helmet renderer not found.");
            if (File.Exists(Path.Combine(Output,"quality_apply_request.txt"))) ApplySamplingQuality(source.sharedMaterial);
            var sourceSun = RenderSettings.sun;
            if (sourceSun == null) throw new InvalidOperationException("The source scene needs RenderSettings.sun.");
            var settings = (MaxRendererPipelineAsset)Field(pipeline, "m_setting");
            bool previousFog = settings.AtmosphereScatteringSetting.EnableAerialPerspective;
            var previousCamera = MaxLightPass.DiagnosticCamera;
            float previousMode = MaxLightPass.DiagnosticLightingMode;
            var utility = new PreviewRenderUtility();
            bool previewOpen = false;
            string brdfHash;
            using (var hash = System.Security.Cryptography.SHA256.Create())
                brdfHash = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(Path.Combine(Application.dataPath,
                    "MaxRendererPipeline/ShaderLibrary/Realtime/BRDF.hlsl")))).Replace("-", "").ToLowerInvariant();
            var report = new Report { capturedUtc = DateTime.UtcNow.ToString("O"), brdfSourceSHA256 = brdfHash,
                sourceScene = source.gameObject.scene.path,
                sourceMaterial = AssetDatabase.GetAssetPath(source.sharedMaterial), cameraPosition = new Vector3(4.8f,2.8f,-4.5f),
                cameraTarget = new Vector3(1.68f,1.413f,0), verticalFov = 32,
                cameraEV100 = settings.ColorOutput.CameraEV100, exposureEV = settings.ColorOutput.ExposureEV,
                exposureMultiplier = settings.ColorOutput.GetExposureMultiplier(), sunIntensity = sourceSun.intensity,
                atmosphereJson = JsonUtility.ToJson(settings.AtmosphereScatteringSetting),
                iblSettingsJson = JsonUtility.ToJson(settings.SkyIBL) };
            try
            {
                settings.AtmosphereScatteringSetting.EnableAerialPerspective = false;
                var helmet = UnityEngine.Object.Instantiate(source.gameObject);
                helmet.name = "Helmet Only Baseline";
                helmet.hideFlags = HideFlags.HideAndDontSave;
                utility.AddSingleGO(helmet);
                var camera = utility.camera;
                camera.allowHDR = true;
                camera.allowMSAA = false;
                camera.transform.position = report.cameraPosition;
                camera.transform.rotation = Quaternion.LookRotation(report.cameraTarget - report.cameraPosition, Vector3.up);
                camera.fieldOfView = report.verticalFov;
                camera.nearClipPlane = .05f;
                camera.farClipPlane = 30;
                camera.backgroundColor = Color.clear;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.useOcclusionCulling = false;
                report.cameraRotation = camera.transform.rotation;
                utility.BeginPreview(new Rect(0,0,Size,Size), GUIStyle.none);
                previewOpen = true;
                report.width = camera.targetTexture.width;
                report.height = camera.targetTexture.height;
                utility.lights[0].transform.rotation = sourceSun.transform.rotation;
                utility.lights[0].color = sourceSun.color;
                utility.lights[0].intensity = sourceSun.intensity;
                utility.lights[0].useColorTemperature = sourceSun.useColorTemperature;
                utility.lights[0].colorTemperature = sourceSun.colorTemperature;
                utility.lights[0].shadows = sourceSun.shadows;
                utility.lights[1].intensity = 0;
                RenderSettings.sun = utility.lights[0];
                MaxLightPass.DiagnosticCamera = camera;
                for (int mode = 0; mode < report.modes.Length; mode++)
                {
                    MaxLightPass.DiagnosticLightingMode = mode;
                    utility.Render(true, false);
                    var outputs = (IDictionary)Field(pipeline, "m_outputResources");
                    long key = (1L << 32) | (uint)camera.targetTexture.GetInstanceID();
                    if (!outputs.Contains(key)) throw new InvalidOperationException("Preview did not execute the MaxSRP HDR output path.");
                    var color = (RenderTexture)Field(outputs[key], "Color");
                    WriteTexture(color, "unity_" + report.modes[mode] + ".f32");
                    if (mode == 0)
                    {
                        var sun = SunLightData.Capture();
                        report.sunDirection = sun.Direction;
                        report.solarIrradiance = sun.LinearColor;
                        var cameras = (IDictionary)Field(pipeline, "m_cameraResources");
                        var buffers = (RenderTexture[])Field(cameras[camera.GetInstanceID()], "Buffers");
                        for (int b=0;b<3;b++) WriteTexture(buffers[b], "unity_gbuffer"+b+".f32");
                    }
                }
                if (File.Exists(Path.Combine(Output, "highlight_request.txt")))
                    CaptureHighlightVariants(utility, pipeline, camera, helmet.GetComponent<Renderer>());
                if (File.Exists(Path.Combine(Output, "imported_textures_request.txt")))
                    CaptureImportedTextures(utility, pipeline, camera, helmet.GetComponent<Renderer>(), report);
                File.WriteAllText(Path.Combine(Output, "unity_helmet.json"), JsonUtility.ToJson(report, true));
            }
            finally
            {
                MaxLightPass.DiagnosticCamera = previousCamera;
                MaxLightPass.DiagnosticLightingMode = previousMode;
                settings.AtmosphereScatteringSetting.EnableAerialPerspective = previousFog;
                if (previewOpen) utility.EndPreview();
                utility.Cleanup();
            }
        }

        [Serializable] private sealed class HighlightReport
        {
            public string capturedUtc, importedNormalFormat, graphicsDevice;
            public int width, height, normalWidth, normalHeight, mipCount;
            public string note = "Preview camera only. Float normal GBuffer and temporary texture/material clones. Source scene/importers unchanged.";
        }

        [Serializable] private sealed class QualityReport
        {
            public string appliedUtc,normalPath,roughnessPath,beforeNormalFormat,beforeRoughnessFormat,afterNormalFormat,afterRoughnessFormat;
            public int normalWidth,normalHeight,roughnessWidth,roughnessHeight;
            public bool roughnessSRGB,normalReadable,roughnessReadable;
        }

        private static void ApplySamplingQuality(Material source)
        {
            string normalPath=AssetDatabase.GetAssetPath(source.GetTexture("_NormalMap"));
            string roughPath=AssetDatabase.GetAssetPath(source.GetTexture("_RoughnessMap"));
            var normal=(TextureImporter)AssetImporter.GetAtPath(normalPath);
            var rough=(TextureImporter)AssetImporter.GetAtPath(roughPath);
            var beforeNormal=normal.GetPlatformTextureSettings("Standalone");
            var beforeRough=rough.GetPlatformTextureSettings("Standalone");
            var report=new QualityReport { appliedUtc=DateTime.UtcNow.ToString("O"),normalPath=normalPath,roughnessPath=roughPath,
                beforeNormalFormat=((Texture2D)source.GetTexture("_NormalMap")).format.ToString(),
                beforeRoughnessFormat=((Texture2D)source.GetTexture("_RoughnessMap")).format.ToString() };
            try
            {
                var normalSettings=normal.GetPlatformTextureSettings("Standalone");
                normalSettings.name="Standalone";normalSettings.overridden=true;normalSettings.format=TextureImporterFormat.BC5;
                normalSettings.maxTextureSize=beforeNormal.overridden?beforeNormal.maxTextureSize:normal.maxTextureSize;
                normalSettings.compressionQuality=100;normalSettings.crunchedCompression=false;
                normal.SetPlatformTextureSettings(normalSettings);normal.SaveAndReimport();
                var roughSettings=rough.GetPlatformTextureSettings("Standalone");
                roughSettings.name="Standalone";roughSettings.overridden=true;roughSettings.format=TextureImporterFormat.BC7;
                roughSettings.maxTextureSize=beforeRough.overridden?beforeRough.maxTextureSize:rough.maxTextureSize;
                roughSettings.compressionQuality=100;roughSettings.crunchedCompression=false;
                rough.SetPlatformTextureSettings(roughSettings);rough.SaveAndReimport();
                var normalTexture=AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
                var roughTexture=AssetDatabase.LoadAssetAtPath<Texture2D>(roughPath);
                if(normalTexture.format!=TextureFormat.BC5 || roughTexture.format!=TextureFormat.BC7)
                    throw new InvalidOperationException("Active platform did not import BC5/BC7; restoring previous overrides.");
                report.afterNormalFormat=normalTexture.format.ToString();report.afterRoughnessFormat=roughTexture.format.ToString();
                report.normalWidth=normalTexture.width;report.normalHeight=normalTexture.height;
                report.roughnessWidth=roughTexture.width;report.roughnessHeight=roughTexture.height;
                report.roughnessSRGB=rough.sRGBTexture;report.normalReadable=normal.isReadable;report.roughnessReadable=rough.isReadable;
                File.WriteAllText(Path.Combine(Output,"sampling_quality_applied.json"),JsonUtility.ToJson(report,true));
                File.Delete(Path.Combine(Output,"quality_apply_request.txt"));
            }
            catch
            {
                normal.SetPlatformTextureSettings(beforeNormal);normal.SaveAndReimport();
                rough.SetPlatformTextureSettings(beforeRough);rough.SaveAndReimport();
                throw;
            }
        }

        [Serializable] private sealed class SamplingReport
        {
            public string capturedUtc, normalFormat, roughnessFormat, normalCandidateFormat, roughnessCandidateFormat;
            public string normalCandidateGraphicsFormat, roughnessCandidateGraphicsFormat;
            public int roughnessWidth,roughnessHeight,normalCandidateBytes,roughnessCandidateBytes;
            public int diagnosticVersion=2,roughnessSourceWidth,roughnessSourceHeight,roughnessCandidateWidth,roughnessCandidateHeight;
            public bool roughnessSRGB;
            public string note = "Temporary candidates only. Matched source mip chains; not an importer emulation. No source/importer edits.";
        }

        private static void CaptureHighlightVariants(PreviewRenderUtility utility, MaxRenderPipeline pipeline,
            Camera camera, Renderer renderer)
        {
            string directory = Path.Combine(Output, "HighlightDiagnosis");
            Directory.CreateDirectory(directory);
            var resources = ((IDictionary)Field(pipeline, "m_cameraResources"))[camera.GetInstanceID()];
            var buffers = (RenderTexture[])Field(resources, "Buffers");
            var ids = (RenderTargetIdentifier[])Field(resources, "BufferIDs");
            var previousBuffer = buffers[1];
            var previousMaterial = renderer.sharedMaterial;
            var normal = (Texture2D)previousMaterial.GetTexture("_NormalMap");
            var material = new Material(previousMaterial) { hideFlags = HideFlags.HideAndDontSave };
            var textures = new List<Texture2D>();
            var floatNormal = new RenderTexture(previousBuffer.width, previousBuffer.height, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                { name = "Diagnostic float world normal", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            try
            {
                if (!floatNormal.Create()) throw new InvalidOperationException("Cannot create diagnostic float normal target.");
                buffers[1] = floatNormal;
                ids[1] = floatNormal;
                renderer.sharedMaterial = material;
                MaxLightPass.DiagnosticLightingMode = 5;
                void SaveVariant(string name)
                {
                    utility.Render(true, false);
                    var outputs = (IDictionary)Field(pipeline, "m_outputResources");
                    long key = (1L << 32) | (uint)camera.targetTexture.GetInstanceID();
                    WriteTexture((RenderTexture)Field(outputs[key], "Color"), "HighlightDiagnosis/unity_"+name+"_direct.f32");
                    WriteTexture(buffers[1], "HighlightDiagnosis/unity_"+name+"_normal.f32");
                    WriteTexture(buffers[2], "HighlightDiagnosis/unity_"+name+"_rm.f32");
                }
                SaveVariant("floatnormal");
                // Imported normal maps usually have Read/Write disabled. Instantiate
                // requires CPU-readable pixels; copy all mips on the GPU instead.
                var mipZero = new Texture2D(normal.width,normal.height,normal.format,normal.mipmapCount>1,true)
                    { name = "Diagnostic mip-zero normal", filterMode = normal.filterMode,
                        wrapMode = normal.wrapMode, anisoLevel = normal.anisoLevel };
                textures.Add(mipZero);
                mipZero.Apply(false,true);
                Graphics.CopyTexture(normal,mipZero);
                mipZero.mipMapBias = -16;
                material.SetTexture("_NormalMap", mipZero);
                SaveVariant("normal_mipzero");
                // Desktop RGBA source: alpha=1, so the shader's RG/AG decoder reads source XY.
                var raw = new Texture2D(2,2,TextureFormat.RGBA32,false,true);
                textures.Add(raw);
                string sourcePath = Path.GetFullPath(Path.Combine(Application.dataPath,"..",AssetDatabase.GetAssetPath(normal)));
                if (!ImageConversion.LoadImage(raw,File.ReadAllBytes(sourcePath),false))
                    throw new InvalidOperationException("Cannot decode source normal image.");
                raw.filterMode = normal.filterMode;
                raw.wrapMode = normal.wrapMode;
                material.SetTexture("_NormalMap",raw);
                SaveVariant("normal_raw_xy");
                var neutral = new Texture2D(1,1,TextureFormat.RGBAFloat,false,true);
                textures.Add(neutral);
                neutral.SetPixel(0,0,new Color(.5f,.5f,1,1));
                neutral.Apply(false,false);
                material.SetTexture("_NormalMap",neutral);
                SaveVariant("geometric_normal");
                if (File.Exists(Path.Combine(Output,"sampling_request.txt")))
                {
                    Texture2D MakeMipped(Texture2D source, bool linear)
                    {
                        var texture = new Texture2D(source.width,source.height,TextureFormat.RGBA32,true,linear)
                            { filterMode=source.filterMode,wrapMode=source.wrapMode,anisoLevel=source.anisoLevel };
                        textures.Add(texture);
                        texture.SetPixels32(source.GetPixels32());
                        texture.Apply(true,false);
                        return texture;
                    }
                    var normalRGBA = MakeMipped(raw,true);
                    material.SetTexture("_NormalMap",normalRGBA);
                    SaveVariant("normal_rgba_mips");
                    var normalBC5 = MakeMipped(raw,true);
                    EditorUtility.CompressTexture(normalBC5,TextureFormat.BC5,TextureCompressionQuality.Best);
                    normalBC5.Apply(false,false);
                    material.SetTexture("_NormalMap",normalBC5);
                    SaveVariant("normal_bc5_mips");
                    normalBC5.mipMapBias=-16;
                    SaveVariant("normal_bc5_mipzero");
                    normalBC5.mipMapBias=0;
                    buffers[1]=previousBuffer; ids[1]=previousBuffer;
                    SaveVariant("normal_bc5_packed");
                    material.SetTexture("_NormalMap",normalRGBA);
                    SaveVariant("normal_rgba_packed");
                    buffers[1]=floatNormal; ids[1]=floatNormal;
                    // Keep a fixed uncompressed normal while isolating roughness sampling.
                    material.SetTexture("_NormalMap",raw);
                    var rough = (Texture2D)previousMaterial.GetTexture("_RoughnessMap");
                    var roughImporter = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(rough));
                    SaveVariant("rough_imported_mips");
                    var roughZero = new Texture2D(rough.width,rough.height,rough.format,rough.mipmapCount>1,!roughImporter.sRGBTexture)
                        { filterMode=rough.filterMode,wrapMode=rough.wrapMode,anisoLevel=rough.anisoLevel };
                    textures.Add(roughZero);roughZero.Apply(false,true);Graphics.CopyTexture(rough,roughZero);
                    roughZero.mipMapBias=-16;
                    material.SetTexture("_RoughnessMap",roughZero);
                    SaveVariant("rough_imported_mipzero");
                    var roughRaw = new Texture2D(2,2,TextureFormat.RGBA32,false,!roughImporter.sRGBTexture);
                    textures.Add(roughRaw);
                    if(!ImageConversion.LoadImage(roughRaw,File.ReadAllBytes(Path.Combine(directory,"roughness_source.png")),false))
                        throw new InvalidOperationException("Prepare HighlightDiagnosis/roughness_source.png from the source roughness image first.");
                    roughRaw.filterMode=rough.filterMode;roughRaw.wrapMode=rough.wrapMode;roughRaw.anisoLevel=rough.anisoLevel;
                    material.SetTexture("_RoughnessMap",roughRaw);
                    SaveVariant("rough_rgba_mipzero");
                    var roughRGBA = MakeMipped(roughRaw,!roughImporter.sRGBTexture);
                    material.SetTexture("_RoughnessMap",roughRGBA);
                    SaveVariant("rough_rgba_mips");
                    var roughBC7 = MakeMipped(roughRaw,!roughImporter.sRGBTexture);
                    EditorUtility.CompressTexture(roughBC7,TextureFormat.BC7,TextureCompressionQuality.Best);
                    roughBC7.Apply(false,false);
                    material.SetTexture("_RoughnessMap",roughBC7);
                    SaveVariant("rough_bc7_mips");
                    roughBC7.mipMapBias=-16;
                    SaveVariant("rough_bc7_mipzero");
                    roughBC7.mipMapBias=0;
                    material.SetTexture("_NormalMap",normalBC5);
                    buffers[1]=previousBuffer;ids[1]=previousBuffer;
                    SaveVariant("sampling_bc5_bc7_packed");
                    material.SetTexture("_NormalMap",normalRGBA);
                    material.SetTexture("_RoughnessMap",roughRGBA);
                    SaveVariant("sampling_rgba_packed");
                    buffers[1]=floatNormal;ids[1]=floatNormal;
                    SaveVariant("sampling_rgba_float");
                    // Match resolution before comparing codecs: source roughness is 4K,
                    // while the imported texture currently has a 2K maximum size.
                    material.SetTexture("_NormalMap",raw);
                    var roughRaw2K = new Texture2D(2,2,TextureFormat.RGBA32,false,!roughImporter.sRGBTexture);
                    textures.Add(roughRaw2K);
                    if(!ImageConversion.LoadImage(roughRaw2K,File.ReadAllBytes(Path.Combine(directory,"roughness_source_2048.png")),false))
                        throw new InvalidOperationException("Prepare roughness_source_2048.png for matched-resolution tests.");
                    roughRaw2K.filterMode=rough.filterMode;roughRaw2K.wrapMode=rough.wrapMode;
                    var roughRGBA2K=MakeMipped(roughRaw2K,!roughImporter.sRGBTexture);
                    material.SetTexture("_RoughnessMap",roughRGBA2K);SaveVariant("rough_rgba2k_mips");
                    var roughBC72K=MakeMipped(roughRaw2K,!roughImporter.sRGBTexture);
                    EditorUtility.CompressTexture(roughBC72K,TextureFormat.BC7,TextureCompressionQuality.Best);roughBC72K.Apply(false,false);
                    material.SetTexture("_RoughnessMap",roughBC72K);SaveVariant("rough_bc7_2k_mips");
                    var roughDXT12K=MakeMipped(roughRaw2K,!roughImporter.sRGBTexture);
                    EditorUtility.CompressTexture(roughDXT12K,TextureFormat.DXT1,TextureCompressionQuality.Best);roughDXT12K.Apply(false,false);
                    material.SetTexture("_RoughnessMap",roughDXT12K);SaveVariant("rough_dxt1_2k_mips");
                    roughRGBA2K.mipMapBias=roughBC72K.mipMapBias=roughDXT12K.mipMapBias=-16;
                    material.SetTexture("_RoughnessMap",roughRGBA2K);SaveVariant("rough_rgba2k_mipzero");
                    material.SetTexture("_RoughnessMap",roughBC72K);SaveVariant("rough_bc7_2k_mipzero");
                    material.SetTexture("_RoughnessMap",roughDXT12K);SaveVariant("rough_dxt1_2k_mipzero");
                    roughRGBA2K.mipMapBias=roughBC72K.mipMapBias=roughDXT12K.mipMapBias=0;
                    material.SetTexture("_NormalMap",normalBC5);material.SetTexture("_RoughnessMap",roughBC72K);
                    buffers[1]=previousBuffer;ids[1]=previousBuffer;SaveVariant("sampling_bc5_bc7_2k_packed");
                    material.SetTexture("_NormalMap",normalRGBA);material.SetTexture("_RoughnessMap",roughRGBA2K);
                    SaveVariant("sampling_rgba2k_packed");
                    buffers[1]=floatNormal;ids[1]=floatNormal;
                    File.WriteAllText(Path.Combine(directory,"sampling_candidates.json"),JsonUtility.ToJson(new SamplingReport {
                        capturedUtc=DateTime.UtcNow.ToString("O"),normalFormat=normal.format.ToString(),roughnessFormat=rough.format.ToString(),
                        normalCandidateFormat=normalBC5.format.ToString(),roughnessCandidateFormat=roughBC7.format.ToString(),roughnessSRGB=roughImporter.sRGBTexture,
                        normalCandidateGraphicsFormat=normalBC5.graphicsFormat.ToString(),roughnessCandidateGraphicsFormat=roughBC7.graphicsFormat.ToString(),
                        roughnessWidth=rough.width,roughnessHeight=rough.height,normalCandidateBytes=normalBC5.GetRawTextureData<byte>().Length,
                        roughnessCandidateBytes=roughBC7.GetRawTextureData<byte>().Length,roughnessSourceWidth=roughRaw.width,roughnessSourceHeight=roughRaw.height,
                        roughnessCandidateWidth=roughBC7.width,roughnessCandidateHeight=roughBC7.height },true));
                    File.Delete(Path.Combine(Output,"sampling_request.txt"));
                }
                File.WriteAllText(Path.Combine(directory,"unity_variants.json"),JsonUtility.ToJson(new HighlightReport {
                    capturedUtc=DateTime.UtcNow.ToString("O"),width=floatNormal.width,height=floatNormal.height,
                    importedNormalFormat=normal.format.ToString(),normalWidth=normal.width,normalHeight=normal.height,
                    mipCount=normal.mipmapCount,graphicsDevice=SystemInfo.graphicsDeviceType.ToString() },true));
                File.Delete(Path.Combine(Output,"highlight_request.txt"));
            }
            finally
            {
                renderer.sharedMaterial = previousMaterial;
                buffers[1] = previousBuffer;
                ids[1] = previousBuffer;
                Shader.SetGlobalTexture("_GBuffer1",previousBuffer);
                floatNormal.Release();
                UnityEngine.Object.DestroyImmediate(floatNormal);
                UnityEngine.Object.DestroyImmediate(material);
                foreach (var texture in textures) UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Serializable] private sealed class ImportedTexture
        {
            public string property, sourcePath, sourceSHA256, importerSHA256, format, graphicsFormat, filterMode, wrapMode;
            public int width, height, mipCount, anisoLevel;
            public float mipMapBias;
            public bool sRGB, readable;
            public string[] mipFiles;
        }

        [Serializable] private sealed class ImportedTextureReport
        {
            public string capturedUtc, graphicsDevice;
            public string note = "Actual GPU-decoded linear RGBA texels at every imported mip. No importer edits. Separate preview uses mip bias -16, bilinear, aniso=0 on temporary compressed clones.";
            public List<ImportedTexture> textures = new List<ImportedTexture>();
        }

        private static string SourceHash(string path)
        {
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        private static void CaptureImportedTextures(PreviewRenderUtility utility, MaxRenderPipeline pipeline,
            Camera camera, Renderer renderer, Report cameraReport)
        {
            string directory = Path.Combine(Output, "ImportedTextures");
            Directory.CreateDirectory(directory);
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/MaxRendererPipeline/Editor/MaxImportedTextureExport.compute");
            if (compute == null) throw new InvalidOperationException("Imported-texture export compute shader is not ready.");
            int kernel = compute.FindKernel("ExportImportedMip");
            var previousMaterial = renderer.sharedMaterial;
            var material = new Material(previousMaterial) { hideFlags = HideFlags.HideAndDontSave };
            var clones = new List<Texture2D>();
            float previousMode = MaxLightPass.DiagnosticLightingMode;
            var report = new ImportedTextureReport { capturedUtc = DateTime.UtcNow.ToString("O"),
                graphicsDevice = SystemInfo.graphicsDeviceType.ToString() };
            try
            {
                foreach (string property in new[] { "_AlbedoMap", "_NormalMap", "_RoughnessMap", "_MetalnessMap" })
                {
                    var texture = previousMaterial.GetTexture(property) as Texture2D;
                    if (texture == null) throw new InvalidOperationException("Missing texture: " + property);
                    string path = AssetDatabase.GetAssetPath(texture);
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    string absolute = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
                    var entry = new ImportedTexture { property = property, sourcePath = path,
                        sourceSHA256 = SourceHash(absolute), importerSHA256 = SourceHash(absolute + ".meta"),
                        width = texture.width, height = texture.height, mipCount = texture.mipmapCount,
                        format = texture.format.ToString(), graphicsFormat = texture.graphicsFormat.ToString(),
                        filterMode = texture.filterMode.ToString(), wrapMode = texture.wrapMode.ToString(),
                        anisoLevel = texture.anisoLevel, mipMapBias = texture.mipMapBias,
                        sRGB = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat), readable = texture.isReadable,
                        mipFiles = new string[texture.mipmapCount] };
                    for (int mip = 0; mip < texture.mipmapCount; mip++)
                    {
                        int width = Mathf.Max(1, texture.width >> mip), height = Mathf.Max(1, texture.height >> mip);
                        var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat,
                            RenderTextureReadWrite.Linear) { enableRandomWrite = true, filterMode = FilterMode.Point };
                        var pixels = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                        var previousActive = RenderTexture.active;
                        try
                        {
                            if (!target.Create()) throw new InvalidOperationException("Cannot allocate imported mip export.");
                            compute.SetTexture(kernel, "_Source", texture); compute.SetTexture(kernel, "_Output", target);
                            compute.SetInts("_Size", width, height); compute.SetInt("_Mip", mip);
                            compute.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1);
                            RenderTexture.active = target;
                            pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0, false); pixels.Apply(false, false);
                            entry.mipFiles[mip] = property.Substring(1) + "_mip" + mip + ".exr";
                            File.WriteAllBytes(Path.Combine(directory, entry.mipFiles[mip]), pixels.EncodeToEXR(
                                Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP));
                        }
                        finally
                        {
                            RenderTexture.active = previousActive;
                            target.Release(); UnityEngine.Object.DestroyImmediate(target);
                            UnityEngine.Object.DestroyImmediate(pixels);
                        }
                    }
                    report.textures.Add(entry);
                    var clone = new Texture2D(texture.width, texture.height, texture.format, texture.mipmapCount > 1,
                        !entry.sRGB) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear,
                            wrapMode = texture.wrapMode, anisoLevel = 0, mipMapBias = -16 };
                    clones.Add(clone); clone.Apply(false, true); Graphics.CopyTexture(texture, clone);
                    material.SetTexture(property, clone);
                }
                renderer.sharedMaterial = material;
                MaxLightPass.DiagnosticLightingMode = 5;
                utility.Render(true, false);
                var outputs = (IDictionary)Field(pipeline, "m_outputResources");
                long key = (1L << 32) | (uint)camera.targetTexture.GetInstanceID();
                WriteTexture((RenderTexture)Field(outputs[key], "Color"), "ImportedTextures/unity_mipzero_direct.f32");
                var resources = ((IDictionary)Field(pipeline, "m_cameraResources"))[camera.GetInstanceID()];
                var buffers = (RenderTexture[])Field(resources, "Buffers");
                for (int i = 0; i < 3; i++) WriteTexture(buffers[i], "ImportedTextures/unity_mipzero_gbuffer" + i + ".f32");
                File.WriteAllText(Path.Combine(directory, "unity_helmet.json"), JsonUtility.ToJson(cameraReport, true));
                File.WriteAllText(Path.Combine(directory, "imported_textures.json"), JsonUtility.ToJson(report, true));
                File.Delete(Path.Combine(Output, "imported_textures_request.txt"));
            }
            finally
            {
                renderer.sharedMaterial = previousMaterial; MaxLightPass.DiagnosticLightingMode = previousMode;
                UnityEngine.Object.DestroyImmediate(material);
                foreach (var texture in clones) UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void WriteTexture(RenderTexture source, string file)
        {
            var previous = RenderTexture.active;
            bool previousSRGB = GL.sRGBWrite;
            var readable = RenderTexture.GetTemporary(source.width,source.height,0,
                RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(source.width,source.height,TextureFormat.RGBAFloat,false,true);
            try
            {
                // ReadPixels cannot convert every packed GBuffer format directly.
                GL.sRGBWrite = false;
                Graphics.Blit(source,readable);
                RenderTexture.active = readable;
                pixels.ReadPixels(new Rect(0,0,source.width,source.height),0,0,false);
                pixels.Apply(false,false);
                using (var writer = new BinaryWriter(File.Create(Path.Combine(Output,file))))
                    foreach (Color color in pixels.GetPixels())
                    { writer.Write(color.r);writer.Write(color.g);writer.Write(color.b);writer.Write(color.a); }
            }
            finally
            {
                RenderTexture.active = previous;
                GL.sRGBWrite = previousSRGB;
                RenderTexture.ReleaseTemporary(readable);
                UnityEngine.Object.DestroyImmediate(pixels);
            }
        }
    }
}
