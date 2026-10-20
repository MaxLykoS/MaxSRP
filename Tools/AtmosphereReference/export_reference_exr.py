"""Run in Blender to package independent linear HDR; no scene is saved."""
import json
from pathlib import Path
import bpy,numpy as np
ROOT=Path(__file__).resolve().parents[2];OUT=ROOT/'OfflineBaseline/AtmosphereReference'
info=json.loads((OUT/'independent_sky_info.json').read_text());W,H=info['width'],info['height']
scene=bpy.context.scene;scene.render.image_settings.file_format='OPEN_EXR';scene.render.image_settings.color_depth='32'
scene.render.image_settings.exr_codec='ZIP';scene.view_settings.view_transform='Standard'
scene.view_settings.look='None';scene.view_settings.exposure=0;scene.view_settings.gamma=1
image=bpy.data.images.new('Independent atmosphere sky',width=W,height=H,float_buffer=True)
image.colorspace_settings.name='Linear Rec.709'
array=np.fromfile(OUT/'independent_sky.f32',np.float32).reshape(H,W,4)
# Blender's default environment projection has the opposite azimuth sign to
# the Unity LUT. Keep the numeric f32 untouched and package a Blender-ready EXR.
image.pixels.foreach_set(array[:,::-1].copy().ravel())
image.save_render(str(OUT/'independent_sky.exr'),scene=scene)
(OUT/'independent_sky_exr_info.json').write_text(json.dumps({'projection':'Blender default environment equirectangular',
    'phi':'(0.5-u)*2*pi in Unity X/Y-up/Z basis; Blender world swaps Unity Y/Z',
    'source':'independent_sky.f32, horizontally mirrored for Blender projection',
    'linear_HDR':True,'sun_disk':False},indent=2),encoding='utf-8')
print('INDEPENDENT_SKY_EXR',str(OUT/'independent_sky.exr'),flush=True)
