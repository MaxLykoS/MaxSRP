"""Attribute the current BC5/BC7 direct mismatch on one frozen pixel mask.

The staged mean differences telescope exactly but depend on the chosen order;
they are controlled interventions, not universal percentages of physical energy.
"""
import hashlib,json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont

ROOT=Path(__file__).resolve().parents[2]
OUT=ROOT/'OfflineBaseline/HelmetOnly/MatchedInputsBC5BC7'
INPUT=OUT/'SourceUnityInputs'
meta=json.loads((INPUT/'unity_helmet.json').read_text(encoding='utf-8'))
info=json.loads((OUT/'render_info.json').read_text(encoding='utf-8'))
W,H=meta['width'],meta['height']
Y=np.array([.2126,.7152,.0722])
def read(directory,name):
    a=np.fromfile(directory/(name+'.f32'),np.float32).reshape(H,W,4).astype(np.float64)
    assert np.isfinite(a).all(),name
    return a
def unit(a):return a/np.maximum(np.linalg.norm(a,axis=-1,keepdims=True),1e-12)
for name,digest in info['input_sha256'].items():
    assert hashlib.sha256((INPUT/name).read_bytes()).hexdigest()==digest,name
assert meta['brdfSourceSHA256']==hashlib.sha256((ROOT/'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/BRDF.hlsl').read_bytes()).hexdigest()
gpu=read(INPUT,'unity_direct_unshadowed')
reference=read(INPUT,'blender_direct_delta_unshadowed')
coverage=read(INPUT,'blender_combined_matched')
mask=(gpu[...,3]>.5)&(coverage[...,3]>.99)
for _ in range(3):
    old=mask.copy()
    mask[1:-1,1:-1]=old[1:-1,1:-1]&old[:-2,1:-1]&old[2:,1:-1]&old[1:-1,:-2]&old[1:-1,2:]
    mask[0]=mask[-1]=False;mask[:,0]=mask[:,-1]=False
previous=json.loads((INPUT/'comparison.json').read_text(encoding='utf-8'))
assert int(mask.sum())==previous['common_interior_pixels']
def mean(a):return float((a[mask,:3]@Y).mean())
def stats(a,b):
    ay=a[mask,:3]@Y;by=b[mask,:3]@Y
    return {'mean':float(ay.mean()),'reference_mean':float(by.mean()),
            'mean_ratio':float(ay.mean()/by.mean()),
            'relative_L1':float(np.abs(ay-by).sum()/by.sum()),
            'relative_L2':float(np.sqrt(np.mean((ay-by)**2))/by.mean())}
images={n:read(OUT,n) for n in info['renders']}
native=images['native_default_filter']
baseline=stats(gpu,reference)
assert abs(baseline['mean_ratio']-previous['comparisons']['direct_unshadowed_vs_direct_delta_unshadowed']['unity_to_blender_mean_ratio'])<1e-7
assert abs(mean(native)/mean(reference)-1)<1e-4,'Native reference changed; investigate before attributing differences.'
g0,g1,g2=[read(INPUT,'unity_gbuffer'+str(i)) for i in range(3)]
pa,pn,prm=[images[n] for n in ['projected_albedo','projected_normal','projected_rm']]
projection={
    'albedo_MAE':float(np.abs(pa[mask,:3]-g0[mask,:3]).mean()),
    'encoded_normal_MAE':float(np.abs(pn[mask,:3]-g1[...,[0,2,1]][mask]).mean()),
    'effective_roughness_metallic_MAE':np.abs(prm[mask,:2]-np.stack([np.maximum(g2[...,2],.05),g2[...,3]],axis=-1)[mask]).mean(axis=0).tolist()}
assert max(projection['albedo_MAE'],projection['encoded_normal_MAE'],*projection['effective_roughness_metallic_MAE'])<3e-5
chain=['native_default_filter','native_narrow_filter','native_narrow_single','hybrid_unity_normal',
       'hybrid_unity_normal_roughness','hybrid_unity_normal_rm','projected_principled_single',
       'projected_matching_bsdf']
gap=mean(native)-mean(gpu)
stages=[]
for i,name in enumerate(chain):
    a=images[name]
    change=0 if i==0 else mean(images[chain[i-1]])-mean(a)
    stages.append({'render':name,'cycles_mean':mean(a),'unity_to_cycles':mean(gpu)/mean(a),
                   'relative_L1_vs_Unity':stats(a,gpu)['relative_L1'],
                   'removed_gap_absolute':change,'removed_gap_over_original_gap':change/gap})
