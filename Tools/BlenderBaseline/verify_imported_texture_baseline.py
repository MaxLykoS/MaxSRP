"""Reopen the new packed baseline and verify its saved F12 display path."""
import json
from pathlib import Path
import bpy,numpy as np

OUT=Path(bpy.data.filepath).parent
meta=json.loads((OUT/'unity_helmet.json').read_text(encoding='utf-8'))
scene=bpy.context.scene
helmet=next(o for o in scene.objects if o.type=='MESH')
mat=helmet.data.materials[0];nodes=mat.node_tree.nodes
output=next(n for n in nodes if n.type=='OUTPUT_MATERIAL')
assert output.inputs['Surface'].links[0].from_node.type=='BSDF_PRINCIPLED'
assert not helmet.visible_diffuse and not helmet.visible_glossy
assert scene.cycles.max_bounces==1 and scene.cycles.filter_width<.011
textures=[n for n in nodes if n.type=='TEX_IMAGE']
assert len(textures)==4 and all(n.image.packed_file for n in textures)
assert all(tuple(n.image.size)==(2048,2048) and n.interpolation=='Linear' for n in textures)
assert all(n.inputs['Vector'].links[0].from_socket.name=='UV' for n in textures)
assert all(n.type!='TEX_COORD' or not n.outputs['Window'].is_linked for n in nodes)
assert all(im.packed_file for im in bpy.data.images if im.type=='IMAGE')
scene.render.resolution_x=scene.render.resolution_y=96
scene.cycles.samples=16;scene.render.image_settings.file_format='OPEN_EXR'
scene.render.image_settings.color_depth='32'
def capture(composite,name):
    scene.render.use_compositing=composite;scene.render.filepath=str(OUT/(name+'.exr'))
    bpy.ops.render.render(write_still=True)
    im=bpy.data.images.load(scene.render.filepath,check_existing=False)
    a=np.array(im.pixels[:],np.float32).reshape(96,96,4);bpy.data.images.remove(im)
    assert np.isfinite(a).all();return a[...,:3]
raw=capture(False,'verify_imported_raw');display=capture(True,'verify_imported_compositor')
x=np.maximum(raw,0)*meta['exposureMultiplier']
expected=np.clip(x*(2.51*x+.03)/(x*(2.43*x+.59)+.14),0,1)
error=float(np.max(np.abs(display-expected)))
assert error<2e-5,('Saved F12 compositor does not match exposure/ACES',error)
scene.render.resolution_x=meta['width'];scene.render.resolution_y=meta['height'];scene.cycles.samples=512
scene.render.image_settings.file_format='PNG';scene.render.image_settings.color_depth='8'
scene.render.filepath=str(OUT/'imported_baseline_f12.png');scene.render.use_compositing=True
bpy.ops.render.render(write_still=True)
report={'packed_texture_nodes':4,'native_UV_only':True,'no_GI':True,'saved_default_surface':'Principled MULTI_GGX',
        'saved_filter_width':scene.cycles.filter_width,'shared_sampling':'mip zero / bilinear',
        'max_EV_ACES_compositor_error':error,'saved_resolution':[meta['width'],meta['height']],
        'note':'Reopened saved file. Verification and renders do not resave or alter its defaults.'}
(OUT/'imported_baseline_verification.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print('IMPORTED_BASELINE_VERIFIED',json.dumps(report),flush=True)
