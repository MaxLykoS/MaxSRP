"""Run in a separate Blender process: --background --factory-startup --python this.py.

This is a controlled lighting reference: source sky radiance is shared with MaxSRP,
while Cycles independently traces BSDFs, visibility, reflections and interreflection.
"""
import argparse
import json
import math
from pathlib import Path
import sys

import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--out', type=Path, default=ROOT / 'OfflineBaseline/SkyboxScene')
parser.add_argument('--samples', type=int, default=256)
parser.add_argument('--percentage', type=int, default=100)
parser.add_argument('--build-only', action='store_true')
args = parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
OUT = args.out.resolve()
manifest = json.loads((OUT / 'manifest.json').read_text(encoding='utf-8'))
sky_info = json.loads((OUT / 'sky_info.json').read_text(encoding='utf-8'))
# Reflection converts Unity's Y-up/+Z-forward coordinates to Blender Z-up/+Y-forward.
C = Matrix(((1,0,0,0),(0,0,1,0),(0,1,0,0),(0,0,0,1)))


def unity_matrix(t):
    p, q, s = [t[k] for k in ('m_LocalPosition','m_LocalRotation','m_LocalScale')]
    rotation = Quaternion((q['w'],q['x'],q['y'],q['z']))
    rotation.normalize()
    return Matrix.LocRotScale(Vector((p['x'],p['y'],p['z'])), rotation, Vector((s['x'],s['y'],s['z'])))


def pick_enum(owner, prop, desired):
    options = [v.identifier for v in owner.bl_rna.properties[prop].enum_items]
    if desired not in options:
        raise ValueError(f'{prop}: {desired} unavailable, have {options}')
    setattr(owner, prop, desired)


def cycles_config(scene):
    try:
        scene.render.engine = 'CYCLES'
    except TypeError as error:
        raise RuntimeError(f'Cycles is required: {error}')
    scene.cycles.samples = args.samples
    scene.cycles.use_denoising = False
    scene.cycles.use_adaptive_sampling = True
    scene.cycles.adaptive_threshold = .005
    scene.cycles.max_bounces = 8
    scene.cycles.diffuse_bounces = 4
    scene.cycles.glossy_bounces = 4
    scene.cycles.transmission_bounces = 4
    scene.cycles.sample_clamp_direct = 0
    scene.cycles.sample_clamp_indirect = 0
    scene.cycles.seed = 20261016
    prefs = bpy.context.preferences.addons['cycles'].preferences
    gpu = False
    try:
        # Cycles uses a callback enum; static RNA enum_items can be empty.
        device_types = [v[0] for v in prefs.get_device_types(bpy.context)]
        if 'OPTIX' not in device_types:
            raise ValueError(f'OptiX unavailable: {device_types}')
        prefs.compute_device_type = next(v for v in device_types if v == 'OPTIX')
        prefs.get_devices()
        for device in prefs.devices:
            device.use = device.type == 'OPTIX'
            gpu |= device.use
    except (ValueError, TypeError):
        pass
    scene.cycles.device = 'GPU' if gpu else 'CPU'
    scene.render.resolution_x, scene.render.resolution_y = manifest['resolution']
    scene.render.resolution_percentage = args.percentage
    scene.render.pixel_aspect_x = scene.render.pixel_aspect_y = 1
    scene.render.film_transparent = False
    # The compositor implements the project's display curve. Standard adds only sRGB encoding.
    scene.view_settings.view_transform = 'Standard'
    scene.view_settings.look = 'None'
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1
    pick_enum(scene.render.image_settings, 'file_format', 'OPEN_EXR')
    scene.render.image_settings.color_depth = '32'
    scene.render.image_settings.exr_codec = 'ZIP'


