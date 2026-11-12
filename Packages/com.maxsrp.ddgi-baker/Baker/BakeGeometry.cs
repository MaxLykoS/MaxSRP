using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MaxSRP.DDGIBaker.Editor
{
    internal sealed class BakeGeometry
    {
        private readonly List<Vector3> positions = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<int> indices = new List<int>();
        private readonly List<Vector2> uv0 = new List<Vector2>();
        private readonly List<BakeMaterial> surfaces = new List<BakeMaterial>();
        private readonly Dictionary<Material, BakeMaterial> surfaceByMaterial = new Dictionary<Material, BakeMaterial>();
        private int materialIndexCount;

        internal int VertexCount => positions.Count;
        internal int TriangleCount => (indices.Count + materialIndexCount) / 3;
        internal int MaterialCount => surfaces.Count;
        internal DdgiBakeMaterialMode MaterialMode { get; private set; }
        internal Bounds Bounds { get; private set; }
        internal string Source { get; private set; }

        internal static BakeGeometry Read(string path)
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "NGMESH1\0")
                throw new InvalidDataException("Invalid NGMESH1 header.");
            int vertices = reader.ReadInt32();
            int indexCount = reader.ReadInt32();
            if (vertices <= 0 || indexCount <= 0 || indexCount % 3 != 0 ||
                reader.BaseStream.Length != 16L + vertices * 24L + indexCount * 4L)
                throw new InvalidDataException("Invalid NGMESH1 size.");
            var result = new BakeGeometry { Source = path, MaterialMode = DdgiBakeMaterialMode.GrayNativeParity };
            result.positions.Capacity = vertices;
            result.normals.Capacity = vertices;
            result.indices.Capacity = indexCount;
            for (int i = 0; i < vertices; i++)
                result.AddVertex(ReadVector(reader), ReadVector(reader));
            for (int i = 0; i < indexCount; i++)
            {
                int index = reader.ReadInt32();
                if ((uint)index >= vertices)
                    throw new InvalidDataException("NGMESH1 index out of range.");
                result.indices.Add(index);
            }
            return result;
        }

        internal static BakeGeometry FromScene(Scene scene, DdgiBakeMaterialMode mode = DdgiBakeMaterialMode.SceneMaterials)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException("Open a scene with static meshes to bake.");
            if (mode == DdgiBakeMaterialMode.SceneMaterials && QualitySettings.activeColorSpace != ColorSpace.Linear)
                throw new InvalidOperationException("Scene material baking requires a Linear color space project.");
            var result = new BakeGeometry
            {
                Source = string.IsNullOrEmpty(scene.path) ? scene.name : scene.path,
                MaterialMode = mode
            };
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
                {
                    if (!renderer.enabled || !renderer.gameObject.isStatic ||
                        !renderer.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh)
                        continue;
                    result.Append(filter.sharedMesh, renderer);
                }
            if (result.TriangleCount == 0)
                throw new InvalidOperationException("No enabled static triangle MeshRenderers in the active scene.");
            return result;
        }

        internal void RequireMaterialMode(DdgiBakeMaterialMode mode)
        {
            if (MaterialMode != mode)
                throw new InvalidOperationException("Scene Materials requires a scene with material and UV data. Use Gray Native Parity explicitly for NGMESH files.");
        }

        internal Mesh CreateMesh()
        {
            var mesh = new Mesh { name = "DDGI bake geometry", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            try
            {
                mesh.SetVertices(positions);
                mesh.SetNormals(normals);
                if (MaterialMode == DdgiBakeMaterialMode.SceneMaterials)
                {
                    mesh.SetUVs(0, uv0);
                    mesh.subMeshCount = surfaces.Count;
                    for (int i = 0; i < surfaces.Count; i++)
                        mesh.SetTriangles(surfaces[i].Indices, i, false);
                }
                else
                {
                    mesh.SetTriangles(indices, 0, false);
                }
                mesh.bounds = Bounds;
                return mesh;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                throw;
            }
        }

        internal Material[] CreateTraceMaterials(Shader surface, out RayTracingSubMeshFlags[] flags)
        {
            if (MaterialMode == DdgiBakeMaterialMode.GrayNativeParity)
            {
                flags = new[] { RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly };
                return new[] { new Material(surface) { hideFlags = HideFlags.HideAndDontSave } };
            }

            flags = new RayTracingSubMeshFlags[surfaces.Count];
            var materials = new Material[surfaces.Count];
            try
            {
                for (int i = 0; i < materials.Length; i++)
                {
                    BakeMaterial source = surfaces[i];
                    materials[i] = new Material(surface) { name = source.Name, hideFlags = HideFlags.HideAndDontSave };
                    materials[i].SetTexture("_BakeAlbedo", source.Albedo);
                    materials[i].SetTexture("_BakeMetalness", source.Metalness);
                    materials[i].SetTexture("_BakeRoughness", source.Roughness);
                    materials[i].SetVector("_BakeUvScaleOffset", source.UvScaleOffset);
                    materials[i].SetVector("_BakeMaterialParams",
                        new Vector4(source.Cutoff, source.UsesBistroMask ? 1f : 0f, source.TwoSided ? 1f : 0f, 1f));
                    flags[i] = RayTracingSubMeshFlags.Enabled;
                    if (source.Cutoff == 0f)
                        flags[i] |= RayTracingSubMeshFlags.ClosestHitOnly;
                }
                return materials;
            }
            catch
            {
                foreach (var material in materials)
                    if (material)
                        Object.DestroyImmediate(material);
                throw;
            }
        }

        private void Append(Mesh mesh, MeshRenderer renderer)
        {
            Matrix4x4 transform = renderer.localToWorldMatrix;
            Matrix4x4 normalMatrix = transform.inverse.transpose;
            bool mirrored = transform.determinant < 0f;
            using var data = MeshUtility.AcquireReadOnlyMeshData(mesh);
            Mesh.MeshData meshData = data[0];
            bool colored = MaterialMode == DdgiBakeMaterialMode.SceneMaterials;
            using var vertexPositions = new NativeArray<Vector3>(meshData.vertexCount, Allocator.Temp);
            using var vertexNormals = new NativeArray<Vector3>(meshData.vertexCount, Allocator.Temp);
            using var vertexUv = new NativeArray<Vector2>(colored ? meshData.vertexCount : 0, Allocator.Temp);
            meshData.GetVertices(vertexPositions);
            bool hasNormals = meshData.HasVertexAttribute(VertexAttribute.Normal);
            if (hasNormals)
                meshData.GetNormals(vertexNormals);
            NativeArray<Vector3> writableNormals = vertexNormals;
            Material[] materials = renderer.sharedMaterials;
            if (colored)
            {
                if (materials.Length != meshData.subMeshCount || !meshData.HasVertexAttribute(VertexAttribute.TexCoord0))
                    throw new InvalidDataException($"{renderer.name} needs UV0 and exactly one material per submesh for the DDGI bake.");
                if (renderer.HasPropertyBlock() || renderer.additionalVertexStreams)
                    throw new InvalidDataException($"{renderer.name} uses per-renderer material or vertex overrides unsupported by the DDGI bake.");
                meshData.GetUVs(0, vertexUv);
            }
            int startVertex = positions.Count;
            int startIndex = indices.Count + materialIndexCount;
            for (int submesh = 0; submesh < meshData.subMeshCount; submesh++)
            {
                var descriptor = meshData.GetSubMesh(submesh);
                if (descriptor.topology != MeshTopology.Triangles)
                    continue;
                Material material = submesh < materials.Length ? materials[submesh] : null;
                if (material && material.HasProperty("_Cutoff") && material.GetFloat("_Cutoff") > 1f)
                    continue;
                if (descriptor.indexCount == 0)
                    continue;
                List<int> destination = colored ? SurfaceFor(material, renderer, submesh).Indices : indices;
                using var subIndices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp);
                meshData.GetIndices(subIndices, submesh, true);
                for (int i = 0; i < subIndices.Length; i += 3)
                {
                    int a = subIndices[i], b = subIndices[i + 1], c = subIndices[i + 2];
                    if (!hasNormals)
                    {
                        Vector3 face = Vector3.Cross(vertexPositions[b] - vertexPositions[a], vertexPositions[c] - vertexPositions[a]);
                        writableNormals[a] += face;
                        writableNormals[b] += face;
                        writableNormals[c] += face;
                    }
                    destination.Add(startVertex + a);
                    destination.Add(startVertex + (mirrored ? c : b));
                    destination.Add(startVertex + (mirrored ? b : c));
                }
                if (colored)
                    materialIndexCount += subIndices.Length;
            }
            if (indices.Count + materialIndexCount == startIndex)
                return;
            for (int i = 0; i < vertexPositions.Length; i++)
            {
                AddVertex(transform.MultiplyPoint3x4(vertexPositions[i]), normalMatrix.MultiplyVector(vertexNormals[i]).normalized);
                if (colored)
                {
                    Vector2 uv = vertexUv[i];
                    if (!DdgiBakeSettings.Finite(uv.x) || !DdgiBakeSettings.Finite(uv.y))
                        throw new InvalidDataException($"{renderer.name} has non-finite UV0.");
                    uv0.Add(uv);
                }
            }
        }

        private BakeMaterial SurfaceFor(Material material, Renderer renderer, int submesh)
        {
            if (!material || !material.shader)
                throw new InvalidDataException($"{renderer.name} submesh {submesh} has no bake material.");
            if (surfaceByMaterial.TryGetValue(material, out var result))
                return result;

            bool bistro = material.shader.name == "MaxSRP/Bistro GBuffer";
            if (!bistro && material.shader.name != "Unlit/PBRGBuffer")
                throw new NotSupportedException($"{renderer.name} material {material.name} uses unsupported shader {material.shader.name}.");
            string albedoName = bistro ? "_BaseColorMap" : "_AlbedoMap";
            Vector2 scale = material.GetTextureScale(albedoName);
            Vector2 offset = material.GetTextureOffset(albedoName);
            float cutoff = bistro ? material.GetFloat("_Cutoff") : 0f;
            float cull = bistro ? material.GetFloat("_Cull") : (float)CullMode.Back;
            if (!DdgiBakeSettings.Finite(cutoff) || !DdgiBakeSettings.Finite(scale.x) || !DdgiBakeSettings.Finite(scale.y) ||
                !DdgiBakeSettings.Finite(offset.x) || !DdgiBakeSettings.Finite(offset.y) ||
                (cull != (float)CullMode.Off && cull != (float)CullMode.Back))
                throw new NotSupportedException($"{material.name} has unsupported bake UV, cutoff or culling settings.");

            result = new BakeMaterial
            {
                Name = material.name + " (DDGI)",
                Albedo = ReadTexture(material, albedoName, Texture2D.whiteTexture),
                Metalness = ReadTexture(material, bistro ? "_MaskMap" : "_MetalnessMap",
                    bistro ? Texture2D.whiteTexture : Texture2D.blackTexture),
                Roughness = bistro ? Texture2D.blackTexture : ReadTexture(material, "_RoughnessMap", Texture2D.blackTexture),
                UvScaleOffset = new Vector4(scale.x, scale.y, offset.x, offset.y),
                Cutoff = Mathf.Max(0f, cutoff),
                UsesBistroMask = bistro,
                TwoSided = cull == (float)CullMode.Off
            };
            surfaces.Add(result);
            surfaceByMaterial.Add(material, result);
            return result;
        }

        private static Texture2D ReadTexture(Material material, string property, Texture2D fallback)
        {
            Texture texture = material.GetTexture(property);
            if (!texture) return fallback;
            if (texture is Texture2D texture2D) return texture2D;
            throw new NotSupportedException($"{material.name} {property} must use a 2D texture for the DDGI bake.");
        }

        private sealed class BakeMaterial
        {
            internal readonly List<int> Indices = new List<int>();
            internal string Name;
            internal Texture2D Albedo, Metalness, Roughness;
            internal Vector4 UvScaleOffset;
            internal float Cutoff;
            internal bool UsesBistroMask, TwoSided;
        }

        private void AddVertex(Vector3 position, Vector3 normal)
        {
            if (!Finite(position) || !Finite(normal))
                throw new InvalidDataException("Non-finite bake geometry vertex.");
            Bounds bounds = Bounds;
            if (positions.Count == 0)
                bounds = new Bounds(position, Vector3.zero);
            else
                bounds.Encapsulate(position);
            Bounds = bounds;
            positions.Add(position);
            normals.Add(normal);
        }

        private static bool Finite(Vector3 value) =>
            DdgiBakeSettings.Finite(value.x) && DdgiBakeSettings.Finite(value.y) && DdgiBakeSettings.Finite(value.z);

        private static Vector3 ReadVector(BinaryReader reader) =>
            new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
