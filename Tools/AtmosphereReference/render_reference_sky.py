"""Independent sky HDR for visual inspection; quantitative tests use 1M paths.

This file never reads a Unity sky/LUT. Its lower-sample image is an illustration,
not the source of the irradiance/confidence numbers in the numerical report.
"""
import argparse,json,time
from pathlib import Path
import numpy as np
import reference_transport as rt

ROOT=Path(__file__).resolve().parents[2];OUT=ROOT/'OfflineBaseline/AtmosphereReference'
parser=argparse.ArgumentParser();parser.add_argument('--samples',type=int,default=2048)
args=parser.parse_args();start=time.time()
r=json.loads((OUT/'independent_results.json').read_text())
probes=json.loads((OUT/'reference_inputs.json').read_text())['probes']
solar=np.array(r['solar_irradiance_internal_RGB']);p=np.array(r['physical_parameters'])
probe=next(v for v in probes if v['sunLabel']=='current' and v['height']==2)
sun=np.array([probe['sunDirection'][c] for c in ['x','y','z']]);sun/=np.linalg.norm(sun)
table=np.load(OUT/('independent_solar_tau_'+r['validation']['table_parameter_hash'][:12]+'.npy'))
W,H=128,64;full=np.ones((H,W,4),np.float32);single=full.copy()
for y in range(H):
    lat=((y+.5)/H-.5)*np.pi
    for x in range(W):
        phi=((x+.5)/W*2-1)*np.pi
        view=np.array([np.cos(lat)*np.cos(phi),np.sin(lat),np.cos(lat)*np.sin(phi)])
        first=rt.single_scatter(2,view,sun,p,table,4096)
        batches=rt.path_trace(2,view,sun,p,table,4,max(1,args.samples//4),20261019+(y*W+x)*97)
        higher=batches[:,:,1:].sum(axis=2).mean(axis=0)
        single[y,x,:3]=first*solar;full[y,x,:3]=(first+higher)*solar
    if y%8==7:print('INDEPENDENT_SKY_ROW',y+1,H,'elapsed',round(time.time()-start,1),flush=True)
full.tofile(OUT/'independent_sky.f32');single.tofile(OUT/'independent_single_sky.f32')
(OUT/'independent_sky_info.json').write_text(json.dumps({'width':W,'height':H,'samples_per_channel_per_pixel':4*max(1,args.samples//4),
    'row_origin':'south pole first; phi=(2*u-1)*pi; Unity Y-up directions',
    'hdr_scale':100000,'sun_disk':False,'observer_height':2,'source':'Independent double precision volume transport; no Unity sky or LUT.',
    'quantitative_reference':'independent_results.json uses 1048576 paths per channel; this HDR is a lower-sample visualization.'},indent=2),encoding='utf-8')
print('INDEPENDENT_SKY_COMPLETE',round(time.time()-start,1),flush=True)