def create_material(data):
    mat = bpy.data.materials.new(data['name'])
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = next(n for n in nodes if n.type == 'BSDF_PRINCIPLED')
    bsdf.inputs['IOR'].default_value = 1.5  # dielectric F0=.04
    bsdf.inputs['Metallic'].default_value = 0
    bsdf.inputs['Roughness'].default_value = .05
    bsdf.inputs['Base Color'].default_value = (1,1,1,1)
    uv = nodes.new('ShaderNodeTexCoord')
    mapping = nodes.new('ShaderNodeVectorMath')
    pick_enum(mapping, 'operation', 'MULTIPLY_ADD')
    mapping.inputs[1].default_value = (data['uv_scale']['x'],data['uv_scale']['y'],1)
    mapping.inputs[2].default_value = (data['uv_offset']['x'],data['uv_offset']['y'],0)
    links.new(uv.outputs['UV'], mapping.inputs[0])
    for prop, info in data['maps'].items():
        if not info:
            continue
        tex = nodes.new('ShaderNodeTexImage')
        tex.label = prop
        # Separate datablocks keep source color-space decisions local to this texture usage.
        tex.image = bpy.data.images.load(info['path'], check_existing=False)
        tex.image.colorspace_settings.name = 'sRGB' if info['srgb'] and prop != '_NormalMap' else 'Non-Color'
        links.new(mapping.outputs['Vector'], tex.inputs['Vector'])
        if prop == '_AlbedoMap':
            links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
        elif prop in ['_MetalnessMap','_RoughnessMap']:
            separate = nodes.new('ShaderNodeSeparateColor')
            links.new(tex.outputs['Color'], separate.inputs['Color'])
            value = separate.outputs['Red']
            if prop == '_RoughnessMap':
                clamp = nodes.new('ShaderNodeMath')
                pick_enum(clamp, 'operation', 'MAXIMUM')
                clamp.inputs[1].default_value = .05
                links.new(value, clamp.inputs[0])
                value = clamp.outputs[0]
            links.new(value, bsdf.inputs['Metallic' if prop == '_MetalnessMap' else 'Roughness'])
        elif prop == '_NormalMap':
            color = tex.outputs['Color']
            if info['flip_green']:
                flip = nodes.new('ShaderNodeVectorMath')
                pick_enum(flip, 'operation', 'MULTIPLY_ADD')
                flip.inputs[1].default_value = (1,-1,1)
                flip.inputs[2].default_value = (0,1,0)
                links.new(color, flip.inputs[0])
                color = flip.outputs['Vector']
            normal = nodes.new('ShaderNodeNormalMap')
            normal.inputs['Strength'].default_value = 1
            links.new(color, normal.inputs['Color'])
            links.new(normal.outputs['Normal'], bsdf.inputs['Normal'])
    return mat


def set_mesh(obj, vertices, normals, uv, faces):
    vertices = np.asarray(vertices)[:, [0,2,1]]
    normals = np.asarray(normals)[:, [0,2,1]]
    faces = np.asarray(faces).copy()
    geometric = np.cross(vertices[faces[:,1]]-vertices[faces[:,0]], vertices[faces[:,2]]-vertices[faces[:,0]])
    reverse = np.sum(geometric*normals[faces[:,0]],axis=1).mean() < 0
    if reverse:
        faces = faces[:, [0,2,1]]
    mesh = bpy.data.meshes.new(obj['name']+'_Mesh')
    mesh.from_pydata(vertices.tolist(), [], faces.tolist())
    mesh.update()
    layer = mesh.uv_layers.new(name='Unity UV0')
    flat_uv = uv[np.array([loop.vertex_index for loop in mesh.loops])].astype(np.float32).ravel()
    layer.data.foreach_set('uv',flat_uv)
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    mesh.normals_split_custom_set_from_vertices(normals.tolist())
    return mesh


