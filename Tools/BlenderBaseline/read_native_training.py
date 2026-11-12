"""Decode explicit native training EXRs in an isolated Blender process."""
import argparse
from pathlib import Path
import sys,json,hashlib
import bpy
import numpy as np

p=argparse.ArgumentParser();p.add_argument('--run',required=True,type=Path)
a=p.parse_args(sys.argv[sys.argv.index('--')+1:]);run=a.run.resolve()
approved=Path(__file__).resolve().parents[2]/'OfflineBaseline/GICalibration'
if not run.is_relative_to(approved.resolve()) or 'Library' in run.parts:raise ValueError('Unapproved input')
out=run/'TrainingDecoded';out.mkdir(exist_ok=True)
report={}
for path in sorted((run/'TrainingData/Lightmap-0').glob('*.exr')):
    im=bpy.data.images.load(str(path),check_existing=False);im.colorspace_settings.name='Non-Color'
    w,h=im.size;pixels=np.empty(w*h*4,np.float32);im.pixels.foreach_get(pixels);pixels=pixels.reshape(h,w,4)
    np.save(out/(path.stem+'.npy'),pixels)
    center=pixels[h//2-2:h//2+2,w//2-2:w//2+2,:3]
    record={'size':[w,h],'centerMean':center.mean((0,1)).tolist(),'min':pixels[:,:,:3].min((0,1)).tolist(),'max':pixels[:,:,:3].max((0,1)).tolist(),'sha256':hashlib.sha256(path.read_bytes()).hexdigest()}
    report[path.name]=record;bpy.data.images.remove(im)
(out/'decoded.json').write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))
