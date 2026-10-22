"""Open/pilot the saved reference without rebuilding or modifying scene assets."""
import json
from pathlib import Path
import unreal as u

level = u.get_editor_subsystem(u.LevelEditorSubsystem)
world = u.EditorLevelLibrary.get_editor_world()
if world is None or world.get_name() != "MaxSRP_Mobile":
    level.load_level("/Game/MaxSRP_Reference/Maps/MaxSRP_Mobile")
actors = u.get_editor_subsystem(u.EditorActorSubsystem)
camera = next(a for a in actors.get_all_level_actors() if a.get_actor_label() == "MaxSRP Comparison Camera")
level.pilot_level_actor(camera)
level.editor_set_game_view(True)
u.SystemLibrary.execute_console_command(u.EditorLevelLibrary.get_editor_world(),"r.SetNearClipPlane 30")
out = Path(u.Paths.project_saved_dir()) / "MaxSRPReference"
cvars = ["r.Mobile.ShadingPath", "r.MobileHDR", "r.Mobile.AntiAliasing", "r.Shadow.CSM.MaxCascades",
         "r.Shadow.MaxCSMResolution", "r.Shadow.MaxResolution", "r.Shadow.Virtual.Enable", "r.AllowStaticLighting",
         "r.DynamicGlobalIlluminationMethod", "r.ReflectionMethod"]
data = {"project": u.Paths.get_project_file_path(), "world": u.EditorLevelLibrary.get_editor_world().get_path_name(),
        "camera_location": str(camera.get_actor_location()), "camera_forward": str(camera.get_actor_forward_vector()),
        "camera_fov": camera.get_component_by_class(u.CameraComponent).field_of_view,
        "cvars": {key:u.SystemLibrary.get_console_variable_int_value(key) for key in cvars}}
(out/"live_preview.json").write_text(json.dumps(data,indent=2,ensure_ascii=False),encoding="utf-8")
u.log("MAXSRP_PREVIEW_READY")