def build_geometry(scene, materials):
    report = []
    for data in manifest['objects']:
        if data['kind'] == 'primitive':
            if data['mesh']['fileID'] != 10202:
                raise ValueError('Only the active Unity cube primitives are mapped')
            bpy.ops.mesh.primitive_cube_add(size=1)
            obj = bpy.context.object
            uv = obj.data.uv_layers.active
            for face in obj.data.polygons:
                normal_u = C.to_3x3() @ face.normal
                axes = (0,2) if abs(normal_u.y)>.5 else ((0,1) if abs(normal_u.z)>.5 else (2,1))
                for index in face.loop_indices:
                    u = C.to_3x3() @ obj.data.vertices[obj.data.loops[index].vertex_index].co
                    uv.data[index].uv = (u[axes[0]]+.5,u[axes[1]]+.5)
            # Shader GBuffer uses smooth imported normals on model meshes, flat normals on cubes.
        elif data['kind'] == 'unity_mesh':
            raw = np.load(data['path'])
            obj = bpy.data.objects.new(data['name'], set_mesh(data,raw['vertices'],raw['normals'],raw['uv'],raw['indices']))
            scene.collection.objects.link(obj)
        elif data['kind'] == 'fbx':
            bpy.ops.object.select_all(action='DESELECT')
            bpy.ops.import_scene.fbx(filepath=data['path'], use_anim=False)
            imported = list(bpy.context.selected_objects)
            meshes = [o for o in imported if o.type == 'MESH']
            if len(meshes) != 1:
                raise ValueError('Source FBX hierarchy needs explicit per-node mapping')
            obj = meshes[0]
            # This single-node FBX stores cm mesh coordinates. Unity overrides its root TRS.
            # Preserve local mesh data, apply the file's centimetre-to-metre scale once.
            source = obj.data
            source.calc_loop_triangles()
            # FBX UVs and authored normals are per corner. Split corners rather than
            # collapsing seams or averaging normals through a shared position index.
            corner_ids = np.array([i for t in source.loop_triangles for i in t.loops])
            vertex_ids = [source.loops[int(i)].vertex_index for i in corner_ids]
            verts = np.array([source.vertices[i].co[:] for i in vertex_ids])*.01
            normals = np.array([source.corner_normals[int(i)].vector[:] for i in corner_ids])
            uvs = np.array([source.uv_layers.active.data[int(i)].uv[:] for i in corner_ids])
            faces = np.arange(len(corner_ids)).reshape(-1,3)
            obj.data = set_mesh(data,verts,normals,uvs,faces)
            obj.parent = None
            for other in imported:
                if other != obj:
                    bpy.data.objects.remove(other, do_unlink=True)
        obj.name = data['name']
        obj.matrix_world = C @ unity_matrix(data['transform']) @ C
        obj.data.materials.clear()
        for key in data['materials']:
            obj.data.materials.append(materials[key])
        obj['Unity source transform'] = json.dumps(data['transform'])
        bpy.context.view_layer.update()
        bounds = np.array([(obj.matrix_world@Vector(p))[:] for p in obj.bound_box])
        report.append({'name':obj.name,'vertices':len(obj.data.vertices),'bounds_min':bounds.min(axis=0).tolist(),'bounds_max':bounds.max(axis=0).tolist()})
    return report


def build_world(scene):
    world = bpy.data.worlds.new('MaxSRP shared sky radiance - no solar disk')
    world.use_nodes = True
    scene.world = world
    background = next(n for n in world.node_tree.nodes if n.type == 'BACKGROUND')
    image = bpy.data.images.new('MaxSRP source sky - linear RGB / 100000', width=sky_info['width'], height=sky_info['height'], float_buffer=True)
    image.colorspace_settings.name = 'Non-Color'
    image.pixels.foreach_set(np.fromfile(OUT/sky_info['buffer'],np.float32))
    image.filepath_raw = str(OUT/'source_sky_linear.exr')
    image.file_format = 'OPEN_EXR'
    image.save()
    tex = world.node_tree.nodes.new('ShaderNodeTexEnvironment')
    tex.image = image
    world.node_tree.links.new(tex.outputs['Color'],background.inputs['Color'])
    background.inputs['Strength'].default_value = 1


def linear(v):
    return v/12.92 if v<=.04045 else ((v+.055)/1.055)**2.4


def build_lights(scene):
    for data in manifest['lights']:
        if data['use_temperature']:
            raise ValueError('Explicit color-temperature conversion required')
        light = bpy.data.lights.new(data['name'], 'SUN' if data['type']==1 else 'POINT')
        obj = bpy.data.objects.new(data['name'], light)
        scene.collection.objects.link(obj)
        light.normalize = True
        if data['type']==1:
            rgb = sky_info['sun_ground_rgb']
            light.energy = max(rgb)
            light.color = tuple(v/max(rgb) for v in rgb)
            direction = C.to_3x3() @ Vector(sky_info['sun_direction_unity'])
            obj.rotation_euler = (-direction).to_track_quat('-Z','Y').to_euler()
            light.angle = 2*math.radians(sky_info['sun_angular_radius_degrees'])
        else:
            watts_numeric = data['intensity']*(1 if data['unit']==1 else 4*math.pi)/100000
            light.energy = watts_numeric
            light.color = tuple(linear(data['color'][k]) for k in ['r','g','b'])
            light.shadow_soft_size = 0
            light.use_custom_distance = False
            obj.location = (C @ unity_matrix(data['transform'])).translation
            obj['Unity range - not a physical cutoff'] = data['range']
        obj['Source intensity'] = data['intensity']


