"""Native-UV Cycles baseline using real GPU-decoded Unity imported textures.

Run against HelmetOnly_Cycles.blend. A new packed .blend is saved; source assets
are untouched. Mip zero + bilinear is an explicit shared diagnostic convention,
not a claim that Cycles automatically reproduces Unity's implicit LOD policy.
"""
import hashlib,json,math
from pathlib import Path
import bpy,numpy as np
from mathutils import Matrix,Quaternion,Vector

ROOT=Path(__file__).resolve().parents[2]
OUT=ROOT/'OfflineBaseline/HelmetOnly/ImportedTextures'
meta=json.loads((OUT/'unity_helmet.json').read_text(encoding='utf-8'))
imported=json.loads((OUT/'imported_textures.json').read_text(encoding='utf-8'))
W,H=meta['width'],meta['height']
assert meta['brdfSourceSHA256']==hashlib.sha256((ROOT/'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/BRDF.hlsl').read_bytes()).hexdigest()
scene=bpy.context.scene
helmet=next(o for o in scene.objects if o.type=='MESH')
sun=next(o for o in scene.objects if o.type=='LIGHT' and o.data.type=='SUN')
background=next(n for n in scene.world.node_tree.nodes if n.type=='BACKGROUND')
q=meta['cameraRotation'];p=meta['cameraPosition']
C=Matrix(((1,0,0,0),(0,0,1,0),(0,1,0,0),(0,0,0,1)))
scene.camera.matrix_world=C@Matrix.LocRotScale(Vector(tuple(p[k] for k in ['x','y','z'])),
    Quaternion(tuple(q[k] for k in ['w','x','y','z'])),Vector((1,1,1)))@Matrix.Diagonal((1,1,-1,1))
scene.camera.data.lens=12/math.tan(math.radians(meta['verticalFov'])/2)
scene.camera.data.sensor_fit='VERTICAL';scene.camera.data.sensor_height=24
scene.render.engine='CYCLES';scene.render.resolution_x=W;scene.render.resolution_y=H
scene.render.resolution_percentage=100;scene.render.film_transparent=True
scene.cycles.samples=512;scene.cycles.seed=20261019;scene.cycles.max_bounces=1
scene.cycles.use_adaptive_sampling=False;scene.cycles.use_denoising=False
scene.cycles.filter_width=.01
scene.cycles.sample_clamp_direct=scene.cycles.sample_clamp_indirect=0
prefs=bpy.context.preferences.addons['cycles'].preferences;prefs.compute_device_type='OPTIX';prefs.get_devices()
for d in prefs.devices:d.use=d.type=='OPTIX'
scene.cycles.device='GPU';sun.data.angle=0;sun.hide_render=False
helmet.visible_diffuse=helmet.visible_glossy=False
helmet.visible_shadow=False
scene.render.image_settings.file_format='OPEN_EXR';scene.render.image_settings.color_depth='32'
scene.view_settings.view_transform='Standard';scene.view_settings.look='None'
scene.view_settings.exposure=0;scene.view_settings.gamma=1

mat=bpy.data.materials.new('Unity imported data - native UV - bilinear mip zero')
mat.use_nodes=True;nodes,links=mat.node_tree.nodes,mat.node_tree.links
nodes.clear();output=nodes.new('ShaderNodeOutputMaterial')
bsdf=nodes.new('ShaderNodeBsdfPrincipled');bsdf.distribution='MULTI_GGX'
bsdf.inputs['IOR'].default_value=1.5;bsdf.inputs['Specular IOR Level'].default_value=.5
for name in ['Coat Weight','Sheen Weight','Subsurface Weight','Transmission Weight']:bsdf.inputs[name].default_value=0
links.new(bsdf.outputs[0],output.inputs['Surface'])
uv=nodes.new('ShaderNodeTexCoord')
def bind(value,socket):
    if hasattr(value,'node'):links.new(value,socket)
    else:socket.default_value=value
def scalar(op,a,b=0):
    n=nodes.new('ShaderNodeMath');n.operation=op
    bind(a,n.inputs[0]);bind(b,n.inputs[1]);return n.outputs[0]
def vector(op,a,b=(0,0,0),c=(0,0,0)):
    n=nodes.new('ShaderNodeVectorMath');n.operation=op
    bind(a,n.inputs[0])
    if op=='SCALE':bind(b,n.inputs['Scale'])
    else:
        if len(n.inputs)>1:bind(b,n.inputs[1])
        if len(n.inputs)>2:bind(c,n.inputs[2])
    return n.outputs['Value'] if op in ['DOT_PRODUCT','LENGTH','DISTANCE'] else n.outputs['Vector']
