"""Quantify controlled highlight renders. Every L1 metric names its reference.
No runtime renderer or scene changes are made by this analysis.
"""
import json,hashlib,math
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont

ROOT=Path(__file__).resolve().parents[2]
BASE=ROOT/'OfflineBaseline/HelmetOnly'
OUT=BASE/'HighlightDiagnosis'
INPUT=OUT/'SourceUnityInputs' if (OUT/'SourceUnityInputs/unity_helmet.json').exists() else BASE
meta=json.loads((INPUT/'unity_helmet.json').read_text(encoding='utf-8'))
W,H=meta['width'],meta['height']
Y=np.array([.2126,.7152,.0722])
def read(path):return np.fromfile(path,np.float32).reshape(H,W,4).astype(np.float64)
def unit(a):return a/np.maximum(np.linalg.norm(a,axis=-1,keepdims=True),1e-12)
gpu=read(INPUT/'unity_direct_unshadowed.f32')
native=read(OUT/'native_default_filter.f32')
mask=(gpu[...,3]>.99)&(native[...,3]>.99)
for _ in range(3):
    old=mask.copy()
    mask[1:-1,1:-1]=old[1:-1,1:-1]&old[:-2,1:-1]&old[2:,1:-1]&old[1:-1,:-2]&old[1:-1,2:]
    mask[0]=mask[-1]=False;mask[:,0]=mask[:,-1]=False
def stats(a,b):
    a,b=a[mask,:3],b[mask,:3]
    ay,by=a@Y,b@Y
    valid=by>max(by.mean()*.01,1e-6)
    return {'mean_luminance':float(ay.mean()),'reference_mean_luminance':float(by.mean()),
            'mean_ratio':float(ay.mean()/by.mean()),'relative_L1':float(np.abs(ay-by).sum()/by.sum()),
            'median_pixel_ratio':float(np.median(ay[valid]/by[valid]))}
def angles(a,b):
    a,b=unit(a[mask]*2-1),unit(b[mask]*2-1)
    v=np.degrees(np.arccos(np.clip(np.sum(a*b,axis=-1),-1,1)))
    return {'mean':float(v.mean()),'median':float(np.median(v)),'p90':float(np.percentile(v,90))}
names=['native_default_filter','native_narrow_filter','hybrid_native_normal','hybrid_unity_normal',
       'hybrid_unity_normal_roughness','hybrid_unity_normal_rm','projected_principled_multi',
       'projected_principled_single','projected_matching_bsdf']
images={n:read(OUT/(n+'.f32')) for n in names}
g0,g1,g2=[read(INPUT/('unity_gbuffer'+str(i)+'.f32')) for i in range(3)]
report={'scope':'No GI. Delta Sun, no shadows; 137k common interior pixels. All main L1 values normalized by captured Unity luminance sum.',
        'unity_capture':meta['capturedUtc'],'brdf_sha256':meta['brdfSourceSHA256'],
        'pixels':int(mask.sum()),'vs_unity_GPU':{n:stats(a,gpu) for n,a in images.items()},
        'projection_checks':{},'normal_angles_degrees':{},'unity_normal_variants':{}}
assert hashlib.sha256((ROOT/'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/BRDF.hlsl').read_bytes()).hexdigest()==meta['brdfSourceSHA256']
pa=read(OUT/'projected_albedo.f32');pn=read(OUT/'projected_normal.f32');prm=read(OUT/'projected_rm.f32')
report['projection_checks']['albedo_mean_absolute_RGB_error']=float(np.abs(pa[mask,:3]-g0[mask,:3]).mean())
report['projection_checks']['normal_mean_absolute_encoded_RGB_error']=float(np.abs(pn[mask,:3]-g1[...,[0,2,1]][mask]).mean())
report['projection_checks']['roughness_metallic_MAE']=np.abs(prm[mask,:2]-np.stack([np.maximum(g2[...,2],.05),g2[...,3]],axis=-1)[mask]).mean(axis=0).tolist()
for name,path in [('native_filtered',BASE/'blender_normal.f32'),('native_narrow',OUT/'native_normal_narrow.f32')]:
    report['normal_angles_degrees']['unity_vs_'+name]=angles(g1[...,[0,2,1]],read(path)[...,:3])
native_rm=read(OUT/'native_rm_narrow.f32')
effective_rm=np.stack([np.maximum(g2[...,2],.05),g2[...,3]],axis=-1)
report['native_effective_roughness_metallic_MAE']=np.abs(native_rm[mask,:2]-effective_rm[mask]).mean(axis=0).tolist()
report['roughness_fraction_below_point_one']=float((effective_rm[mask,0]<.1).mean())
variants={}
for name in ['floatnormal','normal_mipzero','normal_raw_xy','geometric_normal']:
    direct=read(OUT/('unity_'+name+'_direct.f32'))
    normal=read(OUT/('unity_'+name+'_normal.f32'))
    variants[name]=(direct,normal)
    report['unity_normal_variants'][name]={'vs_original_GPU':stats(direct,gpu),
        'angle_vs_native_narrow':angles(normal[...,[0,2,1]],read(OUT/'native_normal_narrow.f32')[...,:3])}
