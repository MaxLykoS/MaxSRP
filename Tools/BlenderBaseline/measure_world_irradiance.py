"""Measure the saved Blender world's actual lighting with six pure Lambert probes.

Run in a separate background process against the baseline .blend; never saves it.
Raw probe radiance times pi is irradiance in the shared RGB / 100000 scale.
"""
import json
import math
from pathlib import Path

import bpy
import numpy as np
from mathutils import Vector

OUT = Path(__file__).resolve().parents[2] / 'OfflineBaseline/SkyboxScene/irradiance_comparison'
OUT.mkdir(parents=True, exist_ok=True)
source_scene = bpy.context.scene
world = source_scene.world
assert world and world.use_nodes
report = {'blend_file':bpy.data.filepath,'source_scene':source_scene.name,'world':world.name,
          'hdr_scale':100000,'world_nodes':[],'world_links':[], 'probes':{}}

for node in world.node_tree.nodes:
    description = {'type':node.type,'name':node.name,'inputs':{}}
    for socket in node.inputs:
        if hasattr(socket,'default_value'):
            value = socket.default_value
            if isinstance(value,(int,float,str,bool)):
                description['inputs'][socket.name] = value
            elif hasattr(value,'__len__'):
                description['inputs'][socket.name] = list(value)
    if node.type == 'TEX_ENVIRONMENT' and node.image:
        im = node.image
        description['image'] = {'name':im.name,'path':im.filepath,'size':list(im.size),
                                'color_space':im.colorspace_settings.name,'packed':bool(im.packed_file)}
        array = np.empty(len(im.pixels),np.float32)
        im.pixels.foreach_get(array)
        filename = 'blender_environment_'+str(len(report['world_nodes']))+'.f32'
        array.tofile(OUT/filename)
        description['image']['raw_file'] = filename
    if node.type == 'TEX_SKY':
        description['sky'] = {p:getattr(node,p) for p in ['sky_type','sun_disc','sun_elevation','sun_rotation','sun_intensity'] if hasattr(node,p)}
    report['world_nodes'].append(description)
for link in world.node_tree.links:
    report['world_links'].append([link.from_node.name,link.from_socket.name,link.to_node.name,link.to_socket.name])

scene = bpy.data.scenes.new('One-shot world irradiance probes')
scene.world = world
scene.render.engine = 'CYCLES'
prefs = bpy.context.preferences.addons['cycles'].preferences
types = [v[0] for v in prefs.get_device_types(bpy.context)]
if 'OPTIX' in types:
    prefs.compute_device_type = next(t for t in types if t=='OPTIX')
    prefs.get_devices()
    for device in prefs.devices:
        device.use = device.type == 'OPTIX'
    scene.cycles.device = 'GPU'
scene.cycles.samples = 4096
scene.cycles.use_adaptive_sampling = False
scene.cycles.use_denoising = False
scene.cycles.max_bounces = scene.cycles.diffuse_bounces = 1
scene.cycles.sample_clamp_direct = scene.cycles.sample_clamp_indirect = 0
scene.cycles.seed = 20261019
scene.render.resolution_x = scene.render.resolution_y = 16
scene.render.resolution_percentage = 100
scene.render.use_compositing = False
scene.render.image_settings.file_format = 'OPEN_EXR'
scene.render.image_settings.color_depth = '32'
scene.render.image_settings.exr_codec = 'ZIP'
mesh = bpy.data.meshes.new('Probe card')
mesh.from_pydata([(-2,-2,0),(2,-2,0),(2,2,0),(-2,2,0)],[],[(0,1,2,3)])
card = bpy.data.objects.new('Unit Lambert probe',mesh)
scene.collection.objects.link(card)
mat = bpy.data.materials.new('Pure Lambert rho=1')
mat.use_nodes = True
nodes = mat.node_tree.nodes
output = next(n for n in nodes if n.type=='OUTPUT_MATERIAL')
bsdf = nodes.new('ShaderNodeBsdfDiffuse')
bsdf.inputs['Color'].default_value = (1,1,1,1)
bsdf.inputs['Roughness'].default_value = 0
mat.node_tree.links.new(bsdf.outputs[0],output.inputs['Surface'])
mesh.materials.append(mat)
camera_data = bpy.data.cameras.new('Probe camera')
camera_data.type = next(e.identifier for e in camera_data.bl_rna.properties['type'].enum_items if e.identifier=='ORTHO')
camera_data.ortho_scale = .1
camera = bpy.data.objects.new('Probe camera',camera_data)
scene.collection.objects.link(camera)
scene.camera = camera
weights = np.array([.2126,.7152,.0722])
# Unity (x,y,z) -> Blender (x,z,y).
for name, direction in [('up',(0,0,1)),('down',(0,0,-1)),('positive_x',(1,0,0)),('negative_x',(-1,0,0)),
                        ('positive_z',(0,1,0)),('negative_z',(0,-1,0))]:
    n = Vector(direction)
    card.rotation_euler = n.to_track_quat('Z','Y').to_euler()
    camera.location = n*3
    camera.rotation_euler = (-n).to_track_quat('-Z','Y').to_euler()
    scene.render.filepath = str(OUT/('blender_probe_'+name+'.exr'))
    bpy.ops.render.render(write_still=True,scene=scene.name)
    im = bpy.data.images.load(scene.render.filepath,check_existing=False)
    array = np.array(im.pixels[:],np.float32).reshape(16,16,4)[:,:,:3]
    assert np.isfinite(array).all()
    rgb = array.mean(axis=(0,1))*math.pi*100000
    report['probes'][name] = {'irradiance_rgb_lux':rgb.tolist(),'illuminance_lux':float(rgb@weights)}
    bpy.data.images.remove(im)
    print('WORLD_PROBE',name,report['probes'][name],flush=True)
(OUT/'blender_world_measurement.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print('BLENDER_WORLD_MEASURED',flush=True)