def combine(r,g,b):
    n=nodes.new('ShaderNodeCombineXYZ')
    for value,socket in zip([r,g,b],n.inputs):bind(value,socket)
    return n.outputs[0]
def split(rgb):
    n=nodes.new('ShaderNodeSeparateXYZ');links.new(rgb,n.inputs[0]);return n.outputs
def quant_vector(value,bits):
    levels=(1<<bits)-1
    return vector('SCALE',vector('FLOOR',vector('MULTIPLY_ADD',value,(levels,)*3,(.5,)*3)),1/levels)
def quant_scalar(value,bits):
    levels=(1<<bits)-1
    return scalar('DIVIDE',scalar('FLOOR',scalar('ADD',scalar('MULTIPLY',value,levels),.5)),levels)

textures={};files={};report={'build_hash':bpy.app.build_hash.decode(),'source_capture':meta['capturedUtc'],
    'scope':'Native UV/tangent evaluation; actual imported GPU texels; mip zero/bilinear; no screen-projected input maps, no GI/shadows.',
    'textures':[],'renders':[]}
for entry in imported['textures']:
    assert hashlib.sha256((ROOT/entry['sourcePath']).read_bytes()).hexdigest()==entry['sourceSHA256']
    assert hashlib.sha256((ROOT/(entry['sourcePath']+'.meta')).read_bytes()).hexdigest()==entry['importerSHA256']
    path=OUT/entry['mipFiles'][0]
    im=bpy.data.images.load(str(path),check_existing=False);im.colorspace_settings.name='Non-Color';im.alpha_mode='CHANNEL_PACKED'
    assert tuple(im.size)==(entry['width'],entry['height'])
    n=nodes.new('ShaderNodeTexImage');n.image=im;n.label=entry['property']+' actual Unity mip zero'
    n.interpolation='Linear';n.extension='REPEAT';links.new(uv.outputs['UV'],n.inputs['Vector'])
    textures[entry['property']]=n;files[entry['property']]=str(path)
    report['textures'].append({**entry,'mip0_sha256':hashlib.sha256(path.read_bytes()).hexdigest()})

albedo=quant_vector(textures['_AlbedoMap'].outputs['Color'],8)
rough=scalar('MAXIMUM',quant_scalar(split(textures['_RoughnessMap'].outputs['Color'])[0],16),.05)
metal=quant_scalar(split(textures['_MetalnessMap'].outputs['Color'])[0],16)
normal_tex=textures['_NormalMap'];parts=split(normal_tex.outputs['Color'])
# Reconstruct Z AFTER bilinear filtering of imported XY, as in Packing.hlsl.
encoded_x=scalar('MULTIPLY',parts[0],normal_tex.outputs['Alpha'])
x=scalar('SUBTRACT',scalar('MULTIPLY',encoded_x,2),1)
y=scalar('SUBTRACT',scalar('MULTIPLY',parts[1],2),1)
z=scalar('SQRT',scalar('MAXIMUM',scalar('SUBTRACT',1,scalar('ADD',scalar('MULTIPLY',x,x),scalar('MULTIPLY',y,y))),0))
normal_map=nodes.new('ShaderNodeNormalMap');normal_map.inputs['Strength'].default_value=1
links.new(combine(encoded_x,parts[1],scalar('MULTIPLY',scalar('ADD',z,1),.5)),normal_map.inputs['Color'])
normal=vector('NORMALIZE',vector('MULTIPLY_ADD',quant_vector(vector('MULTIPLY_ADD',normal_map.outputs[0],(.5,)*3,(.5,)*3),10),(2,)*3,(-1,)*3))
for value,name in [(albedo,'Base Color'),(rough,'Roughness'),(metal,'Metallic'),(normal,'Normal')]:links.new(value,bsdf.inputs[name])
helmet.data.materials[0]=mat

