using System;
using System.IO;
using UnityEngine;

namespace MaxTerrain
{
    /// <summary>
    /// 本地烘焙结果的入口。制作场景可以继续编辑；运行场景只读此处保存的固定
    /// 描述和同一配方的 SVT。它不是 AssetBundle，也不负责热更新或资源下载。
    /// </summary>
    [CreateAssetMenu(menuName = "DirectDelta Mesh/Terrain Publication", fileName = "TerrainPublication")]
    public sealed class MeshTerrainPublication : ScriptableObject
    {
        public MeshWorldVtOverlaySet overlays;
        public MeshMaterialSet surfaceMaterials;
        public string datasetRelativeDirectory, controlRelativeDirectory, archiveRelativeDirectory;
        public bool hexTiling = true;
        public string versionId, recipeIdentity, overlayContentHash;
        public string rawPayloadSha256, bc3PayloadSha256;
        public string authoringScenePath;

        // 这里只检查小配置；大文件和 GPU 配方校验仍由已有的源读取器与 VT archive 执行。
        public bool IsValid => overlays && surfaceMaterials && !string.IsNullOrEmpty(versionId) &&
            Hash(recipeIdentity) && Hash(overlayContentHash) && Hash(rawPayloadSha256) && Hash(bc3PayloadSha256) &&
            !string.IsNullOrEmpty(datasetRelativeDirectory) && !string.IsNullOrEmpty(controlRelativeDirectory) &&
            !string.IsNullOrEmpty(archiveRelativeDirectory);

        public void Validate()
        {
            if (!IsValid) throw new InvalidDataException("The local terrain publication is incomplete. Bake from its authoring scene.");
            MeshMaterialSet.SafeRelative(datasetRelativeDirectory);
            MeshMaterialSet.SafeRelative(controlRelativeDirectory);
            MeshMaterialSet.SafeRelative(archiveRelativeDirectory);
            surfaceMaterials.Validate();
            using (var snapshot = overlays.CreateSnapshot())
                if (snapshot.ContentHash != overlayContentHash)
                    throw new InvalidDataException("Published static overlay data changed. Republish from the authoring scene.");
            string directory = Path.Combine(Application.streamingAssetsPath, archiveRelativeDirectory);
            CheckManifest(directory, false, rawPayloadSha256);
            CheckManifest(Path.Combine(directory, MeshWorldVtArchive.Bc3Subdirectory), true, bc3PayloadSha256);
        }

        void CheckManifest(string directory, bool compressed, string expectedPayload)
        {
            string path = Path.Combine(directory, MeshWorldVtArchive.ManifestName);
            var manifest = JsonUtility.FromJson<MeshWorldVtArchive.Manifest>(File.ReadAllText(path));
            if (manifest == null || manifest.recipe != recipeIdentity || manifest.payloadSha256 != expectedPayload ||
                manifest.version != (compressed ? MeshWorldVtArchive.Bc3FormatVersion : MeshWorldVtArchive.FormatVersion))
                throw new InvalidDataException("The local publication and SVT manifest are from different bakes: " + path);
            var payload = new FileInfo(Path.Combine(directory, MeshWorldVtArchive.PayloadName(expectedPayload, compressed)));
            if (manifest.pages == null || !payload.Exists || payload.Length != (long)manifest.pages.Length * manifest.bytesPerPage)
                throw new InvalidDataException("The published SVT page payload is missing or truncated: " + payload.FullName);
        }

        static bool Hash(string text)
        {
            if (text == null || text.Length != 64) return false;
            foreach (char c in text) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
    }
}
