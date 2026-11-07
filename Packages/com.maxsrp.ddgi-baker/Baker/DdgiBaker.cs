using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MaxSRP.DDGIBaker.Editor
{
    internal static class DdgiBaker
    {
        internal static NativeGiParity.Comparison Run(BakeGeometry geometry, DdgiBakeSettings settings, string destination,
            NativeGiParity.Reference reference = null, int referenceKey = 0)
        {
            string path = Path.GetFullPath(destination);
            if (Directory.Exists(path) || File.Exists(path))
                throw new IOException("Choose a new output directory: " + path);
            if (reference != null && (reference.Report.vertices != geometry.VertexCount || reference.Report.triangles != geometry.TriangleCount))
                throw new InvalidDataException("The input geometry counts differ from the NativeGI reference.");

            string staging = Path.Combine(Path.GetDirectoryName(path), ".ddgi-bake-" + Guid.NewGuid().ToString("N"));
            string atlases = Path.Combine(staging, "baked");
            var timer = Stopwatch.StartNew();
            NativeGiParity.Comparison comparison = null;
            try
            {
                Directory.CreateDirectory(atlases);
                NativeGiParity.Atlas layout;
                using (var gpu = new DdgiGpuBake(geometry, settings))
                {
                    layout = gpu.BuildLayout();
                    NativeGiParity.WriteAtlas(Path.Combine(atlases, "shared_probe_data.ngatlas"), layout);
                    for (int i = 0; i < settings.hours.Length; i++)
                    {
                        var key = gpu.BakeKey(i);
                        NativeGiParity.WriteAtlas(Path.Combine(atlases, $"official_irradiance_{i}.ngatlas"), key.irradiance);
                        if (i == 0)
                            NativeGiParity.WriteAtlas(Path.Combine(atlases, "shared_distance.ngatlas"), key.distance);
                        if (reference != null && i == 0)
                        {
                            comparison = reference.Compare(referenceKey, key.irradiance, key.distance, layout);
                            File.WriteAllText(Path.Combine(staging, "comparison.json"), comparison.ToJson());
                        }
                    }
                }
                var report = MakeReport(geometry, settings, layout, reference?.Report, timer.Elapsed.TotalSeconds);
                File.WriteAllText(Path.Combine(staging, "report.json"), JsonUtility.ToJson(report, true));
                Directory.Move(staging, path);
                Debug.Log($"[DDGI Baker] Baked {settings.hours.Length} TOD key(s), {settings.ProbeCount} probes in {report.bake_seconds:F2}s. {path}");
                if (comparison != null)
                    Debug.Log($"[DDGI Baker] {comparison.hour:g}h vs NativeGI: linear E relative RMSE " +
                        $"{comparison.sampledLinearIrradiance.relativeRmse:P4}, {comparison.classificationMismatches} probe state differences.");
                return comparison;
            }
            finally
            {
                if (!Application.isBatchMode)
                    EditorUtility.ClearProgressBar();
                if (Directory.Exists(staging))
                    Directory.Delete(staging, true);
            }
        }

        internal static NativeGiParity.Comparison RunReference(string project, string nativeReport, int keyIndex,
            string destination, int geometryPasses)
        {
            var reference = NativeGiParity.Load(nativeReport);
            var report = reference.Report;
            if ((uint)keyIndex >= report.keys.Length)
                throw new ArgumentOutOfRangeException(nameof(keyIndex));
            if (report.legacy_probes || report.layout_validation || report.view_bias != 0f || !report.hardware_raytracing)
                throw new InvalidDataException("Choose a modern, unfiltered NativeGI hardware bake with zero view bias.");
            var settings = new DdgiBakeSettings
            {
                origin = DdgiBakeSettings.ReadVector(report.volume_origin),
                counts = new Vector3Int(report.grid[0], report.grid[1], report.grid[2]),
                spacing = report.spacing,
                hours = new[] { report.keys[keyIndex].hour },
                rays = report.rays_per_probe,
                iterations = report.iterations,
                geometryPasses = report.relocation ? geometryPasses : 0,
                rotatingDirections = report.rotating_directions,
                classification = report.classification,
                hysteresis = report.hysteresis,
                gamma = report.gamma,
                normalBias = report.normal_bias,
                distanceExponent = report.distance_exponent,
                minFrontfaceDistance = report.min_frontface_distance,
                fixedBackfaceThreshold = report.fixed_ray_backface_threshold
            };
            if (settings.Relocation != report.relocation)
                throw new InvalidDataException("Geometry passes must match the reference's relocation setting.");
            string meshPath = Path.IsPathRooted(report.scene) ? report.scene : Path.Combine(project, report.scene);
            return Run(BakeGeometry.Read(meshPath), settings, destination, reference, keyIndex);
        }

        private static NativeGiParity.BakeReport MakeReport(BakeGeometry geometry, DdgiBakeSettings settings,
            NativeGiParity.Atlas layout, NativeGiParity.BakeReport reference, double seconds)
        {
            int inactive = 0, relocated = 0;
            for (int i = 0; i < layout.Values.Length; i += 4)
            {
                if (layout.Values[i + 3] == 1f)
                    inactive++;
                if (Mathf.Abs(layout.Values[i]) + Mathf.Abs(layout.Values[i + 1]) + Mathf.Abs(layout.Values[i + 2]) > 1e-6f)
                    relocated++;
            }
            return new NativeGiParity.BakeReport
            {
                scene = geometry.Source,
                device = SystemInfo.graphicsDeviceName,
                ray_backend = "Unity_D3D12_DispatchRays",
                baked_atlas_directory = "baked",
                baked_atlases_written = true,
                hardware_raytracing = true,
                triangles = geometry.TriangleCount,
                vertices = geometry.VertexCount,
                probes = settings.ProbeCount,
                grid = new[] { settings.counts.x, settings.counts.y, settings.counts.z },
                rays_per_probe = settings.rays,
                iterations = settings.iterations,
                geometry_passes = settings.geometryPasses,
                inactive_probes = inactive,
                relocated_probes = relocated,
                official_updates_per_iteration = 2,
                spacing = settings.spacing,
                solar_hour = settings.hours[0],
                hysteresis = settings.hysteresis,
                gamma = settings.gamma,
                normal_bias = settings.normalBias,
                distance_exponent = settings.distanceExponent,
                min_frontface_distance = settings.minFrontfaceDistance,
                fixed_ray_backface_threshold = settings.fixedBackfaceThreshold,
                albedo = new[] { .7f, .7f, .7f },
                sun_normal_illuminance = new[] { 1f, 1f, 1f },
                volume_origin = new[] { settings.origin.x, settings.origin.y, settings.origin.z },
                camera_position = reference?.camera_position,
                camera_forward = reference?.camera_forward,
                classification = settings.classification,
                relocation = settings.Relocation,
                rotating_directions = settings.rotatingDirections,
                bake_seconds = seconds,
                keys = settings.hours.Select(hour => new NativeGiParity.Key { hour = hour }).ToArray()
            };
        }
    }

    public static class DdgiBakerBatch
    {
        public static void CompareNativeGi()
        {
            string project = Argument("-ddgiProjectRoot", Directory.GetParent(Application.dataPath).FullName);
            string reference = Argument("-ddgiReference");
            string output = Argument("-ddgiOutput");
            int key = int.Parse(Argument("-ddgiKey", "0"));
            int geometryPasses = int.Parse(Argument("-ddgiGeometryPasses", "4"));
            var result = DdgiBaker.RunReference(project, reference, key, output, geometryPasses);
            if (result.nonzeroReferenceQueries == 0 || result.sampledLinearIrradiance.relativeRmse > .01 ||
                result.classificationMismatches != 0)
                throw new InvalidOperationException("DDGI parity exceeded 1% relative linear irradiance RMSE, had no lit reference queries, or changed probe states. See comparison.json.");
        }

        private static string Argument(string name, string fallback = null)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, name);
            if (index >= 0 && index + 1 < arguments.Length)
                return arguments[index + 1];
            return fallback ?? throw new ArgumentException("Missing batch argument " + name);
        }
    }
}
