using System;
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

        private readonly DdgiBakeSettings settings;
        private readonly RayTracingShader trace;
        private CommandBuffer commands;
        private ProbeVolume volume;
        private RayTracingAccelerationStructure scene;
        private GameObject instance;
        private Mesh mesh;
        private Material material;
        private bool submitted;

        internal DdgiGpuBake(BakeGeometry geometry, DdgiBakeSettings settings)
        {
            RequireHardware();
            settings.Validate();
            this.settings = settings;
            trace = AssetDatabase.LoadAssetAtPath<RayTracingShader>(Package + "Baker/Shaders/ProbeTrace.raytrace");
            var update = AssetDatabase.LoadAssetAtPath<ComputeShader>(Package + "DDGI/Shaders/ProbeUpdate.compute");
            var surface = AssetDatabase.LoadAssetAtPath<Shader>(Package + "Baker/Shaders/ProbeBakeSurface.shader");
            if (!trace || !update)
                throw new InvalidOperationException("Install the pinned DDGI shaders with " + Package + "DDGI/Shaders/SDK/Install.ps1.");
            if (!surface || !surface.isSupported)
                throw new InvalidOperationException("The DDGI Baker surface shader failed to load or compile.");
            try
            {
                commands = new CommandBuffer { name = "DDGI TOD Bake" };
                volume = new ProbeVolume(update, new ProbeVolume.Description
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
                });
                BuildAccelerationStructure(geometry, surface);
                volume.ClearProbeData(commands);
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

        internal NativeGiParity.Atlas BuildLayout()
        {
            int total = settings.geometryPasses + (settings.classification ? 1 : 0);
            for (int pass = 0; pass < total; pass++)
            {
                Progress("Probe placement", pass, total);
                volume.UploadConstants(commands, ProbeVolume.IdentityRotation, 0f);
                Trace(Vector3.up, true);
                if (pass < settings.geometryPasses)
                    volume.Relocate(commands);
                if (settings.classification)
                    volume.Classify(commands);
                Submit();
                WaitForGpu(volume.ProbeData);
            }
            return ReadAtlas(volume.ProbeData);
        }

        internal (NativeGiParity.Atlas irradiance, NativeGiParity.Atlas distance) BakeKey(int index)
        {
            float hour = settings.hours[index];
            Vector3 sun = DdgiBakeSettings.SunDirection(hour);
            volume.ClearLighting(commands);
            string stage = $"TOD {hour:0.##} h ({index + 1}/{settings.hours.Length})";
            for (int frame = 0; frame < settings.iterations; frame++)
            {
                Progress(stage, frame, settings.iterations);
                Vector4 rotation = settings.rotatingDirections ? DdgiBakeSettings.RayRotation(frame) : ProbeVolume.IdentityRotation;
                volume.UploadConstants(commands, rotation, frame < 8 ? 0f : settings.hysteresis);
                Trace(sun, false);
                volume.Blend(commands);
                // NativeGI repeats the update with weight 1 to settle the octahedral borders.
                volume.UploadConstants(commands, rotation, 1f);
                volume.Blend(commands);
                Submit();
                WaitForGpu(volume.Irradiance);
            }
            return (ReadAtlas(volume.Irradiance), ReadAtlas(volume.Distance));
        }

        private void BuildAccelerationStructure(BakeGeometry geometry, Shader surface)
        {
            // Unity 2022.3 omits renderers in Preview Scenes from this DXR acceleration structure.
            instance = new GameObject("DDGI trace mesh", typeof(MeshFilter), typeof(MeshRenderer))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            mesh = geometry.CreateMesh();
            material = new Material(surface) { hideFlags = HideFlags.HideAndDontSave };
            instance.GetComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = instance.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.rayTracingMode = RayTracingMode.Static;
            var configuration = new RayTracingAccelerationStructure.RASSettings(
                RayTracingAccelerationStructure.ManagementMode.Manual,
                RayTracingAccelerationStructure.RayTracingModeMask.Everything, -1);
            scene = new RayTracingAccelerationStructure(configuration);
            scene.AddInstance(renderer, new[] { RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly },
                enableTriangleCulling: false);
            if (scene.GetInstanceCount() != 1)
                throw new InvalidOperationException("Unity did not register the bake mesh in the DXR acceleration structure.");
            commands.BuildRayTracingAccelerationStructure(scene);
        }

        private void Trace(Vector3 sun, bool geometryOnly)
        {
            commands.SetRayTracingShaderPass(trace, "DDGIProbeBake");
            commands.SetRayTracingAccelerationStructure(trace, "_Scene", scene);
            volume.BindRayTracingResources(commands, trace);
            commands.SetRayTracingVectorParam(trace, "_SunDirection", new Vector4(sun.x, sun.y, sun.z, 0f));
            uint rays = (uint)(geometryOnly ? ProbeVolume.FixedRayCount : settings.rays);
            uint probesPerSlice = (uint)(settings.counts.x * settings.counts.z);
            commands.DispatchRays(trace, "TraceProbes", rays, probesPerSlice, (uint)settings.counts.y);
        }

        private void Submit()
        {
            Graphics.ExecuteCommandBuffer(commands);
            submitted = true;
            commands.Clear();
        }

        private static void WaitForGpu(RenderTexture texture)
        {
            var request = AsyncGPUReadback.Request(texture, 0, 0, 1, 0, 1, 0, 1);
            request.WaitForCompletion();
            if (request.hasError)
                throw new InvalidOperationException("GPU wait failed on " + texture.name);
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
                    WaitForGpu(volume.ProbeData);
            }
            finally
            {
                commands?.Release();
                scene?.Release();
                volume?.Dispose();
                foreach (Object asset in new Object[] { instance, mesh, material })
                    if (asset)
                        Object.DestroyImmediate(asset);
            }
        }
    }
}
