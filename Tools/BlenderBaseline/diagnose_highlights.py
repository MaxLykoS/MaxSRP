"""Controlled Cycles renders: same GPU inputs, alternate BSDFs, point-sized filter.
Only generated diagnostic files are written; no Unity or Blender source assets saved.
"""
import argparse,hashlib,json,math,sys
from pathlib import Path
import bpy,numpy as np

ROOT=Path(__file__).resolve().parents[2]
BASE=ROOT/'OfflineBaseline/HelmetOnly'
parser=argparse.ArgumentParser()
parser.add_argument('--input-directory',type=Path)
parser.add_argument('--output-directory',type=Path)
parser.add_argument('--convergence',action='store_true')
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
OUT=args.output_directory or BASE/'HighlightDiagnosis'
OUT.mkdir(exist_ok=True)
INPUT=args.input_directory or (OUT/'SourceUnityInputs' if (OUT/'SourceUnityInputs/unity_helmet.json').exists() else BASE)
meta=json.loads((INPUT/'unity_helmet.json').read_text(encoding='utf-8'))
assert hashlib.sha256((ROOT/'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/BRDF.hlsl').read_bytes()).hexdigest()==meta['brdfSourceSHA256']
W,H=meta['width'],meta['height']
def read(name):return np.fromfile(INPUT/(name+'.f32'),np.float32).reshape(H,W,4)
def unit(v):return v/np.maximum(np.linalg.norm(v,axis=-1,keepdims=True),1e-12)
scene=bpy.context.scene
helmet=next(o for o in scene.objects if o.type=='MESH')
sun=next(o for o in scene.objects if o.type=='LIGHT' and o.data.type=='SUN')
original=helmet.data.materials[0]
bsdf=next(n for n in original.node_tree.nodes if n.type=='BSDF_PRINCIPLED')
report={'source_capture':meta['capturedUtc'],'scope':'No GI. Delta Sun, no shadows. Actual Unity GBuffer projected through camera window coordinates.',
        'input_directory':str(INPUT),'blender_version':bpy.app.version_string,'build_hash':bpy.app.build_hash.decode(),
        'input_sha256':{name:hashlib.sha256((INPUT/name).read_bytes()).hexdigest() for name in ['unity_helmet.json','unity_direct_unshadowed.f32','unity_gbuffer0.f32','unity_gbuffer1.f32','unity_gbuffer2.f32']},
        'original_pixel_filter':scene.cycles.pixel_filter_type,'original_filter_width':scene.cycles.filter_width,
        'principled_distribution':bsdf.distribution,'renders':[]}
prefs=bpy.context.preferences.addons['cycles'].preferences
prefs.compute_device_type=next(t[0] for t in prefs.get_device_types(bpy.context) if t[0]=='OPTIX')
prefs.get_devices()
for d in prefs.devices:d.use=d.type=='OPTIX'
scene.cycles.device='GPU'
scene.cycles.samples=512
scene.cycles.seed=20261019
scene.cycles.use_adaptive_sampling=False
scene.cycles.use_denoising=False
scene.cycles.max_bounces=1
scene.render.use_compositing=False
scene.render.image_settings.file_format='OPEN_EXR'
scene.render.image_settings.color_depth='32'
scene.render.film_transparent=True
sun.data.angle=0
sun.hide_render=False
helmet.visible_shadow=False
helmet.visible_diffuse=False
helmet.visible_glossy=False
next(n for n in scene.world.node_tree.nodes if n.type=='BACKGROUND').inputs['Strength'].default_value=0

def render(label):
    scene.render.filepath=str(OUT/(label+'.exr'))
    bpy.ops.render.render(write_still=True)
    image=bpy.data.images.load(scene.render.filepath,check_existing=False)
    a=np.array(image.pixels[:],np.float32).reshape(H,W,4)
    bpy.data.images.remove(image)
    assert np.isfinite(a).all(),label
    a.tofile(OUT/(label+'.f32'))
    report['renders'].append(label)
    print('HIGHLIGHT_RENDER',label,flush=True)

