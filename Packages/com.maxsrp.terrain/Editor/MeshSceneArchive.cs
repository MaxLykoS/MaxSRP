using System;
using System.IO;
using UnityEditor;

namespace MaxTerrain.Editor
{
    /// <summary>
    /// 历史场景放在 Assets 之外，保留原始 YAML/meta 与 GUID。
    /// 当前制作场景可从归档模板创建；只有显式打开旧检查点时才恢复旧资产。
    /// </summary>
    public static class MeshSceneArchive
    {
        public const string RelativeRoot = "Docs/Archive/Scenes";

        static string ArchivedPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                !assetPath.EndsWith(".unity", StringComparison.Ordinal) || assetPath.Contains("..") || assetPath.Contains("\\"))
                throw new ArgumentException("Expected a project-relative Assets/*.unity path.", nameof(assetPath));
            return Path.GetFullPath(Path.Combine(RelativeRoot, assetPath));
        }

        /// <summary>新场景使用新 GUID；不复制历史 meta，不将模板恢复到 Assets。</summary>
        public static void CopyTemplate(string sourceScene, string destinationScene)
        {
            ArchivedPath(destinationScene); // 同样校验目标路径。
            if (File.Exists(destinationScene)) throw new IOException("The destination scene already exists: " + destinationScene);
            if (File.Exists(sourceScene))
            {
                if (!AssetDatabase.CopyAsset(sourceScene, destinationScene))
                    throw new IOException("Could not copy scene template: " + sourceScene);
                return;
            }
            string archived = ArchivedPath(sourceScene);
            if (!File.Exists(archived)) throw new FileNotFoundException("The scene template is missing from its archive.", archived);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationScene));
            File.Copy(archived, destinationScene);
            AssetDatabase.ImportAsset(destinationScene, ImportAssetOptions.ForceSynchronousImport);
        }

        /// <summary>旧入口按需恢复原场景和原 GUID；日常 Workflow 不调用此方法。</summary>
        public static void Restore(string assetPath)
        {
            string archived = ArchivedPath(assetPath);
            if (File.Exists(assetPath)) return;
            if (!File.Exists(archived) || !File.Exists(archived + ".meta"))
                throw new FileNotFoundException("The historical scene and its meta are not in the archive.", archived);
            if (File.Exists(assetPath + ".meta"))
                throw new IOException("A scene meta already exists at the restore path: " + assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath));
            File.Copy(archived, assetPath);
            File.Copy(archived + ".meta", assetPath + ".meta");
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }
    }
}