# A separate packed scene preserves native UV evaluation and the original sky.
# Its F12 view is combined lighting; numeric captures below isolate direct only.
background.inputs['Strength'].default_value=1
scene.render.use_compositing=True
scene.render.image_settings.file_format='PNG';scene.render.image_settings.color_depth='8'
scene.render.filepath=str(OUT/'imported_baseline_f12.png')
scene['baseline_scope']='Unity imported texture data; native UV; mip zero bilinear; no scene GI; EV/ACES compositor.'
bpy.data.orphans_purge(do_recursive=True)
bpy.ops.file.pack_all()
blend=OUT/'HelmetOnly_ImportedTextures_Cycles.blend'
bpy.ops.wm.save_as_mainfile(filepath=str(blend))
report['saved_blend']=str(blend)
scene.render.use_compositing=False
scene.render.image_settings.file_format='OPEN_EXR';scene.render.image_settings.color_depth='32'
background.inputs['Strength'].default_value=0
def render(name):
    scene.render.filepath=str(OUT/(name+'.exr'));bpy.ops.render.render(write_still=True)
    im=bpy.data.images.load(scene.render.filepath,check_existing=False)
    a=np.array(im.pixels[:],np.float32).reshape(H,W,4);bpy.data.images.remove(im)
    assert np.isfinite(a).all(),name;a.tofile(OUT/(name+'.f32'))
    report['renders'].append(name);print('IMPORTED_BASELINE_RENDER',name,flush=True)
render('native_imported_multi')
bsdf.distribution='GGX';render('native_imported_single')
emission=nodes.new('ShaderNodeEmission');links.new(emission.outputs[0],output.inputs['Surface'])
for name,socket in [('native_imported_albedo',albedo),('native_imported_rm',combine(rough,metal,0)),
                    ('native_imported_normal',vector('MULTIPLY_ADD',normal,(.5,)*3,(.5,)*3))]:
    links.new(socket,emission.inputs['Color']);render(name)

# Independent native-UV material with the production Schlick + Lambert weights.
# No camera-window image or precomputed per-pixel Fresnel is used here.
geo=nodes.new('ShaderNodeNewGeometry')
light=tuple(meta['sunDirection'][k] for k in ['x','z','y'])
half=vector('NORMALIZE',vector('ADD',geo.outputs['Incoming'],light))
vh=scalar('MINIMUM',scalar('MAXIMUM',vector('DOT_PRODUCT',geo.outputs['Incoming'],half),0),1)
power=scalar('POWER',scalar('SUBTRACT',1,vh),5)
f0=vector('ADD',vector('SCALE',albedo,metal),combine(*[scalar('MULTIPLY',.04,scalar('SUBTRACT',1,metal))]*3))
F=vector('ADD',f0,vector('SCALE',vector('SUBTRACT',(1,)*3,f0),power))
diff=vector('MULTIPLY',vector('SCALE',vector('SUBTRACT',(1,)*3,F),scalar('SUBTRACT',1,metal)),albedo)
glossy=nodes.new('ShaderNodeBsdfGlossy');glossy.distribution='GGX'
lambert=nodes.new('ShaderNodeBsdfDiffuse');lambert.inputs['Roughness'].default_value=0
for node,col in [(glossy,F),(lambert,diff)]:
    links.new(col,node.inputs['Color']);links.new(normal,node.inputs['Normal'])
links.new(rough,glossy.inputs['Roughness'])
add=nodes.new('ShaderNodeAddShader');links.new(glossy.outputs[0],add.inputs[0]);links.new(lambert.outputs[0],add.inputs[1])
links.new(add.outputs[0],output.inputs['Surface'])
render('native_imported_matching_bsdf')
# Odd resolution scaling keeps the selected subpixel exactly on the original
# pixel centre. This narrows the effective filter without exceeding Cycles'
# filter_width minimum. It is point-sampling validation, not supersampled AA.
scene.render.resolution_x=W*5;scene.render.resolution_y=H*5;scene.cycles.samples=64
def point_render(name):
    scene.render.filepath=str(OUT/'point_sampling_temporary.exr');bpy.ops.render.render(write_still=True)
    im=bpy.data.images.load(scene.render.filepath,check_existing=False)
    a=np.array(im.pixels[:],np.float32).reshape(H*5,W*5,4)[2::5,2::5].copy();bpy.data.images.remove(im)
    assert a.shape==(H,W,4) and np.isfinite(a).all()
    a.tofile(OUT/(name+'.f32'));report['renders'].append(name)
    print('IMPORTED_POINT_RENDER',name,flush=True)
point_render('native_imported_matching_bsdf_point')
links.new(bsdf.outputs[0],output.inputs['Surface']);bsdf.distribution='MULTI_GGX'
point_render('native_imported_multi_point')
(OUT/'native_imported_info.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print('IMPORTED_BASELINE_COMPLETE',str(blend),flush=True)
