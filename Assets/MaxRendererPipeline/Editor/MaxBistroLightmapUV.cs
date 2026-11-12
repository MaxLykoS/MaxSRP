using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MaxSRP.EditorTools
{
    /// <summary>Scene-authored UV budgets, applied once by the Bistro model importer.</summary>
    public static class MaxBistroLightmapUV
    {
        public const string ModelPath = "Assets/ThirdParty/Bistro/BistroExterior.fbx";
        public const string RecipePath = "Assets/Scenes/BistroTest/Lighting/BistroUvBudget.json";
        public const string LayoutManifestPath = "Assets/ThirdParty/Bistro/LightmapUV/manifest.json";
        internal const uint PostprocessorVersion = 4;
        private const string CacheFolder = "Assets/ThirdParty/Bistro/LightmapUV";
        public const float Density = 4.0f;
        public const int AtlasSide = 2048;
        private const int RecipeVersion = 1;
        private const int GuardTexels = 3;
        private const double MaximumLostAreaRatio = 1e-4;
        private const string ScenePath = "Assets/Scenes/BistroTest/BistroTest.unity";
        private const string MarkerPrefix = "MaxSRP.BistroLightmapUV=";
        private const string LayoutMarkerPrefix = "MaxSRP.BistroLightmapUVLayout=";
        private const string SourceCapturePrefix = "MaxSRP.BistroLightmapUVSourceCapture=";
        private const string ReportFolder = "OfflineBaseline/BistroLightmap";
        private const string ProgressPath = ReportFolder + "/uv_preparation.json";
        private const string ProofPath = ReportFolder + "/uv_import_proof.json";
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly Dictionary<int, MeshData> MeshCache = new Dictionary<int, MeshData>();
        private static readonly Dictionary<int, SurfaceData> SurfaceCache = new Dictionary<int, SurfaceData>();
        private static Recipe s_recipe;
        private static long s_recipeTicks, s_recipeLength;
        private static string s_recipeByteHash;
        private static long s_recipeHashTicks, s_recipeHashLength;
        private static bool s_cacheReleaseScheduled;
        private static LayoutManifest s_layout;
        private static string s_layoutHash, s_layoutRecipeHash;
        private static long s_layoutTicks, s_layoutLength;

        [Serializable] private sealed class SourceEntry
        {
            public string meshName, file, attributes, sourceGeometryHash;
            public double worldSurfaceArea;
            public int vertices, triangles;
        }
        [Serializable] private sealed class SourceManifest
        {
            public bool complete;
            public int version = 1;
            public string utc, recipeHash;
            public float density = Density;
            public int padding = 2;
            public SourceEntry[] meshes;
        }

        [Serializable] private sealed class LayoutEntry
        {
            public string meshName, file, sourceGeometryHash, payloadHash;
            public int squareSide, vertices, triangles;
            public double lostWorldArea;
            public int unassignedNonDegenerateFaces, selfOverlapPairs, filterOverlapPixels;
        }
        [Serializable] private sealed class LayoutManifest
        {
            public int version;
            public bool complete;
            public string recipeHash, algorithm;
            public float density;
            public int padding;
            public LayoutEntry[] meshes;
        }
        [Serializable] private sealed class ImportProof
        {
            public int version = 1;
            public bool complete;
            public string utc, state, recipeHash, layoutHash;
            public uint postprocessorVersion;
            public int processedMeshCount, targetMeshCount;
        }
        private sealed class SourceBytes
        {
            public byte[] Geometry, Attributes;
            public int Vertices, Triangles;
            public string Hash;
        }

        [Serializable]
        internal sealed class Recipe
        {
            public int version = RecipeVersion;
            public float density = Density;
            public int guardTexels = GuardTexels;
            public string createdUtc;
            public Entry[] meshes = Array.Empty<Entry>();
        }

        [Serializable]
        internal sealed class Entry
        {
            public string meshName;
            public int triangleCount;
            public Vector3 boundsCenter;
            public Vector3 boundsSize;
            // The largest authored instance determines this mesh's shared unwrap budget.
            public double worldSurfaceArea;
            public double minimumWorldSurfaceArea;
            public Vector3 minimumAbsoluteScale;
            public Vector3 maximumAbsoluteScale;
            public int instanceCount;
            // Retained for reading Recipe1 files; external layouts do not consume these.
            public int uvIslandCount;
            public int targetSide;
            public float packMargin;
        }

        [Serializable]
        private sealed class Progress
        {
            public string utc;
            public string status;
            public string recipeHash;
            public string layoutHash;
            public int processedMeshCount;
            public int targetMeshCount;
            public string currentMesh;
            public bool importing;
            public bool prepared;
            public string[] errors = Array.Empty<string>();
        }

        private sealed class MeshData
        {
            public Mesh Mesh;
            public Vector3[] Positions;
            public Vector2[] UV;
            public int[] Triangles;
            public bool[] ZeroUvArea;
            public bool HasUV2;
            public double UvArea;
            public int Islands;
            public Vector2 UvMin, UvMax;
            public int TriangleCount => Triangles.Length / 3;
        }

        private sealed class SurfaceData
        {
            public MeshData Mesh;
            public Matrix4x4 Matrix;
            public double Area;
            public double LostArea;
            public int LostTriangles;
        }

        private sealed class Receiver
        {
            public MeshRenderer Renderer;
            public MeshData Mesh;
            public SurfaceData Surface;
        }

        private struct VertexUvKey : IEquatable<VertexUvKey>
        {
            public Vector3 Position;
            public Vector2 UV;
            public bool Equals(VertexUvKey other) => Position.Equals(other.Position) && UV.Equals(other.UV);
            public override bool Equals(object other) => other is VertexUvKey key && Equals(key);
            public override int GetHashCode() { unchecked { return Position.GetHashCode() * 397 ^ UV.GetHashCode(); } }
        }

        private struct EdgeKey : IEquatable<EdgeKey>
        {
            public int A, B;
            public EdgeKey(int a, int b) { A = Math.Min(a, b); B = Math.Max(a, b); }
            public bool Equals(EdgeKey other) => A == other.A && B == other.B;
            public override bool Equals(object other) => other is EdgeKey key && Equals(key);
            public override int GetHashCode() { unchecked { return A * 397 ^ B; } }
        }

        /// <summary>Captures actual callback geometry before UV splitting and final mesh optimization.</summary>
        public static string CaptureSourceGeometry(Scene scene)
        {
            RequireScene(scene);
            if (Lightmapping.isRunning || MaxBistroSkyVisibilityBaker.IsRunning || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Source UV capture requires an idle editor.");
            string run = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string folder = ReportFolder + "/UVSources/" + run;
            var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
            string pattern = @"(?m)^" + Regex.Escape(SourceCapturePrefix) + @"[^\r\n]*\r?\n?";
            string original = Regex.Replace(importer.userData ?? "", pattern, "");
            try
            {
                InvalidateProof("source-capture-requested");
                importer.generateSecondaryUV = false;
                string captureData = SetOwnedMarker(original, MarkerPrefix + "v" + RecipeVersion + ":" + RecipeHash());
                importer.userData = captureData.TrimEnd('\r', '\n') + "\n" + SourceCapturePrefix + run;
                WriteProgress("source-capture-requested", RecipeHash(), 0, ReadRecipe().meshes.Length, "", true, false);
                AssetDatabase.WriteImportSettingsIfDirty(ModelPath);
                AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.DontDownloadFromCacheServer);
                if (!File.Exists(AbsolutePath(folder + "/manifest.json")) || !JsonUtility.FromJson<SourceManifest>(File.ReadAllText(AbsolutePath(folder + "/manifest.json"))).complete)
                    throw new InvalidOperationException("Source geometry capture did not complete: " + folder);
                return folder;
            }
            finally
            {
                importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
                importer.userData = original;
                AssetDatabase.WriteImportSettingsIfDirty(ModelPath);
                ClearCaches();
            }
        }

        private static void CaptureImportedSources(GameObject root, Recipe recipe, string run)
        {
            string folder = ReportFolder + "/UVSources/" + run;
            Directory.CreateDirectory(AbsolutePath(folder));
            var wanted = recipe.meshes.ToDictionary(entry => entry.meshName, StringComparer.Ordinal);
            var meshes = root.GetComponentsInChildren<MeshFilter>(true).Where(filter => filter.sharedMesh && wanted.ContainsKey(filter.sharedMesh.name))
                .Select(filter => filter.sharedMesh).Distinct().OrderBy(mesh => mesh.name, StringComparer.Ordinal).ToArray();
            var records = new List<SourceEntry>();
            string hash = RecipeHash();
            foreach (Mesh mesh in meshes)
            {
                string name = records.Count.ToString("D4", Invariant);
                string file = name + ".bin", attributes = name + ".attributes.bin";
                WriteProgress("capturing-source-geometry", hash, records.Count, wanted.Count, mesh.name, true, false);
                SourceBytes source = SerializeSourceBytes(mesh);
                File.WriteAllBytes(AbsolutePath(folder + "/" + file), source.Geometry);
                File.WriteAllBytes(AbsolutePath(folder + "/" + attributes), source.Attributes);
                records.Add(new SourceEntry { meshName = mesh.name, file = file, attributes = attributes,
                    worldSurfaceArea = wanted[mesh.name].worldSurfaceArea, vertices = source.Vertices, triangles = source.Triangles, sourceGeometryHash = source.Hash });
            }
            if (records.Count != wanted.Count) throw new InvalidOperationException("Source mesh count does not match the scene recipe.");
            File.WriteAllText(AbsolutePath(folder + "/manifest.json"), JsonUtility.ToJson(new SourceManifest { complete = true, utc = DateTime.UtcNow.ToString("O"), recipeHash = hash, meshes = records.ToArray() }, true), new UTF8Encoding(false));
            WriteProgress("source-capture-complete", hash, records.Count, wanted.Count, "", false, false);
        }

        // Keep this byte-for-byte compatible with the two files captured before UV splitting.
        private static SourceBytes SerializeSourceBytes(Mesh mesh)
        {
            var result = new SourceBytes { Vertices = mesh.vertexCount };
            using (var geometry = new MemoryStream())
            using (var attributes = new MemoryStream())
            using (var writer = new BinaryWriter(geometry))
            using (var attributeWriter = new BinaryWriter(attributes))
            using (var array = MeshUtility.AcquireReadOnlyMeshData(mesh))
            using (var positions = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Temp))
            using (var normals = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Temp))
            using (var uv = new NativeArray<Vector2>(mesh.vertexCount, Allocator.Temp))
            {
                var data = array[0]; data.GetVertices(positions); data.GetNormals(normals); data.GetUVs(0, uv);
                writer.Write(mesh.vertexCount); writer.Write(data.subMeshCount);
                writer.Write(1f); writer.Write(1f); writer.Write(0f); writer.Write(0f);
                foreach (Vector3 point in positions) { writer.Write(point.x); writer.Write(point.y); writer.Write(point.z); }
                foreach (Vector2 value in uv) { writer.Write(value.x); writer.Write(value.y); }
                for (int submesh = 0; submesh < data.subMeshCount; ++submesh)
                {
                    var descriptor = data.GetSubMesh(submesh);
                    if (descriptor.topology != MeshTopology.Triangles || descriptor.indexCount % 3 != 0) throw new InvalidOperationException("Nontriangle source: " + mesh.name);
                    using (var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp))
                    {
                        data.GetIndices(indices, submesh, true); writer.Write(indices.Length);
                        foreach (int index in indices) writer.Write(index);
                        result.Triangles += indices.Length / 3;
                    }
                }
                attributeWriter.Write(mesh.vertexCount);
                foreach (Vector3 normal in normals) { attributeWriter.Write(normal.x); attributeWriter.Write(normal.y); attributeWriter.Write(normal.z); }
                foreach (Vector2 value in uv) { attributeWriter.Write(value.x); attributeWriter.Write(value.y); }
                writer.Flush(); attributeWriter.Flush();
                result.Geometry = geometry.ToArray(); result.Attributes = attributes.ToArray();
            }
            using (var sha = SHA256.Create())
            {
                sha.TransformBlock(result.Geometry, 0, result.Geometry.Length, result.Geometry, 0);
                sha.TransformFinalBlock(result.Attributes, 0, result.Attributes.Length);
                result.Hash = Hex(sha.Hash);
            }
            return result;
        }

        private static LayoutManifest ReadLayout()
        {
            string path = AbsolutePath(LayoutManifestPath);
            if (!File.Exists(path)) throw new InvalidOperationException("Missing external Bistro UV cache. CaptureSourceGeometry, run the Bistro UV CLI, then Prepare: " + LayoutManifestPath);
            var file = new FileInfo(path);
            string recipeHash = RecipeHash();
            if (s_layout != null && s_layoutTicks == file.LastWriteTimeUtc.Ticks && s_layoutLength == file.Length && s_layoutRecipeHash == recipeHash) return s_layout;
            byte[] bytes = File.ReadAllBytes(path);
            LayoutManifest layout = JsonUtility.FromJson<LayoutManifest>(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            Recipe recipe = ReadRecipe();
            if (layout == null || layout.version != 1 || !layout.complete || layout.recipeHash != recipeHash || layout.density != Density || layout.padding != 2 ||
                string.IsNullOrEmpty(layout.algorithm) || layout.meshes == null || layout.meshes.Length != recipe.meshes.Length)
                throw new InvalidOperationException("External Bistro UV manifest is incomplete or incompatible with this recipe. Regenerate its complete cache.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (LayoutEntry entry in layout.meshes)
            {
                if (entry == null || string.IsNullOrEmpty(entry.meshName) || !names.Add(entry.meshName) || !IsHash(entry.sourceGeometryHash) || !IsHash(entry.payloadHash) ||
                    entry.vertices <= 0 || entry.triangles <= 0 || entry.squareSide <= 0 || entry.squareSide > AtlasSide ||
                    double.IsNaN(entry.lostWorldArea) || double.IsInfinity(entry.lostWorldArea) || entry.lostWorldArea < 0 ||
                    entry.unassignedNonDegenerateFaces != 0 || entry.selfOverlapPairs != 0 || entry.filterOverlapPixels != 0)
                    throw new InvalidOperationException("External Bistro UV manifest has an invalid or unverified mesh: " + entry?.meshName);
                Entry source = FindEntry(recipe, entry.meshName);
                if (source.triangleCount != entry.triangles || entry.lostWorldArea > Math.Max(1e-12, source.worldSurfaceArea * MaximumLostAreaRatio))
                    throw new InvalidOperationException("External Bistro UV manifest loses geometry or has a stale triangle count: " + entry.meshName);
                PayloadPath(entry); // Resolve and check the path before any payload is read.
            }
            s_layout = layout; s_layoutHash = Sha256(bytes); s_layoutRecipeHash = recipeHash;
            s_layoutTicks = file.LastWriteTimeUtc.Ticks; s_layoutLength = file.Length;
            return layout;
        }

        private static string LayoutHash() { ReadLayout(); return s_layoutHash; }
        private static LayoutEntry FindLayoutEntry(string meshName)
        {
            LayoutEntry entry = Array.Find(ReadLayout().meshes, value => value.meshName == meshName);
            if (entry == null) throw new InvalidOperationException("External UV cache does not cover mesh: " + meshName);
            return entry;
        }

        private static string PayloadPath(LayoutEntry entry)
        {
            if (string.IsNullOrEmpty(entry.file) || Path.IsPathRooted(entry.file) || !entry.file.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid external UV payload path: " + entry.meshName);
            string root = Path.GetFullPath(AbsolutePath(CacheFolder)) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, entry.file.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("UV payload escapes its local Bistro cache: " + entry.meshName);
            return path;
        }

        private static Vector2[] ReadPayload(LayoutEntry entry)
        {
            string path = PayloadPath(entry);
            if (!File.Exists(path)) throw new InvalidOperationException("Missing external UV payload. Capture source geometry and run the Bistro UV CLI: " + entry.file);
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.LongLength != 44L + 24L * entry.triangles || Sha256(bytes) != entry.payloadHash)
                throw new InvalidOperationException("External UV payload hash/length mismatch: " + entry.meshName);
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "MXUV" || reader.ReadInt32() != 1 || reader.ReadInt32() != entry.triangles || Hex(reader.ReadBytes(32)) != entry.sourceGeometryHash)
                    throw new InvalidOperationException("External UV payload header/source hash mismatch: " + entry.meshName);
                var uv = new Vector2[checked(entry.triangles * 3)];
                for (int i = 0; i < uv.Length; ++i)
                {
                    uv[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    if (!Finite(uv[i]) || uv[i].x < -1e-6f || uv[i].y < -1e-6f || uv[i].x > 1.000001f || uv[i].y > 1.000001f)
                        throw new InvalidOperationException("Invalid external per-corner UV: " + entry.meshName + " corner=" + i);
                }
                return uv;
            }
        }

        private static void ValidatePayloads(LayoutManifest layout)
        {
            foreach (LayoutEntry entry in layout.meshes) ReadPayload(entry);
        }

        internal static string[] ExternalDependencyPaths()
        {
            if (!File.Exists(AbsolutePath(LayoutManifestPath))) return Array.Empty<string>();
            LayoutManifest layout = ReadLayout();
            return new[] { LayoutManifestPath }.Concat(layout.meshes.Select(entry => CacheFolder + "/" + entry.file.Replace('\\', '/'))).ToArray();
        }

        // Vertex ids and buffer layout can change when UV2 splits vertices. Compare all
        // other attribute bytes in original submesh/triangle/corner semantic order instead.
        private static string CornerAttributesHash(Mesh mesh)
        {
            VertexAttributeDescriptor[] attributes = mesh.GetVertexAttributes().Where(value => value.attribute != VertexAttribute.TexCoord1).OrderBy(value => (int)value.attribute).ToArray();
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            using (var array = MeshUtility.AcquireReadOnlyMeshData(mesh))
            {
                var data = array[0];
                var buffers = new Dictionary<int, byte[]>();
                var strides = new int[attributes.Length]; var offsets = new int[attributes.Length]; var lengths = new int[attributes.Length];
                writer.Write(attributes.Length);
                for (int attributeIndex = 0; attributeIndex < attributes.Length; ++attributeIndex)
                {
                    VertexAttributeDescriptor attribute = attributes[attributeIndex];
                    if (!buffers.ContainsKey(attribute.stream)) buffers.Add(attribute.stream, data.GetVertexData<byte>(attribute.stream).ToArray());
                    strides[attributeIndex] = mesh.GetVertexBufferStride(attribute.stream);
                    offsets[attributeIndex] = mesh.GetVertexAttributeOffset(attribute.attribute);
                    lengths[attributeIndex] = FormatBytes(attribute.format) * attribute.dimension;
                    writer.Write((int)attribute.attribute); writer.Write((int)attribute.format); writer.Write(attribute.dimension);
                }
                writer.Write(data.subMeshCount);
                for (int submesh = 0; submesh < data.subMeshCount; ++submesh)
                {
                    var descriptor = data.GetSubMesh(submesh);
                    writer.Write((int)descriptor.topology); writer.Write(descriptor.indexCount);
                    using (var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp))
                    {
                        data.GetIndices(indices, submesh, true);
                        foreach (int vertex in indices)
                        for (int attributeIndex = 0; attributeIndex < attributes.Length; ++attributeIndex)
                        {
                            int offset = checked(vertex * strides[attributeIndex] + offsets[attributeIndex]);
                            int length = lengths[attributeIndex];
                            byte[] buffer = buffers[attributes[attributeIndex].stream];
                            if (offset < 0 || offset > buffer.Length - length) throw new InvalidOperationException("Invalid source corner attribute range: " + mesh.name);
                            writer.Write(buffer, offset, length);
                        }
                    }
                }
                writer.Flush();
                using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream.GetBuffer(), 0, checked((int)stream.Length)));
            }
        }

        private static int FormatBytes(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32: case VertexAttributeFormat.UInt32: case VertexAttributeFormat.SInt32: return 4;
                case VertexAttributeFormat.Float16: case VertexAttributeFormat.UNorm16: case VertexAttributeFormat.SNorm16: case VertexAttributeFormat.UInt16: case VertexAttributeFormat.SInt16: return 2;
                case VertexAttributeFormat.UNorm8: case VertexAttributeFormat.SNorm8: case VertexAttributeFormat.UInt8: case VertexAttributeFormat.SInt8: return 1;
                default: throw new InvalidOperationException("Unsupported source vertex attribute format: " + format);
            }
        }

        private static ImportProof ReadProof() => File.Exists(AbsolutePath(ProofPath)) ? JsonUtility.FromJson<ImportProof>(File.ReadAllText(AbsolutePath(ProofPath))) : null;
        private static bool ProofMatches(ImportProof proof, string recipeHash, string layoutHash, int count) =>
            proof != null && proof.version == 1 && proof.complete && proof.recipeHash == recipeHash && proof.layoutHash == layoutHash &&
            proof.postprocessorVersion == PostprocessorVersion && proof.processedMeshCount == count && proof.targetMeshCount == count &&
            (proof.state == "applied" || proof.state == "validated");
        private static void WriteProof(string state, string recipeHash, string layoutHash, int processed, int target, bool complete)
        {
            Directory.CreateDirectory(AbsolutePath(ReportFolder));
            File.WriteAllText(AbsolutePath(ProofPath), JsonUtility.ToJson(new ImportProof { utc = DateTime.UtcNow.ToString("O"), state = state,
                recipeHash = recipeHash, layoutHash = layoutHash, processedMeshCount = processed, targetMeshCount = target,
                complete = complete, postprocessorVersion = PostprocessorVersion }, true), new UTF8Encoding(false));
        }
        private static void InvalidateProof(string reason) => WriteProof(reason, "", "", 0, 0, false);
        private static bool IsHash(string value) => value != null && Regex.IsMatch(value, "^[0-9a-f]{64}$");
        private static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
        private static string Sha256(byte[] value) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(value)); }

        /// <summary>Creates or validates the parameter recipe, then imports only when necessary.</summary>
        public static void Ensure(Scene scene)
        {
            RequireScene(scene);
            if (Lightmapping.isRunning || MaxBistroSkyVisibilityBaker.IsRunning)
                throw new InvalidOperationException("Bistro UV preparation requires an idle lightmapper.");
            string hash = "";
            try
            {
                ClearCaches();
                WriteProgress("checking-scene", hash, 0, 0, "", false, false);
                List<Receiver> receivers = Gather(scene);
                Recipe recipe;
                if (File.Exists(AbsolutePath(RecipePath)))
                {
                    recipe = ReadRecipe();
                    ValidateRecipeScene(recipe, receivers);
                }
                else
                {
                    recipe = CreateRecipe(receivers);
                    Directory.CreateDirectory(Path.GetDirectoryName(AbsolutePath(RecipePath)));
                    File.WriteAllText(AbsolutePath(RecipePath), JsonUtility.ToJson(recipe, true), new UTF8Encoding(false));
                    AssetDatabase.ImportAsset(RecipePath, ImportAssetOptions.ForceUpdate);
                }
                hash = RecipeHash();
                LayoutManifest layout = ReadLayout();
                ValidatePayloads(layout);
                string layoutHash = LayoutHash();
                var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
                if (!importer) throw new InvalidOperationException("Bistro model importer is missing: " + ModelPath);
                string marker = MarkerPrefix + "v" + RecipeVersion + ":" + hash;
                string updatedUserData = SetOwnedMarker(importer.userData, marker);
                updatedUserData = SetOwnedLine(updatedUserData, LayoutMarkerPrefix, LayoutMarkerPrefix + "v1:" + layoutHash);
                bool importWasComplete = ProofMatches(ReadProof(), hash, layoutHash, recipe.meshes.Length);
                bool reimport = importer.generateSecondaryUV || importer.userData != updatedUserData || !importWasComplete;
                WriteProgress("recipe-validated", hash, 0, recipe.meshes.Length, "", false, false);
                if (reimport)
                {
                    importer.generateSecondaryUV = false;
                    importer.userData = updatedUserData;
                    WriteProgress("reimport-requested", hash, 0, recipe.meshes.Length, "", true, false);
                    AssetDatabase.WriteImportSettingsIfDirty(ModelPath);
                    AssetDatabase.ImportAsset(ModelPath, ImportAssetOptions.ForceUpdate |
                        ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.DontDownloadFromCacheServer);
                    Progress completed = ReadProgress();
                    if (completed == null || completed.recipeHash != hash || completed.layoutHash != layoutHash || completed.status != "import-complete")
                    {
                        string details = completed != null && completed.recipeHash == hash && completed.errors != null
                            ? string.Join("\n", completed.errors) : "";
                        throw new InvalidOperationException("The Bistro custom UV import did not complete" +
                            (completed == null ? "" : " at " + completed.currentMesh + " (" + completed.processedMeshCount + "/" + completed.targetMeshCount + ")") +
                            ". See " + ProgressPath + (string.IsNullOrEmpty(details) ? "" : "\n" + details));
                    }
                    ClearCaches();
                    // Reimport replaces native Mesh objects; never reuse pre-import references.
                    scene = SceneManager.GetSceneByPath(ScenePath);
                    receivers = Gather(scene);
                }
                ValidateRecipeScene(recipe, receivers);
                ValidateActualUV(recipe, receivers);
                WriteProof("validated", hash, layoutHash, recipe.meshes.Length, recipe.meshes.Length, true);
                WriteProgress("prepared", hash, recipe.meshes.Length, recipe.meshes.Length, "", false, true);
            }
            catch (Exception exception)
            {
                Progress previous = ReadProgress();
                if (previous != null && previous.recipeHash == hash && previous.status == "import-failed")
                {
                    // Keep the importer-owned failure and its original counters/current mesh.
                    // ImportAsset can return or throw a generic wrapper after the real failure.
                    throw new InvalidOperationException("Bistro custom UV import failed at " + previous.currentMesh +
                        " (" + previous.processedMeshCount + "/" + previous.targetMeshCount + "):\n" +
                        string.Join("\n", previous.errors ?? Array.Empty<string>()), exception);
                }
                WriteProgress("failed", hash, previous?.processedMeshCount ?? 0, previous?.targetMeshCount ?? 0,
                    previous?.currentMesh ?? "", false, false, exception.ToString());
                throw;
            }
            finally { ScheduleCacheRelease(); }
        }

        /// <summary>Expected native linear density multiplier; does not edit this renderer.</summary>
        public static float GetScale(MeshRenderer renderer)
        {
            ScheduleCacheRelease();
            if (!renderer) throw new ArgumentNullException(nameof(renderer));
            var filter = renderer.GetComponent<MeshFilter>();
            if (!filter || !filter.sharedMesh) throw new InvalidOperationException("Lightmap receiver has no mesh: " + renderer.name);
            if (!IsBistroMesh(filter.sharedMesh)) return 1.0f;
            Recipe recipe = ReadRecipe();
            Entry entry = FindEntry(recipe, filter.sharedMesh.name);
            MeshData mesh = GetMesh(filter.sharedMesh);
            SurfaceData surface = GetSurface(renderer, mesh);
            ValidateEntryGeometry(entry, mesh);
            ValidateInstance(entry, renderer, surface);
            return (float)Scale(entry, mesh, surface, renderer.name);
        }

        /// <summary>Validates recipe, geometry, instance scale ranges and generated UV budgets.</summary>
        public static void Validate(Scene scene)
        {
            ScheduleCacheRelease();
            RequireScene(scene);
            Recipe recipe = ReadRecipe();
            LayoutManifest layout = ReadLayout();
            ValidatePayloads(layout);
            string layoutHash = LayoutHash();
            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (!importer || importer.generateSecondaryUV || !TryMarkerHash(importer.userData, out string markerHash) || markerHash != RecipeHash() ||
                !TryOwnedHash(importer.userData, LayoutMarkerPrefix, out string markerLayout) || markerLayout != layoutHash)
                throw new InvalidOperationException("Bistro custom UV importer/recipe marker is stale. Run Prepare before baking.");
            ImportProof proof = ReadProof();
            if (!ProofMatches(proof, markerHash, layoutHash, recipe.meshes.Length) || proof.state != "validated")
                throw new InvalidOperationException("Bistro external UV import has no complete matching validated proof. Run Prepare before baking.");
            List<Receiver> receivers = Gather(scene);
            ValidateRecipeScene(recipe, receivers);
            ValidateActualUV(recipe, receivers);
            // Renderer.scaleInLightmap is deliberately checked by the caller after Prepare assigns it.
        }

        /// <summary>Writes the actual post-import plan outside Assets and returns its summary.</summary>
        public static string Describe(Scene scene)
        {
            Validate(scene);
            Recipe recipe = ReadRecipe();
            LayoutManifest layout = ReadLayout();
            var csv = new StringBuilder("renderer,mesh,worldArea,uvArea,actualIslandCount,packedSquareSide,packedPadding,expectedNativeScale,assignedNativeScale,predictedTileSide,effectiveAreaDensity,rectangleTexels,lostUvWorldArea,lostUvAreaRatio,lostUvTriangles\n");
            var lines = new List<string>();
            double surfaceTexels = 0, rectangleTexels = 0, lostArea = 0, worldArea = 0, largestSide = 0;
            int receivers = 0, lostTriangles = 0;
            foreach (Receiver receiver in Gather(scene))
            {
                Entry entry = FindEntry(recipe, receiver.Mesh.Mesh.name);
                LayoutEntry packed = FindLayoutEntry(entry.meshName);
                double scale = Scale(entry, receiver.Mesh, receiver.Surface, receiver.Renderer.name);
                double side = Math.Sqrt(receiver.Surface.Area) * Density * scale;
                double texels = side * side;
                surfaceTexels += receiver.Surface.Area * Density * Density;
                rectangleTexels += texels; worldArea += receiver.Surface.Area;
                lostArea += receiver.Surface.LostArea; lostTriangles += receiver.Surface.LostTriangles;
                largestSide = Math.Max(largestSide, side); ++receivers;
                csv.AppendLine(string.Join(",", new[] { Csv(receiver.Renderer.name), Csv(entry.meshName), F(receiver.Surface.Area), F(receiver.Mesh.UvArea), receiver.Mesh.Islands.ToString(Invariant), packed.squareSide.ToString(Invariant), layout.padding.ToString(Invariant), F(scale), F(receiver.Renderer.scaleInLightmap), F(side), F(Math.Sqrt(receiver.Mesh.UvArea * texels / receiver.Surface.Area)), F(texels), F(receiver.Surface.LostArea), F(receiver.Surface.LostArea / receiver.Surface.Area), receiver.Surface.LostTriangles.ToString(Invariant) }));
            }
            double atlases = Math.Ceiling(rectangleTexels / ((double)AtlasSide * AtlasSide));
            lines.Add("UTC=" + DateTime.UtcNow.ToString("O") + "; recipe=" + RecipePath + "; hash=" + RecipeHash() + "; layoutHash=" + LayoutHash());
            lines.Add("Receivers=" + receivers + "; uniqueMeshBudgets=" + recipe.meshes.Length + "; targetDensity=" + Density + " texels/m; cacheAlgorithm=" + layout.algorithm + "; packedPadding=" + layout.padding + " pixels.");
            lines.Add("worldSurfaceArea=" + F(worldArea) + "; baseSurfaceTexels=" + F(surfaceTexels) + "; nativeRectangleTexels=" + F(rectangleTexels) + "; maximumPredictedTileSide=" + F(largestSide) + "; areaOnly2048AtlasLowerBound=" + F(atlases));
            lines.Add("UV-degenerate nonzero-world triangles=" + lostTriangles + "; lostWorldArea=" + F(lostArea) + "; totalLostAreaRatio=" + F(worldArea > 0 ? lostArea / worldArea : 0) + "; perReceiverRejectRatio=" + F(MaximumLostAreaRatio));
            lines.Add("Logical RGBAHalf color + RGBA8 direction + R8 sky at the area-only atlas lower bound: " + F(atlases * AtlasSide * AtlasSide * 13 / (1024 * 1024)) + " MiB without mips. Packing waste and bake/transient memory are excluded.");
            lines.Add("Native scale=externalPackedSquareSide/sqrt(instanceWorldArea*4^2). Both UV axes share that square normalization. Recipe1 retains geometry/instance metadata; its legacy unwrap fields do not control this layout.");
            lines.Add("This is a predicted allocation plan, not final native ST or filter-footprint proof. Native HasUVOverlaps and the post-bake gate remain mandatory. Final density must be checked using actual atlas dimensions and renderer ST; atlas area lower bounds exclude packing overhead.");
            string result = string.Join("\n", lines);
            Directory.CreateDirectory(AbsolutePath(ReportFolder));
            File.WriteAllText(AbsolutePath(ReportFolder + "/uv_plan.csv"), csv.ToString(), new UTF8Encoding(false));
            File.WriteAllText(AbsolutePath(ReportFolder + "/uv_plan.txt"), result, new UTF8Encoding(false));
            return result;
        }

        internal static void ProcessImportedModel(GameObject root, ModelImporter importer)
        {
            if (!TryMarkerHash(importer.userData, out string markerHash)) return;
            Recipe recipe = ReadRecipe();
            string hash = RecipeHash();
            if (markerHash != hash || importer.generateSecondaryUV)
                throw new InvalidOperationException("Bistro UV recipe/hash does not match its custom-import marker; run Prepare.");
            Match capture = Regex.Match(importer.userData ?? "", @"(?m)^" + Regex.Escape(SourceCapturePrefix) + @"(\d{8}-\d{6})\r?$");
            if (capture.Success)
            {
                InvalidateProof("source-capture");
                CaptureImportedSources(root, recipe, capture.Groups[1].Value);
                return;
            }
            LayoutManifest layout = ReadLayout();
            string layoutHash = LayoutHash();
            if (!TryOwnedHash(importer.userData, LayoutMarkerPrefix, out string markerLayout) || markerLayout != layoutHash)
                throw new InvalidOperationException("Bistro external UV layout marker is stale; run Prepare after generating the complete cache.");
            var entries = recipe.meshes.ToDictionary(entry => entry.meshName, StringComparer.Ordinal);
            var meshes = new HashSet<Mesh>();
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true)) if (filter.sharedMesh) meshes.Add(filter.sharedMesh);
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (renderer.sharedMesh) meshes.Add(renderer.sharedMesh);
            var targets = meshes.Where(mesh => entries.ContainsKey(mesh.name)).OrderBy(mesh => mesh.name, StringComparer.Ordinal).ToArray();
            var matchedNames = new HashSet<string>(StringComparer.Ordinal);
            int processed = 0;
            string currentMesh = "";
            try
            {
                InvalidateProof("importing");
                ClearCaches();
                WriteProgress("import-started", hash, 0, targets.Length, "", true, false);
                foreach (Mesh mesh in targets)
                {
                    currentMesh = mesh.name;
                    Entry entry = entries[mesh.name];
                    MeshData before = GetMesh(mesh);
                    ValidateEntryGeometry(entry, before);
                    int blendShapes = mesh.blendShapeCount;
                    SourceBytes source = SerializeSourceBytes(mesh);
                    LayoutEntry packed = FindLayoutEntry(mesh.name);
                    if (source.Hash != packed.sourceGeometryHash || source.Vertices != packed.vertices || source.Triangles != packed.triangles)
                        throw new InvalidOperationException("Bistro external UV source geometry hash/count mismatch: " + mesh.name + ". Capture source geometry and regenerate the cache.");
                    Vector2[] perTriangleUV = ReadPayload(packed);
                    string sourceCorners = CornerAttributesHash(mesh);
                    // Per-corner UVs can split every triangle corner into its own vertex.
                    if (mesh.indexFormat == IndexFormat.UInt16 && before.Triangles.Length > ushort.MaxValue)
                    {
                        WriteProgress("promoting-index-format", hash, processed, targets.Length, mesh.name, true, false);
                        SetIndexFormatPreservingIndices(mesh, IndexFormat.UInt32);
                    }
                    WriteProgress("applying-external-uv", hash, processed, targets.Length, mesh.name, true, false);
                    if (!MeshUtility.SetPerTriangleUV2(mesh, perTriangleUV))
                        throw new InvalidOperationException("External per-triangle UV application failed: " + mesh.name + "; indexFormat=" + mesh.indexFormat);
                    if (CornerAttributesHash(mesh) != sourceCorners)
                        throw new InvalidOperationException("External UV application changed source per-corner vertex attributes: " + mesh.name);
                    MeshCache.Remove(mesh.GetInstanceID());
                    MeshData after = GetMesh(mesh);
                    ValidateEntryGeometry(entry, after);
                    if (!after.HasUV2 || after.UvArea <= 1e-18) throw new InvalidOperationException("Custom unwrap produced no valid UV2: " + mesh.name);
                    for (int corner = 0; corner < after.Triangles.Length; ++corner)
                        if (!after.UV[after.Triangles[corner]].Equals(perTriangleUV[corner]))
                            throw new InvalidOperationException("External UV application changed a per-corner UV value: " + mesh.name);
                    if (mesh.blendShapeCount != blendShapes) throw new InvalidOperationException("Unwrap changed blend shape count: " + mesh.name);
                    matchedNames.Add(mesh.name); ++processed;
                    WriteProgress("mesh-complete", hash, processed, targets.Length, mesh.name, true, false);
                }
                string[] missing = entries.Keys.Where(name => !matchedNames.Contains(name)).ToArray();
                if (missing.Length > 0) throw new InvalidOperationException("Recipe meshes were not found in the imported Bistro: " + string.Join(", ", missing));
                WriteProof("applied", hash, layoutHash, processed, targets.Length, true);
                WriteProgress("import-complete", hash, processed, targets.Length, "", false, false);
            }
            catch (Exception exception)
            {
                InvalidateProof("import-failed");
                WriteProgress("import-failed", hash, processed, targets.Length, currentMesh, false, false, exception.ToString());
                throw;
            }
            finally { ClearCaches(); }
        }

        private static void SetIndexFormatPreservingIndices(Mesh mesh, IndexFormat target)
        {
            if (mesh.indexFormat == target) return;
            if (mesh.indexFormat != IndexFormat.UInt16 || target != IndexFormat.UInt32)
                throw new NotSupportedException("Only lossless UInt16-to-UInt32 promotion is supported: " + mesh.name);
            Bounds bounds = mesh.bounds;
            int vertexCount = mesh.vertexCount;
            SubMeshDescriptor[] submeshes;
            uint[] indices;
            using (var source = MeshUtility.AcquireReadOnlyMeshData(mesh))
            {
                var data = source[0];
                var original = data.GetIndexData<ushort>();
                indices = new uint[original.Length];
                for (int i = 0; i < original.Length; ++i) indices[i] = original[i];
                submeshes = new SubMeshDescriptor[data.subMeshCount];
                for (int i = 0; i < submeshes.Length; ++i) submeshes[i] = data.GetSubMesh(i);
            }
            // indexFormat clears the index buffer and resets subMeshCount. Restore the raw
            // buffer rather than flattened triangles, preserving every baseVertex and gap.
            var flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;
            mesh.indexFormat = target;
            mesh.SetIndexBufferParams(indices.Length, target);
            mesh.SetIndexBufferData(indices, 0, 0, indices.Length, flags);
            mesh.subMeshCount = submeshes.Length;
            for (int i = 0; i < submeshes.Length; ++i) mesh.SetSubMesh(i, submeshes[i], flags);
            mesh.bounds = bounds;
            if (mesh.vertexCount != vertexCount || mesh.subMeshCount != submeshes.Length || !mesh.bounds.Equals(bounds))
                throw new InvalidOperationException("Index-format promotion changed source geometry metadata: " + mesh.name);
            using (var restored = MeshUtility.AcquireReadOnlyMeshData(mesh))
            {
                var data = restored[0];
                var actual = data.GetIndexData<uint>();
                if (actual.Length != indices.Length) throw new InvalidOperationException("Index-format promotion changed buffer length: " + mesh.name);
                for (int i = 0; i < indices.Length; ++i)
                    if (actual[i] != indices[i]) throw new InvalidOperationException("Index-format promotion changed source indices: " + mesh.name);
                for (int i = 0; i < submeshes.Length; ++i)
                {
                    SubMeshDescriptor expected = submeshes[i], value = data.GetSubMesh(i);
                    if (value.indexStart != expected.indexStart || value.indexCount != expected.indexCount || value.baseVertex != expected.baseVertex ||
                        value.topology != expected.topology || value.firstVertex != expected.firstVertex || value.vertexCount != expected.vertexCount || !value.bounds.Equals(expected.bounds))
                        throw new InvalidOperationException("Index-format promotion changed submesh " + i + ": " + mesh.name);
                }
            }
        }

        private static Recipe CreateRecipe(List<Receiver> receivers)
        {
            if (receivers.Count == 0) throw new InvalidOperationException("No visible Bistro mesh receivers found.");
            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (Receiver receiver in receivers)
            {
                MeshData mesh = receiver.Mesh;
                if (!entries.TryGetValue(mesh.Mesh.name, out Entry entry))
                {
                    Bounds bounds = mesh.Mesh.bounds;
                    Vector3 scale = AbsoluteScale(receiver.Renderer.transform.lossyScale);
                    entry = new Entry { meshName = mesh.Mesh.name, triangleCount = mesh.TriangleCount, boundsCenter = bounds.center, boundsSize = bounds.size,
                        worldSurfaceArea = receiver.Surface.Area, minimumWorldSurfaceArea = receiver.Surface.Area,
                        minimumAbsoluteScale = scale, maximumAbsoluteScale = scale, uvIslandCount = Math.Max(1, mesh.Islands) };
                    entries.Add(entry.meshName, entry);
                }
                else ValidateEntryGeometry(entry, mesh);
                entry.worldSurfaceArea = Math.Max(entry.worldSurfaceArea, receiver.Surface.Area);
                entry.minimumWorldSurfaceArea = Math.Min(entry.minimumWorldSurfaceArea, receiver.Surface.Area);
                entry.minimumAbsoluteScale = Vector3.Min(entry.minimumAbsoluteScale, AbsoluteScale(receiver.Renderer.transform.lossyScale));
                entry.maximumAbsoluteScale = Vector3.Max(entry.maximumAbsoluteScale, AbsoluteScale(receiver.Renderer.transform.lossyScale));
                entry.uvIslandCount = Math.Max(entry.uvIslandCount, mesh.Islands);
                ++entry.instanceCount;
            }
            foreach (Entry entry in entries.Values)
            {
                // New recipes describe geometry/instances only. The external packer owns
                // its square side and padding, including whether it can fit a2048 tile.
                entry.targetSide = 0;
                entry.packMargin = 0;
            }
            return new Recipe { createdUtc = DateTime.UtcNow.ToString("O"), meshes = entries.Values.OrderBy(entry => entry.meshName, StringComparer.Ordinal).ToArray() };
        }

        private static Recipe ReadRecipe()
        {
            if (!File.Exists(AbsolutePath(RecipePath))) throw new InvalidOperationException("Missing Bistro UV recipe; run Prepare: " + RecipePath);
            var file = new FileInfo(AbsolutePath(RecipePath));
            if (s_recipe != null && file.LastWriteTimeUtc.Ticks == s_recipeTicks && file.Length == s_recipeLength) return s_recipe;
            Recipe recipe = JsonUtility.FromJson<Recipe>(File.ReadAllText(AbsolutePath(RecipePath)));
            if (recipe == null || recipe.version != RecipeVersion || recipe.density != Density || recipe.guardTexels != GuardTexels || recipe.meshes == null || recipe.meshes.Length == 0)
                throw new InvalidOperationException("Unsupported or empty Bistro UV recipe: " + RecipePath);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entry entry in recipe.meshes)
            {
                if (entry == null || string.IsNullOrEmpty(entry.meshName) || !names.Add(entry.meshName) || entry.triangleCount <= 0 ||
                    !Finite(entry.boundsCenter) || !Finite(entry.boundsSize) || entry.boundsSize.x < 0 || entry.boundsSize.y < 0 || entry.boundsSize.z < 0 ||
                    !Finite(entry.minimumAbsoluteScale) || !Finite(entry.maximumAbsoluteScale) || entry.instanceCount <= 0 ||
                    !FinitePositive(entry.worldSurfaceArea) || !FinitePositive(entry.minimumWorldSurfaceArea) || entry.minimumWorldSurfaceArea > entry.worldSurfaceArea)
                    throw new InvalidOperationException("Invalid Bistro UV recipe entry: " + (entry == null ? "null" : entry.meshName));
                for (int axis = 0; axis < 3; ++axis)
                    if (entry.minimumAbsoluteScale[axis] <= 0 || entry.maximumAbsoluteScale[axis] < entry.minimumAbsoluteScale[axis])
                        throw new InvalidOperationException("Invalid instance scale range in Bistro UV recipe: " + entry.meshName);
            }
            s_recipe = recipe; s_recipeTicks = file.LastWriteTimeUtc.Ticks; s_recipeLength = file.Length;
            return recipe;
        }

        private static void ValidateRecipeScene(Recipe recipe, List<Receiver> receivers)
        {
            if (receivers.Count == 0) throw new InvalidOperationException("No visible Bistro receivers found.");
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (Receiver receiver in receivers)
            {
                Entry entry = FindEntry(recipe, receiver.Mesh.Mesh.name);
                ValidateEntryGeometry(entry, receiver.Mesh);
                ValidateInstance(entry, receiver.Renderer, receiver.Surface);
                used.Add(entry.meshName);
            }
            string[] missing = recipe.meshes.Where(entry => !used.Contains(entry.meshName)).Select(entry => entry.meshName).ToArray();
            if (missing.Length > 0) throw new InvalidOperationException("UV recipe contains meshes no longer present among visible scene receivers: " + string.Join(", ", missing));
        }

        private static void ValidateActualUV(Recipe recipe, List<Receiver> receivers)
        {
            foreach (Receiver receiver in receivers)
                Scale(FindEntry(recipe, receiver.Mesh.Mesh.name), receiver.Mesh, receiver.Surface, receiver.Renderer.name);
        }

        private static void ValidateEntryGeometry(Entry entry, MeshData mesh)
        {
            Bounds bounds = mesh.Mesh.bounds;
            if (entry.triangleCount != mesh.TriangleCount || !NearBounds(entry.boundsCenter, bounds.center, entry.boundsSize) || !NearBounds(entry.boundsSize, bounds.size, entry.boundsSize))
                throw new InvalidOperationException("Bistro UV recipe geometry mismatch (name/triangle count/bounds): " + entry.meshName);
        }

        private static void ValidateInstance(Entry entry, MeshRenderer renderer, SurfaceData surface)
        {
            if (!FinitePositive(surface.Area)) throw new InvalidOperationException("Invalid world surface area: " + renderer.name);
            const double relativeTolerance = 0.005;
            if (surface.Area < entry.minimumWorldSurfaceArea * (1 - relativeTolerance) - 1e-8 || surface.Area > entry.worldSurfaceArea * (1 + relativeTolerance) + 1e-8)
                throw new InvalidOperationException("Bistro instance surface area is outside its authored UV budget; update the recipe: " + renderer.name);
            Vector3 scale = AbsoluteScale(renderer.transform.lossyScale);
            for (int axis = 0; axis < 3; ++axis)
                if (!float.IsFinite(scale[axis]) || scale[axis] < entry.minimumAbsoluteScale[axis] * (1 - relativeTolerance) - 1e-6 || scale[axis] > entry.maximumAbsoluteScale[axis] * (1 + relativeTolerance) + 1e-6)
                    throw new InvalidOperationException("Bistro instance scale is outside its authored UV budget; update the recipe: " + renderer.name);
        }

        private static double Scale(Entry entry, MeshData mesh, SurfaceData surface, string receiverName)
        {
            if (!mesh.HasUV2 || !FinitePositive(mesh.UvArea) || !FinitePositive(surface.Area)) throw new InvalidOperationException("No usable UV2/surface area: " + receiverName);
            if (mesh.UvArea > 1.001 || mesh.UvMin.x < -1e-5 || mesh.UvMin.y < -1e-5 || mesh.UvMax.x > 1.00001 || mesh.UvMax.y > 1.00001)
                throw new InvalidOperationException("Custom UV2 must fit the unit square without summed area beyond one: " + receiverName);
            double lostRatio = surface.LostArea / surface.Area;
            if (lostRatio > MaximumLostAreaRatio)
                throw new InvalidOperationException("UV-degenerate triangles lose excessive surface area: " + receiverName + "; ratio=" + F(lostRatio));
            LayoutEntry packed = FindLayoutEntry(entry.meshName);
            double baseSide = Math.Sqrt(surface.Area * Density * Density);
            double result = packed.squareSide / baseSide;
            double actualSide = baseSide * result;
            double effectiveDensity = Math.Sqrt(mesh.UvArea * actualSide * actualSide / surface.Area);
            if (effectiveDensity < Density * 0.995)
                throw new InvalidOperationException("External UV area density is below the requested4 texels/m: " + receiverName + "; actual=" + F(effectiveDensity));
            if (!FinitePositive(result) || actualSide > AtlasSide + 1e-4)
                throw new InvalidOperationException("Bistro UV density/guard budget exceeds a2048 tile: " + receiverName + "; side=" + F(actualSide) + ". Split the mesh; the budget will not be silently reduced.");
            return result;
        }

        private static List<Receiver> Gather(Scene scene)
        {
            var result = new List<Receiver>();
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || !renderer.sharedMaterials.Any(material => material && (!material.HasProperty("_Cutoff") || material.GetFloat("_Cutoff") <= 1))) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (!filter || !filter.sharedMesh || !IsBistroMesh(filter.sharedMesh)) continue;
                if (renderer.isPartOfStaticBatch) throw new InvalidOperationException("Prepare Bistro UVs before combining static geometry: " + renderer.name);
                if (renderer.additionalVertexStreams && renderer.additionalVertexStreams.HasVertexAttribute(VertexAttribute.TexCoord1))
                    throw new InvalidOperationException("An overriding UV2 stream prevents recipe validation: " + renderer.name);
                MeshData mesh = GetMesh(filter.sharedMesh);
                result.Add(new Receiver { Renderer = renderer, Mesh = mesh, Surface = GetSurface(renderer, mesh) });
            }
            return result;
        }

        private static MeshData GetMesh(Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            if (MeshCache.TryGetValue(id, out MeshData cached) && cached.Mesh == mesh) return cached;
            var result = new MeshData { Mesh = mesh, HasUV2 = mesh.HasVertexAttribute(VertexAttribute.TexCoord1), UvMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity), UvMax = new Vector2(float.NegativeInfinity, float.NegativeInfinity) };
            var triangles = new List<int>();
            using (var array = MeshUtility.AcquireReadOnlyMeshData(mesh))
            using (var positions = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Temp))
            using (var uv = new NativeArray<Vector2>(mesh.vertexCount, Allocator.Temp))
            {
                var data = array[0]; data.GetVertices(positions);
                if (result.HasUV2) data.GetUVs(1, uv);
                result.Positions = positions.ToArray(); result.UV = uv.ToArray();
                for (int submesh = 0; submesh < data.subMeshCount; ++submesh)
                {
                    var descriptor = data.GetSubMesh(submesh);
                    if (descriptor.topology != MeshTopology.Triangles || descriptor.indexCount % 3 != 0)
                        throw new InvalidOperationException("Bistro UV budgeting requires triangle submeshes: " + mesh.name);
                    using (var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp))
                    {
                        data.GetIndices(indices, submesh, true); // Includes baseVertex, including UInt16 sources.
                        for (int i = 0; i < indices.Length; ++i)
                        {
                            int vertex = indices[i];
                            if ((uint)vertex >= (uint)positions.Length) throw new InvalidOperationException("Invalid triangle index: " + mesh.name);
                            triangles.Add(vertex);
                        }
                    }
                }
            }
            result.Triangles = triangles.ToArray(); result.ZeroUvArea = new bool[result.TriangleCount];
            for (int i = 0; i < result.Triangles.Length; i += 3)
            {
                int ia = result.Triangles[i], ib = result.Triangles[i + 1], ic = result.Triangles[i + 2];
                if (!Finite(result.Positions[ia]) || !Finite(result.Positions[ib]) || !Finite(result.Positions[ic])) throw new InvalidOperationException("Nonfinite Bistro geometry: " + mesh.name);
                if (!result.HasUV2) { result.ZeroUvArea[i / 3] = true; continue; }
                Vector2 a = result.UV[ia], b = result.UV[ib], c = result.UV[ic];
                if (!Finite(a) || !Finite(b) || !Finite(c)) throw new InvalidOperationException("Nonfinite Bistro UV2: " + mesh.name);
                Vector2 e1 = b - a, e2 = c - a;
                double area = Math.Abs((double)e1.x * e2.y - (double)e1.y * e2.x) * 0.5;
                result.UvArea += area; result.ZeroUvArea[i / 3] = area <= 1e-18;
                result.UvMin = Vector2.Min(result.UvMin, Vector2.Min(a, Vector2.Min(b, c)));
                result.UvMax = Vector2.Max(result.UvMax, Vector2.Max(a, Vector2.Max(b, c)));
            }
            result.Islands = result.HasUV2 ? CountIslands(result) : 0;
            MeshCache[id] = result;
            return result;
        }

        private static SurfaceData GetSurface(MeshRenderer renderer, MeshData mesh)
        {
            int id = renderer.GetInstanceID(); Matrix4x4 matrix = renderer.localToWorldMatrix;
            if (SurfaceCache.TryGetValue(id, out SurfaceData cached) && cached.Mesh == mesh && cached.Matrix.Equals(matrix)) return cached;
            var result = new SurfaceData { Mesh = mesh, Matrix = matrix };
            for (int i = 0; i < mesh.Triangles.Length; i += 3)
            {
                Vector3 a = mesh.Positions[mesh.Triangles[i]], b = mesh.Positions[mesh.Triangles[i + 1]], c = mesh.Positions[mesh.Triangles[i + 2]];
                Vector3 e1 = matrix.MultiplyVector(b - a), e2 = matrix.MultiplyVector(c - a);
                double area = Vector3.Cross(e1, e2).magnitude * 0.5;
                if (double.IsNaN(area) || double.IsInfinity(area)) throw new InvalidOperationException("Nonfinite world triangle area: " + renderer.name);
                result.Area += area;
                if (mesh.ZeroUvArea[i / 3] && area > 1e-12) { result.LostArea += area; ++result.LostTriangles; }
            }
            SurfaceCache[id] = result;
            return result;
        }

        // Expected O(V+T): weld exact position/UV duplicates, then join triangles by shared edges.
        private static int CountIslands(MeshData mesh)
        {
            var welded = new int[mesh.Positions.Length];
            var vertices = new Dictionary<VertexUvKey, int>();
            for (int i = 0; i < welded.Length; ++i)
            {
                var key = new VertexUvKey { Position = mesh.Positions[i], UV = mesh.UV[i] };
                if (!vertices.TryGetValue(key, out int index)) { index = vertices.Count; vertices.Add(key, index); }
                welded[i] = index;
            }
            var parents = new int[mesh.TriangleCount]; var ranks = new byte[parents.Length];
            for (int i = 0; i < parents.Length; ++i) parents[i] = i;
            var edges = new Dictionary<EdgeKey, int>();
            int components = parents.Length;
            for (int triangle = 0; triangle < parents.Length; ++triangle)
            {
                int offset = triangle * 3;
                for (int edge = 0; edge < 3; ++edge)
                {
                    int a = welded[mesh.Triangles[offset + edge]], b = welded[mesh.Triangles[offset + (edge + 1) % 3]];
                    if (a == b) continue;
                    var key = new EdgeKey(a, b);
                    if (!edges.TryGetValue(key, out int other)) { edges.Add(key, triangle); continue; }
                    int ra = Find(parents, triangle), rb = Find(parents, other);
                    if (ra == rb) continue;
                    if (ranks[ra] < ranks[rb]) parents[ra] = rb;
                    else { parents[rb] = ra; if (ranks[ra] == ranks[rb]) ++ranks[ra]; }
                    --components;
                }
            }
            return components;
        }

        private static int Find(int[] parents, int index)
        {
            while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; }
            return index;
        }

        private static Entry FindEntry(Recipe recipe, string name)
        {
            Entry entry = Array.Find(recipe.meshes, value => value.meshName == name);
            if (entry == null) throw new InvalidOperationException("Mesh is not covered by the Bistro UV recipe: " + name);
            return entry;
        }
        private static bool IsBistroMesh(Mesh mesh) => string.Equals(AssetDatabase.GetAssetPath(mesh), ModelPath, StringComparison.OrdinalIgnoreCase);
        internal static string AbsolutePath(string projectRelativePath) =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath), projectRelativePath.Replace('/', Path.DirectorySeparatorChar));
        private static Vector3 AbsoluteScale(Vector3 scale) => new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool Finite(Vector2 value) => float.IsFinite(value.x) && float.IsFinite(value.y);
        private static bool FinitePositive(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
        private static bool NearBounds(Vector3 expected, Vector3 actual, Vector3 size)
        {
            for (int i = 0; i < 3; ++i) if (!float.IsFinite(actual[i]) || Math.Abs(actual[i] - expected[i]) > Math.Max(1e-4, Math.Abs(size[i]) * 1e-5)) return false;
            return true;
        }
        private static void RequireScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || scene.path != ScenePath) throw new InvalidOperationException("Open the saved BistroTest scene before preparing its UV recipe.");
        }
        private static void ClearCaches() { MeshCache.Clear(); SurfaceCache.Clear(); }
        private static void ScheduleCacheRelease()
        {
            // One synchronous Prepare/BuildReport/GetScale batch shares mesh data. Release
            // the large editor-only arrays after callers finish this editor update.
            if (s_cacheReleaseScheduled) return;
            s_cacheReleaseScheduled = true;
            EditorApplication.update += ReleaseCaches;
        }
        private static void ReleaseCaches()
        {
            EditorApplication.update -= ReleaseCaches;
            s_cacheReleaseScheduled = false;
            ClearCaches();
        }
        private static string F(double value) => value.ToString("G12", Invariant);
        private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        private static string RecipeHash()
        {
            // Hash the persisted source bytes. Re-serializing float values after a
            // JsonUtility round trip need not reproduce their original spelling.
            var file = new FileInfo(AbsolutePath(RecipePath));
            if (s_recipeByteHash != null && s_recipeHashTicks == file.LastWriteTimeUtc.Ticks && s_recipeHashLength == file.Length) return s_recipeByteHash;
            s_recipeByteHash = Sha256(File.ReadAllBytes(file.FullName));
            s_recipeHashTicks = file.LastWriteTimeUtc.Ticks; s_recipeHashLength = file.Length;
            return s_recipeByteHash;
        }
        private static string SetOwnedMarker(string userData, string marker)
        {
            return SetOwnedLine(userData, MarkerPrefix, marker);
        }
        private static string SetOwnedLine(string userData, string prefix, string marker)
        {
            userData = userData ?? "";
            string pattern = @"(?m)^" + Regex.Escape(prefix) + @"[^\r\n]*";
            if (Regex.IsMatch(userData, pattern)) return Regex.Replace(userData, pattern, marker);
            return userData + (userData.Length > 0 && !userData.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "") + marker;
        }
        private static bool TryMarkerHash(string userData, out string hash)
        {
            return TryOwnedHash(userData, MarkerPrefix, out hash);
        }
        private static bool TryOwnedHash(string userData, string prefix, out string hash)
        {
            Match match = Regex.Match(userData ?? "", @"(?m)^" + Regex.Escape(prefix) + @"v1:([0-9a-f]{64})\r?$");
            hash = match.Success ? match.Groups[1].Value : null;
            return match.Success;
        }
        private static void WriteProgress(string status, string hash, int processed, int target, string mesh, bool importing, bool prepared, params string[] errors)
        {
            Directory.CreateDirectory(AbsolutePath(ReportFolder));
            File.WriteAllText(AbsolutePath(ProgressPath), JsonUtility.ToJson(new Progress { utc = DateTime.UtcNow.ToString("O"), status = status, recipeHash = hash, layoutHash = s_layoutHash ?? "",
                processedMeshCount = processed, targetMeshCount = target, currentMesh = mesh, importing = importing, prepared = prepared, errors = errors ?? Array.Empty<string>() }, true), new UTF8Encoding(false));
        }
        private static Progress ReadProgress() => File.Exists(AbsolutePath(ProgressPath)) ? JsonUtility.FromJson<Progress>(File.ReadAllText(AbsolutePath(ProgressPath))) : null;

        internal static void TraceImportCallback(string phase, string assetPath, ModelImporter importer)
        {
            bool markerMatched = TryMarkerHash(importer.userData, out string markerHash);
            bool recipeExists = File.Exists(AbsolutePath(RecipePath));
            bool recipeHashMatched = false;
            string recipeError = "";
            if (recipeExists)
            {
                try { recipeHashMatched = markerMatched && RecipeHash() == markerHash; }
                catch (Exception exception) { recipeError = exception.Message.Replace('\r', ' ').Replace('\n', ' '); }
            }
            Directory.CreateDirectory(AbsolutePath(ReportFolder));
            // Deliberately excludes command-line arguments and the rest of importer.userData.
            File.AppendAllText(AbsolutePath(ReportFolder + "/uv_import_callback.txt"),
                "utc=" + DateTime.UtcNow.ToString("O") + "; phase=" + phase + "; assetPath=" + assetPath +
                "; markerMatched=" + markerMatched + "; markerHash=" + (markerHash ?? "none") +
                "; recipeExists=" + recipeExists + "; recipeHashMatched=" + recipeHashMatched +
                "; isWorker=" + AssetDatabase.IsAssetImportWorkerProcess() + "; currentDirectory=" + Directory.GetCurrentDirectory() +
                "; projectRoot=" + Path.GetDirectoryName(Application.dataPath) + "; recipeError=" + recipeError + "\n", new UTF8Encoding(false));
        }
    }

    internal sealed class MaxBistroLightmapUVPostprocessor : AssetPostprocessor
    {
        public override int GetPostprocessOrder() => 1000;
        public override uint GetVersion() => MaxBistroLightmapUV.PostprocessorVersion;
        private void OnPreprocessModel()
        {
            if (!string.Equals(assetPath, MaxBistroLightmapUV.ModelPath, StringComparison.OrdinalIgnoreCase)) return;
            MaxBistroLightmapUV.TraceImportCallback("OnPreprocessModel", assetPath, (ModelImporter)assetImporter);
            if (File.Exists(MaxBistroLightmapUV.AbsolutePath(MaxBistroLightmapUV.RecipePath)))
                context.DependsOnSourceAsset(MaxBistroLightmapUV.RecipePath);
            // A source capture deliberately bypasses the external layout so it can be
            // regenerated even if that layout is missing or incompatible.
            if (!Regex.IsMatch(assetImporter.userData ?? "", @"(?m)^MaxSRP\.BistroLightmapUVSourceCapture="))
                foreach (string dependency in MaxBistroLightmapUV.ExternalDependencyPaths())
                    if (File.Exists(MaxBistroLightmapUV.AbsolutePath(dependency))) context.DependsOnSourceAsset(dependency);
        }
        private void OnPostprocessModel(GameObject root)
        {
            if (string.Equals(assetPath, MaxBistroLightmapUV.ModelPath, StringComparison.OrdinalIgnoreCase))
            {
                MaxBistroLightmapUV.TraceImportCallback("OnPostprocessModel", assetPath, (ModelImporter)assetImporter);
                MaxBistroLightmapUV.ProcessImportedModel(root, (ModelImporter)assetImporter);
            }
        }
    }
}
