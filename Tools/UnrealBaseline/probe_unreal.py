"""Inspect the installed UE Python API before building the reference level."""
import json
from pathlib import Path
import unreal

out = Path(unreal.Paths.project_saved_dir()) / "MaxSRPReference"
out.mkdir(parents=True, exist_ok=True)
names = ["FbxImportUI", "FbxStaticMeshImportData", "SkyAtmosphereComponent", "DirectionalLightComponent",
         "SkyLightComponent", "PostProcessSettings", "CameraComponent", "TextureFactory", "TextureImportSettings",
         "MaterialExpressionSkyAtmosphereViewLuminance", "MaterialExpressionSkyAtmosphereLightDiskLuminance",
         "MaterialExpressionSkyAtmosphereAerialPerspective", "MaterialExpressionTextureSampleParameterCube",
         "MaterialExpressionTextureCoordinate", "MaterialEditingLibrary", "EditorLevelLibrary",
         "EditorActorSubsystem", "StaticMeshEditorSubsystem", "MaterialExpressionMultiply"]
data = {}
for name in names:
    cls = getattr(unreal, name, None)
    data[name] = None if cls is None else {"doc": cls.__doc__, "members": dir(cls)}
(out / "python_api.json").write_text(json.dumps(data, indent=2), encoding="utf-8")
unreal.log("MAXSRP_PROBE_SUCCESS " + str(out))
