"""Validate the native-UV imported-texture reference against its GPU capture."""
import hashlib,json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont

ROOT=Path(__file__).resolve().parents[2]
BASE=ROOT/'OfflineBaseline/HelmetOnly';OUT=BASE/'ImportedTextures'
meta=json.loads((OUT/'unity_helmet.json').read_text(encoding='utf-8'))
info=json.loads((OUT/'native_imported_info.json').read_text(encoding='utf-8'))
W,H=meta['width'],meta['height'];Y=np.array([.2126,.7152,.0722])
def read(directory,name):
    a=np.fromfile(directory/(name+'.f32'),np.float32).reshape(H,W,4).astype(np.float64)
    assert np.isfinite(a).all(),name;return a
def unit(a):return a/np.maximum(np.linalg.norm(a,axis=-1,keepdims=True),1e-12)
gpu=read(OUT,'unity_mipzero_direct');native=read(OUT,'native_imported_multi')
coverage=read(BASE/'MatchedInputsBC5BC7/SourceUnityInputs','blender_combined_matched')
mask=(gpu[...,3]>.5)&(coverage[...,3]>.99)
for _ in range(3):
    old=mask.copy();mask[1:-1,1:-1]=old[1:-1,1:-1]&old[:-2,1:-1]&old[2:,1:-1]&old[1:-1,:-2]&old[1:-1,2:]
    mask[0]=mask[-1]=False;mask[:,0]=mask[:,-1]=False
assert (native[...,3][mask]>.99).all()
def stats(a,b):
    ay=a[mask,:3]@Y;by=b[mask,:3]@Y
    return {'mean':float(ay.mean()),'reference_mean':float(by.mean()),'mean_ratio':float(ay.mean()/by.mean()),
            'relative_L1':float(np.abs(ay-by).sum()/by.sum())}
assert hashlib.sha256((ROOT/'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/BRDF.hlsl').read_bytes()).hexdigest()==meta['brdfSourceSHA256']
for e in info['textures']:
    assert e['width']==2048 and e['height']==2048 and e['mipCount']==12
    assert hashlib.sha256((ROOT/e['sourcePath']).read_bytes()).hexdigest()==e['sourceSHA256']
    assert hashlib.sha256((ROOT/(e['sourcePath']+'.meta')).read_bytes()).hexdigest()==e['importerSHA256']
    assert hashlib.sha256((OUT/e['mipFiles'][0]).read_bytes()).hexdigest()==e['mip0_sha256']
    assert all((OUT/n).is_file() for n in e['mipFiles'])
images={n:read(OUT,n) for n in info['renders']}
g0,g1,g2=[read(OUT,'unity_mipzero_gbuffer'+str(i)) for i in range(3)]
n1=unit(g1[...,[0,2,1]][mask]*2-1);n2=unit(images['native_imported_normal'][mask,:3]*2-1)
angles=np.degrees(np.arccos(np.clip((n1*n2).sum(axis=-1),-1,1)))
rm=np.stack([np.maximum(g2[...,2],.05),g2[...,3]],axis=-1)
input_errors={'albedo_RGB_MAE':float(np.abs(g0[mask,:3]-images['native_imported_albedo'][mask,:3]).mean()),
              'effective_roughness_metallic_MAE':np.abs(rm[mask]-images['native_imported_rm'][mask,:2]).mean(axis=0).tolist(),
              'normal_degrees':{'median':float(np.median(angles)),'p90':float(np.percentile(angles,90))}}
report={'scope':'Native UV/tangent calculations with actual imported mip-zero texels; both sides bilinear, no GI/shadow/fog/exposure. Production mip policy unchanged.',
        'capturedUtc':meta['capturedUtc'],'pixels':int(mask.sum()),'blender_build':info['build_hash'],
        'input_errors':input_errors,'cycles_vs_Unity_mipzero':{},
        'source_scene':info['saved_blend'],'all_48_imported_mips_exported':True}
for name in ['native_imported_multi','native_imported_single','native_imported_matching_bsdf',
             'native_imported_multi_point','native_imported_matching_bsdf_point']:
    report['cycles_vs_Unity_mipzero'][name]=stats(images[name],gpu)
report['narrow_filter_vs_point']={name:stats(images[name],images[name+'_point'])
    for name in ['native_imported_multi','native_imported_matching_bsdf']}
report['production_default_mips_vs_mipzero_control']=stats(read(BASE,'unity_direct_unshadowed'),gpu)
report['original_4k_reference_vs_current_production']=stats(read(BASE,'blender_direct_delta_unshadowed'),read(BASE,'unity_direct_unshadowed'))
assert input_errors['albedo_RGB_MAE']<.002
assert max(input_errors['effective_roughness_metallic_MAE'])<.002
assert input_errors['normal_degrees']['median']<.02
# These are native-raster-vs-ray sampling checks, not proof of pixel identity.
assert abs(report['cycles_vs_Unity_mipzero']['native_imported_matching_bsdf_point']['mean_ratio']-1)<.03
report['native_input_mean_screening_pass']=abs(report['cycles_vs_Unity_mipzero']['native_imported_multi_point']['mean_ratio']-1)<.05
(OUT/'imported_baseline_report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')

def preview(a):
    c=np.maximum(a[...,:3],0)*meta['exposureMultiplier']
    c=np.clip(c*(2.51*c+.03)/(c*(2.43*c+.59)+.14),0,1)
    c=np.where(c<=.0031308,12.92*c,1.055*c**(1/2.4)-.055)
    alpha=np.clip(a[...,3:4],0,1);c=c*alpha+.055*(1-alpha)
    return Image.fromarray(np.uint8(np.clip(c[::-1],0,1)*255+.5))
panels=[('Unity: imported / mip zero',gpu),('Cycles: native UV / Principled',images['native_imported_multi_point']),
        ('Cycles: native UV / same BRDF',images['native_imported_matching_bsdf_point'])]
tile=512;label=44;zoom=256
sheet=Image.new('RGB',(tile*3,tile+zoom+label*2),(22,22,22));draw=ImageDraw.Draw(sheet)
font=ImageFont.truetype('C:/Windows/Fonts/arial.ttf',19)
energy=(gpu[...,:3]@Y)*mask;hot=(energy>np.quantile(energy[mask],.98))&mask
cy,cx=np.nonzero(hot);cx,cy=int(cx.mean()),H-1-int(cy.mean())
crop=(max(0,cx-128),max(0,cy-64),min(W,cx+128),min(H,cy+64))
for i,(title,a) in enumerate(panels):
    im=preview(a);draw.text((tile*i+10,12),title,font=font,fill='white')
    sheet.paste(im.resize((tile,tile),Image.Resampling.LANCZOS),(tile*i,label))
    draw.text((tile*i+10,tile+label+12),'Same crop / same exposure',font=font,fill='white')
    sheet.paste(im.crop(crop).resize((tile,zoom),Image.Resampling.NEAREST),(tile*i,tile+label*2))
sheet.save(OUT/'native_imported_comparison.png')
for i,(_,a) in enumerate(panels):preview(a).save(OUT/('preview_'+str(i)+'.png'))
print(json.dumps(report,indent=2))
