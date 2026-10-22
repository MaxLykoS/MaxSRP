"""Build the current MaxSRP scene in UE 5.3, using UE's native mobile shading."""
import json
import math
import traceback
from pathlib import Path
import unreal as u

SOURCE = Path("D:/UnityProject/MaxSRP/OfflineBaseline/UnrealReference")
MANIFEST = json.loads((SOURCE / "prepared_manifest.json").read_text(encoding="utf-8"))
DEST = "/Game/MaxSRP_Reference"
MAP = DEST + "/Maps/MaxSRP_Mobile"
OUT = Path(u.Paths.project_saved_dir()) / "MaxSRPReference"
OUT.mkdir(parents=True, exist_ok=True)
REPORT = {"source": str(SOURCE), "level": MAP, "objects": [], "lights": [], "warnings": [], "material_inputs": {}, "bounds_errors_cm": []}
actors = u.get_editor_subsystem(u.EditorActorSubsystem)
assets = u.EditorAssetLibrary
tools = u.AssetToolsHelpers.get_asset_tools()
matlib = u.MaterialEditingLibrary

def prop(obj, name, value, required=True):
    try:
        obj.set_editor_property(name, value)
    except Exception as exc:
        if required:
            raise
        REPORT["warnings"].append(f"{obj.get_name()}.{name}: {exc}")

def position(v, scale=100):
    return u.Vector(v[2]*scale, v[0]*scale, v[1]*scale)

def orientation(q):
    return u.Quat(q[2], q[0], q[1], q[3]).rotator()

def lc(rgb):
    return u.LinearColor(float(rgb[0]), float(rgb[1]), float(rgb[2]), 1)

def new_asset(name, folder, cls, factory):
    existing = assets.load_asset(folder + "/" + name)
    return existing if existing else tools.create_asset(name, folder, cls, factory)

def import_task(path, name, folder, options=None):
    task = u.AssetImportTask()
    task.filename = str(path)
    task.destination_path = folder
    task.destination_name = name
    task.automated = True
    task.replace_existing = True
    task.save = True
    if options is not None:
        task.options = options
    tools.import_asset_tasks([task])
    result = task.get_editor_property("imported_object_paths")
    if not result:
        raise RuntimeError("Import returned no asset: " + str(path))
    return assets.load_asset(result[0])

def node(mat, cls, x=0, y=0, **values):
    n = matlib.create_material_expression(mat, cls, x, y)
    for name, value in values.items():
        prop(n, name, value)
    return n

def connect(a, output, b, input_name):
    if not matlib.connect_material_expressions(a, output, b, input_name):
        raise RuntimeError(f"Material connection failed: {a.get_name()} -> {b.get_name()}:{input_name}")

