"""Run in a separate background Blender; inputs are read-only Unity GPU/geometry exports.

blender --factory-startup --background --python-exit-code 1 --python ... -- \
  --input OfflineBaseline/GIWhitebox/Closed/CyclesReference/<exportUTC>

Use --calibration-only --output <approved-output> to check numeric point normalization
without loading any Unity data. This is an RGB numerical embedding, never W == lm.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys
from datetime import datetime, timezone

import bpy
import numpy as np


def args_parse():
    parser = argparse.ArgumentParser()
    parser.add_argument('--input', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--calibration-only', action='store_true')
    parser.add_argument('--samples', type=int, default=1024)
    parser.add_argument('--seed', type=int, default=20261027)
    parser.add_argument('--device', choices=('OPTIX', 'CPU'), default='OPTIX')
    return parser.parse_args(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])


def utc():
    return datetime.now(timezone.utc).isoformat()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, obj):
    path.write_text(json.dumps(obj, indent=2, allow_nan=False), encoding='utf-8')


def safe_output(path):
    approved = Path(__file__).resolve().parents[2] / 'OfflineBaseline' / 'GIWhitebox'
    path = path.resolve()
    if not path.is_relative_to(approved.resolve()):
        raise ValueError('Output must stay inside approved OfflineBaseline/GIWhitebox')
    path.mkdir(parents=True, exist_ok=True)
    return path


def set_cycles(scene, samples, seed, device):
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = samples
    scene.cycles.seed = seed
    scene.cycles.use_animated_seed = False
    scene.cycles.use_adaptive_sampling = False
    scene.cycles.use_denoising = False
    scene.cycles.sample_clamp_direct = 0
    scene.cycles.sample_clamp_indirect = 0
    scene.cycles.max_bounces = 4
    scene.cycles.diffuse_bounces = 4
    scene.cycles.glossy_bounces = 0
    scene.cycles.transmission_bounces = 0
    scene.cycles.volume_bounces = 0
    scene.cycles.transparent_max_bounces = 0
    scene.cycles.caustics_reflective = False
    scene.cycles.caustics_refractive = False
    if hasattr(scene.cycles, 'use_fast_gi'):
        scene.cycles.use_fast_gi = False
    scene.render.use_compositing = False
    scene.render.use_sequencer = False
    scene.render.film_transparent = False
    scene.render.image_settings.file_format = 'OPEN_EXR'
    scene.render.image_settings.color_depth = '32'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.view_settings.view_transform = 'Raw'
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1
    scene.render.resolution_percentage = 100
    scene.world = bpy.data.worlds.new(scene.name + ' black world')
    scene.world.use_nodes = True
    scene.world.node_tree.nodes['Background'].inputs['Color'].default_value = (0,0,0,1)
    scene.world.node_tree.nodes['Background'].inputs['Strength'].default_value = 0
    if device == 'CPU':
        scene.cycles.device = 'CPU'
    else:
        prefs = bpy.context.preferences.addons['cycles'].preferences
        prefs.compute_device_type = 'OPTIX'
        prefs.get_devices()
        found = []
        for item in prefs.devices:
            item.use = item.type == 'OPTIX'
            if item.use:
                found.append(item.name)
        if not found:
            raise RuntimeError('No OptiX device: calibration/bake has not started')
        scene.cycles.device = 'GPU'
    return [item.name for item in bpy.context.preferences.addons['cycles'].preferences.devices if item.use] if device != 'CPU' else ['CPU']


def select_scene(scene):
    bpy.context.window.scene = scene
    bpy.ops.object.select_all(action='DESELECT')


def diffuse_material(name, rgb, two_sided=False):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    nodes.clear()
    output = nodes.new('ShaderNodeOutputMaterial')
    diffuse = nodes.new('ShaderNodeBsdfDiffuse')
    diffuse.inputs['Color'].default_value = (*map(float, rgb), 1)
    diffuse.inputs['Roughness'].default_value = 0  # Lambert, not Oren-Nayar.
    if two_sided:
        links.new(diffuse.outputs['BSDF'], output.inputs['Surface'])
    else:
        geometry = nodes.new('ShaderNodeNewGeometry')
        black = nodes.new('ShaderNodeBsdfDiffuse')
        black.inputs['Color'].default_value = (0,0,0,1)
        black.inputs['Roughness'].default_value = 0
        mix = nodes.new('ShaderNodeMixShader')
        links.new(geometry.outputs['Backfacing'], mix.inputs[0])
        links.new(diffuse.outputs['BSDF'], mix.inputs[1])
        links.new(black.outputs['BSDF'], mix.inputs[2])
        links.new(mix.outputs[0], output.inputs['Surface'])
    return mat


def add_point(scene, name, position, power, color):
    data = bpy.data.lights.new(name, 'POINT')
    data.energy = float(power)
    data.color = tuple(map(float,color))
    if hasattr(data,'normalize'):
        data.normalize = True
    data.shadow_soft_size = 0
    data.use_shadow = True
    if hasattr(data,'use_custom_distance'):
        data.use_custom_distance = False
    obj = bpy.data.objects.new(name, data)
    scene.collection.objects.link(obj)
    obj.location = position
    return obj


def calibration(out, args):
    scene = bpy.data.scenes.new('GI whitebox numeric POINT calibration')
    select_scene(scene)
    devices = set_cycles(scene, 64, args.seed, args.device)
    scene.render.resolution_x = scene.render.resolution_y = 32
    scene.cycles.max_bounces = scene.cycles.diffuse_bounces = 0
    scene.cycles.pixel_filter_type = 'BOX'
    scene.cycles.filter_width = .01
    mesh = bpy.data.meshes.new('Unit white Lambert calibration card')
    mesh.from_pydata([(-.2,-.2,0),(.2,-.2,0),(.2,.2,0),(-.2,.2,0)],[],[(0,1,2,3)])
    mesh.materials.append(diffuse_material('Unit white Lambert', (1,1,1)))
    obj = bpy.data.objects.new('Calibration card',mesh)
    scene.collection.objects.link(obj)
    camera_data = bpy.data.cameras.new('Calibration ortho camera')
    camera_data.type = 'ORTHO'; camera_data.ortho_scale = .02
    camera = bpy.data.objects.new('Calibration ortho camera',camera_data)
    camera.location = (0,0,3); scene.collection.objects.link(camera); scene.camera = camera
    light = add_point(scene,'Normalized numeric POINT, power 1',(0,0,1),1,(1,1,1))
    report = {'utc':utc(),'blender_version':bpy.app.version_string,
              'build_hash':bpy.app.build_hash.decode(),'devices':devices,
              'scope':'White Lambert direct-only card, black world, delta POINT, normalized numeric power=1. No spectral lm/W claim or fitted scale.',
              'denoise':False,'clamp_direct':0,'clamp_indirect':0,'measurements':[]}
    for distance in (1,2):
        light.location.z = distance
        path = out / f'calibration_point_power1_distance{distance}.exr'
        scene.render.filepath = str(path)
        bpy.ops.render.render(write_still=True,scene=scene.name)
        image = bpy.data.images.load(str(path), check_existing=False)
        pixels = np.asarray(image.pixels[:],dtype=np.float32).reshape(32,32,4)
        bpy.data.images.remove(image)
        # Exact central four orthographic ray hits, to avoid area/cosine bias.
        coords = ((np.arange(32)+.5)/32-.5)*camera_data.ortho_scale
        yy,xx = np.meshgrid(coords[15:17],coords[15:17],indexing='ij')
        expected = float((distance/(4*math.pi**2*(distance**2+xx**2+yy**2)**1.5)).mean())
        actual = float(pixels[15:17,15:17,:3].mean())
        error = abs(actual/expected-1)
        item = {'distance':distance,'numeric_power':1,'measured_white_lambert_radiance':actual,
                'analytic_white_lambert_radiance':expected,'relative_error':error}
        report['measurements'].append(item)
        if not np.isfinite(pixels).all() or error>.003:
            report['valid'] = False
            write_json(out/'light_unit_calibration.json',report)
            raise AssertionError(f'POINT numeric calibration failed; no bake: {item}')
    ratio = report['measurements'][0]['measured_white_lambert_radiance']/report['measurements'][1]['measured_white_lambert_radiance']
    report['measured_distance1_to_distance2_ratio'] = ratio
    report['valid'] = abs(ratio/4-1)<.003
    # Verify the bake API itself, including Color-off: its first receiver uses
    # albedo 0.18 while the expected output remains uncolored irradiance/pi.
    light.location.z=1
    uv_layer=mesh.uv_layers.new(name='CalibrationUV')
    for loop,uv in zip(mesh.polygons[0].loop_indices,[(0,0),(1,0),(1,1),(0,1)]):
        uv_layer.data[loop].uv=uv
    material=mesh.materials[0]
    diffuse=next(n for n in material.node_tree.nodes if n.type=='BSDF_DIFFUSE' and n.inputs['Color'].default_value[0]>.5)
    diffuse.inputs['Color'].default_value=(.18,.18,.18,1)
    image=bpy.data.images.new('Direct color-off bake calibration',width=32,height=32,float_buffer=True,is_data=True)
    image.colorspace_settings.name='Non-Color'
    target=material.node_tree.nodes.new('ShaderNodeTexImage');target.image=image
    material.node_tree.nodes.active=target
    obj.select_set(True);bpy.context.view_layer.objects.active=obj
    bpy.ops.object.bake(type='DIFFUSE',pass_filter={'DIRECT'},use_clear=True,margin=0,
                        use_selected_to_active=False,uv_layer='CalibrationUV')
    pixels=np.asarray(image.pixels[:],dtype=np.float32).reshape(32,32,4)
    bake_coords=((np.arange(32)+.5)/32-.5)*.4
    yy,xx=np.meshgrid(bake_coords[15:17],bake_coords[15:17],indexing='ij')
    expected=float((1/(4*math.pi**2*(1+xx**2+yy**2)**1.5)).mean())
    actual=float(pixels[15:17,15:17,:3].mean());error=abs(actual/expected-1)
    report['direct_coloroff_bake']={'source_albedo':.18,'color_included':False,
                                  'measured':actual,'expected_EOverPi':expected,'relative_error':error}
    pixels.astype('<f4').tofile(out/'calibration_direct_coloroff.rgba32f')
    report['valid']=report['valid'] and error<.003 and bool(np.isfinite(pixels).all())
    write_json(out/'light_unit_calibration.json',report)
    if not report['valid']:
        raise AssertionError('POINT inverse-square check failed; no bake')
    print('GI_POINT_CALIBRATION',json.dumps(report),flush=True)
    # Remove only this process's temporary calibration scene, never any live UI.
    select_scene(bpy.data.scenes.new('GI whitebox pending import'))
    bpy.data.scenes.remove(scene)
    return report


def rotate(v):
    # Proper right-handed rotation, det +1. Winding and normal direction survive.
    return (float(v[0]),-float(v[2]),float(v[1]))


def build_scene(manifest, args):
    scene = bpy.data.scenes.new(Path(manifest['scene']).stem+' Cycles artificial indirect reference')
    select_scene(scene)
    devices = set_cycles(scene,args.samples,args.seed,args.device)
    scene.cycles.max_bounces = scene.cycles.diffuse_bounces = int(manifest['nativeMaximumBounces'])
    light = manifest['pointLight']
    add_point(scene,'Exported white 1000 lm - native RGB units',rotate(light['positionWS']),
              light['normalizedCyclesNumericPower'],light['linearColor'])
    objects = {}
    for atlas in manifest['atlases']:
        faces = [f for f in manifest['faces'] if f['atlas']==atlas['index']]
        if not faces:
            raise ValueError('Empty target atlas')
        vertices, polygons, uvs, material_ids = [],[],[],[]
        materials = []
        for face in faces:
            p = np.asarray(face['positionsWS'],dtype=np.float64)
            n = np.asarray(face['normalsWS'],dtype=np.float64)
            indices = face['triangles']
            for triangle in np.asarray(indices).reshape(-1,3):
                ng = np.cross(p[triangle[1]]-p[triangle[0]],p[triangle[2]]-p[triangle[0]])
                ng /= np.linalg.norm(ng)
                if min(n@ng)<.9999:
                    raise ValueError('Non-flat or inconsistent source normal; fixture requires exact geometric receiver normals')
            key = (tuple(face['metaAlbedo']),bool(face['twoSided']))
            if key not in materials:
                materials.append(key)
            material_index = materials.index(key)
            base = len(vertices)
            vertices.extend(map(rotate,face['positionsWS']))
            for triangle in np.asarray(indices).reshape(-1,3):
                polygons.append(tuple(base+int(k) for k in triangle))
                uvs.append([face['atlasUV'][int(k)] for k in triangle])
                material_ids.append(material_index)
        mesh = bpy.data.meshes.new('Actual Unity world geometry atlas '+str(atlas['index']))
        mesh.from_pydata(vertices,[],polygons);mesh.update()
        for rgb,two_sided in materials:
            mesh.materials.append(diffuse_material('Meta Lambert '+str(rgb),rgb,two_sided))
        uv_layer = mesh.uv_layers.new(name='NativeLightmapAtlas')
        for poly,poly_uv,material_index in zip(mesh.polygons,uvs,material_ids):
            poly.use_smooth = False;poly.material_index = material_index
            for loop,uv in zip(poly.loop_indices,poly_uv):
                uv_layer.data[loop].uv = uv
        obj = bpy.data.objects.new(mesh.name,mesh);scene.collection.objects.link(obj)
        image = bpy.data.images.new('Cycles indirect color-off atlas '+str(atlas['index']),
                                    width=atlas['width'],height=atlas['height'],alpha=True,float_buffer=True,is_data=True)
        image.colorspace_settings.name='Non-Color'
        image.generated_color=(0,0,0,0)
        for material in mesh.materials:
            nodes=material.node_tree.nodes
            for node in nodes:node.select=False
            node=nodes.new('ShaderNodeTexImage');node.image=image;node.select=True;nodes.active=node
        objects[atlas['index']] = (obj,image)
    return scene,objects,devices


def bake(manifest, source, out, args, unit_report):
    scene,objects,devices = build_scene(manifest,args)
    state={'startedUtc':utc(),'completed':False,'scene':manifest['scene'],'nativeRunId':manifest['nativeRunId'],
           'source_manifest_sha256':sha(source/'manifest.json'),'devices':devices,
           'blender_version':bpy.app.version_string,'build_hash':bpy.app.build_hash.decode(),
           'samples':args.samples,'seed':args.seed,'max_bounces':scene.cycles.max_bounces,
           'denoise':False,'clamp_direct':0,'clamp_indirect':0,'margin':0,
           'bake_type':'DIFFUSE','pass_filter':['INDIRECT'],'receiver_color_included':False,
           'world':'black','material':'Pure Lambert with actual Unity Meta effective albedo; backfaces black absorbing when source doubleSidedGI=false',
           'coordinate_transform':'Unity(x,y,z)->Blender(x,-z,y); determinant +1',
           'unit_convention':'Normalized POINT power=4*pi*candela/nativeIntensityScale. Bake result is native RGB E_indirect/pi; times nativeToSceneHDR for diagnostic9. Numeric embedding only; not SI watts=lumens.',
           'nativeDirectionalWarning':'Native color alone can be a direction-encoded base. Compare both raw diag9 and color multiplied by exported geom-normal directional reconstruction factor; no fitted energy scale.',
           'calibration_valid':unit_report['valid'],'atlases':[]}
    write_json(out/'cycles_state.json',state)
    scene.render.bake.use_pass_direct=False;scene.render.bake.use_pass_indirect=True
    scene.render.bake.use_pass_color=False;scene.render.bake.margin=0
    scene.render.bake.use_selected_to_active=False
    try:
        for atlas in manifest['atlases']:
            index=atlas['index'];obj,image=objects[index]
            select_scene(scene);obj.select_set(True);bpy.context.view_layer.objects.active=obj
            print('GI_CYCLES_BAKE_START',index,atlas['width'],atlas['height'],args.samples,flush=True)
            bpy.ops.object.bake(type='DIFFUSE',pass_filter={'INDIRECT'},use_clear=True,margin=0,
                                use_selected_to_active=False,uv_layer='NativeLightmapAtlas')
            rgba=np.asarray(image.pixels[:],dtype=np.float32).reshape(atlas['height'],atlas['width'],4)
            if not np.isfinite(rgba).all() or (rgba[:,:,:3]<-1e-7).any():
                raise ValueError('Cycles bake contains non-finite/negative native linear values')
            raw=out/f'cycles_indirect_{index}.rgba32f';rgba.astype('<f4').tofile(raw)
            image.filepath_raw=str(out/f'cycles_indirect_{index}.exr');image.file_format='OPEN_EXR'
            if hasattr(image,'use_half_precision'):image.use_half_precision=False
            image.save_render(filepath=image.filepath_raw,scene=scene)
            record={'index':index,'width':atlas['width'],'height':atlas['height'],
                    'raw':raw.name,'rawSHA256':sha(raw),'exr':Path(image.filepath_raw).name,
                    'rgb_sum':float(rgba[:,:,:3].sum(dtype=np.float64)),
                    'maximum':float(rgba[:,:,:3].max()),'row_order':'bottom-left; same as Unity raw Texture2D storage'}
            state['atlases'].append(record);write_json(out/'cycles_state.json',state)
            print('GI_CYCLES_BAKE_DONE',json.dumps(record),flush=True)
        state['completed']=True;state['completedUtc']=utc()
        write_json(out/'cycles_state.json',state)
        bpy.ops.wm.save_as_mainfile(filepath=str(out/'GIWhitebox_CyclesReference.blend'))
    except Exception as error:
        state['error']=str(error);state['failedUtc']=utc();write_json(out/'cycles_state.json',state)
        raise


def main():
    args=args_parse()
    if args.samples<64:raise ValueError('Use at least 64 samples; no silent quality reduction')
    if args.calibration_only:
        if not args.output:raise ValueError('--output required for calibration')
        out=safe_output(args.output)
        calibration(out,args)
        return
    if not args.input:raise ValueError('--input required for bake')
    source=args.input.resolve();manifest_path=source/'manifest.json'
    manifest=json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    if manifest.get('schemaVersion')!=1 or Path(manifest['scene']).stem not in ('GIWhiteboxClosed','GIWhiteboxOpen'):
        raise ValueError('Input is not a current whitebox export')
    for atlas in manifest['atlases']:
        for key in ('color','direction'):
            if sha(source/atlas[key+'Raw'])!=atlas[key+'SHA256']:raise ValueError('Native GPU raw SHA mismatch')
    out=safe_output(args.output or source/'Cycles')
    if (out/'cycles_state.json').exists():raise ValueError('Use a new output directory; do not overwrite a reference')
    calibration_report=calibration(out,args)
    bake(manifest,source,out,args,calibration_report)


if __name__=='__main__':
    main()