def build_cameras(scene):
    source = manifest['cameras'][0]
    data = bpy.data.cameras.new('Unity Main Camera')
    obj = bpy.data.objects.new('Unity Main Camera',data)
    scene.collection.objects.link(obj)
    obj.matrix_world = C @ unity_matrix(source['transform']) @ Matrix.Diagonal((1,1,-1,1))
    data.sensor_fit = 'VERTICAL'
    data.sensor_height = 24
    data.lens = 12/math.tan(math.radians(source['vfov'])/2)
    data.clip_start, data.clip_end = source['near'], source['far']
    data.dof.use_dof = False
    scene.camera = obj
    detail = bpy.data.objects.new('Detail Camera - shading normal reference',data.copy())
    scene.collection.objects.link(detail)
    detail.location = (5,-6,3)
    detail.rotation_euler = (Vector((0,1,.8))-detail.location).to_track_quat('-Z','Y').to_euler()
    return obj, detail


def compositor(scene):
    # Blender 5.2 stores compositor nodes in a standalone node group.
    group = bpy.data.node_groups.new('MaxSRP EV100 + fitted ACES', 'CompositorNodeTree')
    scene.compositing_node_group = group
    nodes, links = group.nodes, group.links
    render = nodes.new('CompositorNodeRLayers')
    render.scene = scene
    separate = nodes.new('CompositorNodeSeparateColor')
    links.new(render.outputs['Image'], separate.inputs['Image'])
    combine = nodes.new('CompositorNodeCombineColor')
    exposure = 100000/(1.2*2**manifest['exposure']['CameraEV100'])*2**manifest['exposure']['ExposureEV']
    def math_node(op, a, b):
        n = nodes.new('ShaderNodeMath')
        pick_enum(n,'operation',op)
        for socket,value in zip(n.inputs,[a,b]):
            if isinstance(value,(float,int)):
                socket.default_value = value
            else:
                links.new(value,socket)
        return n.outputs[0]
    for channel in ['Red','Green','Blue']:
        x = math_node('MULTIPLY', separate.outputs[channel], exposure)
        numerator = math_node('MULTIPLY',x,math_node('ADD',math_node('MULTIPLY',x,2.51),.03))
        denominator = math_node('ADD',math_node('MULTIPLY',x,math_node('ADD',math_node('MULTIPLY',x,2.43),.59)),.14)
        tone = math_node('DIVIDE',numerator,denominator)
        tone = math_node('MINIMUM',math_node('MAXIMUM',tone,0),1)
        links.new(tone,combine.inputs[channel])
    output = nodes.new('NodeGroupOutput')
    group.interface.new_socket(name='Image',in_out='OUTPUT',socket_type='NodeSocketColor')
    links.new(combine.outputs['Image'],output.inputs['Image'])
    return exposure


def render(scene, camera, stem):
    scene.camera = camera
    scene.render.use_compositing = False
    scene.render.filepath = str(OUT/(stem+'_linear.exr'))
    bpy.ops.render.render(write_still=True, scene=scene.name)
    # Write display PNG explicitly from the untouched linear EXR.
    im = bpy.data.images.load(scene.render.filepath,check_existing=False)
    w,h = im.size[:]
    pixels = np.empty(w*h*4,np.float32)
    im.pixels.foreach_get(pixels)
    rgb = pixels.reshape(h,w,4)[:,:,:3]
    assert np.isfinite(rgb).all(), 'Cycles output contains NaN/Inf'
    x = np.maximum(rgb,0)*EXPOSURE
    mapped = np.clip(x*(2.51*x+.03)/(x*(2.43*x+.59)+.14),0,1)
    preview = bpy.data.images.new(stem+'_ACES_preview',width=w,height=h,float_buffer=True)
    preview.colorspace_settings.name = 'Linear Rec.709'
    rgba = np.ones((h,w,4),np.float32)
    rgba[:,:,:3] = mapped
    preview.pixels.foreach_set(rgba.ravel())
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_depth = '8'
    preview.save_render(str(OUT/(stem+'_aces.png')),scene=scene)
    scene.render.image_settings.file_format = 'OPEN_EXR'
    scene.render.image_settings.color_depth = '32'
    print('BASELINE_RENDER',stem, w,h,'range',float(rgb.min()),float(rgb.max()),flush=True)
    bpy.data.images.remove(im)
    bpy.data.images.remove(preview)


