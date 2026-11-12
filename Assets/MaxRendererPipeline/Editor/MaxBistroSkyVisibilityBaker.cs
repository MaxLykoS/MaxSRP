using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MaxSRP.EditorTools
{
    // Editor-only visibility integration: no BVH or trace resources survive in the player.
    // Runs AFTER Unity's lightmapper, using its actual UV2, index and scale/offset.
    public static class MaxBistroSkyVisibilityBaker
    {
        private const string DirectoryPath = MaxBistroLightmapBaker.BakeFolder + "/SkyVisibility";
        private const string ReportDirectoryPath = "OfflineBaseline/BistroLightmap";
        private const string ToolPath = "Assets/MaxRendererPipeline/Editor/";
        private const int DefaultSamples = 128, Tile = 64;
        private const int DefaultSamplesPerDispatch = 4, DefaultDispatchesPerUpdate = 8;
        public static bool IsRunning => job != null;
        public static string Status { get; private set; } = "Idle";
        public static event Action<bool, string> Completed;
        private static Job job;
        [Serializable] public sealed class Options
        {
            public bool ContinuousOrigins = true;
            public bool OccluderTwoSided = true;
            public bool RobustRayOrigin = true;
            public bool RobustIntersections = true;
            public string Label = "V3";
        }


        [StructLayout(LayoutKind.Sequential)] private struct Triangle
        {
            public Vector3 Vertex; public uint Material;
            public Vector3 Vertex1; public float Uv0X;
            public Vector3 Vertex2; public float Uv0Y;
            public Vector2 Uv1, Uv2;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Node
        {
            public Vector3 Lower; public int Left;
            public Vector3 Upper; public int Right;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MaterialData
        { public Vector4 St, Flags, Wrapping; }
        private sealed class CpuMesh { public Vector3[] Vertices, Normals; public Vector2[] UV, UV2; public int[][] Indices; }
        private sealed class Part { public MeshRenderer Renderer; public Mesh Mesh; public int Submesh, Material; }
        private sealed class SourceMaterial { public Material Material; public Texture Texture; public MaterialData Data; }
        private sealed class Bvh { public Triangle[] Triangles; public int[] Order; public Node[] Nodes; }
        [Serializable] private sealed class Report
        {
            public string scene, outputDirectory, reportDirectory, startedUtc, completedUtc, status, error;
            public string definition = "Version 3: cosine-sampled receiving geometric-normal hemisphere visibility over full directional domain. Scalar uniform-environment approximation to full-sphere diffuse IBL; not directionally weighted blue-sky transport, reflected GI, or specular visibility.";
            // Zero denotes uncached per-path spatial samples, not zero coverage.
            public int version = 3, spatialOriginsPerTexel;
            public string algorithmLabel, rayOrigin = "Source-triangle exclusion and 8-ULP geometric-normal offset, 2^-20 absolute offset near zero";
            public bool continuousOrigins, occluderTwoSided, robustRayOrigin, robustIntersections;
            public string surfaceSampling = "Per-path area-uniform point on conservative UV-clipped fragments; legacy four-origin cache retained for controlled comparisons";
            public int[] fragmentCounts;
            public string coverage = "Conservative triangle/texel intersections and chart-owned bilinear padding. Invalid samples never become full visibility.";
            public int[] chartCounts, paddingTexels, conflictingTexels, originCounts, alphaEmptyTexels;
            public string alphaHandling = "GPU alpha extraction, source UV transform/wrap, cutoff test, non-readable source textures supported. Glass with cutoff > 1 is excluded.";
            public string geometry = "Static scene geometry, flat geometric receiving normals, opaque two-sided blockers independent of raster culling; UV2 and lightmap ST from completed native bake.";
            public int samples = DefaultSamples, samplesPerDispatch, dispatchesPerUpdate;
            public int lightmapAtlases, resolution, triangleCount, nodeCount, rendererCount, cutoutTextures;
            public int[] rasterizedTexels;
            public float rayLength, originOffset = 0.002f;
            public long approximateGpuBytes;
            public string gpuBudgetDefinition = "Conservative transient visibility GPU bytes: geometry buffers + alpha array + sparse surface texels/origins/accumulation + result array. " +
                "Excludes native lightmaps, CPU mesh/readback/result copies, driver allocations and the normal scene render pipeline.";
            public double seconds;
        }
        private sealed class Job : IDisposable
        {
            public Scene Scene;
            public string OutputDirectory, ReportDirectory;
            public readonly List<MeshRenderer> Renderers = new List<MeshRenderer>();
            public readonly List<Part> Parts = new List<Part>();
            public readonly List<SourceMaterial> Materials = new List<SourceMaterial>();
            public readonly Dictionary<Material, int> MaterialIds = new Dictionary<Material, int>();
            public readonly Dictionary<Mesh, CpuMesh> Meshes = new Dictionary<Mesh, CpuMesh>();
            public List<Triangle> Triangles = new List<Triangle>();
            public readonly Dictionary<int,List<MaxSkyVisibilitySurfaceSampling.InputTriangle>> Surfaces = new Dictionary<int,List<MaxSkyVisibilitySurfaceSampling.InputTriangle>>();
            public Task<MaxSkyVisibilitySurfaceSampling.Result> SurfaceTask;
            public MaxSkyVisibilitySurfaceSampling.Result Surface;
            public ComputeBuffer SurfaceTexels, SurfaceOrigins, SurfaceAccumulation, SurfaceFragments, SurfaceFragmentRanges;
            public Options Settings = new Options();
            public int SurfaceCursor;

            public CancellationTokenSource Cancellation = new CancellationTokenSource();
            public Task<Bvh> BvhTask;
            public ComputeShader Compute;
            public Material Raster;
            public ComputeBuffer TriangleBuffer, OrderBuffer, NodeBuffer, MaterialBuffer;
            public Texture2DArray Alpha, Result;
            public RenderTexture Positions, Normals, Accumulation, Finished;
            public int RendererCursor, Atlas, AtlasCount, Size, TileIndex, Sample, TraceKernel, FinishKernel;
            public int Samples = DefaultSamples, SamplesPerDispatch = DefaultSamplesPerDispatch, DispatchesPerUpdate = DefaultDispatchesPerUpdate;
            public bool Prepared, ReadbackPending, GpuPending, Cancelled, Fixture, SurfaceFixture;
            public bool ResultPending;
            public string Stage = "Collecting static meshes";
            public double Started = EditorApplication.timeSinceStartup;
            public readonly Report Report = new Report();
            public Bounds Bounds;

            public void Dispose()
            {
                Cancellation.Cancel();
                TriangleBuffer?.Release(); OrderBuffer?.Release(); NodeBuffer?.Release(); MaterialBuffer?.Release();
                SurfaceTexels?.Release(); SurfaceOrigins?.Release(); SurfaceAccumulation?.Release(); SurfaceFragments?.Release(); SurfaceFragmentRanges?.Release();
                foreach (RenderTexture target in new[] { Positions, Normals, Accumulation, Finished })
                    if (target) { if(RenderTexture.active==target)RenderTexture.active=null; target.Release(); Object.DestroyImmediate(target); }
                foreach (Object obj in new Object[] { Compute, Raster, Alpha }) if (obj) Object.DestroyImmediate(obj);
                if (Result && !AssetDatabase.Contains(Result)) Object.DestroyImmediate(Result);
                Cancellation.Dispose();
            }
        }

        public static void Begin(Scene scene)
        {
            if (scene.path != MaxBistroSceneBuilder.ScenePath)
                throw new InvalidOperationException("Sky visibility requires the full Bistro scene.");
            Begin(scene, DirectoryPath, ReportDirectoryPath);
        }

        public static void Begin(Scene scene, string outputDirectory, string reportDirectory)
        { Begin(scene, outputDirectory, reportDirectory, new Options()); }

        public static void Begin(Scene scene, string outputDirectory, string reportDirectory, Options options)
        {
            if (IsRunning) throw new InvalidOperationException("Sky visibility is already baking.");
            if (EditorApplication.isPlayingOrWillChangePlaymode || Lightmapping.isRunning)
                throw new InvalidOperationException("Start sky visibility in edit mode after native lightmapping has finished.");
            ValidateSceneOutputPaths(scene, outputDirectory, reportDirectory);
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supports2DArrayTextures || !SystemInfo.supportsAsyncGPUReadback)
                throw new NotSupportedException("Sky visibility needs compute, texture arrays and asynchronous GPU readback.");
            var j = new Job
            {
                Scene = scene, OutputDirectory = outputDirectory, ReportDirectory = reportDirectory,
                Settings = options ?? throw new ArgumentNullException(nameof(options)),
                Samples = DefaultSamples,
                SamplesPerDispatch = DefaultSamplesPerDispatch,
                DispatchesPerUpdate = DefaultDispatchesPerUpdate
            };
            try
            {
                foreach (var root in scene.GetRootGameObjects())
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    if ((GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & StaticEditorFlags.ContributeGI) == 0) continue;
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (!filter || !filter.sharedMesh) continue;
                    j.Renderers.Add(renderer);
                    if (j.Renderers.Count == 1) j.Bounds = renderer.bounds; else j.Bounds.Encapsulate(renderer.bounds);
                    int index = renderer.lightmapIndex;
                    if (index < 0 || index >= 0xfffe) continue;
                    j.AtlasCount = Mathf.Max(j.AtlasCount, index + 1);
                }
                if (j.AtlasCount == 0) throw new InvalidOperationException("No completed native lightmap indices found in " + scene.path + ".");
                LightmapData[] lightmaps = LightmapSettings.lightmaps;
                if (lightmaps.Length < j.AtlasCount) throw new InvalidOperationException("Native lightmap table is incomplete.");
                int dimension = 0;
                for (int i = 0; i < j.AtlasCount; ++i)
                {
                    var texture = lightmaps[i].lightmapColor;
                    if (!texture || texture.width != texture.height) throw new InvalidOperationException("Expected square native lightmap atlases.");
                    // Unity may use a smaller final atlas. Normalized UV layouts still map
                    // correctly into equal-sized array slices; only texel density changes.
                    dimension = Mathf.Max(dimension, texture.width);
                }
                j.Size = Mathf.Max(32, dimension);
                j.Compute = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(ToolPath + "MaxBistroSkyVisibility.compute"));
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ToolPath + "MaxBistroSkyVisibilityUV.shader");
                if (!j.Compute || !shader) throw new InvalidOperationException("Sky visibility shaders have not imported successfully.");
                j.Compute.hideFlags = HideFlags.HideAndDontSave;
                j.Raster = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                j.TraceKernel = j.Compute.FindKernel("TraceSurface"); j.FinishKernel = j.Compute.FindKernel("FinishSky");
                j.Report.rayOrigin = j.Settings.RobustRayOrigin ? "Only source-triangle exclusion + 8-ULP Ng offset, absolute 2^-20 near zero" : "Legacy fixed 0.002m Ng offset and 1e-5m hit minimum";
                j.Report.surfaceSampling = j.Settings.ContinuousOrigins ? "A fresh area-uniform point on clipped fragments for each path sample" : "Legacy fixed spatial origins, chosen repeatedly for angular integration";
                j.Report.geometry = j.Settings.OccluderTwoSided ? "Opaque blockers are two-sided independently of raster cull; geometric receiving normals; native UV2/ST" : "Legacy blocker sidedness follows material doubleSidedGI/raster cull";
                j.Report.algorithmLabel = j.Settings.Label;
                j.Report.continuousOrigins=j.Settings.ContinuousOrigins;
                j.Report.occluderTwoSided=j.Settings.OccluderTwoSided;
                j.Report.robustRayOrigin=j.Settings.RobustRayOrigin;
                j.Report.robustIntersections=j.Settings.RobustIntersections;
                j.Report.scene = scene.path; j.Report.startedUtc = DateTime.UtcNow.ToString("O");
                j.Report.outputDirectory = outputDirectory; j.Report.reportDirectory = reportDirectory;
                j.Report.lightmapAtlases = j.AtlasCount; j.Report.resolution = j.Size; j.Report.rendererCount = j.Renderers.Count;
                j.Report.rayLength = Mathf.Max(1, j.Bounds.size.magnitude * 1.01f);
                job = j;
                AssemblyReloadEvents.beforeAssemblyReload += StopBeforeReload;
                EditorApplication.quitting += StopBeforeReload;
                EditorApplication.update += Tick;
                WriteReport(j, "Collecting geometry", null);
                Status = "Collecting " + scene.name + " static geometry for sky visibility";
            }
            catch { j.Dispose(); throw; }
        }

        private static void ValidateSceneOutputPaths(Scene scene, string outputDirectory, string reportDirectory)
        {
            if (!scene.IsValid() || !scene.isLoaded || scene.path != MaxBistroSceneBuilder.ScenePath)
                throw new InvalidOperationException("Sky visibility requires the full Bistro scene.");
            if (SceneManager.sceneCount != 1 || SceneManager.GetActiveScene() != scene)
                throw new InvalidOperationException("Only the target scene may be loaded.");
            if (!string.Equals(outputDirectory, DirectoryPath, StringComparison.Ordinal) ||
                !string.Equals(reportDirectory, ReportDirectoryPath, StringComparison.Ordinal))
                throw new InvalidOperationException("Sky visibility output paths do not match " + scene.path +
                    "; expected " + DirectoryPath + " and " + ReportDirectoryPath + ".");
        }

        public static void Cancel()
        {
            if (job == null) return;
            job.Cancelled = true;
            job.Cancellation.Cancel();
            // GPU callbacks retain these resources until the submitted batch/readback completes.
            if (!job.GpuPending && !job.ReadbackPending) Finish(false, "Sky visibility cancelled; native lightmaps were preserved.");
        }

        // Production-kernel preflight. Does not open a scene, refresh assets or start a bake.
        public static string ValidateGpu()
        {
            if (IsRunning || Lightmapping.isRunning) throw new InvalidOperationException("Run GI preflight before starting a bake.");
            var results = new List<string> { ValidateUvRaster(8), ValidateUvRaster(2048) };
            for (int mode = 0; mode < 4; ++mode)
            {
                var j = new Job { Size = 1, AtlasCount = 1, Fixture = true };
                Texture2D pixel = null, normal = null, alpha = null;
                try
                {
                    j.Compute = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(ToolPath + "MaxBistroSkyVisibility.compute"));
                    j.Raster = new Material(AssetDatabase.LoadAssetAtPath<Shader>(ToolPath + "MaxBistroSkyVisibilityUV.shader"));
                    j.TraceKernel = j.Compute.FindKernel("TraceSky"); j.FinishKernel = j.Compute.FindKernel("FinishSky");
                    j.Report.rayLength = 100;
                    alpha = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                    alpha.SetPixel(0, 0, new Color(1, 1, 1, mode == 2 ? 0 : 1)); alpha.Apply(false, false);
                    j.Materials.Add(new SourceMaterial { Texture = alpha, Data = new MaterialData {
                        St = new Vector4(1, 1, 0, 0), Flags = new Vector4(mode >= 2 ? 0.5f : 0, 0, 1, 0) } });
                    var triangles = new List<Triangle>();
                    Vector3[] v = { new Vector3(-1,-1,-1), new Vector3(1,-1,-1), new Vector3(1,1,-1), new Vector3(-1,1,-1),
                        new Vector3(-1,-1,1), new Vector3(1,-1,1), new Vector3(1,1,1), new Vector3(-1,1,1) };
                    int[] index = { 0,1,2, 0,2,3, 4,6,5, 4,7,6, 0,4,5, 0,5,1, 3,2,6, 3,6,7, 0,3,7, 0,7,4, 1,5,6, 1,6,2 };
                    for (int n = 0; n < index.Length; n += 3)
                    {
                        Vector3 p = v[index[n]] + (mode == 0 ? Vector3.right * 1000 : Vector3.zero);
                        triangles.Add(new Triangle { Vertex = p, Vertex1 = v[index[n + 1]] + (mode == 0 ? Vector3.right * 1000 : Vector3.zero), Vertex2 = v[index[n + 2]] + (mode == 0 ? Vector3.right * 1000 : Vector3.zero) });
                    }
                    PrepareGpu(j, BuildBvh(triangles.ToArray(), CancellationToken.None));
                    pixel = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
                    normal = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
                    pixel.SetPixel(0, 0, new Color(0, 0, 0, 1)); pixel.Apply(false, false);
                    normal.SetPixel(0, 0, new Color(0, 1, 0, 1)); normal.Apply(false, false);
                    Graphics.Blit(pixel, j.Positions); Graphics.Blit(normal, j.Normals);
                    j.Compute.SetInts("_TileOrigin", 0, 0); j.Compute.SetInt("_SampleStart", 0);
                    j.Compute.SetInt("_SamplesPerBatch", j.Samples);
                    j.Compute.Dispatch(j.TraceKernel, 1, 1, 1); j.Compute.Dispatch(j.FinishKernel, 1, 1, 1);
                    AsyncGPUReadbackRequest readback = AsyncGPUReadback.Request(j.Finished, 0, TextureFormat.RFloat);
                    readback.WaitForCompletion();
                    if (readback.hasError) throw new InvalidOperationException("Sky visibility GPU fixture readback failed.");
                    float measured = readback.GetData<float>()[0];
                    float expected = mode == 0 || mode == 2 ? 1 : 0;
                    if (!float.IsNaN(measured) && Mathf.Abs(measured - expected) < 1e-4f)
                        results.Add(new[] { "unobstructed", "closed box", "alpha transparent", "alpha opaque" }[mode] + "=" + measured.ToString("G6", System.Globalization.CultureInfo.InvariantCulture));
                    else
                    {
                        AsyncGPUReadbackRequest alphaRead = AsyncGPUReadback.Request(j.Alpha, 0, TextureFormat.R8);
                        alphaRead.WaitForCompletion();
                        var bufferRead = new MaterialData[1]; j.MaterialBuffer.GetData(bufferRead);
                        string alphaDebug = alphaRead.hasError ? "readback error" : alphaRead.GetData<byte>()[0].ToString();
                        throw new InvalidOperationException($"Sky visibility fixture {mode}: expected {expected}, got {measured}; GPU alpha byte={alphaDebug}, GPU-buffer flags={bufferRead[0].Flags}.");
                    }
                }
                finally
                {
                    j.Dispose();
                    foreach (Texture2D texture in new[] { pixel, normal, alpha }) if (texture) Object.DestroyImmediate(texture);
                }
            }
            return "PASS sky visibility production compute: " + string.Join(", ", results);
        }

        // Exercises the exact sparse production kernel, including buffer strides,
        // arbitrary normal directions and sidedness. No assets/scenes are modified.
        public static string ValidateSurfaceGpu()
        {
            if(IsRunning || Lightmapping.isRunning)throw new InvalidOperationException("Wait for baking before GPU fixtures.");
            var results=new List<string>();
            foreach(Vector3 normal in new[]{Vector3.up,Vector3.down,Vector3.right})
            for(int mode=0;mode<6;++mode)
            {
                var j=new Job{Size=1,AtlasCount=1,Fixture=true,SurfaceFixture=true,Samples=1024};
                Texture2D alpha=null;
                try{
                    j.Compute=Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(ToolPath+"MaxBistroSkyVisibility.compute"));
                    j.Raster=new Material(AssetDatabase.LoadAssetAtPath<Shader>(ToolPath+"MaxBistroSkyVisibilityUV.shader"));
                    j.TraceKernel=j.Compute.FindKernel("TraceSurface");j.FinishKernel=j.Compute.FindKernel("FinishSky");
                    j.Report.rayLength=100;
                    alpha=new Texture2D(1,1,TextureFormat.RGBA32,false,true);
                    alpha.SetPixel(0,0,new Color(1,1,1,mode==2?0:1));alpha.Apply();
                    j.Materials.Add(new SourceMaterial{Texture=alpha,Data=new MaterialData{St=new Vector4(1,1,0,0),Flags=new Vector4(mode==2||mode==3?.5f:0,0,mode<4?1:0,0)}});
                    j.Materials.Add(new SourceMaterial{Texture=Texture2D.whiteTexture,Data=new MaterialData{St=new Vector4(1,1,0,0),Flags=new Vector4(0,0,0,0)}});
                    Vector3[] v={new Vector3(-1,-1,-1),new Vector3(1,-1,-1),new Vector3(1,1,-1),new Vector3(-1,1,-1),new Vector3(-1,-1,1),new Vector3(1,-1,1),new Vector3(1,1,1),new Vector3(-1,1,1)};
                    int[] ids={0,1,2,0,2,3,4,6,5,4,7,6,0,4,5,0,5,1,3,2,6,3,6,7,0,3,7,0,7,4,1,5,6,1,6,2};
                    var tris=new List<Triangle>();
                    for(int i=0;i<ids.Length;i+=3){int a=ids[i],b=ids[i+1],c=ids[i+2];if(mode==5){int t=b;b=c;c=t;}
                        tris.Add(new Triangle{Vertex=v[a]+(mode==0?Vector3.right*1000:Vector3.zero),Vertex1=v[b]+(mode==0?Vector3.right*1000:Vector3.zero),Vertex2=v[c]+(mode==0?Vector3.right*1000:Vector3.zero)});}
                    PrepareGpu(j,BuildBvh(tris.ToArray(),CancellationToken.None));
                    j.SurfaceTexels=new ComputeBuffer(1,16);j.SurfaceOrigins=new ComputeBuffer(4,40);j.SurfaceAccumulation=new ComputeBuffer(1,8);
                    j.SurfaceFragments=new ComputeBuffer(1,88);j.SurfaceFragmentRanges=new ComputeBuffer(1,16);
                    j.SurfaceFragments.SetData(new MaxSkyVisibilitySurfaceSampling.Fragment[1]);
                    j.SurfaceFragmentRanges.SetData(new MaxSkyVisibilitySurfaceSampling.FragmentRange[1]);
                    j.Compute.SetBuffer(j.TraceKernel,"_SurfaceFragments",j.SurfaceFragments);j.Compute.SetBuffer(j.TraceKernel,"_SurfaceFragmentRanges",j.SurfaceFragmentRanges);
                    j.SurfaceTexels.SetData(new[]{new MaxSkyVisibilitySurfaceSampling.Texel{OriginCount=4,Chart=1}});
                    var origins=new MaxSkyVisibilitySurfaceSampling.Origin[4];
                    for(int k=0;k<4;++k)origins[k]=new MaxSkyVisibilitySurfaceSampling.Origin{Position=new Vector3((k%2-.5f)*.1f,0,(k/2-.5f)*.1f),Normal=normal,Material=1};
                    j.SurfaceOrigins.SetData(origins);
                    j.Compute.SetBuffer(j.TraceKernel,"_SurfaceTexels",j.SurfaceTexels);j.Compute.SetBuffer(j.TraceKernel,"_SurfaceOrigins",j.SurfaceOrigins);j.Compute.SetBuffer(j.TraceKernel,"_SurfaceAccumulation",j.SurfaceAccumulation);
                    j.Compute.SetInt("_SurfaceCount",1);j.Compute.SetInt("_SurfaceStart",0);j.Compute.SetInt("_SampleStart",0);j.Compute.SetInt("_SamplesPerBatch",j.Samples);
                    j.Compute.Dispatch(j.TraceKernel,1,1,1);
                    var read=AsyncGPUReadback.Request(j.SurfaceAccumulation);read.WaitForCompletion();
                    if(read.hasError)throw new InvalidOperationException("Sparse fixture readback failed.");
                    Vector2 sums=read.GetData<Vector2>()[0];float value=sums.x/sums.y,expected=mode==0||mode==2||mode==5?1:0;
                    if(!float.IsFinite(value)||Mathf.Abs(value-expected)>1e-5f||sums.y!=j.Samples)throw new InvalidOperationException("Sparse GPU fixture failed mode="+mode+" normal="+normal+" sum="+sums);
                    results.Add(normal+"/"+mode+"="+value);
                }finally{j.Dispose();if(alpha)Object.DestroyImmediate(alpha);}
            }
            return "PASS sparse GPU: open, closed, alpha-hole/opaque, inward/outward single-sided box for up/down/right; "+string.Join(", ",results);
        }

        private static string ValidateUvRaster(int size)
        {
            Material raster = null;
            Mesh mesh = null;
            RenderTexture positions = null, normals = null;
            var cmd = new CommandBuffer { name = "Sky visibility UV contract fixture" };
            try
            {
                raster = new Material(AssetDatabase.LoadAssetAtPath<Shader>(ToolPath + "MaxBistroSkyVisibilityUV.shader"));
                mesh = new Mesh { vertices = new[] { new Vector3(0,2,0), new Vector3(8,2,0), new Vector3(8,2,8), new Vector3(0,2,8) },
                    normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
                    uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up },
                    uv2 = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up },
                    triangles = new[] { 0,2,1, 0,3,2 } };
                positions = Target(size, RenderTextureFormat.ARGBFloat, false, 24);
                normals = Target(size, RenderTextureFormat.ARGBFloat, false);
                var properties = new MaterialPropertyBlock();
                properties.SetMatrix("_BakeObjectToWorld", Matrix4x4.identity);
                properties.SetMatrix("_BakeNormalToWorld", Matrix4x4.identity);
                properties.SetVector("_BakeLightmapST", new Vector4(0.5f, 0.5f, 0.25f, 0.25f));
                properties.SetVector("_BakeBaseST", new Vector4(1, 1, 0, 0));
                properties.SetTexture("_BakeBaseMap", Texture2D.whiteTexture);
                properties.SetFloat("_BakeCutoff", 0);
                cmd.SetRenderTarget(new[] { new RenderTargetIdentifier(positions), new RenderTargetIdentifier(normals) }, positions);
                cmd.ClearRenderTarget(true, true, Color.clear);
                cmd.SetViewport(new Rect(0, 0, size, size));
                cmd.DrawMesh(mesh, Matrix4x4.identity, raster, 0, 0, properties);
                Graphics.ExecuteCommandBuffer(cmd);
                AsyncGPUReadbackRequest positionRead = AsyncGPUReadback.Request(positions, 0, TextureFormat.RGBAFloat);
                AsyncGPUReadbackRequest normalRead = AsyncGPUReadback.Request(normals, 0, TextureFormat.RGBAFloat);
                positionRead.WaitForCompletion(); normalRead.WaitForCompletion();
                if (positionRead.hasError || normalRead.hasError) throw new InvalidOperationException("UV raster fixture readback failed.");
                NativeArray<Vector4> p = positionRead.GetData<Vector4>(), n = normalRead.GetData<Vector4>();
                int low = size / 4, high = size * 3 / 4 - 1;
                float firstWorld = ((low + 0.5f) / size - 0.25f) * 16;
                float lastWorld = ((high + 0.5f) / size - 0.25f) * 16;
                Vector4 first = p[low + low * size], last = p[high + high * size];
                int occupied = 0;
                foreach (Vector4 point in p) if (point.w > 0.5f) ++occupied;
                if (occupied != size * size / 4 ||
                    (first - new Vector4(firstWorld,2,firstWorld,1)).sqrMagnitude > 1e-5f ||
                    (last - new Vector4(lastWorld,2,lastWorld,1)).sqrMagnitude > 1e-5f || p[0].w != 0)
                    throw new InvalidOperationException($"UV atlas/ST/Y contract failed: first={first}, last={last}, outside={p[0]}.");
                if (Vector3.Dot((Vector3)n[low + low * size], Vector3.up) < 0.999f)
                    throw new InvalidOperationException("UV receiving geometric normal orientation failed: " + n[low + low * size]);
                return "UV raster " + size + " ST/Y/geometric-normal=correct";
            }
            finally
            {
                cmd.Release();
                foreach (RenderTexture target in new[] { positions, normals }) if (target) { if(RenderTexture.active==target)RenderTexture.active=null; target.Release(); Object.DestroyImmediate(target); }
                if (raster) Object.DestroyImmediate(raster); if (mesh) Object.DestroyImmediate(mesh);
            }
        }

        private static void StopBeforeReload()
        {
            if (job == null) return;
            job.Cancelled = true;
            job.Cancellation.Cancel();
            // The current GPU group is bounded; complete its callbacks before unloading code.
            AsyncGPUReadback.WaitAllRequests();
            if (job != null) Finish(false, "Sky visibility stopped before editor reload; native lightmaps were preserved.");
        }

        private static void Tick()
        {
            Job j = job;
            if (j == null) return;
            try
            {
                if (j.Cancelled)
                { if (!j.GpuPending && !j.ReadbackPending) Finish(false, "Sky visibility cancelled; native lightmaps were preserved."); return; }
                float progress = j.Prepared ? 0.15f + 0.85f * (j.Atlas + (float)j.Sample / j.Samples) / j.AtlasCount : 0.05f;
                if (EditorUtility.DisplayCancelableProgressBar("Bistro sky visibility", Status, progress)) { Cancel(); return; }
                if (j.RendererCursor < j.Renderers.Count)
                {
                    double until = EditorApplication.timeSinceStartup + 0.025;
                    do { AddRenderer(j, j.Renderers[j.RendererCursor++]); }
                    while (j.RendererCursor < j.Renderers.Count && EditorApplication.timeSinceStartup < until);
                    Status = $"Sky visibility geometry {j.RendererCursor}/{j.Renderers.Count}, {j.Triangles.Count:N0} triangles";
                    return;
                }
                if (j.BvhTask == null)
                {
                    j.Meshes.Clear();
                    Triangle[] triangles = j.Triangles.ToArray(); j.Triangles = null;
                    CancellationToken token = j.Cancellation.Token;
                    j.BvhTask = Task.Run(() => BuildBvh(triangles, token), token);
                    Status = $"Building sky visibility BVH ({triangles.Length:N0} triangles)";
                    return;
                }
                if (!j.BvhTask.IsCompleted) return;
                if (!j.Prepared)
                {
                    Bvh bvh = j.BvhTask.GetAwaiter().GetResult();
                    PrepareGpu(j, bvh);
                    j.Prepared = true;
                    PrepareAtlas(j);
                    return;
                }
                if (j.GpuPending || j.ReadbackPending) return;
                if (j.Surface == null && j.SurfaceTask != null)
                {
                    if (!j.SurfaceTask.IsCompleted) return;
                    PrepareSurfaceBuffers(j, j.SurfaceTask.GetAwaiter().GetResult());
                    return;
                }
                if (j.ResultPending) { j.ResultPending = false; ++j.Atlas; if (j.Atlas >= j.AtlasCount) { SaveResults(j); Finish(true, "Bistro sky visibility and material bindings completed."); return; } PrepareAtlas(j); return; }
                Dispatch(j);
            }
            catch (Exception error) { Debug.LogException(error); Finish(false, error.ToString()); }
        }

        private static CpuMesh ReadMesh(Mesh mesh)
        {
            using (Mesh.MeshDataArray dataArray = MeshUtility.AcquireReadOnlyMeshData(mesh))
            {
                Mesh.MeshData data = dataArray[0];
                var vertices = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp);
                var uv = new NativeArray<Vector2>(data.vertexCount, Allocator.Temp);
                var uv2 = new NativeArray<Vector2>(data.vertexCount, Allocator.Temp);
                var normals = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp);
                try
                {
                    data.GetVertices(vertices);
                    bool hasUv = mesh.HasVertexAttribute(VertexAttribute.TexCoord0);
                    if (hasUv) data.GetUVs(0, uv);
                    if (mesh.HasVertexAttribute(VertexAttribute.TexCoord1)) data.GetUVs(1, uv2);
                    if (mesh.HasVertexAttribute(VertexAttribute.Normal)) data.GetNormals(normals);
                    var result = new CpuMesh { Vertices = vertices.ToArray(), Normals = normals.ToArray(), UV = uv.ToArray(), UV2 = uv2.ToArray(), Indices = new int[data.subMeshCount][] };
                    for (int submesh = 0; submesh < data.subMeshCount; ++submesh)
                    {
                        SubMeshDescriptor desc = data.GetSubMesh(submesh);
                        if (desc.topology != MeshTopology.Triangles) { result.Indices[submesh] = Array.Empty<int>(); continue; }
                        int[] indices = new int[desc.indexCount];
                        if (data.indexFormat == IndexFormat.UInt16)
                        { var source = data.GetIndexData<ushort>(); for (int n = 0; n < indices.Length; ++n) indices[n] = source[desc.indexStart + n] + desc.baseVertex; }
                        else
                        { var source = data.GetIndexData<uint>(); for (int n = 0; n < indices.Length; ++n) indices[n] = (int)source[desc.indexStart + n] + desc.baseVertex; }
                        result.Indices[submesh] = indices;
                    }
                    return result;
                }
                finally { vertices.Dispose(); uv.Dispose(); uv2.Dispose(); normals.Dispose(); }
            }
        }

        private static int MaterialIndex(Job j, Material material)
        {
            if (j.MaterialIds.TryGetValue(material, out int index)) return index;
            float cutoff = material.HasProperty("_Cutoff") ? material.GetFloat("_Cutoff") : 0;
            Texture texture = material.HasProperty("_BaseColorMap") ? material.GetTexture("_BaseColorMap") : Texture2D.whiteTexture;
            Vector2 scale = material.HasProperty("_BaseColorMap") ? material.GetTextureScale("_BaseColorMap") : Vector2.one;
            Vector2 offset = material.HasProperty("_BaseColorMap") ? material.GetTextureOffset("_BaseColorMap") : Vector2.zero;
            bool twoSided = material.doubleSidedGI || (material.HasProperty("_Cull") && material.GetFloat("_Cull") == 0);
            var source = new SourceMaterial { Material = material, Texture = texture ? texture : Texture2D.whiteTexture,
                Data = new MaterialData { St = new Vector4(scale.x, scale.y, offset.x, offset.y),
                    Flags = new Vector4(cutoff, -1, twoSided ? 1 : 0, 0),
                    Wrapping = new Vector4(texture ? (float)texture.wrapModeU : 0, texture ? (float)texture.wrapModeV : 0, 0, 0) } };
            index = j.Materials.Count; j.Materials.Add(source); j.MaterialIds.Add(material, index); return index;
        }

        private static void AddRenderer(Job j, MeshRenderer renderer)
        {
            Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            if (!j.Meshes.TryGetValue(mesh, out CpuMesh cpu)) { cpu = ReadMesh(mesh); j.Meshes.Add(mesh, cpu); }
            Matrix4x4 matrix = renderer.localToWorldMatrix;
            bool mirrored = matrix.determinant < 0;
            Material[] materials = renderer.sharedMaterials;
            for (int submesh = 0; submesh < cpu.Indices.Length; ++submesh)
            {
                if (submesh >= materials.Length || !materials[submesh]) continue;
                Material material = materials[submesh];
                if (material.HasProperty("_Cutoff") && material.GetFloat("_Cutoff") > 1) continue;
                int materialId = MaterialIndex(j, material);
                int[] indices = cpu.Indices[submesh];
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    if (mirrored) { int swap = b; b = c; c = swap; }
                    Vector3 p0 = matrix.MultiplyPoint3x4(cpu.Vertices[a]);
                    Vector3 e1 = matrix.MultiplyPoint3x4(cpu.Vertices[b]) - p0, e2 = matrix.MultiplyPoint3x4(cpu.Vertices[c]) - p0;
                    if (Vector3.Cross(e1, e2).sqrMagnitude < 1e-16f) continue;
                    int sourceTriangle = j.Triangles.Count;
                    int atlas = renderer.lightmapIndex;
                    if (atlas >= 0 && atlas < j.AtlasCount)
                    {
                        if (!j.Surfaces.TryGetValue(atlas, out var surfaces)) { surfaces = new List<MaxSkyVisibilitySurfaceSampling.InputTriangle>(); j.Surfaces.Add(atlas, surfaces); }
                        Vector4 st = renderer.lightmapScaleOffset;
                        Vector3 normal = Vector3.Cross(e1,e2).normalized;
                        Vector3 reference = matrix.inverse.transpose.MultiplyVector(cpu.Normals[a]+cpu.Normals[b]+cpu.Normals[c]);
                        if (Vector3.Dot(normal,reference) < 0) normal = -normal;
                        surfaces.Add(new MaxSkyVisibilitySurfaceSampling.InputTriangle {
                            P0=p0,P1=matrix.MultiplyPoint3x4(cpu.Vertices[b]),P2=matrix.MultiplyPoint3x4(cpu.Vertices[c]),Normal=normal,
                            Uv0=cpu.UV[a],Uv1=cpu.UV[b],Uv2=cpu.UV[c],
                            Atlas0=Vector2.Scale(cpu.UV2[a],new Vector2(st.x,st.y))+new Vector2(st.z,st.w),
                            Atlas1=Vector2.Scale(cpu.UV2[b],new Vector2(st.x,st.y))+new Vector2(st.z,st.w),
                            Atlas2=Vector2.Scale(cpu.UV2[c],new Vector2(st.x,st.y))+new Vector2(st.z,st.w),
                            Renderer=renderer.GetInstanceID(),Material=materialId,SourceTriangle=sourceTriangle });
                    }
                    j.Triangles.Add(new Triangle { Vertex = p0, Vertex1 = matrix.MultiplyPoint3x4(cpu.Vertices[b]), Vertex2 = matrix.MultiplyPoint3x4(cpu.Vertices[c]), Material = (uint)materialId,
                        Uv0X = cpu.UV[a].x, Uv0Y = cpu.UV[a].y, Uv1 = cpu.UV[b], Uv2 = cpu.UV[c] });
                }
                int lm = renderer.lightmapIndex;
                if (lm >= 0 && lm < j.AtlasCount) j.Parts.Add(new Part { Renderer = renderer, Mesh = mesh, Submesh = submesh, Material = materialId });
            }
        }

        private static float Center(Triangle t, int axis) => ((t.Vertex + t.Vertex1 + t.Vertex2) / 3)[axis];
        private static Bvh BuildBvh(Triangle[] triangles, CancellationToken token)
        {
            if (triangles.Length == 0) throw new InvalidOperationException("No static geometry for sky visibility.");
            int[] order = new int[triangles.Length]; for (int i = 0; i < order.Length; ++i) order[i] = i;
            var nodes = new List<Node>(triangles.Length / 3);
            BuildNode(triangles, order, nodes, 0, order.Length, token);
            return new Bvh { Triangles = triangles, Order = order, Nodes = nodes.ToArray() };
        }
        private static int BuildNode(Triangle[] triangles, int[] order, List<Node> nodes, int first, int count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Vector3 lower = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 upper = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            Vector3 centerLower = lower, centerUpper = upper;
            for (int i = first; i < first + count; ++i)
            {
                Triangle t = triangles[order[i]];
                lower = Vector3.Min(lower, Vector3.Min(t.Vertex, Vector3.Min(t.Vertex1, t.Vertex2)));
                upper = Vector3.Max(upper, Vector3.Max(t.Vertex, Vector3.Max(t.Vertex1, t.Vertex2)));
                Vector3 center = (t.Vertex + t.Vertex1 + t.Vertex2) / 3;
                centerLower = Vector3.Min(centerLower, center); centerUpper = Vector3.Max(centerUpper, center);
            }
            int index = nodes.Count; nodes.Add(new Node { Lower = lower, Upper = upper, Left = -first - 1, Right = count });
            if (count <= 8) return index;
            Vector3 span = centerUpper - centerLower;
            int axis = span.x >= span.y && span.x >= span.z ? 0 : (span.y >= span.z ? 1 : 2);
            int median = first + count / 2;
            int left = first, right = first + count - 1;
            while (left < right)
            {
                float pivot = Center(triangles[order[(left + right) / 2]], axis);
                int a = left, b = right;
                while (a <= b)
                {
                    while (Center(triangles[order[a]], axis) < pivot) ++a;
                    while (Center(triangles[order[b]], axis) > pivot) --b;
                    if (a > b) break;
                    int swap = order[a]; order[a++] = order[b]; order[b--] = swap;
                }
                if (median <= b) right = b; else if (median >= a) left = a; else break;
            }
            int childLeft = BuildNode(triangles, order, nodes, first, median - first, token);
            int childRight = BuildNode(triangles, order, nodes, median, first + count - median, token);
            nodes[index] = new Node { Lower = lower, Upper = upper, Left = childLeft, Right = childRight };
            return index;
        }

        private static RenderTexture Target(int size, RenderTextureFormat format, bool randomWrite, int depth = 0)
        {
            var result = new RenderTexture(size, size, depth, format, RenderTextureReadWrite.Linear)
            { enableRandomWrite = randomWrite, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            if (!result.Create()) { Object.DestroyImmediate(result); throw new InvalidOperationException("Sky visibility GPU target allocation failed."); }
            return result;
        }

        private static void PrepareGpu(Job j, Bvh bvh)
        {
            j.TriangleBuffer = new ComputeBuffer(bvh.Triangles.Length, Marshal.SizeOf(typeof(Triangle)));
            j.OrderBuffer = new ComputeBuffer(bvh.Order.Length, sizeof(int));
            j.NodeBuffer = new ComputeBuffer(bvh.Nodes.Length, Marshal.SizeOf(typeof(Node)));
            j.TriangleBuffer.SetData(bvh.Triangles); j.OrderBuffer.SetData(bvh.Order); j.NodeBuffer.SetData(bvh.Nodes);
            j.Report.triangleCount = bvh.Triangles.Length; j.Report.nodeCount = bvh.Nodes.Length;
            var alphaTextures = new List<Texture>();
            foreach (SourceMaterial material in j.Materials)
            {
                if (material.Data.Flags.x <= 0) continue;
                int slice = alphaTextures.IndexOf(material.Texture);
                if (slice < 0) { slice = alphaTextures.Count; alphaTextures.Add(material.Texture); }
                material.Data.Flags.y = slice;
            }
            int alphaSize = 1;
            foreach (Texture texture in alphaTextures) alphaSize = Mathf.Max(alphaSize, Mathf.Max(texture.width, texture.height));
            long alphaBytes = (long)alphaSize * alphaSize * Mathf.Max(1, alphaTextures.Count);
            if (alphaBytes > 512L * 1024 * 1024) throw new InvalidOperationException("Sky visibility alpha arrays exceed the 512 MiB transient budget; reduce source alpha texture resolution explicitly first.");
            j.Alpha = new Texture2DArray(alphaSize, alphaSize, Mathf.Max(1, alphaTextures.Count), TextureFormat.R8, false, true)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.HideAndDontSave };
            j.Alpha.Apply(false, true);
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture alphaTarget = Target(alphaSize, RenderTextureFormat.R8, false);
            try
            {
                for (int i = 0; i < alphaTextures.Count; ++i)
                {
                    j.Raster.SetTexture("_MainTex", alphaTextures[i]);
                    Graphics.Blit(alphaTextures[i], alphaTarget, j.Raster, 1);
                    Graphics.CopyTexture(alphaTarget, 0, 0, j.Alpha, i, 0);
                }
            }
            finally { RenderTexture.active = previousActive; alphaTarget.Release(); Object.DestroyImmediate(alphaTarget); }
            var materialData = new MaterialData[j.Materials.Count];
            for (int i = 0; i < materialData.Length; ++i) materialData[i] = j.Materials[i].Data;
            j.MaterialBuffer = new ComputeBuffer(materialData.Length, Marshal.SizeOf(typeof(MaterialData))); j.MaterialBuffer.SetData(materialData);
            // A null depth attachment on this D3D11 Editor path can suppress MRT
            // rasterization at production sizes even with ZTest Always. Bind a
            // matching explicit attachment; the full-size fixture verifies it.
            if (j.Fixture && !j.SurfaceFixture)
            {
                j.Positions = Target(j.Size, RenderTextureFormat.ARGBFloat, false, 24);
                j.Normals = Target(j.Size, RenderTextureFormat.ARGBFloat, false);
                j.Accumulation = Target(j.Size, RenderTextureFormat.RGFloat, true);
                j.Finished = Target(j.Size, RenderTextureFormat.RFloat, true);
            }
            j.Result = new Texture2DArray(j.Size, j.Size, j.AtlasCount, TextureFormat.R8, false, true)
            { name = "Bistro Sky Visibility", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 0 };
            j.Compute.SetBuffer(j.TraceKernel, "_Triangles", j.TriangleBuffer);
            j.Compute.SetBuffer(j.TraceKernel, "_TriangleOrder", j.OrderBuffer);
            j.Compute.SetBuffer(j.TraceKernel, "_Nodes", j.NodeBuffer);
            j.Compute.SetBuffer(j.TraceKernel, "_Materials", j.MaterialBuffer);
            j.Compute.SetTexture(j.TraceKernel, "_Alpha", j.Alpha);
            if (j.Fixture && !j.SurfaceFixture)
            {
                foreach (int kernel in new[] { j.TraceKernel, j.FinishKernel })
                {
                    j.Compute.SetTexture(kernel, "_Positions", j.Positions);
                    j.Compute.SetTexture(kernel, "_Accumulation", j.Accumulation);
                }
                j.Compute.SetTexture(j.TraceKernel, "_Normals", j.Normals);
                j.Compute.SetTexture(j.FinishKernel, "_Finished", j.Finished);
            }
            j.Compute.SetInt("_UseContinuousOrigins", !j.Fixture && j.Settings.ContinuousOrigins ? 1 : 0);
            j.Compute.SetInt("_OccluderTwoSided", !j.Fixture && j.Settings.OccluderTwoSided ? 1 : 0);
            j.Compute.SetInt("_RobustRayOrigin", !j.Fixture && j.Settings.RobustRayOrigin ? 1 : 0);
            j.Compute.SetInt("_RobustIntersections", !j.Fixture && j.Settings.RobustIntersections ? 1 : 0);
            j.Compute.SetInt("_Size", j.Size); j.Compute.SetInt("_SampleCount", j.Samples);
            j.Compute.SetInt("_SamplesPerBatch", j.SamplesPerDispatch);
            j.Compute.SetFloat("_RayDistance", j.Report.rayLength); j.Compute.SetFloat("_RayOffset", j.Report.originOffset);
            j.Report.cutoutTextures = alphaTextures.Count;
            j.Report.approximateGpuBytes = (long)bvh.Triangles.Length * 64 + (long)bvh.Order.Length * 4 + (long)bvh.Nodes.Length * 32 + alphaBytes + (long)j.Size * j.Size * (j.Fixture ? 52 + j.AtlasCount : j.AtlasCount);
            // Release the large managed BVH arrays while the GPU job runs.
            j.BvhTask = Task.FromResult<Bvh>(null);
            if (!j.Fixture) WriteReport(j, "Tracing", null);
        }

        private static void PrepareAtlas(Job j)
        {
            j.Sample = 0; j.SurfaceCursor = 0;
            j.SurfaceTexels?.Release(); j.SurfaceOrigins?.Release(); j.SurfaceAccumulation?.Release(); j.SurfaceFragments?.Release(); j.SurfaceFragmentRanges?.Release();
            j.SurfaceTexels = j.SurfaceOrigins = j.SurfaceAccumulation = j.SurfaceFragments = j.SurfaceFragmentRanges = null;
            j.Surface = null;
            if (!j.Surfaces.TryGetValue(j.Atlas, out var triangles)) throw new InvalidOperationException("Empty receiving atlas.");
            // V3 samples fragments directly. Keep only one compatibility origin
            // for layout validation instead of uploading an unused 4/16-point cache.
            int origins = j.Settings.ContinuousOrigins ? 1 : 16;
            j.Report.spatialOriginsPerTexel = j.Settings.ContinuousOrigins ? 0 : origins;
            int size = j.Size;
            var token = j.Cancellation.Token;
            j.SurfaceTask = Task.Run(() => MaxSkyVisibilitySurfaceSampling.Build(triangles, size, origins, 2, token), token);
            Status = "Building conservative chart-owned surface samples for atlas " + j.Atlas;
            WriteReport(j,"Building surface samples",null);
        }

        private static void PrepareSurfaceBuffers(Job j, MaxSkyVisibilitySurfaceSampling.Result surface)
        {
            if (surface.Texels.Length == 0 || surface.Origins.Length == 0) throw new InvalidOperationException("Empty conservative UV coverage.");
            int conflicts = 0, padding = 0;
            for (int i=0;i<surface.ChartOwners.Length;++i) { if(surface.ChartOwners[i]<0) ++conflicts; if(surface.Coverage[i]==2) ++padding; }
            // Shared filter footprints must not silently average unrelated charts.
            if (conflicts > 0) throw new InvalidOperationException("Conservative UV/filter ownership conflicts: " + conflicts + ". Repack native charts; no sky data published.");
            j.Surface = surface;
            j.SurfaceTexels = new ComputeBuffer(surface.Texels.Length,16);
            j.SurfaceOrigins = new ComputeBuffer(surface.Origins.Length,40);
            j.SurfaceAccumulation = new ComputeBuffer(surface.Texels.Length,8);
            j.SurfaceFragments = new ComputeBuffer(Mathf.Max(1,surface.Fragments.Length),88);
            j.SurfaceFragmentRanges = new ComputeBuffer(surface.FragmentRanges.Length,16);
            j.SurfaceFragments.SetData(surface.Fragments);j.SurfaceFragmentRanges.SetData(surface.FragmentRanges);
            j.Compute.SetBuffer(j.TraceKernel,"_SurfaceFragments",j.SurfaceFragments);
            j.Compute.SetBuffer(j.TraceKernel,"_SurfaceFragmentRanges",j.SurfaceFragmentRanges);
            j.SurfaceTexels.SetData(surface.Texels); j.SurfaceOrigins.SetData(surface.Origins);
            j.Compute.SetBuffer(j.TraceKernel,"_SurfaceTexels",j.SurfaceTexels);
            j.Compute.SetBuffer(j.TraceKernel,"_SurfaceOrigins",j.SurfaceOrigins);
            j.Compute.SetBuffer(j.TraceKernel,"_SurfaceAccumulation",j.SurfaceAccumulation);
            j.Compute.SetInt("_SurfaceCount",surface.Texels.Length);
            if(j.Report.rasterizedTexels==null) {
                j.Report.rasterizedTexels=new int[j.AtlasCount]; j.Report.paddingTexels=new int[j.AtlasCount]; j.Report.chartCounts=new int[j.AtlasCount];
                j.Report.conflictingTexels=new int[j.AtlasCount]; j.Report.originCounts=new int[j.AtlasCount];
                j.Report.alphaEmptyTexels=new int[j.AtlasCount];j.Report.fragmentCounts=new int[j.AtlasCount];
            }
            j.Report.rasterizedTexels[j.Atlas]=surface.Texels.Length;
            j.Report.fragmentCounts[j.Atlas]=surface.Fragments.Length;
            j.Report.originCounts[j.Atlas]=surface.Origins.Length; j.Report.chartCounts[j.Atlas]=surface.Stats.Charts;
            File.WriteAllText(Path.Combine(j.ReportDirectory,"sky_surface_stats_"+j.Atlas+".json"),JsonUtility.ToJson(surface.Stats,true));
            j.Report.paddingTexels[j.Atlas]=padding; j.Report.conflictingTexels[j.Atlas]=conflicts;
            j.Report.approximateGpuBytes += surface.Texels.LongLength*24 + surface.Origins.LongLength*40 + surface.Fragments.LongLength*88 + surface.FragmentRanges.LongLength*16;
            Directory.CreateDirectory(j.ReportDirectory);
            File.WriteAllBytes(Path.Combine(j.ReportDirectory,"sky_coverage_"+j.Atlas+".u8"),surface.Coverage);
            byte[] owner = new byte[surface.ChartOwners.Length*4]; Buffer.BlockCopy(surface.ChartOwners,0,owner,0,owner.Length);
            File.WriteAllBytes(Path.Combine(j.ReportDirectory,"sky_chart_"+j.Atlas+".i32"),owner);
            WriteReport(j,"Tracing",null);
        }

        private static void Dispatch(Job j)
        {
            const int workSize = 4096;
            for(int dispatch=0;dispatch<j.DispatchesPerUpdate && j.Sample<j.Samples;++dispatch)
            {
                j.Compute.SetInt("_SurfaceStart",j.SurfaceCursor);
                j.Compute.SetInt("_SampleStart",j.Sample);
                int count=Mathf.Min(workSize,j.Surface.Texels.Length-j.SurfaceCursor);
                j.Compute.Dispatch(j.TraceKernel,(count+63)/64,1,1);
                j.SurfaceCursor+=count;
                if(j.SurfaceCursor>=j.Surface.Texels.Length) {j.SurfaceCursor=0;j.Sample+=j.SamplesPerDispatch;}
            }
            Status=$"Sky visibility atlas {j.Atlas+1}/{j.AtlasCount}: {Mathf.Min(j.Sample,j.Samples)}/{j.Samples} rays, {j.Surface.Texels.Length:N0} surface texels";
            j.GpuPending=true;
            AsyncGPUReadback.Request(j.SurfaceAccumulation,8,0,request => {
                if(job!=j)return;
                j.GpuPending=false;
                if(request.hasError){Finish(false,"GPU sparse visibility batch failed.");return;}
                if(j.Cancelled || j.Sample<j.Samples)return;
                try{FinishAtlas(j);}catch(Exception error){Debug.LogException(error);Finish(false,error.ToString());}
            });
        }

        private static void FinishAtlas(Job j)
        {
            j.ReadbackPending=true;
            AsyncGPUReadback.Request(j.SurfaceAccumulation,request => {
                if(job!=j)return;
                j.ReadbackPending=false;
                if(j.Cancelled)return;
                if(request.hasError){Finish(false,"Visibility structured-buffer readback failed.");return;}
                try {
                    var sums=request.GetData<Vector2>();
                    var bytes=new byte[j.Size*j.Size];
                    var seeds=new int[bytes.Length];
                    for(int p=0;p<seeds.Length;++p)seeds[p]=-1;
                    int invalid=0;
                    for(int i=0;i<sums.Length;++i){
                        Vector2 sum=sums[i]; int p=(int)j.Surface.Texels[i].Pixel;
                        if(!float.IsFinite(sum.x)||!float.IsFinite(sum.y)||sum.y<0)throw new InvalidOperationException("Invalid visibility accumulation.");
                        if(sum.y<=0){++invalid;continue;}
                        bytes[p]=(byte)Mathf.RoundToInt(Mathf.Clamp01(sum.x/sum.y)*255);seeds[p]=p;
                    }
                    // Only a covered, valid sample in the exact same chart may supply
                    // padding. Never turn missing samples into full visibility.
                    for(int p=0;p<bytes.Length;++p){
                        if(j.Surface.Coverage[p]!=2 && !(j.Surface.Coverage[p]==1 && seeds[p]<0))continue;
                        int owner=j.Surface.ChartOwners[p],px=p%j.Size,py=p/j.Size,best=-1,dist=int.MaxValue;
                        for(int y=-3;y<=3;++y)for(int x=-3;x<=3;++x){
                            int qx=px+x,qy=py+y;if(qx<0||qy<0||qx>=j.Size||qy>=j.Size)continue;
                            int q=qx+qy*j.Size,d=x*x+y*y;
                            if(seeds[q]>=0 && j.Surface.ChartOwners[q]==owner && d<dist){best=q;dist=d;}
                        }
                        if(best>=0)bytes[p]=bytes[best];
                    }
                    j.Report.alphaEmptyTexels[j.Atlas]=invalid;
                    string prefix=Path.Combine(j.ReportDirectory,"sky_visibility_"+j.Atlas);
                    File.WriteAllBytes(prefix+".u8",bytes);
                    var preview=new Texture2D(j.Size,j.Size,TextureFormat.R8,false,true);
                    try{preview.LoadRawTextureData(bytes);preview.Apply();File.WriteAllBytes(prefix+".png",preview.EncodeToPNG());}
                    finally{Object.DestroyImmediate(preview);}
                    j.Result.SetPixelData(bytes,0,j.Atlas);j.ResultPending=true;
                }catch(Exception error){Debug.LogException(error);Finish(false,error.ToString());}
            });
        }

        private static void SaveResults(Job j)
        {
            ValidateSceneOutputPaths(j.Scene, j.OutputDirectory, j.ReportDirectory);
            EnsureFolder(j.OutputDirectory);
            j.Result.Apply(false, false);
            string path = j.OutputDirectory + "/BistroSkyVisibility.asset";
            Texture2DArray previous = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
            if (previous)
            { EditorUtility.CopySerialized(j.Result, previous); Object.DestroyImmediate(j.Result); j.Result = previous; EditorUtility.SetDirty(previous); }
            else AssetDatabase.CreateAsset(j.Result, path);
            MaxBakedLightingData data = null;
            foreach (GameObject root in j.Scene.GetRootGameObjects())
            {
                data = root.GetComponentInChildren<MaxBakedLightingData>(true);
                if (data) break;
            }
            if (!data) { var go = new GameObject("Bistro Baked Lighting"); SceneManager.MoveGameObjectToScene(go, j.Scene); data = go.AddComponent<MaxBakedLightingData>(); }
            var original = new List<MaxBakedLightingData.RendererMaterials>();
            var variants = new Dictionary<string, Material>();
            EnsureFolder(j.OutputDirectory + "/Materials");
            foreach (MeshRenderer renderer in j.Renderers)
            {
                int index = renderer.lightmapIndex;
                if (index < 0 || index >= j.AtlasCount) continue;
                Material[] sources = renderer.sharedMaterials;
                var assigned = (Material[])sources.Clone();
                original.Add(new MaxBakedLightingData.RendererMaterials { Renderer = renderer, SourceMaterials = sources });
                for (int slot = 0; slot < assigned.Length; ++slot)
                {
                    Material source = sources[slot];
                    if (!source || !source.HasProperty("_MaxSkyVisibilitySlice")) continue;
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId))
                        throw new InvalidOperationException("Sky visibility source material must be a persisted asset: " + source.name);
                    string key = guid + "_" + localId + "_LM" + index;
                    if (!variants.TryGetValue(key, out Material variant))
                    {
                        string materialPath = j.OutputDirectory + "/Materials/" + key + ".mat";
                        variant = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                        if (!variant) { variant = new Material(source); AssetDatabase.CreateAsset(variant, materialPath); }
                        else { variant.shader = source.shader; variant.CopyPropertiesFromMaterial(source); }
                        variant.name = Path.GetFileNameWithoutExtension(materialPath);
                        variant.SetFloat("_MaxSkyVisibilitySlice", index);
                        EditorUtility.SetDirty(variant); variants.Add(key, variant);
                    }
                    assigned[slot] = variant;
                }
                renderer.sharedMaterials = assigned;
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                EditorUtility.SetDirty(renderer);
            }
            data.SkyVisibility = j.Result;
            data.OriginalMaterials = original.ToArray();
            data.GeneratedMaterials = new List<Material>(variants.Values).ToArray();
            data.BakeDescription = $"Sky visibility {j.Settings.Label} (uniform environment scalar): {j.AtlasCount} x {j.Size}² R8, {j.Samples} samples, {DateTime.UtcNow:O}. Native lightmap UV layout; no sky energy baked.";
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(j.Scene);
            if (!EditorSceneManager.SaveScene(j.Scene)) throw new IOException("Could not save Bistro scene after baked-lighting binding.");
            if (RenderPipelineManager.currentPipeline is MaxRenderPipeline pipeline)
                foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
                    if (camera.cameraType == CameraType.SceneView || camera.gameObject.scene == j.Scene)
                        pipeline.ResetTemporalHistory(camera);
            EditorApplication.QueuePlayerLoopUpdate(); SceneView.RepaintAll();
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = path.Substring(0, path.LastIndexOf('/')); EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(path.LastIndexOf('/') + 1));
        }
        private static void WriteReport(Job j, string status, string error)
        {
            j.Report.samples = j.Samples; j.Report.samplesPerDispatch = j.SamplesPerDispatch;
            j.Report.dispatchesPerUpdate = j.DispatchesPerUpdate;
            j.Report.status = status; j.Report.error = error; j.Report.seconds = EditorApplication.timeSinceStartup - j.Started;
            if (status == "Completed" || status == "Failed") j.Report.completedUtc = DateTime.UtcNow.ToString("O");
            string output = Path.GetFullPath(j.ReportDirectory); Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "sky_visibility.json"), JsonUtility.ToJson(j.Report, true));
        }
        private static void Finish(bool success, string message)
        {
            Job j = job; if (j == null) return;
            job = null;
            EditorApplication.update -= Tick; EditorApplication.quitting -= StopBeforeReload;
            AssemblyReloadEvents.beforeAssemblyReload -= StopBeforeReload;
            EditorUtility.ClearProgressBar();
            try { WriteReport(j, success ? "Completed" : "Failed", success ? null : message); }
            finally { j.Dispose(); }
            Status = message;
            if (success) Debug.Log(message); else Debug.LogWarning(message);
            // Full-scene subscribers own hard-coded validation/status paths. Never
            // notify them about a Tiny bake, even when a stale Full pending flag exists.
            if (j.Scene.path == MaxBistroSceneBuilder.ScenePath) Completed?.Invoke(success, message);
        }
    }
}