report['normal_angles_degrees']['float_vs_packed_Unity']=angles(variants['floatnormal'][1][...,:3],g1[...,:3])
report['normal_angles_degrees']['import_mipzero_vs_raw_xy']=angles(variants['normal_mipzero'][1][...,:3],variants['normal_raw_xy'][1][...,:3])
report['normal_angles_degrees']['geometric_Unity_vs_Cycles']=angles(variants['geometric_normal'][1][...,[0,2,1]],read(OUT/'native_geometric_normal.f32')[...,:3])
report['unity_variant_info']=json.loads((OUT/'unity_variants.json').read_text(encoding='utf-8'))

# Analytic GGX/Schlick/Lambert replica at the same pixel centres, plus pure endpoints.
q=meta['cameraRotation'];x,y,z,w=[q[k] for k in ['x','y','z','w']]
R=np.array([[1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)],
            [2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)],
            [2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)]])
xx,yy=np.meshgrid((np.arange(W)+.5)/W*2-1,(np.arange(H)+.5)/H*2-1)
t=np.tan(np.radians(meta['verticalFov']/2))
V=-unit(np.stack([xx*t*W/H,yy*t,np.ones_like(xx)],axis=-1)@R.T)
L=unit(np.array([meta['sunDirection'][k] for k in ['x','y','z']]))
N=unit(g1[:,:,:3]*2-1)
half=unit(V+L)
nv=np.clip(np.sum(N*V,axis=-1),0,1);nl=np.clip(N@L,0,1);nh=np.clip(np.sum(N*half,axis=-1),0,1)
vh=np.clip(np.sum(V*half,axis=-1),0,1)
a2=np.maximum(g2[...,2],.05)**4
D=a2/np.maximum(np.pi*((1-nh*nh)+a2*nh*nh)**2,1e-10)
vis=.5/np.maximum(nl*np.sqrt(a2+(1-a2)*nv*nv)+nv*np.sqrt(a2+(1-a2)*nl*nl),1e-10)
vis=np.where((nl>0)&(nv>0),vis,0)
irradiance=np.array(json.loads((ROOT/'OfflineBaseline/SkyboxScene/sky_info.json').read_text())['sun_ground_rgb'])
def replica(metal,exact_dielectric=False):
    f0=.04*(1-metal)+g0[:,:,:3]*metal
    F=f0+(1-f0)*(1-vh[...,None])**5
    if exact_dielectric:
        eta=1.5;ct=np.sqrt(1-(1-vh*vh)/(eta*eta))
        dielectric=.5*(((vh-eta*ct)/(vh+eta*ct))**2+((eta*vh-ct)/(eta*vh+ct))**2)
        F=dielectric[...,None]
    color=((1-F)*(1-metal)*g0[:,:,:3]/np.pi+(D*vis)[...,None]*F)*irradiance*nl[...,None]
    return np.concatenate([color,gpu[...,3:4]],axis=-1)
report['CPU_replica_vs_GPU']=stats(replica(g2[...,3:4]),gpu)
report['BRDF_endpoints']={}
for value,label in [(0,'dielectric'),(1,'metal')]:
    ref=read(OUT/('projected_'+label+'_only.f32'))
    report['BRDF_endpoints'][label]={'schlick_replica_vs_Cycles':stats(replica(value),ref)}
    if value==0:report['BRDF_endpoints'][label]['exact_Fresnel_with_same_Lambert_weight_vs_Cycles']=stats(replica(0,True),ref)
report['principled_single_vs_matching_bsdf']=stats(images['projected_principled_single'],images['projected_matching_bsdf'])

def preview(a):
    rgb=np.maximum(a[:,:,:3],0)*meta['exposureMultiplier']
    rgb=np.clip(rgb*(2.51*rgb+.03)/(rgb*(2.43*rgb+.59)+.14),0,1)
    rgb=np.where(rgb<=.0031308,rgb*12.92,1.055*np.power(rgb,1/2.4)-.055)
    alpha=np.clip(a[...,3:4],0,1)
    rgb=rgb*alpha+.055*(1-alpha)
    return Image.fromarray(np.uint8(np.clip(rgb[::-1],0,1)*255+.5))
panels=[('Unity GPU',gpu),('Cycles native inputs',native),('Cycles shared GBuffer',images['projected_principled_single']),
        ('Shared inputs + matching BSDF',images['projected_matching_bsdf'])]
tile=384;label_height=42;zoom_height=256
sheet=Image.new('RGB',(tile*4,tile+zoom_height+label_height*2),(22,22,22))
draw=ImageDraw.Draw(sheet);font=ImageFont.truetype('C:/Windows/Fonts/arial.ttf',17)
energy=(gpu[:,:,:3]@Y)*mask
hot=(energy>np.quantile(energy[mask],.98))&mask
cy,cx=np.nonzero(hot)
cx,cy=int(np.mean(cx)),H-1-int(np.mean(cy))
crop=(max(0,cx-120),max(0,cy-80),min(W,cx+120),min(H,cy+80))
report['preview_zoom_crop_top_left_pixels']=list(crop)
for i,(label,a) in enumerate(panels):
    im=preview(a)
    draw.text((tile*i+8,10),label,font=font,fill='white')
    sheet.paste(im.resize((tile,tile),Image.Resampling.LANCZOS),(tile*i,label_height))
    draw.text((tile*i+8,tile+label_height+10),'Same highlight crop / EV100 15',font=font,fill='white')
    sheet.paste(im.crop(crop).resize((tile,zoom_height),Image.Resampling.NEAREST),(tile*i,tile+label_height*2))
sheet.save(OUT/'highlight_comparison.png')
(OUT/'highlight_report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report,indent=2))