def build_material(key, data):
    mat = new_asset(key, DEST + "/Materials", u.Material, u.MaterialFactoryNew())
    matlib.delete_all_material_expressions(mat)
    prop(mat, "shading_model", u.MaterialShadingModel.MSM_DEFAULT_LIT)
    # Native UE BRDF: Specular=.5 corresponds to dielectric F0=.04.
    spec = node(mat, u.MaterialExpressionConstant, 0, 700, r=.5)
    matlib.connect_material_property(spec, "", u.MaterialProperty.MP_SPECULAR)
    uv = node(mat, u.MaterialExpressionTextureCoordinate, -800, 0)
    scale = node(mat, u.MaterialExpressionConstant2Vector, -800, 150, r=data["uv_scale"][0], g=data["uv_scale"][1])
    offset = node(mat, u.MaterialExpressionConstant2Vector, -800, 300, r=data["uv_offset"][0], g=1-data["uv_scale"][1]-data["uv_offset"][1])
    mul = node(mat, u.MaterialExpressionMultiply, -600, 0)
    add = node(mat, u.MaterialExpressionAdd, -400, 0)
    connect(uv, "", mul, "A"); connect(scale, "", mul, "B")
    connect(mul, "", add, "A"); connect(offset, "", add, "B")
    inputs = {"albedo": u.MaterialProperty.MP_BASE_COLOR, "metallic": u.MaterialProperty.MP_METALLIC,
              "roughness": u.MaterialProperty.MP_ROUGHNESS, "normal": u.MaterialProperty.MP_NORMAL}
    for i, (role, target) in enumerate(inputs.items()):
        path = data["prepared_maps"][role]
        if path:
            texture = import_task(path, Path(path).stem, DEST + "/Textures")
            prop(texture, "srgb", role == "albedo")
            prop(texture, "max_texture_size", 2048)
            prop(texture, "compression_settings", u.TextureCompressionSettings.TC_NORMALMAP if role == "normal" else u.TextureCompressionSettings.TC_BC7)
            if role == "normal":
                # Unity tangent normal +Y -> UE/DirectX -Y; OBJ import also flips UV V.
                prop(texture, "flip_green_channel", True)
            texture.post_edit_change() if hasattr(texture, "post_edit_change") else None
            assets.save_loaded_asset(texture)
            sample = node(mat, u.MaterialExpressionTextureSample, -200, i*220, texture=texture)
            prop(sample, "sampler_type", u.MaterialSamplerType.SAMPLERTYPE_NORMAL if role == "normal" else u.MaterialSamplerType.SAMPLERTYPE_COLOR if role == "albedo" else u.MaterialSamplerType.SAMPLERTYPE_LINEAR_COLOR)
            connect(add, "", sample, "UVs")
            output = "RGB" if role in ("albedo", "normal") else "R"
        else:
            if role == "normal":
                continue
            sample = node(mat, u.MaterialExpressionConstant, 0, i*220, r=1 if role=="albedo" else 0)
            output = ""
        if role == "roughness":
            floor = node(mat, u.MaterialExpressionMax, 100, i*220, const_b=.05)
            connect(sample, output, floor, "A")
            sample, output = floor, ""
        if not matlib.connect_material_property(sample, output, target):
            raise RuntimeError("Failed material property " + role)
    matlib.recompile_material(mat)
    assets.save_loaded_asset(mat)
    REPORT["material_inputs"][key] = data
    return mat

def spawn(cls, name, loc=None, rot=None):
    actor = actors.spawn_actor_from_class(cls, loc or u.Vector(), rot or u.Rotator())
    if actor is None:
        raise RuntimeError("Cannot spawn " + name)
    actor.set_actor_label(name)
    actor.set_folder_path("MaxSRP_Reference")
    return actor

def movable(component):
    component.set_mobility(u.ComponentMobility.MOVABLE)