render('native_default_filter')
scene.cycles.filter_width=.01
render('native_narrow_filter')
if args.convergence:
    bsdf.distribution='GGX';render('native_narrow_single')
    bsdf.distribution=report['principled_distribution']

def image_node(mat,label,array):
    image=bpy.data.images.new(label,width=W,height=H,float_buffer=True)
    image.colorspace_settings.name='Non-Color'
    image.pixels.foreach_set(np.asarray(array,np.float32).ravel())
    tex=mat.node_tree.nodes.new('ShaderNodeTexImage')
    tex.image=image
    tex.interpolation='Closest'
    tex.extension='EXTEND'
    coords=mat.node_tree.nodes.new('ShaderNodeTexCoord')
    mat.node_tree.links.new(coords.outputs['Window'],tex.inputs['Vector'])
    return tex.outputs['Color']

g0,g1,g2=[read('unity_gbuffer'+str(i)) for i in range(3)]
normal=g1.copy()
normal[:,:,:3]=normal[:,:,[0,2,1]]
projected=original.copy()
pn,pl=projected.node_tree.nodes,projected.node_tree.links
p=next(n for n in pn if n.type=='BSDF_PRINCIPLED')
native_sockets={prop:p.inputs[prop].links[0].from_socket for prop in ['Base Color','Normal','Roughness','Metallic']}
for prop in ['Base Color','Normal','Roughness','Metallic']:
    for link in list(p.inputs[prop].links):pl.remove(link)
pl.new(image_node(projected,'Unity linear albedo',g0),p.inputs['Base Color'])
rm=pn.new('ShaderNodeSeparateColor')
pl.new(image_node(projected,'Unity roughness and metallic',g2),rm.inputs['Color'])
# GBuffer2 B=roughness, A=metallic. A is a separate output on the image node.
rough=pn.new('ShaderNodeMath');rough.operation='MAXIMUM';rough.inputs[1].default_value=.05
pl.new(rm.outputs['Blue'],rough.inputs[0]);pl.new(rough.outputs[0],p.inputs['Roughness'])
rm_tex=next(n for n in pn if n.type=='TEX_IMAGE' and n.image.name=='Unity roughness and metallic')
pl.new(rm_tex.outputs['Alpha'],p.inputs['Metallic'])
normal_tex=image_node(projected,'Unity encoded world normal',normal)
decode=pn.new('ShaderNodeVectorMath');decode.operation='MULTIPLY_ADD'
decode.inputs[1].default_value=(2,2,2);decode.inputs[2].default_value=(-1,-1,-1)
pl.new(normal_tex,decode.inputs[0]);pl.new(decode.outputs['Vector'],p.inputs['Normal'])
helmet.data.materials[0]=projected
render('projected_principled_multi')
if args.convergence:
    scene.cycles.filter_width=report['original_filter_width']
    render('projected_principled_multi_default_filter')
    scene.cycles.filter_width=.01
p.distribution='GGX'
render('projected_principled_single')
metal_socket=p.inputs['Metallic'].links[0].from_socket
pl.remove(p.inputs['Metallic'].links[0])
for value,label in [(0,'projected_dielectric_only'),(1,'projected_metal_only')]:
    p.inputs['Metallic'].default_value=value
    render(label)
pl.new(metal_socket,p.inputs['Metallic'])
unity_sockets={prop:p.inputs[prop].links[0].from_socket for prop in native_sockets}
def set_inputs(sources):
    for prop,socket in sources.items():pl.new(socket,p.inputs[prop])
set_inputs({**unity_sockets,'Normal':native_sockets['Normal']})
render('hybrid_native_normal')
set_inputs({**native_sockets,'Normal':unity_sockets['Normal']})
render('hybrid_unity_normal')
if args.convergence:
    set_inputs({**native_sockets,'Roughness':unity_sockets['Roughness']})
    render('hybrid_unity_roughness')
