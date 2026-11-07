using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace MaxSRP.DDGIBaker.Editor
{
    internal sealed class BakeGeometry
    {
        private readonly List<Vector3> positions = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<int> indices = new List<int>();

        internal int VertexCount => positions.Count;
        internal int TriangleCount => indices.Count / 3;
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
            var result = new BakeGeometry { Source = path };
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

        internal static BakeGeometry FromScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException("Open a scene with static meshes to bake.");
            var result = new BakeGeometry { Source = string.IsNullOrEmpty(scene.path) ? scene.name : scene.path };
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

        internal Mesh CreateMesh()
        {
            var mesh = new Mesh { name = "DDGI bake geometry", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            try
            {
                mesh.SetVertices(positions);
                mesh.SetNormals(normals);
                mesh.SetTriangles(indices, 0, false);
                mesh.bounds = Bounds;
                return mesh;
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(mesh);
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
            using var vertexPositions = new NativeArray<Vector3>(meshData.vertexCount, Allocator.Temp);
            using var vertexNormals = new NativeArray<Vector3>(meshData.vertexCount, Allocator.Temp);
            meshData.GetVertices(vertexPositions);
            bool hasNormals = meshData.HasVertexAttribute(VertexAttribute.Normal);
            if (hasNormals)
                meshData.GetNormals(vertexNormals);
            NativeArray<Vector3> writableNormals = vertexNormals;
            Material[] materials = renderer.sharedMaterials;
            int startVertex = positions.Count;
            int startIndex = indices.Count;
            for (int submesh = 0; submesh < meshData.subMeshCount; submesh++)
            {
                var descriptor = meshData.GetSubMesh(submesh);
                if (descriptor.topology != MeshTopology.Triangles)
                    continue;
                Material material = submesh < materials.Length ? materials[submesh] : null;
                if (material && material.HasProperty("_Cutoff") && material.GetFloat("_Cutoff") > 1f)
                    continue;
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
                    indices.Add(startVertex + a);
                    indices.Add(startVertex + (mirrored ? c : b));
                    indices.Add(startVertex + (mirrored ? b : c));
                }
            }
            if (indices.Count == startIndex)
                return;
            for (int i = 0; i < vertexPositions.Length; i++)
                AddVertex(transform.MultiplyPoint3x4(vertexPositions[i]), normalMatrix.MultiplyVector(vertexNormals[i]).normalized);
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
