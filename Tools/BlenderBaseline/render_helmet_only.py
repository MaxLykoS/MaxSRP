"""Render the helmet alone using the camera from the real Unity preview capture.

No surface interreflection: max_bounces=1. The additional unoccluded environment
variant matches the scope of Unity's global sky IBL (which has no local visibility).
"""
import json
import math
from pathlib import Path
import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT/'OfflineBaseline/HelmetOnly'
actual_camera_available = (OUT/'unity_helmet.json').exists()
meta = json.loads((OUT/('unity_helmet.json' if actual_camera_available else 'camera_plan.json')).read_text(encoding='utf-8'))
scene = bpy.context.scene
scene.name = 'Helmet only - no scene GI'
helmet = next(o for o in scene.objects if o.name=='node_damagedHelmet_-6514')
sun = next(o for o in scene.objects if o.type=='LIGHT' and o.data.type=='SUN')
for obj in list(scene.objects):
    if obj not in [helmet,sun]:
        bpy.data.objects.remove(obj,do_unlink=True)
background = next(n for n in scene.world.node_tree.nodes if n.type=='BACKGROUND')
assert background.inputs['Strength'].default_value == 1
camera_data = bpy.data.cameras.new('Unity helmet test camera')
camera = bpy.data.objects.new('Unity helmet test camera',camera_data)
scene.collection.objects.link(camera)
scene.camera = camera
C = Matrix(((1,0,0,0),(0,0,1,0),(0,1,0,0),(0,0,0,1)))
p = meta['cameraPosition']
if 'cameraRotation' in meta:
    q = meta['cameraRotation']
    matrix = Matrix.LocRotScale(Vector((p['x'],p['y'],p['z'])),Quaternion((q['w'],q['x'],q['y'],q['z'])),Vector((1,1,1)))
    camera.matrix_world = C@matrix@Matrix.Diagonal((1,1,-1,1))
else:
    target = meta['cameraTarget']
    camera.location = (p['x'],p['z'],p['y'])
    camera.rotation_euler = (Vector((target['x'],target['z'],target['y']))-camera.location).to_track_quat('-Z','Y').to_euler()
camera_data.sensor_fit = next(e.identifier for e in camera_data.bl_rna.properties['sensor_fit'].enum_items if e.identifier=='VERTICAL')
camera_data.sensor_height = 24
camera_data.lens = 12/math.tan(math.radians(meta['verticalFov'])/2)
camera_data.clip_start = .05
camera_data.clip_end = 30
camera_data.dof.use_dof = False
scene.render.engine = 'CYCLES'
prefs = bpy.context.preferences.addons['cycles'].preferences
types = [e[0] for e in prefs.get_device_types(bpy.context)]
if 'OPTIX' in types:
    prefs.compute_device_type = next(v for v in types if v=='OPTIX')
    prefs.get_devices()
    for device in prefs.devices:
        device.use = device.type=='OPTIX'
    scene.cycles.device = 'GPU'
scene.cycles.samples = 512
scene.cycles.use_adaptive_sampling = False
scene.cycles.use_denoising = False
scene.cycles.max_bounces = 1
scene.cycles.diffuse_bounces = 1
scene.cycles.glossy_bounces = 1
scene.cycles.sample_clamp_direct = scene.cycles.sample_clamp_indirect = 0
scene.cycles.seed = 20261019
scene.render.resolution_x,scene.render.resolution_y = meta['width'],meta['height']
scene.render.resolution_percentage = 100
scene.render.film_transparent = True
scene.render.use_compositing = False
scene.view_settings.view_transform = 'Standard'
scene.view_settings.look = 'None'
scene.view_settings.exposure = 0
scene.view_settings.gamma = 1
scene.render.image_settings.file_format = 'OPEN_EXR'
scene.render.image_settings.color_depth = '32'
scene.render.image_settings.exr_codec = 'ZIP'
W,H = meta['width'],meta['height']
exposure = meta['exposureMultiplier']
values = {}


def save(array,label,aces=True):
    array = np.asarray(array,np.float32)
    assert array.shape==(H,W,4) and np.isfinite(array).all(), label
    array.tofile(OUT/('blender_'+label+'.f32'))
    image = bpy.data.images.new(label,width=W,height=H,float_buffer=True)
    image.colorspace_settings.name = 'Linear Rec.709'
    image.pixels.foreach_set(array.ravel())
    scene.render.image_settings.file_format = 'OPEN_EXR'
    scene.render.image_settings.color_depth = '32'
    image.save_render(str(OUT/('blender_'+label+'.exr')),scene=scene)
    if aces:
        x = np.maximum(array[:,:,:3],0)*exposure
        preview = array.copy()
        preview[:,:,:3] = np.clip(x*(2.51*x+.03)/(x*(2.43*x+.59)+.14),0,1)
        image.pixels.foreach_set(preview.ravel())
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_depth = '8'
    image.save_render(str(OUT/('blender_'+label+'.png')),scene=scene)
    scene.render.image_settings.file_format = 'OPEN_EXR'
    scene.render.image_settings.color_depth = '32'
    bpy.data.images.remove(image)