def calibrate_lights():
    calibration = bpy.data.scenes.new('Temporary RGB light-unit calibration')
    cycles_config(calibration)
    calibration.cycles.samples = 64
    calibration.cycles.use_adaptive_sampling = False
    calibration.render.resolution_x = calibration.render.resolution_y = 32
    calibration.render.resolution_percentage = 100
    calibration.render.use_compositing = False
    mesh = bpy.data.meshes.new('Calibration card')
    mesh.from_pydata([(-1,-1,0),(1,-1,0),(1,1,0),(-1,1,0)],[],[(0,1,2,3)])
    obj = bpy.data.objects.new('Lambert 18 percent card',mesh)
    calibration.collection.objects.link(obj)
    mat = bpy.data.materials.new('Lambert 18 percent')
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    output = next(n for n in nodes if n.type == 'OUTPUT_MATERIAL')
    diffuse = nodes.new('ShaderNodeBsdfDiffuse')
    diffuse.inputs['Color'].default_value = (.18,.18,.18,1)
    diffuse.inputs['Roughness'].default_value = 0
    mat.node_tree.links.new(diffuse.outputs[0],output.inputs['Surface'])
    mesh.materials.append(mat)
    camera_data = bpy.data.cameras.new('Calibration camera')
    pick_enum(camera_data,'type','ORTHO')
    camera_data.ortho_scale = .1
    camera = bpy.data.objects.new('Calibration camera',camera_data)
    calibration.collection.objects.link(camera)
    camera.location = (0,0,3)
    calibration.camera = camera
    values = {}
    for kind,power in [('SUN',1),('POINT',4*math.pi)]:
        light = bpy.data.lights.new('Calibration '+kind,kind)
        light.normalize = True
        light.energy = power
        if kind == 'SUN':
            light.angle = 0
        else:
            light.shadow_soft_size = 0
        emitter = bpy.data.objects.new('Calibration '+kind,light)
        calibration.collection.objects.link(emitter)
        emitter.location = (0,0,1)
        calibration.render.filepath = str(OUT/('calibration_'+kind.lower()+'.exr'))
        bpy.ops.render.render(write_still=True,scene=calibration.name)
        image = bpy.data.images.load(calibration.render.filepath,check_existing=False)
        array = np.array(image.pixels[:],dtype=np.float32).reshape(32,32,4)
        measured = float(array[15:17,15:17,:3].mean())
        expected = .18/math.pi
        error = abs(measured/expected-1)
        if error>.002:
            raise AssertionError(f'{kind} unit calibration failed: {measured}, expected {expected}')
        values[kind] = {'measured':measured,'expected':expected,'relative_error':error}
        bpy.data.images.remove(image)
        bpy.data.objects.remove(emitter,do_unlink=True)
    bpy.data.scenes.remove(calibration)
    print('LIGHT_UNIT_CALIBRATION',json.dumps(values),flush=True)
    return values


# Factory startup belongs to this separate process, never to the user's open Blender.
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
scene = bpy.context.scene
scene.name = 'SkyboxScene - Cycles controlled baseline'
cycles_config(scene)
materials = {k:create_material(v) for k,v in manifest['materials'].items()}
report = build_geometry(scene,materials)
build_world(scene)
build_lights(scene)
main_camera,detail_camera = build_cameras(scene)
EXPOSURE = compositor(scene)
scene['Baseline scope'] = 'Shared source sky and attenuated solar input; independent Cycles surface light transport. No camera aerial fog.'
scene['Source scene'] = manifest['source_scene']
scene['HDR units'] = 'RGB photometric embedding: radiance=cd/m2/100000; irradiance=lux/100000; flux=lumens/100000. Numeric calibration required; not a claim of spectral lm/W equivalence.'
scene.render.use_compositing = True
scene.render.image_settings.file_format = 'PNG'
scene.render.image_settings.color_depth = '8'
scene.render.filepath = str(OUT/'interactive_aces.png')
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type == 'VIEW_3D':
            area.spaces.active.region_3d.view_perspective = 'CAMERA'
bpy.ops.file.pack_all()
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'SkyboxScene_Cycles.blend'))
(OUT/'import_report.json').write_text(json.dumps({'objects':report,'exposure_multiplier':EXPOSURE,'device':scene.cycles.device,
    'samples':args.samples,'blender_version':bpy.app.version_string,'source_sky':sky_info},indent=2),encoding='utf-8')
if not args.build_only:
    calibration = calibrate_lights()
    (OUT/'light_unit_calibration.json').write_text(json.dumps(calibration,indent=2),encoding='utf-8')
    scene.render.image_settings.file_format = 'OPEN_EXR'
    scene.render.image_settings.color_depth = '32'
    render(scene,main_camera,'main')
    render(scene,detail_camera,'detail')
print('BASELINE_COMPLETE',OUT,flush=True)