set_inputs({**native_sockets,'Normal':unity_sockets['Normal'],'Roughness':unity_sockets['Roughness']})
render('hybrid_unity_normal_roughness')
set_inputs({**native_sockets,'Normal':unity_sockets['Normal'],'Roughness':unity_sockets['Roughness'],'Metallic':unity_sockets['Metallic']})
render('hybrid_unity_normal_rm')
set_inputs(unity_sockets)

# Emit the projected inputs to verify that projection and color-space are exact.
output=next(n for n in pn if n.type=='OUTPUT_MATERIAL')
emission=pn.new('ShaderNodeEmission')
pl.new(emission.outputs[0],output.inputs['Surface'])
for label,socket in [('projected_albedo',p.inputs['Base Color'].links[0].from_socket),('projected_normal',normal_tex)]:
    pl.new(socket,emission.inputs['Color']);render(label)
combine=pn.new('ShaderNodeCombineColor')
for prop,color in [('Roughness','Red'),('Metallic','Green')]:pl.new(p.inputs[prop].links[0].from_socket,combine.inputs[color])
pl.new(combine.outputs[0],emission.inputs['Color']);render('projected_rm')

# Reconstruct F and diffuse weights from identical pixel-centre camera rays.
q=meta['cameraRotation'];x,y,z,w=[q[k] for k in ['x','y','z','w']]
R=np.array([[1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)],
            [2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)],
            [2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)]])
xx,yy=np.meshgrid((np.arange(W)+.5)/W*2-1,(np.arange(H)+.5)/H*2-1)
t=np.tan(np.radians(meta['verticalFov']/2))
V=-unit(np.stack([xx*t*W/H,yy*t,np.ones_like(xx)],axis=-1)@R.T)
L=unit(np.array([meta['sunDirection'][k] for k in ['x','y','z']]))
vh=np.clip(np.sum(V*unit(V+L),axis=-1,keepdims=True),0,1)
metal=g2[:,:,3:4]
f0=.04*(1-metal)+g0[:,:,:3]*metal
F=f0+(1-f0)*(1-vh)**5
f_image=np.concatenate([F,np.ones((H,W,1))],axis=-1)
d_image=np.concatenate([(1-F)*(1-metal)*g0[:,:,:3],np.ones((H,W,1))],axis=-1)
custom=bpy.data.materials.new('Unity matching single scatter GGX plus Lambert')
custom.use_nodes=True
cn,cl=custom.node_tree.nodes,custom.node_tree.links
cn.clear()
out=cn.new('ShaderNodeOutputMaterial')
glossy=cn.new('ShaderNodeBsdfGlossy');glossy.distribution='GGX'
diffuse=cn.new('ShaderNodeBsdfDiffuse');diffuse.inputs['Roughness'].default_value=0
add=cn.new('ShaderNodeAddShader')
cl.new(glossy.outputs[0],add.inputs[0]);cl.new(diffuse.outputs[0],add.inputs[1]);cl.new(add.outputs[0],out.inputs['Surface'])
cl.new(image_node(custom,'Schlick Fresnel from Unity inputs',f_image),glossy.inputs['Color'])
cl.new(image_node(custom,'Lambert weight from Unity inputs',d_image),diffuse.inputs['Color'])
rough_image=g2.copy();rough_image[:,:,:3]=np.maximum(g2[:,:,2:3],.05);rough_image[:,:,3]=1
rough_socket=image_node(custom,'Unity clamped roughness',rough_image)
cl.new(rough_socket,glossy.inputs['Roughness'])
dn=cn.new('ShaderNodeVectorMath');dn.operation='MULTIPLY_ADD';dn.inputs[1].default_value=(2,2,2);dn.inputs[2].default_value=(-1,-1,-1)
cl.new(image_node(custom,'Unity world normal custom',normal),dn.inputs[0])
for node in [glossy,diffuse]:cl.new(dn.outputs['Vector'],node.inputs['Normal'])
helmet.data.materials[0]=custom
render('projected_matching_bsdf')
if args.convergence:
    # The projection-validation renders temporarily connected Emission to this
    # material output. Restore the BSDF before sampling-convergence captures.
    pl.new(p.outputs['BSDF'],output.inputs['Surface'])
    # The shared maps use nearest lookup. Only primary-ray footprint and the
    # Cycles shading-point/normal validity rules can vary within each pixel.
    scene.cycles.samples=64
    render('projected_matching_bsdf_64')
    scene.cycles.samples=2048
    render('projected_matching_bsdf_2048')
    helmet.data.materials[0]=projected
    p.distribution='GGX';render('projected_principled_single_2048')
    p.distribution='MULTI_GGX';render('projected_principled_multi_2048')
    p.distribution='GGX';scene.cycles.samples=512
    helmet.data.materials[0]=custom
    ct=np.sqrt(1-(1-vh*vh)/(1.5*1.5))
    dielectric_exact=.5*(((vh-1.5*ct)/(vh+1.5*ct))**2+((1.5*vh-ct)/(1.5*vh+ct))**2)
    metal_F=g0[:,:,:3]+(1-g0[:,:,:3])*(1-vh)**5
    exact_F=(1-metal)*dielectric_exact+metal*metal_F
    separate_diffuse=(1-metal)*(1-(.04+.96*(1-vh)**5))*g0[:,:,:3]
    for label,specular,diff in [
        ('projected_exact_dielectric_specular',exact_F,d_image[:,:,:3]),
        ('projected_separate_diffuse',F,separate_diffuse),
        ('projected_exact_and_separate',exact_F,separate_diffuse)]:
        for node,array,suffix in [(glossy,specular,' specular'),(diffuse,diff,' diffuse')]:
            rgba=np.concatenate([array,np.ones((H,W,1))],axis=-1)
            cl.new(image_node(custom,label+suffix,rgba),node.inputs['Color'])
        render(label)

