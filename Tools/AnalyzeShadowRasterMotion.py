"""Analyze the D3D11 CaptureShadowRasterMotion fixture using an independent ray/box reference.
The reference includes 256 disk-light directions, no shadow map, TAA or scene GI.
Finite-disk ray visibility and PCSS are different approximations; absolute errors are diagnostic.
"""
import argparse
import json
import numpy as np
from pathlib import Path
from PIL import Image
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('folder',nargs='?',type=Path,default=Path('OfflineBaseline/ShadowRasterMotion'));p=parser.parse_args().folder;w,h=384,216
arrays={r:np.stack([np.fromfile(p/f'{r}_{f:03}.f32',np.float32).reshape(h,w) for f in range(48)]) for r in [1024,2048,4096]}
for r,a in arrays.items():
 ys,xs=np.where(np.min(a,axis=0)<.99);print(r,'range',a.min(),a.max(),'bbox',xs.min(),ys.min(),xs.max(),ys.max())
 Image.fromarray((np.clip(a[0],0,1)*255).astype(np.uint8)[::-1]).save(p/f'{r}.png')
# Follow PatternSurface on D3D11: clip.y = -(uv.y * 2 - 1) for these Unity readbacks.
m=np.loadtxt(p/'1024_inverseVP.txt').reshape(4,4)
y,x=np.mgrid[:h,:w];x=(x+.5)/w*2-1;y=1-(y+.5)/h*2
near=np.stack([x,y,np.ones_like(x),np.ones_like(x)],-1)@m.T
far=np.stack([x,y,np.zeros_like(x),np.ones_like(x)],-1)@m.T
near=near[:,:,:3]/near[:,:,3:];far=far[:,:,:3]/far[:,:,3:];d=far-near
world=near+d*(-near[:,:,1]/d[:,:,1])[...,None]
print('center ground',world[h//2,w//2])
ys,xs=np.where(np.min(arrays[1024],axis=0)<.999)
roi=np.zeros((h,w),bool);roi[max(0,ys.min()-12):min(h,ys.max()+13),max(0,xs.min()-12):min(w,xs.max()+13)]=True
points=world[roi].copy();points[:,1]=.1
pitch,yaw=np.deg2rad([55,-30]);L=np.array([-np.sin(yaw)*np.cos(pitch),np.sin(pitch),-np.cos(yaw)*np.cos(pitch)])
points+=L*(.005*(2-L[1]))
tangent=np.cross(L,[0,1,0]);tangent/=np.linalg.norm(tangent);bitangent=np.cross(L,tangent)
ref=[]
for frame in range(48):
 base=points.copy();base[:,0]-=frame*.004
 visible=np.zeros(len(base))
 for sample in range(256):
  radius=np.sqrt((sample+.5)/256)*np.tan(np.deg2rad(1));angle=sample*2.399963229728653
  direction=L+radius*(np.cos(angle)*tangent+np.sin(angle)*bitangent);direction/=np.linalg.norm(direction)
  t0=(np.array([-.5,4,-.5])-base)/direction;t1=(np.array([.5,6,.5])-base)/direction
  enter=np.maximum(np.minimum(t0,t1).max(axis=1),0);leave=np.maximum(t0,t1).min(axis=1)
  visible+=(leave<enter)
 ref.append(visible/256)
ref=np.array(ref)
report={'analytic_max_interframe_rms':float(np.sqrt(np.square(np.diff(ref,axis=0)).mean(axis=1)).max()),'resolutions':{}}
for r,a in arrays.items():
 vals=a[:,roi];mask=(ref>.01)&(ref<.99) | ((vals>.01)&(vals<.99))
 report['resolutions'][str(r)]={'mean_abs_visibility_error':float(np.abs(vals-ref)[mask].mean()),'mean_square_visibility_error':float(np.square(vals-ref)[mask].mean()),'temporal_residual_rms':float(np.sqrt(np.square(np.diff(vals-ref,axis=0)).mean())),'max_interframe_rms':float(np.sqrt(np.square(np.diff(vals,axis=0)).mean(axis=1)).max())}
 print(r,'refMAE',np.abs(vals-ref)[mask].mean(),'MSE',np.square(vals-ref)[mask].mean(),'temporalResidualRMS',np.sqrt(np.square(np.diff(vals-ref,axis=0)).mean()),'largestFrameRMS',np.sqrt(np.square(np.diff(vals,axis=0)).mean(axis=1)).max())
refImg=np.ones((h,w));refImg[roi]=ref[0];Image.fromarray((refImg*255).astype(np.uint8)[::-1]).save(p/'analytic.png')
np.savez_compressed(p/'analysis_data.npz',reference=ref,roi=roi,world=world)

(p/'analysis.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
