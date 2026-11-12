"""Read the known one-bounce oracle's validity control in a fresh Blender CLI.
No scene opening, Unity calls, assets changes, rendering, or baking.
"""
import argparse,json,sys
from pathlib import Path
from datetime import datetime,timezone
import bpy
import numpy as np
sys.path.insert(0,str(Path(__file__).resolve().parent))
from read_tiny_filter_training import exr_header,stamp

p=argparse.ArgumentParser();p.add_argument('--run',required=True,type=Path)
a=p.parse_args(sys.argv[sys.argv.index('--')+1:]);run=a.run.resolve()
project=Path(__file__).resolve().parents[2]
if not run.is_relative_to(project/'OfflineBaseline/GICalibration/Bounce') or any(x.lower()=='library' for x in run.parts):raise ValueError('Only known Bounce control report is permitted')
status=json.loads((run/'status.json').read_text(encoding='utf-8-sig'))
if status['phase']!='complete' or not status['valid'] or not status['restored']:raise ValueError('Control is not an already completed oracle')
w,h=status['width'],status['height'];st=status['lightmapST'];guard=1/16
yy,xx=np.mgrid[:h,:w]
u=(((xx+.5)/w-st['z'])/st['x']-guard)/(1-2*guard)
v=(((yy+.5)/h-st['w'])/st['y']-guard)/(1-2*guard)
mask=(u>=.25)&(u<=.75)&(v>=.25)&(v<=.75);center=(np.abs(u-.5)<.025)&(np.abs(v-.5)<.025)
def region(a,m):
    v=a[m];values,counts=np.unique(v,axis=0,return_counts=True)
    return {'pixels':int(m.sum()),'meanRGBA':v.mean(axis=0,dtype=np.float64).tolist(),'minRGBA':v.min(axis=0).tolist(),'maxRGBA':v.max(axis=0).tolist(),'uniqueRGBA':[{'value':r.tolist(),'count':int(c)} for r,c in zip(values[:32],counts[:32])],'uniqueCount':len(values)}
report={'utc':datetime.now(timezone.utc).isoformat(),'run':str(run),'statusSHA256':stamp(run/'status.json'),'receiverNg':status['receiverNg'],'lightmapST':st,'uvGuard':guard,'sampledPixels':int(mask.sum()),'centerPixels':int(center.sum()),'sensorPurpose':'Known physical Lambert one-bounce positive control; sensor Ng down, no self projection, reflector Ng up; native energy agrees with independent integral within about 0.4%. This does NOT by itself expose native validity semantics.','files':{}}
native=np.fromfile(run/'Atlas.rgba16f',dtype='<f2').reshape(h,w,4).astype(np.float32)
report['nativeROI']=region(native,mask);report['nativeCenter']=region(native,center)
for stem in ('texel_validity','coverage_mask','plane_normals_ws','interp_normals_ws','positions_ws','lightmap','indirect'):
    path=run/'TrainingData/Lightmap-0'/(stem+'.exr');header=exr_header(path)
    im=bpy.data.images.load(str(path),check_existing=False);default=im.colorspace_settings.name;im.colorspace_settings.name='Non-Color'
    if tuple(im.size)!=(w,h):raise ValueError('Unexpected control data dimensions')
    pixels=np.empty(w*h*4,np.float32);im.pixels.foreach_get(pixels);pixels=pixels.reshape(h,w,4)
    record={'file':stamp(path),'header':header,'defaultBlenderColorSpace':default,'selectedColorSpace':'Non-Color','interior':region(pixels,mask),'center':region(pixels,center)}
    if stem=='lightmap':
        record['meanAbsRGBvsNativeIdentity']=float(np.abs(pixels[mask,:3]-native[mask,:3]).mean())
        record['meanAbsRGBvsNativeIfTrainingFlipped']=float(np.abs(pixels[::-1][mask,:3]-native[mask,:3]).mean())
    report['files'][stem]=record;bpy.data.images.remove(im);del pixels
out=run/'ValidityControl';out.mkdir(exist_ok=True);(out/'control.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps({'output':str(out/'control.json'),'validity':report['files']['texel_validity'],'coverage':report['files']['coverage_mask']['interior'],'normal':report['files']['plane_normals_ws']['interior'],'lightmap':report['files']['lightmap']['interior'],'orientation':{k:v for k,v in report['files']['lightmap'].items() if k.startswith('meanAbs')}},indent=2))
