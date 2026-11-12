using System;
using System.IO;
using System.Threading;
using MaxSRP.DDGI;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MaxSRP.DDGIBaker.Editor
{
    internal sealed class DdgiGpuBake : IDisposable
    {
        private const string Package = "Packages/com.maxsrp.ddgi-baker/";
        private const int CompactTraceSlicesPerBatch = 8;
        private const int GpuWaitTimeoutMilliseconds = 60000;

        private readonly DdgiBakeSettings settings;
        private readonly RayTracingShader trace;
        private CommandBuffer commands;
        private ProbeVolume volume;
        private CompactBakeProbeVolume compactVolume;
        private CoarseProbeFeedback coarseFeedback;
        private RayTracingAccelerationStructure scene;
        private GameObject instance;
        private Mesh mesh;
        private Material[] materials;
        private RenderTexture syncMarker;
        private GraphicsFence completionFence;
        private bool submitted;

        internal DdgiGpuBake(BakeGeometry geometry, DdgiBakeSettings settings)
        {
            RequireHardware();
            settings.Validate();
            geometry.RequireMaterialMode(settings.materialMode);
            this.settings = settings;
            if (settings.compactVolume)
                EnsureCompactShaders();
            trace = AssetDatabase.LoadAssetAtPath<RayTracingShader>(Package + (settings.compactVolume
                ? "Baker/Shaders/CompactProbeTrace.raytrace" : "Baker/Shaders/ProbeTrace.raytrace"));
            var update = AssetDatabase.LoadAssetAtPath<ComputeShader>(Package + (settings.compactVolume
                ? "Baker/Shaders/CompactProbeUpdate.compute" : "DDGI/Shaders/ProbeUpdate.compute"));
            var surface = AssetDatabase.LoadAssetAtPath<Shader>(Package + "Baker/Shaders/ProbeBakeSurface.shader");
            if (!trace || !update)
                throw new InvalidOperationException("Install the pinned DDGI shaders with " + Package + "DDGI/Shaders/SDK/Install.ps1.");
            if (!surface || !surface.isSupported)
                throw new InvalidOperationException("The DDGI Baker surface shader failed to load or compile.");
            if (settings.materialMode == DdgiBakeMaterialMode.SceneMaterials && surface.FindPropertyIndex("_BakeAlbedo") < 0)
                throw new InvalidOperationException("The DDGI ray hit shader did not expose its scene material properties.");
            try
            {
                if (!string.IsNullOrWhiteSpace(settings.coarseFeedbackReport))
                    coarseFeedback = new CoarseProbeFeedback(geometry, settings);
                commands = new CommandBuffer { name = "DDGI TOD Bake" };
                if (!SystemInfo.supportsGraphicsFence)
                {
                    syncMarker = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                    {
                        name = "DDGI GPU sync marker", hideFlags = HideFlags.HideAndDontSave
                    };
                    if (!syncMarker.Create())
                        throw new InvalidOperationException("Could not allocate the DDGI GPU sync marker.");
                }
                if (settings.compactVolume && Application.isBatchMode)
                    Debug.Log(syncMarker ? "[DDGI Baker] GPU synchronization uses a 1×1 marker." :
                        "[DDGI Baker] GPU synchronization uses a graphics fence.");
                var description = new ProbeVolume.Description
                {
                    Origin = settings.origin,
                    Counts = settings.counts,
                    Spacing = Vector3.one * settings.spacing,
                    Rays = settings.rays,
                    MaxRayDistance = 1e27f,
                    NormalBias = settings.normalBias,
                    DistanceExponent = settings.distanceExponent,
                    EncodingGamma = settings.gamma,
                    IrradianceThreshold = 1e20f,
                    BrightnessThreshold = 1e20f,
                    MinFrontfaceDistance = settings.minFrontfaceDistance,
                    RandomBackfaceThreshold = .1f,
                    FixedBackfaceThreshold = settings.fixedBackfaceThreshold,
                    Relocation = settings.Relocation,
                    Classification = settings.classification
                };
                if (settings.compactVolume)
                    compactVolume = new CompactBakeProbeVolume(update, description);
                else
                    volume = new ProbeVolume(update, description);
                BuildAccelerationStructure(geometry, surface);
                if (compactVolume != null) compactVolume.ClearProbeData(commands);
                else volume.ClearProbeData(commands);
                Submit();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal static void RequireHardware()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || !SystemInfo.supportsRayTracing)
                throw new NotSupportedException("DDGI baking needs a Windows Editor running Direct3D12 on a DXR GPU. Restart Unity with -force-d3d12.");
            if (!SystemInfo.supportsAsyncGPUReadback || !SystemInfo.supports2DArrayTextures ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.LoadStore) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, FormatUsage.LoadStore))
                throw new NotSupportedException("The current graphics device lacks DDGI array UAV or GPU readback support.");
        }

        private static void EnsureCompactShaders()
        {
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.LoadStore) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.Linear) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16_SFloat, FormatUsage.LoadStore) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16G16_SFloat, FormatUsage.Linear))
                throw new NotSupportedException("Compact volume baking needs filterable half-float irradiance and distance UAVs.");
            const string sdk = "../../DDGI/Shaders/SDK/";
            if (!File.Exists(Package + "DDGI/Shaders/SDK/rtxgi-sdk/shaders/ddgi/Irradiance.hlsl"))
                throw new InvalidOperationException("Install the pinned DDGI shaders with " + Package + "DDGI/Shaders/SDK/Install.ps1.");

            // Load the normal Unity config once, then select the SDK's X/Y atlas planes.
            // Both variants compile the same authored raygen and official kernel bodies.
            const string prefix = "#include \"" + sdk + "UnitySdk.hlsl\"\n" +
                "#undef RTXGI_COORDINATE_SYSTEM\n#define RTXGI_COORDINATE_SYSTEM RTXGI_COORDINATE_SYSTEM_RIGHT_Z_UP\n" +
                "#define MAX_DDGI_COMPACT_TRACE 1\n";
            Publish("Baker/Shaders/CompactProbeTrace.raytrace",
                File.ReadAllText(Package + "Baker/Shaders/ProbeTrace.raytrace.txt"));
            Publish("Baker/Shaders/CompactProbeUpdate.compute",
                File.ReadAllText(Package + "DDGI/Shaders/ProbeUpdate.compute.txt").Replace("\"SDK/", "\"" + sdk));

            void Publish(string path, string body)
            {
                path = Package + path;
                string source = prefix + body;
                if (!File.Exists(path) || File.ReadAllText(path) != source)
                {
                    File.WriteAllText(path, source);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                }
            }
        }

        internal RenderTexture ProbeDataTexture => compactVolume?.ProbeData ?? volume.ProbeData;
        internal RenderTexture IrradianceTexture => compactVolume?.Irradiance ?? volume.Irradiance;
        internal RenderTexture DistanceTexture => compactVolume?.Distance ?? volume.Distance;

        internal NativeGiParity.Atlas BuildLayout() => ReadAtlas(BuildLayoutTexture());

        internal RenderTexture BuildLayoutTexture()
        {
            int total = settings.geometryPasses + (settings.classification ? 1 : 0);
            for (int pass = 0; pass < total; pass++)
            {
                Progress("Probe placement", pass, total);
                if (settings.compactVolume && Application.isBatchMode)
                    Debug.Log($"[DDGI Baker] Probe placement {pass + 1}/{total}.");
                UploadConstants(ProbeVolume.IdentityRotation, 0f);
                Trace(Vector3.up, true);
                if (pass < settings.geometryPasses)
                {
                    if (compactVolume != null) compactVolume.Relocate(commands);
                    else volume.Relocate(commands);
                }
                if (settings.classification)
                {
                    if (compactVolume != null) compactVolume.Classify(commands);
                    else volume.Classify(commands);
                }
                Submit();
                WaitForGpu($"probe placement update {pass + 1}/{total}");
            }
            return ProbeDataTexture;
        }

        internal (NativeGiParity.Atlas irradiance, NativeGiParity.Atlas distance) BakeKey(int index)
        {
            BakeLighting(index);
            return (ReadAtlas(IrradianceTexture), ReadAtlas(DistanceTexture));
        }

        internal void BakeLighting(int index)
        {
            float hour = settings.hours[index];
            Vector3 sun = DdgiBakeSettings.SunDirection(hour);
            coarseFeedback?.SelectKey(index);
            if (compactVolume != null) compactVolume.ClearLighting(commands);
            else volume.ClearLighting(commands);
            string stage = $"TOD {hour:0.##} h ({index + 1}/{settings.hours.Length})";
            for (int frame = 0; frame < settings.iterations; frame++)
            {
                Progress(stage, frame, settings.iterations);
                bool logFrame = settings.compactVolume && Application.isBatchMode &&
                    (frame % 16 == 0 || frame == settings.iterations - 1);
                if (logFrame)
                    Debug.Log($"[DDGI Baker] {stage}, iteration {frame + 1}/{settings.iterations}: trace.");
                Vector4 rotation = settings.rotatingDirections ? DdgiBakeSettings.RayRotation(frame) : ProbeVolume.IdentityRotation;
                UploadConstants(rotation, frame < 8 ? 0f : settings.hysteresis);
                Trace(sun, false);
                if (logFrame)
                    Debug.Log($"[DDGI Baker] {stage}, iteration {frame + 1}/{settings.iterations}: trace complete, blending.");
                Blend();
                // NativeGI repeats the update with weight 1 to settle the octahedral borders.
                UploadConstants(rotation, 1f);
                Blend();
                Submit();
                WaitForGpu($"{stage}, iteration {frame + 1}/{settings.iterations} blend");
                if (logFrame)
                    Debug.Log($"[DDGI Baker] {stage}, iteration {frame + 1}/{settings.iterations}: blend complete.");
            }
        }

        private void UploadConstants(Vector4 rotation, float hysteresis)
        {
            if (compactVolume != null) compactVolume.UploadConstants(commands, rotation, hysteresis);
            else volume.UploadConstants(commands, rotation, hysteresis);
        }

        private void Blend()
        {
            if (compactVolume != null) compactVolume.Blend(commands);
            else volume.Blend(commands);
        }

        private void BuildAccelerationStructure(BakeGeometry geometry, Shader surface)
        {
            // Unity 2022.3 omits renderers in Preview Scenes from this DXR acceleration structure.
            instance = new GameObject("DDGI trace mesh", typeof(MeshFilter), typeof(MeshRenderer))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            mesh = geometry.CreateMesh();
            materials = geometry.CreateTraceMaterials(surface, out var flags);
            instance.GetComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = instance.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            renderer.rayTracingMode = RayTracingMode.Static;
            var configuration = new RayTracingAccelerationStructure.RASSettings(
                RayTracingAccelerationStructure.ManagementMode.Manual,
                RayTracingAccelerationStructure.RayTracingModeMask.Everything, -1);
            scene = new RayTracingAccelerationStructure(configuration);
            scene.AddInstance(renderer, flags, enableTriangleCulling: false);
            if (scene.GetInstanceCount() != 1)
                throw new InvalidOperationException("Unity did not register the bake mesh in the DXR acceleration structure.");
            commands.BuildRayTracingAccelerationStructure(scene);
        }

        private void Trace(Vector3 sun, bool geometryOnly)
        {
            Vector3Int counts = settings.counts;
            if (!settings.compactVolume)
            {
                DispatchTrace(sun, geometryOnly, 0, counts.y);
                return;
            }

            // All batches read the previous irradiance field and write disjoint ray slices.
            // Only the caller blends the completed ray data into the next irradiance field.
            for (int firstSlice = 0; firstSlice < counts.z; firstSlice += CompactTraceSlicesPerBatch)
            {
                int sliceCount = Math.Min(CompactTraceSlicesPerBatch, counts.z - firstSlice);
                DispatchTrace(sun, geometryOnly, firstSlice, sliceCount);
                Submit();
                WaitForGpu($"{(geometryOnly ? "placement" : "lighting")} trace slices " +
                    $"{firstSlice + 1}–{firstSlice + sliceCount}/{counts.z}");
            }
        }

        private void DispatchTrace(Vector3 sun, bool geometryOnly, int firstSlice, int sliceCount)
        {
            commands.SetRayTracingShaderPass(trace, "DDGIProbeBake");
            commands.SetRayTracingAccelerationStructure(trace, "_Scene", scene);
            if (compactVolume != null) compactVolume.BindRayTracingResources(commands, trace);
            else volume.BindRayTracingResources(commands, trace);
            CoarseProbeFeedback.Bind(commands, trace, IrradianceTexture, DistanceTexture, ProbeDataTexture,
                geometryOnly ? null : coarseFeedback);
            commands.SetRayTracingVectorParam(trace, "_SunDirection", new Vector4(sun.x, sun.y, sun.z, 0f));
            commands.SetRayTracingIntParam(trace, "_SceneMaterials",
                settings.materialMode == DdgiBakeMaterialMode.SceneMaterials ? 1 : 0);
            if (settings.compactVolume)
                commands.SetRayTracingIntParam(trace, "_ProbeDispatchSliceOffset", firstSlice);
            uint rays = (uint)(geometryOnly ? ProbeVolume.FixedRayCount : settings.rays);
            Vector3Int counts = settings.counts;
            uint probesPerSlice = (uint)(counts.x * (settings.compactVolume ? counts.y : counts.z));
            commands.DispatchRays(trace, "TraceProbes", rays, probesPerSlice, (uint)sliceCount);
        }

        private void Submit()
        {
            if (syncMarker)
            {
                commands.SetRenderTarget(syncMarker);
                commands.ClearRenderTarget(false, true, Color.clear);
            }
            else
            {
                completionFence = commands.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation,
                    SynchronisationStageFlags.AllGPUOperations);
            }
            Graphics.ExecuteCommandBuffer(commands);
            submitted = true;
            commands.Clear();
        }

        private void WaitForGpu(string stage)
        {
            if (!submitted) return;
            try
            {
                if (syncMarker)
                {
                    // Unity can stage an entire array slice for even a one-texel read.
                    // The marker is written after the bake work and has only one texel.
                    var request = AsyncGPUReadback.Request(syncMarker, 0);
                    request.WaitForCompletion();
                    if (request.hasError)
                        throw new InvalidOperationException($"GPU wait failed during {stage}.");
                    return;
                }

                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                while (!completionFence.passed)
                {
                    if (elapsed.ElapsedMilliseconds >= GpuWaitTimeoutMilliseconds)
                        throw new TimeoutException($"GPU did not finish {stage} within {GpuWaitTimeoutMilliseconds / 1000} seconds.");
                    Thread.Sleep(1);
                }
            }
            finally
            {
                submitted = false;
            }
        }

        private static NativeGiParity.Atlas ReadAtlas(RenderTexture texture)
        {
            var request = AsyncGPUReadback.Request(texture, 0);
            request.WaitForCompletion();
            if (request.hasError || request.layerCount != texture.volumeDepth)
                throw new InvalidOperationException("GPU readback failed on " + texture.name);
            int layerSize = checked(texture.width * texture.height * 4);
            var data = new float[checked(layerSize * request.layerCount)];
            for (int layer = 0; layer < request.layerCount; layer++)
                NativeArray<float>.Copy(request.GetData<float>(layer), 0, data, layer * layerSize, layerSize);
            return new NativeGiParity.Atlas(texture.width, texture.height, texture.volumeDepth, 4, data);
        }

        private static void Progress(string stage, int step, int count)
        {
            if (!Application.isBatchMode && EditorUtility.DisplayCancelableProgressBar(
                "DDGI GPU Bake", $"{stage}, {step + 1}/{count}", (float)step / count))
                throw new OperationCanceledException("DDGI bake canceled.");
        }

        public void Dispose()
        {
            try
            {
                if (submitted)
                    WaitForGpu("bake cleanup");
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[DDGI Baker] GPU wait during cleanup failed: {exception.Message}");
            }
            finally
            {
                commands?.Release();
                scene?.Release();
                volume?.Dispose();
                compactVolume?.Dispose();
                coarseFeedback?.Dispose();
                foreach (Object asset in new Object[] { instance, mesh, syncMarker })
                    if (asset)
                        Object.DestroyImmediate(asset);
                if (materials != null)
                    foreach (var material in materials)
                        if (material)
                            Object.DestroyImmediate(material);
            }
        }
    }
}
