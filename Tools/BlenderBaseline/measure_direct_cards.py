"""Independent Cycles constant-metal cards isolate the GGX geometry term.

Sun angle 0, no environment/shadows/bounces, orthographic rays, F0=1.
No source blend or Unity scene is saved or changed.
"""
import json,math
from pathlib import Path
import bpy,numpy as np
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT/'OfflineBaseline/HelmetOnly'
original = bpy.context.scene
source_sun = next(o for o in original.objects if o.type=='LIGHT' and o.data.type=='SUN')
source_bsdf = next(n for o in original.objects if o.type=='MESH' for m in o.data.materials if m and m.use_nodes
                   for n in m.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
report = {'source_sun':{'type':source_sun.data.type,'angle_degrees':math.degrees(source_sun.data.angle),
                       'energy':source_sun.data.energy,'color':list(source_sun.data.color)},
          'source_principled_distribution':source_bsdf.distribution,
          'scope':'Constant white metal F0=1. Orthographic camera. Delta Sun irradiance 1. Black World. No shadows, indirect bounces, textures or normal mapping.',
          'cases':[]}
scene = bpy.data.scenes.new('Direct BRDF cards')
bpy.context.window.scene = scene
scene.render.engine='CYCLES'
prefs = bpy.context.preferences.addons['cycles'].preferences
types = [e[0] for e in prefs.get_device_types(bpy.context)]
if 'OPTIX' in types:
    prefs.compute_device_type=next(t for t in types if t=='OPTIX')
    prefs.get_devices()
    for d in prefs.devices:d.use=d.type=='OPTIX'
    scene.cycles.device='GPU'
scene.cycles.samples=16
scene.cycles.use_denoising=False
scene.cycles.use_adaptive_sampling=False
scene.cycles.max_bounces=1
scene.render.resolution_x=scene.render.resolution_y=4
scene.render.resolution_percentage=100
scene.render.use_compositing=False
scene.render.image_settings.file_format='OPEN_EXR'
scene.render.image_settings.color_depth='32'
scene.view_settings.view_transform='Standard'
scene.view_settings.look='None'
scene.world=bpy.data.worlds.new('Black measurement world')
scene.world.use_nodes=True
next(n for n in scene.world.node_tree.nodes if n.type=='BACKGROUND').inputs['Strength'].default_value=0
mesh=bpy.data.meshes.new('Card')
mesh.from_pydata([(-5,-5,0),(5,-5,0),(5,5,0),(-5,5,0)],[],[(0,1,2,3)])
card=bpy.data.objects.new('Card',mesh)
scene.collection.objects.link(card)
card.visible_shadow=False
mat=bpy.data.materials.new('Constant white metal')
mat.use_nodes=True
bsdf=next(n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
bsdf.inputs['Base Color'].default_value=(1,1,1,1)
bsdf.inputs['Metallic'].default_value=1
bsdf.inputs['Coat Weight'].default_value=0
mesh.materials.append(mat)
camera=bpy.data.objects.new('Ortho',bpy.data.cameras.new('Ortho'))
scene.collection.objects.link(camera)
scene.camera=camera
camera.data.type='ORTHO'
camera.data.ortho_scale=.1
light=bpy.data.objects.new('Delta Sun',bpy.data.lights.new('Delta Sun','SUN'))
scene.collection.objects.link(light)
light.data.energy=1
light.data.angle=0
light.data.normalize=True

for cosine in [.3,.5,.8]:
    sine=math.sqrt(1-cosine*cosine)
    camera.location=(3*sine,0,3*cosine)
    camera.rotation_euler=(-camera.location).to_track_quat('-Z','Y').to_euler()
    L=Vector((-sine,0,cosine))
    light.rotation_euler=(-L).to_track_quat('-Z','Y').to_euler()
    for roughness in [.1,.3,.6,.9]:
        bsdf.inputs['Roughness'].default_value=roughness
        a2=roughness**4
        D=1/(math.pi*a2)
        k=(roughness+1)**2/8
        Gschlick=(cosine/(cosine*(1-k)+k))**2
        Gsmith=(2*cosine/(cosine+math.sqrt(a2+(1-a2)*cosine*cosine)))**2
        Gcorr=cosine/math.sqrt(a2+(1-a2)*cosine*cosine)
        predictions={'unity_schlick_epsilon':D*Gschlick/(4*cosine*cosine+.001)*cosine,
                     'schlick_no_epsilon':D*Gschlick/(4*cosine),
                     'smith_exact':D*Gsmith/(4*cosine),
                     'smith_correlated':D*Gcorr/(4*cosine)}
        row={'NdotV_equals_NdotL':cosine,'roughness':roughness,'predictions':predictions,'cycles':{}}
        for distribution in ['GGX','MULTI_GGX']:
            bsdf.distribution=next(e.identifier for e in bsdf.bl_rna.properties['distribution'].enum_items if e.identifier==distribution)
            scene.render.filepath=str(OUT/'direct_card_tmp.exr')
            bpy.ops.render.render(write_still=True)
            image=bpy.data.images.load(scene.render.filepath,check_existing=False)
            pixels=np.array(image.pixels[:],np.float32).reshape(4,4,4)
            bpy.data.images.remove(image)
            row['cycles'][distribution]=float(pixels[:,:,:3].mean())
        report['cases'].append(row)
        print('CARD',json.dumps(row),flush=True)
# Repeat the actual textured helmet with Cycles' single-scattering GGX option.
# Compare with the existing MULTI_GGX delta-Sun capture; no saved blend is changed.
bpy.context.window.scene=original
helmet=next(o for o in original.objects if o.type=='MESH')
helmet.visible_shadow=False
helmet.visible_diffuse=False
helmet.visible_glossy=False
source_sun.hide_render=False
source_sun.data.angle=0
next(n for n in original.world.node_tree.nodes if n.type=='BACKGROUND').inputs['Strength'].default_value=0
source_bsdf.distribution=next(e.identifier for e in source_bsdf.bl_rna.properties['distribution'].enum_items if e.identifier=='GGX')
original.render.use_compositing=False
original.render.image_settings.file_format='OPEN_EXR'
original.render.image_settings.color_depth='32'
original.render.filepath=str(OUT/'blender_direct_delta_single_ggx.exr')
bpy.ops.render.render(write_still=True)
image=bpy.data.images.load(original.render.filepath,check_existing=False)
W,H=original.render.resolution_x,original.render.resolution_y
pixels=np.array(image.pixels[:],np.float32).reshape(H,W,4)
bpy.data.images.remove(image)
pixels.tofile(OUT/'blender_direct_delta_single_ggx.f32')
report['helmet_single_ggx_output']='blender_direct_delta_single_ggx.f32'
(OUT/'direct_card_diagnosis.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print('DIRECT_CARDS_COMPLETE',flush=True)
