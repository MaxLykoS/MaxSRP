"""One local, downward Lambert probe with/without scene objects and lights.

The helmet itself is excluded so the probe measures incident light at its position.
This is not a claim that a global cubemap should reproduce spatial visibility.
"""
import json
import math
from pathlib import Path
import bpy
import numpy as np
from mathutils import Vector

OUT = Path(__file__).resolve().parents[2]/'OfflineBaseline/SkyboxScene/irradiance_comparison'
source = bpy.context.scene
scene = bpy.data.scenes.new('Local downward irradiance probe - helmet excluded')
scene.world = source.world
scene.render.engine = 'CYCLES'
prefs = bpy.context.preferences.addons['cycles'].preferences
types = [v[0] for v in prefs.get_device_types(bpy.context)]
if 'OPTIX' in types:
    prefs.compute_device_type = next(v for v in types if v=='OPTIX')
    prefs.get_devices()
    for device in prefs.devices:
        device.use = device.type=='OPTIX'
    scene.cycles.device = 'GPU'
scene.cycles.samples = 4096
scene.cycles.use_adaptive_sampling = False
scene.cycles.use_denoising = False
scene.cycles.max_bounces = 8
scene.cycles.diffuse_bounces = 4
scene.cycles.glossy_bounces = 4
scene.cycles.seed = 20261019
scene.render.resolution_x = scene.render.resolution_y = 16
scene.render.resolution_percentage = 100
scene.render.use_compositing = False
scene.render.image_settings.file_format = 'OPEN_EXR'
scene.render.image_settings.color_depth = '32'
scene.render.image_settings.exr_codec = 'ZIP'
mesh = bpy.data.meshes.new('Small local irradiance card')
mesh.from_pydata([(-.002,-.002,0),(.002,-.002,0),(.002,.002,0),(-.002,.002,0)],[],[(0,1,2,3)])
card = bpy.data.objects.new('Downward unit Lambert card',mesh)
card.location = (1.68,0,1.413)
normal = Vector((0,0,-1))
card.rotation_euler = normal.to_track_quat('Z','Y').to_euler()
scene.collection.objects.link(card)
mat = bpy.data.materials.new('Unit Lambert for incident irradiance')
mat.use_nodes = True
nodes = mat.node_tree.nodes
output = next(n for n in nodes if n.type=='OUTPUT_MATERIAL')
diffuse = nodes.new('ShaderNodeBsdfDiffuse')
diffuse.inputs['Color'].default_value = (1,1,1,1)
diffuse.inputs['Roughness'].default_value = 0
mat.node_tree.links.new(diffuse.outputs[0],output.inputs['Surface'])
mesh.materials.append(mat)
data = bpy.data.cameras.new('Close probe camera')
data.type = next(e.identifier for e in data.bl_rna.properties['type'].enum_items if e.identifier=='ORTHO')
data.ortho_scale = .001
data.clip_start = .0001
camera = bpy.data.objects.new('Close probe camera',data)
camera.location = card.location+normal*.01
camera.rotation_euler = (-normal).to_track_quat('-Z','Y').to_euler()
scene.collection.objects.link(camera)
scene.camera = camera
report = {'probe_unity_position':[1.68,1.413,0],'normal_unity':[0,-1,0],
          'excluded_self':'node_damagedHelmet_-6514','measurements':{}}
for label,with_scene in [('sky_only',False),('sky_objects_and_lights',True)]:
    if with_scene:
        for obj in source.objects:
            if obj.type in ['MESH','LIGHT'] and obj.name!='node_damagedHelmet_-6514':
                scene.collection.objects.link(obj)
    scene.render.filepath = str(OUT/('ground_bounce_'+label+'.exr'))
    bpy.ops.render.render(write_still=True,scene=scene.name)
    image = bpy.data.images.load(scene.render.filepath,check_existing=False)
    array = np.array(image.pixels[:],np.float32).reshape(16,16,4)[:,:,:3]
    assert np.isfinite(array).all()
    rgb = array.mean(axis=(0,1))*math.pi*100000
    report['measurements'][label] = {'irradiance_rgb_lux':rgb.tolist(),'illuminance_lux':float(rgb@np.array([.2126,.7152,.0722]))}
    bpy.data.images.remove(image)
    print('LOCAL_PROBE',label,report['measurements'][label],flush=True)
(OUT/'local_ground_bounce.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
