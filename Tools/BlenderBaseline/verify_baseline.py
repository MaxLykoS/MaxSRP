"""Run against the saved .blend in a separate background Blender."""
import json
from pathlib import Path
import bpy
import numpy as np

scene = bpy.context.scene
out = Path(bpy.data.filepath).parent
images = [im for im in bpy.data.images if im.type == 'IMAGE']
assert all(im.packed_file for im in images), 'Baseline must not depend on the source texture paths'
assert scene.render.resolution_x == 1920 and scene.render.resolution_y == 1080
assert scene.render.resolution_percentage == 100 and scene.cycles.samples == 256
assert len([o for o in scene.objects if o.type=='MESH']) == 5
assert len([o for o in scene.objects if o.type=='LIGHT']) == 4
assert len([o for o in scene.objects if o.type=='CAMERA']) == 2
scene.cycles.samples = 16
scene.cycles.use_adaptive_sampling = False
scene.render.resolution_x,scene.render.resolution_y = 96,54
scene.render.image_settings.file_format = 'OPEN_EXR'
scene.render.image_settings.color_depth = '32'


def sample(compositing,stem):
    scene.render.use_compositing = compositing
    scene.render.filepath = str(out/(stem+'.exr'))
    bpy.ops.render.render(write_still=True)
    image = bpy.data.images.load(scene.render.filepath,check_existing=False)
    array = np.array(image.pixels[:],np.float32).reshape(-1,4)[:,:3]
    bpy.data.images.remove(image)
    return array


raw = sample(False,'verify_raw')
actual = sample(True,'verify_compositor')
manifest = json.loads((out/'manifest.json').read_text(encoding='utf-8'))
exposure = 100000/(1.2*2**manifest['exposure']['CameraEV100'])*2**manifest['exposure']['ExposureEV']
x = np.maximum(raw,0)*exposure
expected = np.clip(x*(2.51*x+.03)/(x*(2.43*x+.59)+.14),0,1)
error = float(np.max(np.abs(actual-expected)))
assert error<1e-5, f'Saved Blender compositor does not reproduce the project curve: {error}'
report = {'packed_images':len(images),'mesh_objects':5,'lights':4,'cameras':2,
          'saved_resolution':[1920,1080],'samples':256,'max_compositor_error':error,
          'finite_raw':bool(np.isfinite(raw).all()),'finite_display':bool(np.isfinite(actual).all())}
(out/'verification.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print('BASELINE_VERIFIED',json.dumps(report),flush=True)
