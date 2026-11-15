using UnityEditor;
using UnityEngine;

namespace MaxSRP.EditorTools
{
    // Restricted to the locally restored ORCA package. All settings persist in metas.
    internal sealed class MaxBistroAssetImporter : AssetPostprocessor
    {
        private bool IsBistro => assetPath.StartsWith("Assets/ThirdParty/Bistro/");

        private void OnPreprocessModel()
        {
            if (!IsBistro) return;
            var model = (ModelImporter)assetImporter;
            model.importAnimation = false;
            model.importCameras = true;
            model.importLights = false; // The FBX only contains a legacy, uncalibrated sun.
            model.globalScale = 1;
            model.useFileScale = true; // FBX UnitScaleFactor=100: source units are metres.
            model.importNormals = ModelImporterNormals.Import;
            model.importTangents = ModelImporterTangents.CalculateMikk;
            model.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            model.materialLocation = ModelImporterMaterialLocation.InPrefab;
            model.isReadable = false;
            model.addCollider = false;
        }

        private void OnPreprocessTexture()
        {
            if (!IsBistro || !assetPath.EndsWith(".png")) return;
            var texture = (TextureImporter)assetImporter;
            bool normal = assetPath.EndsWith("_Normal.png");
            bool mask = assetPath.EndsWith("_Specular.png");
            texture.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            texture.sRGBTexture = !normal && !mask;
            texture.flipGreenChannel = normal; // ORCA ships DirectX tangent-space normals.
            texture.mipmapEnabled = true;
            texture.streamingMipmaps = false;
            texture.maxTextureSize = 2048;
            texture.isReadable = false;
            texture.alphaIsTransparency = false;
            texture.wrapMode = TextureWrapMode.Repeat;
            var desktop = texture.GetPlatformTextureSettings("Standalone");
            desktop.overridden = true;
            desktop.maxTextureSize = 2048;
            desktop.format = normal ? TextureImporterFormat.BC5 : TextureImporterFormat.BC7;
            desktop.compressionQuality = 100;
            texture.SetPlatformTextureSettings(desktop);
        }
    }
}