# Native inputs sampled with a narrow filter, before any BSDF.
helmet.data.materials[0]=original
diagnostic=original.copy()
dn,dl=diagnostic.node_tree.nodes,diagnostic.node_tree.links
dp=next(n for n in dn if n.type=='BSDF_PRINCIPLED')
do=next(n for n in dn if n.type=='OUTPUT_MATERIAL')
de=dn.new('ShaderNodeEmission');dl.new(de.outputs[0],do.inputs['Surface'])
helmet.data.materials[0]=diagnostic
for prop,label in [('Base Color','native_albedo_narrow'),('Normal','native_normal_narrow')]:
    socket=dp.inputs[prop].links[0].from_socket
    if prop=='Normal':
        encode=dn.new('ShaderNodeVectorMath');encode.operation='MULTIPLY_ADD';encode.inputs[1].default_value=(.5,.5,.5);encode.inputs[2].default_value=(.5,.5,.5)
        dl.new(socket,encode.inputs[0]);socket=encode.outputs['Vector']
    dl.new(socket,de.inputs['Color']);render(label)
combine=dn.new('ShaderNodeCombineColor')
for prop,color in [('Roughness','Red'),('Metallic','Green')]:dl.new(dp.inputs[prop].links[0].from_socket,combine.inputs[color])
dl.new(combine.outputs[0],de.inputs['Color']);render('native_rm_narrow')
# Smooth mesh normal without a normal map, for an independent tangent-frame check.
geo=dn.new('ShaderNodeNewGeometry')
encode=dn.new('ShaderNodeVectorMath');encode.operation='MULTIPLY_ADD'
encode.inputs[1].default_value=(.5,.5,.5);encode.inputs[2].default_value=(.5,.5,.5)
dl.new(geo.outputs['Normal'],encode.inputs[0]);dl.new(encode.outputs['Vector'],de.inputs['Color'])
render('native_geometric_normal')
(OUT/'render_info.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print('HIGHLIGHT_DIAGNOSIS_COMPLETE',flush=True)
