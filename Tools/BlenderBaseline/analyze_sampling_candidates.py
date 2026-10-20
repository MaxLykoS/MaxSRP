"""Compare compressed candidates against the same uncompressed source and mip chain."""
import json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont

ROOT=Path(__file__).resolve().parents[2]
BASE=ROOT/'OfflineBaseline/HelmetOnly'
OUT=BASE/'HighlightDiagnosis'
INPUT=OUT/'SourceUnityInputs' if (OUT/'SourceUnityInputs/unity_helmet.json').exists() else BASE
meta=json.loads((INPUT/'unity_helmet.json').read_text(encoding='utf-8'))
info=json.loads((OUT/'sampling_candidates.json').read_text(encoding='utf-8'))
assert info.get('diagnosticVersion')==2,'Run the matched-resolution matrix first'
W,H=meta['width'],meta['height']
Y=np.array([.2126,.7152,.0722])
def read(name,channel='direct'):
    return np.fromfile(OUT/('unity_'+name+'_'+channel+'.f32'),np.float32).reshape(H,W,4).astype(np.float64)
baseline=np.fromfile(INPUT/'unity_direct_unshadowed.f32',np.float32).reshape(H,W,4).astype(np.float64)
cycles=np.fromfile(OUT/'native_narrow_filter.f32',np.float32).reshape(H,W,4).astype(np.float64)
mask=(baseline[...,3]>.99)&(cycles[...,3]>.99)
for _ in range(3):
    old=mask.copy();mask[1:-1,1:-1]=old[1:-1,1:-1]&old[:-2,1:-1]&old[2:,1:-1]&old[1:-1,:-2]&old[1:-1,2:]
    mask[0]=mask[-1]=False;mask[:,0]=mask[:,-1]=False
def stats(a,b):
    ay,by=a[mask,:3]@Y,b[mask,:3]@Y
    return {'mean_ratio':float(ay.mean()/by.mean()),'relative_L1':float(np.abs(ay-by).sum()/by.sum())}
def angle(a,b):
    a,b=a[mask,:3]*2-1,b[mask,:3]*2-1
    a/=np.maximum(np.linalg.norm(a,axis=-1,keepdims=True),1e-12)
    b/=np.maximum(np.linalg.norm(b,axis=-1,keepdims=True),1e-12)
    v=np.degrees(np.arccos(np.clip((a*b).sum(-1),-1,1)))
    return {'median':float(np.median(v)),'mean':float(v.mean()),'p90':float(np.percentile(v,90))}
pairs=[('normal_mipzero','normal_raw_xy'),('normal_bc5_mipzero','normal_raw_xy'),
       ('normal_bc5_mips','normal_rgba_mips'),('normal_bc5_packed','normal_rgba_packed'),
       ('rough_imported_mipzero','rough_rgba_mipzero'),('rough_bc7_mipzero','rough_rgba_mipzero'),
       ('rough_imported_mips','rough_rgba_mips'),('rough_bc7_mips','rough_rgba_mips'),
       ('rough_dxt1_2k_mipzero','rough_rgba2k_mipzero'),('rough_bc7_2k_mipzero','rough_rgba2k_mipzero'),
       ('rough_dxt1_2k_mips','rough_rgba2k_mips'),('rough_bc7_2k_mips','rough_rgba2k_mips'),
       ('rough_rgba2k_mips','rough_rgba_mips'),
       ('sampling_bc5_bc7_packed','sampling_rgba_packed'),('sampling_rgba_packed','sampling_rgba_float')]
report={'metadata':info,'scope':'Same-source compression tests, no source importer edits. Mip-zero pairs isolate compression. Mip chains generated in memory are not guaranteed identical to Unity import preprocessing.',
        'common_pixels':int(mask.sum()),'comparisons':{},'vs_cycles_narrow':{}}
for a,b in pairs:
    ga,gb=read(a,'rm'),read(b,'rm')
    report['comparisons'][a+'_vs_'+b]={'brightness':stats(read(a),read(b)),
        'normal_angle_degrees':angle(read(a,'normal'),read(b,'normal')),
        'same_resolution':not(a=='rough_imported_mips' or a=='rough_imported_mipzero' or a=='rough_rgba2k_mips'),
        'effective_roughness_MAE':float(np.abs(np.maximum(ga[mask,2],.05)-np.maximum(gb[mask,2],.05)).mean())}
for n in ['sampling_bc5_bc7_packed','sampling_rgba_packed','sampling_rgba_float','sampling_bc5_bc7_2k_packed','sampling_rgba2k_packed']:
    report['vs_cycles_narrow'][n]=stats(read(n),cycles)
report['vs_cycles_narrow']['production_before']=stats(baseline,cycles)

def preview(a):
    c=np.maximum(a[:,:,:3],0)*meta['exposureMultiplier']
    c=np.clip(c*(2.51*c+.03)/(c*(2.43*c+.59)+.14),0,1)
    c=np.where(c<=.0031308,c*12.92,1.055*c**(1/2.4)-.055)
    alpha=a[...,3:4]
    return Image.fromarray(np.uint8(np.clip((c*alpha+.055*(1-alpha))[::-1],0,1)*255+.5))
panels=[('Current DXT5 / DXT1',baseline),('BC5 normal / BC7 roughness',read('sampling_bc5_bc7_packed')),
        ('Uncompressed, same mips',read('sampling_rgba_packed')),('Cycles native narrow filter',cycles)]
tile=384;head=42
sheet=Image.new('RGB',(tile*4,tile+head),(22,22,22));draw=ImageDraw.Draw(sheet)
font=ImageFont.truetype('C:/Windows/Fonts/arial.ttf',16)
for i,(label,a) in enumerate(panels):
    draw.text((i*tile+8,10),label,font=font,fill='white')
    sheet.paste(preview(a).resize((tile,tile),Image.Resampling.LANCZOS),(i*tile,head))
sheet.save(OUT/'sampling_candidates.png')
(OUT/'sampling_report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report,indent=2))