def main():
    for folder in ("Meshes", "Textures", "Materials", "Maps"):
        assets.make_directory(DEST + "/" + folder)
    mats = {key: build_material(key, value) for key, value in MANIFEST["materials"].items()}
    meshes = {}
    for obj in MANIFEST["objects"]:
        options = u.FbxImportUI()
        prop(options, "automated_import_should_detect_type", False)
        prop(options, "mesh_type_to_import", u.FBXImportType.FBXIT_STATIC_MESH)
        prop(options, "import_as_skeletal", False)
        prop(options, "import_materials", False)
        prop(options, "import_textures", False)
        data = options.static_mesh_import_data
        for name, value in {"convert_scene": False, "convert_scene_unit": False, "force_front_x_axis": False,
                            "combine_meshes": True, "auto_generate_collision": False,
                            "normal_import_method": u.FBXNormalImportMethod.FBXNIM_IMPORT_NORMALS}.items():
            prop(data, name, value)
        mesh = import_task(SOURCE / obj["mesh"], obj["key"], DEST + "/Meshes", options)
        mesh.set_material(0, mats[obj["material"]])
        assets.save_loaded_asset(mesh)
        meshes[obj["key"]] = mesh
    # Create only the dedicated reference map; never clear an existing user map.
    level = u.get_editor_subsystem(u.LevelEditorSubsystem)
    if assets.does_asset_exist(MAP):
        level.load_level(MAP)
        for actor in actors.get_all_level_actors():
            if str(actor.get_folder_path()) == "MaxSRP_Reference":
                actors.destroy_actor(actor)
    else:
        if not level.new_level(MAP):
            raise RuntimeError("Cannot create reference level")
    for obj in MANIFEST["objects"]:
        actor = spawn(u.StaticMeshActor, obj["name"], position(obj["unity_position"]))
        component = actor.static_mesh_component
        component.set_static_mesh(meshes[obj["key"]])
        movable(component)
        prop(component, "cast_shadow", obj["cast_shadows"])
        prop(component, "receive_mobile_csm_shadows", True, False)
        component.set_collision_enabled(u.CollisionEnabled.NO_COLLISION)
        center, extent = actor.get_actor_bounds(False)
        want_center = position(obj["bounds_center"])
        want_extent = position(obj["bounds_size"], 50)
        error = max(abs(center.x-want_center.x), abs(center.y-want_center.y), abs(center.z-want_center.z),
                    abs(extent.x-want_extent.x), abs(extent.y-want_extent.y), abs(extent.z-want_extent.z))
        if error > .1:
            raise RuntimeError("Coordinate/bounds mismatch " + obj["name"] + ": " + str(error) + " cm")
        REPORT["bounds_errors_cm"].append(error)
        REPORT["objects"].append({"label": obj["name"], "asset": meshes[obj["key"]].get_path_name(), "bounds_error_cm": error, "triangles": obj["triangles"]})
    atmosphere = spawn(u.SkyAtmosphere, "UE SkyAtmosphere - matched MaxSRP coefficients")
    sky = atmosphere.get_component_by_class(u.SkyAtmosphereComponent)
    p = MANIFEST["atmosphere"]
    for name, value in {"bottom_radius": p["_PlanetRadius"]/1000, "atmosphere_height": p["_AtmosphereHeight"]/1000,
                        "rayleigh_exponential_distribution": p["_RayleighScatteringScalarHeight"]/1000,
                        "rayleigh_scattering_scale": p["_RayleighScatteringScale"], "rayleigh_scattering": lc([.005802,.013558,.0331]),
                        "mie_exponential_distribution": p["_MieScatteringScalarHeight"]/1000,
                        "mie_scattering_scale": p["_MieScatteringScale"], "mie_absorption_scale": p["_MieScatteringScale"],
                        "mie_scattering": lc([.003996]*3), "mie_absorption": lc([.000444]*3),
                        "mie_anisotropy": p["_MieAnisotropy"], "other_absorption_scale": p["_OzoneAbsorptionScale"],
                        "other_absorption": lc([.000650,.001881,.000085]), "ground_albedo": u.Color(0,0,0,255)}.items():
        prop(sky, name, value)
    csm = MANIFEST["csm"]
    for light in MANIFEST["lights"]:
        if light["type"] not in (1,2):
            continue
        actor = spawn(u.DirectionalLight if light["type"]==1 else u.PointLight, light["name"], position(light["position"]), orientation(light["rotation"]))
        component = actor.get_component_by_class(u.LightComponent)
        movable(component)
        component.set_light_color(lc(light["color_linear"]), False)
        component.set_intensity(light["intensity"] if light["type"]==1 else light["candela"])
        prop(component, "cast_shadows", light["shadows"] != 0)
        if light["type"]==1:
            for name, value in {"atmosphere_sun_light": light["is_sun"], "atmosphere_sun_light_index": 0,
                                "light_source_angle": 2*p["_SunDiskAngle"], "dynamic_shadow_cascades": 4,
                                "dynamic_shadow_distance_movable_light": csm["ShadowDistance"]*100,
                                "cascade_distribution_exponent": csm["DistributionExponent"],
                                "cascade_transition_fraction": csm["CascadeBlendFraction"],
                                "shadow_distance_fadeout_fraction": csm["DistanceFadeFraction"]}.items():
                prop(component, name, value)
        else:
            prop(component, "intensity_units", u.LightUnits.CANDELAS)
            prop(component, "use_inverse_squared_falloff", True)
            prop(component, "attenuation_radius", light["range"]*100)
            prop(component, "cast_shadows", False)
        REPORT["lights"].append(light)
    sky_mat = new_asset("M_MobileAtmosphereSky", DEST+"/Materials", u.Material, u.MaterialFactoryNew())
    matlib.delete_all_material_expressions(sky_mat)
    prop(sky_mat, "shading_model", u.MaterialShadingModel.MSM_UNLIT)
    prop(sky_mat, "is_sky", True); prop(sky_mat, "two_sided", True)
    luminance = node(sky_mat, u.MaterialExpressionSkyAtmosphereViewLuminance, -400, 0)
    disk = node(sky_mat, u.MaterialExpressionSkyAtmosphereLightDiskLuminance, -400, 200)
    add = node(sky_mat, u.MaterialExpressionAdd, -100, 0)
    connect(luminance,"",add,"A");connect(disk,"",add,"B")
    matlib.connect_material_property(add,"",u.MaterialProperty.MP_EMISSIVE_COLOR)
    matlib.recompile_material(sky_mat);assets.save_loaded_asset(sky_mat)
    dome = spawn(u.StaticMeshActor, "Mobile Sky Dome - native UE atmosphere")
    dome.static_mesh_component.set_static_mesh(assets.load_asset("/Engine/BasicShapes/Sphere"))
    dome.static_mesh_component.set_material(0,sky_mat);dome.set_actor_scale3d(u.Vector(10000,10000,10000))
    prop(dome.static_mesh_component,"cast_shadow",False)
    dome.static_mesh_component.set_collision_enabled(u.CollisionEnabled.NO_COLLISION)
    skylight = spawn(u.SkyLight,"Sky IBL - captured sky only",u.Vector(0,0,200))
    component = skylight.get_component_by_class(u.SkyLightComponent);movable(component)
    prop(component,"source_type",u.SkyLightSourceType.SLS_CAPTURED_SCENE)
    prop(component,"real_time_capture",False);prop(component,"cubemap_resolution",128)
    prop(component,"sky_distance_threshold",100000.0)
    prop(component,"lower_hemisphere_is_black",True)
    component.set_intensity(1)
    pp_actor = spawn(u.PostProcessVolume,"Manual EV100 - no GI, AO, bloom or motion blur")
    prop(pp_actor,"unbound",True)
    pp = pp_actor.get_editor_property("settings")
    ev = MANIFEST["exposure"]["CameraEV100"]
    settings = {"auto_exposure_method":u.AutoExposureMethod.AEM_MANUAL,
                "auto_exposure_apply_physical_camera_exposure":True,
                "auto_exposure_bias":MANIFEST["exposure"]["ExposureEV"],
                "camera_iso":100.0,"camera_shutter_speed":2**ev/256.0,"depth_of_field_fstop":16.0,
                "bloom_intensity":0.0,"ambient_occlusion_intensity":0.0,"motion_blur_amount":0.0,
                "vignette_intensity":0.0,"scene_fringe_intensity":0.0}
    for name,value in settings.items():
        prop(pp,"override_"+name,True);prop(pp,name,value)
    prop(pp_actor,"settings",pp)
    for data in MANIFEST["cameras"]:
        camera = spawn(u.CameraActor,"MaxSRP Comparison Camera",position(data["position"]),orientation(data["rotation"]))
        component = camera.get_component_by_class(u.CameraComponent)
        component.set_field_of_view(data["hfov"])
        prop(component,"aspect_ratio",data["aspect"]);prop(component,"constrain_aspect_ratio",False)
        prop(camera,"auto_activate_for_player",u.AutoReceiveInput.PLAYER0)
        u.EditorLevelLibrary.set_level_viewport_camera_info(position(data["position"]),orientation(data["rotation"]))
        spawn(u.PlayerStart,"MaxSRP Camera Start",position(data["position"]),orientation(data["rotation"]))
        REPORT["camera"]=data
    world = u.EditorLevelLibrary.get_editor_world()
    prop(world.get_world_settings(),"force_no_precomputed_lighting",True)
    if not level.save_current_level():
        raise RuntimeError("Failed to save reference level")
    assets.save_directory(DEST,only_if_is_dirty=False,recursive=True)
    REPORT["success"]=True
    REPORT["atmosphere"]=p;REPORT["csm"]=csm;REPORT["exposure"]=MANIFEST["exposure"]
    (OUT/"import_report.json").write_text(json.dumps(REPORT,indent=2,ensure_ascii=False),encoding="utf-8")
    u.log("MAXSRP_BUILD_SUCCESS "+MAP)

try:
    main()
except Exception:
    REPORT["success"]=False;REPORT["error"]=traceback.format_exc()
    (OUT/"import_report.json").write_text(json.dumps(REPORT,indent=2,ensure_ascii=False),encoding="utf-8")
    u.log_error(REPORT["error"])
    raise