remaining=mean(images[chain[-1]])-mean(gpu)
assert abs(sum(s['removed_gap_absolute'] for s in stages)+remaining-gap)<1e-12
convergence={}
for stem in ['projected_matching_bsdf','projected_principled_single','projected_principled_multi']:
    high=images[stem+'_2048']
    convergence[stem]={'512_vs_2048':stats(images[stem],high),'2048_vs_Unity':stats(high,gpu)}
    if stem+'_64' in images:convergence[stem]['64_vs_2048']=stats(images[stem+'_64'],high)
    assert abs(convergence[stem]['512_vs_2048']['mean_ratio']-1)<5e-4,(stem,'mean did not converge')
    assert convergence[stem]['512_vs_2048']['relative_L1']<1e-3,(stem,'local response did not converge')
model_tests={n:stats(images[n],gpu) for n in ['projected_matching_bsdf','projected_exact_dielectric_specular',
                                          'projected_separate_diffuse','projected_exact_and_separate',
                                          'projected_principled_single','projected_principled_multi']}
model_tests['multi_vs_single']=stats(images['projected_principled_multi_2048'],images['projected_principled_single_2048'])
model_tests['exact_specular_vs_matching']=stats(images['projected_exact_dielectric_specular'],images['projected_matching_bsdf'])
model_tests['exact_specular_vs_principled_single']=stats(images['projected_exact_dielectric_specular'],images['projected_principled_single'])

# Independent pixel-centre evaluation using the actual GBuffer and the same
# direct Sun normalization. This is a NumPy replica, not GPU shader execution.
q=meta['cameraRotation'];x,y,z,w=[q[k] for k in ['x','y','z','w']]
R=np.array([[1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)],
            [2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)],
            [2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)]])
xx,yy=np.meshgrid((np.arange(W)+.5)/W*2-1,(np.arange(H)+.5)/H*2-1)
t=np.tan(np.radians(meta['verticalFov']/2))
V=-unit(np.stack([xx*t*W/H,yy*t,np.ones_like(xx)],axis=-1)@R.T)
L=unit(np.array([meta['sunDirection'][k] for k in ['x','y','z']]))
N=unit(g1[:,:,:3]*2-1);half=unit(V+L)
nv=np.clip(np.sum(N*V,axis=-1),0,1);nl=np.clip(N@L,0,1);nh=np.clip(np.sum(N*half,axis=-1),0,1)
vh=np.clip(np.sum(V*half,axis=-1),0,1);a2=np.maximum(g2[...,2],.05)**4
D=a2/np.maximum(np.pi*((1-nh*nh)+a2*nh*nh)**2,1e-10)
vis=.5/np.maximum(nl*np.sqrt(a2+(1-a2)*nv*nv)+nv*np.sqrt(a2+(1-a2)*nl*nl),1e-10)
vis=np.where((nl>0)&(nv>0),vis,0)
metal=g2[...,3:4];f0=.04*(1-metal)+g0[:,:,:3]*metal
F=f0+(1-f0)*(1-vh[...,None])**5
irradiance=np.array(json.loads((ROOT/'OfflineBaseline/SkyboxScene/sky_info.json').read_text())['sun_ground_rgb'])
replica=np.concatenate([((1-F)*(1-metal)*g0[:,:,:3]/np.pi+(D*vis)[...,None]*F)*irradiance*nl[...,None],gpu[...,3:4]],axis=-1)
replica_check=stats(replica,gpu)
assert abs(replica_check['mean_ratio']-1)<.005,'CPU replica does not explain the GPU response.'
normal_angle=np.degrees(np.arccos(np.clip(np.sum(unit(g1[...,[0,2,1]][mask]*2-1)*unit(images['native_normal_narrow'][mask,:3]*2-1),axis=-1),-1,1)))
native_rm=images['native_rm_narrow'][mask,:2]
effective_rm=np.stack([np.maximum(g2[...,2],.05),g2[...,3]],axis=-1)[mask]
# Average both intervention orders rather than assigning every interaction to
# whichever input was changed last. These values only describe this experiment.
f00=mean(images['native_narrow_single']);f10=mean(images['hybrid_unity_normal'])
f01=mean(images['hybrid_unity_roughness']);f11=mean(images['hybrid_unity_normal_roughness'])
normal_effect=.5*((f00-f10)+(f01-f11));roughness_effect=.5*((f00-f01)+(f10-f11))
factorial={'native_N_native_R':f00,'Unity_N_native_R':f10,'native_N_Unity_R':f01,'Unity_N_Unity_R':f11,
           'normal_order_averaged_reduction':normal_effect,'roughness_order_averaged_reduction':roughness_effect,
           'normal_order_averaged_reduction_over_original_cycles_mean':normal_effect/mean(native),
           'roughness_order_averaged_reduction_over_original_cycles_mean':roughness_effect/mean(native)}
