"""Direct-light formula probes on one flat untextured surface: no self reflection.
Record the exact Blender build and compare GGX versus Principled MULTI_GGX.
"""
import json,math
from pathlib import Path
import bpy,numpy as np
from mathutils import Vector
OUT=Path(__file__).resolve().parents[2]/'OfflineBaseline/FormulaComparison'
OUT.mkdir(exist_ok=True)
scene=bpy.context.scene
for o in list(scene.objects):bpy.data.objects.remove(o,do_unlink=True)
scene.render.engine='CYCLES'
prefs=bpy.context.preferences.addons['cycles'].preferences
prefs.compute_device_type=next(v[0] for v in prefs.get_device_types(bpy.context) if v[0]=='OPTIX')
prefs.get_devices()
for d in prefs.devices:d.use=d.type=='OPTIX'
scene.cycles.device='GPU';scene.cycles.samples=16
scene.cycles.max_bounces=1;scene.cycles.use_adaptive_sampling=False;scene.cycles.use_denoising=False
scene.render.resolution_x=scene.render.resolution_y=4;scene.render.resolution_percentage=100
scene.render.use_compositing=False
scene.render.image_settings.file_format='OPEN_EXR';scene.render.image_settings.color_depth='32'
scene.view_settings.view_transform='Standard';scene.view_settings.look='None'
scene.world.use_nodes=True
next(n for n in scene.world.node_tree.nodes if n.type=='BACKGROUND').inputs['Strength'].default_value=0
mesh=bpy.data.meshes.new('One flat card')
mesh.from_pydata([(-5,-5,0),(5,-5,0),(5,5,0),(-5,5,0)],[],[(0,1,2,3)])
card=bpy.data.objects.new('One flat card',mesh);scene.collection.objects.link(card)
card.visible_shadow=card.visible_glossy=card.visible_diffuse=False
mat=bpy.data.materials.new('Principled formula probe');mat.use_nodes=True;mesh.materials.append(mat)
bsdf=next(n for n in mat.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
bsdf.inputs['IOR'].default_value=1.5
for name in ['Coat Weight','Sheen Weight','Subsurface Weight','Transmission Weight']:bsdf.inputs[name].default_value=0
cam=bpy.data.objects.new('Ortho',bpy.data.cameras.new('Ortho'));scene.collection.objects.link(cam);scene.camera=cam
cam.data.type='ORTHO';cam.data.ortho_scale=.1
sun=bpy.data.objects.new('Unit delta sun',bpy.data.lights.new('Unit delta sun','SUN'));scene.collection.objects.link(sun)
sun.data.energy=1;sun.data.angle=0;sun.data.normalize=True
report={'blender_version':bpy.app.version_string,'build_hash':bpy.app.build_hash.decode(),
        'scope':'One flat card, no textures, no environment, no secondary-ray visibility, unit delta-Sun irradiance, same N/V/L/material inputs. Values include NdotL.',
        'cases':[]}
for cosine in [1,.5,math.cos(math.radians(75))]:
    sine=math.sqrt(1-cosine*cosine)
    cam.location=(3*sine,0,3*cosine);cam.rotation_euler=(-cam.location).to_track_quat('-Z','Y').to_euler()
    sun.rotation_euler=(-Vector((-sine,0,cosine))).to_track_quat('-Z','Y').to_euler()
    for rough in [.3,.7]:
        for base,metal in [(c,m) for c in [.18,.8] for m in [0,.5,1]]+[(0,0)]:
            bsdf.inputs['Roughness'].default_value=rough;bsdf.inputs['Metallic'].default_value=metal
            bsdf.inputs['Base Color'].default_value=(base,base,base,1)
            row={'cosine':cosine,'roughness':rough,'base_color':base,'metalness':metal}
            for dist in ['GGX','MULTI_GGX']:
                bsdf.distribution=dist
                scene.render.filepath=str(OUT/'point_tmp.exr');bpy.ops.render.render(write_still=True)
                im=bpy.data.images.load(scene.render.filepath,check_existing=False)
                px=np.array(im.pixels[:],np.float32).reshape(4,4,4);bpy.data.images.remove(im)
                row['cycles_'+dist]=float(px[:,:,:3].mean())
            report['cases'].append(row)
(OUT/'cycles_formula_points.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print('CYCLES_FORMULA_POINTS',len(report['cases']),report['build_hash'],flush=True)