def render(label):
    scene.render.filepath = str(OUT/('blender_'+label+'_render.exr'))
    bpy.ops.render.render(write_still=True)
    image = bpy.data.images.load(scene.render.filepath,check_existing=False)
    array = np.array(image.pixels[:],np.float32).reshape(H,W,4)
    bpy.data.images.remove(image)
    save(array,label)
    values[label] = array
    print('HELMET_RENDER',label,flush=True)


# Save an editable, physically single-bounce helmet-only reference, preserving source file.
background.inputs['Strength'].default_value = 1
sun.hide_render = False
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            area.spaces.active.region_3d.view_perspective = 'CAMERA'
scene.render.use_compositing = True  # Existing baseline graph applies the matching EV/ACES for F12.
scene.render.image_settings.file_format = 'PNG'
scene.render.image_settings.color_depth = '8'
scene.render.filepath = str(OUT/'helmet_f12.png')
saved_sun_angle = sun.data.angle
sun.data.angle = 0  # Editable file defaults to the same delta Sun as Unity direct shading.
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'HelmetOnly_Cycles.blend'))
sun.data.angle = saved_sun_angle  # Also retain the original 2-degree reference renders below.
scene.render.use_compositing = False
scene.render.image_settings.file_format = 'OPEN_EXR'
scene.render.image_settings.color_depth = '32'
render('combined_physical')
background.inputs['Strength'].default_value = 0
render('direct')
background.inputs['Strength'].default_value = 1
sun.hide_render = True
render('ibl_physical')
helmet.visible_shadow = False
helmet.visible_diffuse = False
helmet.visible_glossy = False
render('ibl')
background.inputs['Strength'].default_value = 0
sun.hide_render = False
render('direct_unshadowed')
sun_angle = sun.data.angle
sun.data.angle = 0
render('direct_delta_unshadowed')
helmet.visible_shadow = True
render('direct_delta')
delta_matched = values['direct_delta']+values['ibl']
delta_matched[:,:,3] = values['direct_delta'][:,:,3]
save(delta_matched,'combined_delta_matched')
sun.data.angle = sun_angle
matched = values['direct']+values['ibl']
matched[:,:,3] = values['direct'][:,:,3]
save(matched,'combined_matched')

# Diagnostic material outputs, before BSDF and scene illumination.
original = helmet.data.materials[0]
for channel in ['albedo','normal','roughness_metallic']:
    diagnostic = original.copy()
    nodes,links = diagnostic.node_tree.nodes,diagnostic.node_tree.links
    bsdf = next(n for n in nodes if n.type=='BSDF_PRINCIPLED')
    output = next(n for n in nodes if n.type=='OUTPUT_MATERIAL')
    emission = nodes.new('ShaderNodeEmission')
    links.new(emission.outputs[0],output.inputs['Surface'])
    if channel=='albedo':
        socket = bsdf.inputs['Base Color']
        if socket.is_linked:
            links.new(socket.links[0].from_socket,emission.inputs['Color'])
        else:
            emission.inputs['Color'].default_value = socket.default_value
    elif channel=='normal':
        socket = bsdf.inputs['Normal']
        if socket.is_linked:
            normal = socket.links[0].from_socket
        else:
            normal = nodes.new('ShaderNodeNewGeometry').outputs['Normal']
        encode = nodes.new('ShaderNodeVectorMath')
        encode.operation = next(e.identifier for e in encode.bl_rna.properties['operation'].enum_items if e.identifier=='MULTIPLY_ADD')
        encode.inputs[1].default_value = (.5,.5,.5)
        encode.inputs[2].default_value = (.5,.5,.5)
        links.new(normal,encode.inputs[0])
        links.new(encode.outputs['Vector'],emission.inputs['Color'])
    else:
        combine = nodes.new('ShaderNodeCombineColor')
        for prop,color in [('Roughness','Red'),('Metallic','Green')]:
            socket = bsdf.inputs[prop]
            if socket.is_linked:
                links.new(socket.links[0].from_socket,combine.inputs[color])
            else:
                combine.inputs[color].default_value = socket.default_value
        links.new(combine.outputs['Color'],emission.inputs['Color'])
    helmet.data.materials[0] = diagnostic
    scene.render.filepath = str(OUT/('blender_'+channel+'_render.exr'))
    bpy.ops.render.render(write_still=True)
    image = bpy.data.images.load(scene.render.filepath,check_existing=False)
    array = np.array(image.pixels[:],np.float32).reshape(H,W,4)
    save(array,channel,aces=False)
    bpy.data.images.remove(image)
    helmet.data.materials[0] = original
    bpy.data.materials.remove(diagnostic)
(OUT/'blender_helmet.json').write_text(json.dumps({'samples':512,'max_surface_bounces':1,'camera_from_actual_unity_capture':actual_camera_available,
    'camera_matrix_world':[list(row) for row in camera.matrix_world], 'vertical_fov':meta['verticalFov'], 'resolution':[W,H],
    'source_blend':'SkyboxScene_Cycles.blend','no_other_geometry':True,'sun_only_no_points':True,
    'matched_reference':'Cycles shadowed direct + unoccluded environment IBL; no GI',
    'physical_reference':'Cycles single-bounce with local occlusion, no surface interreflection'},indent=2),encoding='utf-8')
print('HELMET_BASELINE_COMPLETE',flush=True)
