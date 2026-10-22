"""Read back the live native mobile setup and capture the matched camera."""
import json
from pathlib import Path
import unreal as u

out = Path(u.Paths.project_saved_dir()) / "MaxSRPReference"
actors = u.get_editor_subsystem(u.EditorActorSubsystem).get_all_level_actors()
camera = next(a for a in actors if a.get_actor_label() == "MaxSRP Comparison Camera")
report = {"world": u.EditorLevelLibrary.get_editor_world().get_path_name(), "lights": [], "camera": {}, "postprocess": {}, "textures": [], "sky_lights": []}
component = camera.get_component_by_class(u.CameraComponent)
report["camera"] = {"forward": str(camera.get_actor_forward_vector()), "up": str(camera.get_actor_up_vector()),
                    "position": str(camera.get_actor_location()), "hfov": component.field_of_view, "aspect": component.aspect_ratio}
for actor in actors:
    sky = actor.get_component_by_class(u.SkyLightComponent)
    if sky is not None:
        report["sky_lights"].append({"position":str(actor.get_actor_location()),"real_time_capture":sky.real_time_capture,"resolution":sky.cubemap_resolution,"sky_distance_threshold_cm":sky.sky_distance_threshold})
    light = actor.get_component_by_class(u.LightComponent)
    if light is not None:
        entry = {"label":actor.get_actor_label(), "type":light.get_class().get_name(), "intensity":light.intensity}
        if isinstance(light,u.DirectionalLightComponent):
            entry.update({key:light.get_editor_property(key) for key in ["dynamic_shadow_cascades","dynamic_shadow_distance_movable_light","cascade_distribution_exponent","light_source_angle","shadow_bias","shadow_slope_bias"]})
        if isinstance(light,u.PointLightComponent):
            entry.update({"units":str(light.intensity_units), "range_cm":light.attenuation_radius})
        report["lights"].append(entry)
    if isinstance(actor,u.PostProcessVolume):
        pp=actor.settings
        report["postprocess"]={key:str(pp.get_editor_property(key)) for key in ["auto_exposure_method","auto_exposure_apply_physical_camera_exposure","auto_exposure_bias","camera_iso","camera_shutter_speed","depth_of_field_fstop","bloom_intensity","ambient_occlusion_intensity"]}
for path in u.EditorAssetLibrary.list_assets("/Game/MaxSRP_Reference/Textures",recursive=True):
    texture=u.EditorAssetLibrary.load_asset(path)
    report["textures"].append({"path":path,"srgb":texture.srgb,"compression":str(texture.compression_settings),"flip_green":texture.flip_green_channel})
report["preview"]={"shader_platform":"PCD3D_ES3_1","feature_level":"ES3_1","evidence":"Saved EditorPerProjectUserSettings + actual PCD3D_ES3_1 shader compilation log"}
(out/"live_validation.json").write_text(json.dumps(report,indent=2,ensure_ascii=False),encoding="utf-8")
u.AutomationLibrary.finish_loading_before_screenshot()
task = u.AutomationLibrary.take_high_res_screenshot(1920,1080,str(out/"UE_Mobile_Comparison.png"),camera=camera,delay=2.0)
u.SystemLibrary.execute_console_command(u.EditorLevelLibrary.get_editor_world(),"r.DumpRenderTargetPoolMemory")
u.log("MAXSRP_CAPTURE_QUEUED")