assert abs(normal_effect+roughness_effect-(f00-f11))<1e-12

report={'scope':'Frozen BC5/BC7 GPU capture; common original interior mask; no GI/shadows/exposure; staged interventions are order-dependent.',
        'capturedUtc':meta['capturedUtc'],'brdfSourceSHA256':meta['brdfSourceSHA256'],'blender_build':info['build_hash'],
        'pixels':int(mask.sum()),'baseline_Unity_vs_Cycles':baseline,
        'native_rerender_vs_original':stats(native,reference),'projection_checks':projection,
        'stages':stages,'convergence':convergence,'material_model_tests':model_tests,
        'normal_roughness_two_factor_experiment':factorial,
        'remaining_matching_response_minus_GPU_over_original_gap':remaining/gap,
        'CPU_replica_vs_GPU':replica_check,
        'Unity_effective_roughness_fraction_below_point_one':float((effective_rm[:,0]<.1).mean()),
        'Unity_effective_roughness_fraction_at_point_zero_five':float((effective_rm[:,0]<=.050001).mean()),
        'native_vs_Unity_normal_degrees':{'median':float(np.median(normal_angle)),'p90':float(np.percentile(normal_angle,90))},
        'native_vs_Unity_effective_roughness_metallic_MAE':np.abs(native_rm-effective_rm).mean(axis=0).tolist()}
(OUT/'matched_inputs_report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')

def preview(a):
    rgb=np.maximum(a[:,:,:3],0)*meta['exposureMultiplier']
    rgb=np.clip(rgb*(2.51*rgb+.03)/(rgb*(2.43*rgb+.59)+.14),0,1)
    rgb=np.where(rgb<=.0031308,rgb*12.92,1.055*np.power(rgb,1/2.4)-.055)
    alpha=np.clip(a[...,3:4],0,1);rgb=rgb*alpha+.055*(1-alpha)
    return Image.fromarray(np.uint8(np.clip(rgb[::-1],0,1)*255+.5))
panels=[('Unity GPU: BC5 / BC7',gpu),('Cycles: native inputs',native),
        ('Cycles: shared inputs',images['projected_principled_multi_2048']),
        ('Shared inputs + same BSDF',images['projected_matching_bsdf_2048'])]
tile=384;label=44;zoom=240
sheet=Image.new('RGB',(tile*4,tile+zoom+label*2),(22,22,22))
draw=ImageDraw.Draw(sheet);font=ImageFont.truetype('C:/Windows/Fonts/arial.ttf',17)
energy=(gpu[:,:,:3]@Y)*mask;hot=(energy>np.quantile(energy[mask],.98))&mask
cy,cx=np.nonzero(hot);cx,cy=int(cx.mean()),H-1-int(cy.mean())
crop=(max(0,cx-120),max(0,cy-75),min(W,cx+120),min(H,cy+75))
for i,(title,a) in enumerate(panels):
    im=preview(a);draw.text((tile*i+8,12),title,font=font,fill='white')
    sheet.paste(im.resize((tile,tile),Image.Resampling.LANCZOS),(tile*i,label))
    draw.text((tile*i+8,tile+label+12),'Same crop / same exposure',font=font,fill='white')
    sheet.paste(im.crop(crop).resize((tile,zoom),Image.Resampling.NEAREST),(tile*i,tile+2*label))
sheet.save(OUT/'matched_inputs_comparison.png')
for s in stages:print(s['render'],json.dumps(s))
print('CONVERGENCE',json.dumps(convergence));print('MODELS',json.dumps(model_tests))
print('CPU_REPLICA',json.dumps(replica_check))
