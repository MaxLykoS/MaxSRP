"""Independent two-plane Cycles one-bounce oracle; separate background process only.

blender --background --factory-startup --python-exit-code 1 --python this.py -- \
  --output OfflineBaseline/GICalibration/Followup/CyclesOneBounce/<UTC>

No Unity input, room asset, or previous reference is opened or changed.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import sys

import bpy
import numpy as np


def utc():
    return datetime.now(timezone.utc).isoformat()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def dump(path,value):
    path.write_text(json.dumps(value,indent=2,allow_nan=False),encoding='utf-8')


def rotate(p):
    return (float(p[0]),-float(p[2]),float(p[1]))


def lambert(name,rho):
    material=bpy.data.materials.new(name)
    material.use_nodes=True
    nodes=material.node_tree.nodes;nodes.clear()
    output=nodes.new('ShaderNodeOutputMaterial')
    diffuse=nodes.new('ShaderNodeBsdfDiffuse')
    diffuse.inputs['Color'].default_value=(rho,rho,rho,1)
    diffuse.inputs['Roughness'].default_value=0
    material.node_tree.links.new(diffuse.outputs['BSDF'],output.inputs['Surface'])
    return material


def plane(scene,name,vertices,material):
    mesh=bpy.data.meshes.new(name)
    mesh.from_pydata(list(map(rotate,vertices)),[],[(0,1,2,3)]);mesh.update()
    mesh.polygons[0].use_smooth=False
    layer=mesh.uv_layers.new(name='SensorUV')
    for loop,uv in zip(mesh.polygons[0].loop_indices,[(0,0),(1,0),(1,1),(0,1)]):
        layer.data[loop].uv=uv
    mesh.materials.append(material)
    obj=bpy.data.objects.new(name,mesh);scene.collection.objects.link(obj)
    return obj


def analytical(qx,qz,order):
    """E_Q, without receiver rho/pi. Gaussian quadrature independent of Cycles."""
    x,w=np.polynomial.legendre.leggauss(order)
    xx,zz=np.meshgrid(x,x,indexing='xy');ww=np.outer(w,w)
    source=((.5*2/math.pi)*ww/(xx*xx+zz*zz+4)**1.5)
    qx=np.asarray(qx).reshape(-1);qz=np.asarray(qz).reshape(-1)
    answer=[]
    for first in range(0,len(qx),8):
        r2=(xx[None]-qx[first:first+8,None,None])**2+(zz[None]-qz[first:first+8,None,None])**2+1
        answer.extend((source[None]/r2**2).sum(axis=(1,2)).tolist())
    return np.asarray(answer)


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--samples',type=int,default=2048)
    parser.add_argument('--threads',type=int,default=4)
    args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
    root=Path(__file__).resolve().parents[2]
    out=args.output.resolve()
    if not out.is_relative_to(root/'OfflineBaseline'/'GICalibration'/'Followup'):
        raise ValueError('Output must stay within approved GICalibration/Followup')
    out.mkdir(parents=True,exist_ok=True)
    if (out/'state.json').exists():raise ValueError('Do not overwrite an oracle run')
    if args.samples<2048:raise ValueError('At least the authorized 2048 samples are required')
    scene=bpy.context.scene
    bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
    scene.name='Independent virtual-sensor one-bounce oracle'
    scene.render.engine='CYCLES';scene.cycles.device='CPU'
    scene.render.threads_mode='FIXED';scene.render.threads=args.threads
    scene.cycles.samples=args.samples;scene.cycles.seed=20261027
    scene.cycles.use_animated_seed=False;scene.cycles.use_adaptive_sampling=False
    scene.cycles.use_denoising=False
    scene.cycles.sample_clamp_direct=0;scene.cycles.sample_clamp_indirect=0
    scene.cycles.glossy_bounces=0;scene.cycles.transmission_bounces=0
    scene.cycles.transparent_max_bounces=0;scene.cycles.volume_bounces=0
    scene.cycles.caustics_reflective=False;scene.cycles.caustics_refractive=False
    if hasattr(scene.cycles,'use_fast_gi'):scene.cycles.use_fast_gi=False
    if hasattr(scene.cycles,'film_exposure'):scene.cycles.film_exposure=1
    scene.render.use_compositing=False;scene.render.use_sequencer=False
    scene.render.film_transparent=False
    scene.render.image_settings.file_format='OPEN_EXR';scene.render.image_settings.color_depth='32'
    scene.render.image_settings.color_mode='RGBA'
    scene.view_settings.view_transform='Raw';scene.view_settings.exposure=0;scene.view_settings.gamma=1
    scene.world=bpy.data.worlds.new('Exactly black world');scene.world.use_nodes=True
    scene.world.node_tree.nodes['Background'].inputs['Color'].default_value=(0,0,0,1)
    scene.world.node_tree.nodes['Background'].inputs['Strength'].default_value=0
    reflector=plane(scene,'2m gray reflector, Unity y0 Ng up',
                    [(-1,0,-1),(-1,0,1),(1,0,1),(1,0,-1)],lambert('Reflector Lambert rho .5',.5))
    sensor=plane(scene,'Virtual 2cm sensor, Unity y1 Ng down',
                 [(-.01,1,-.01),(.01,1,-.01),(.01,1,.01),(-.01,1,.01)],lambert('Sensor Lambert rho .5; color excluded',.5))
    sensor.visible_shadow=False;sensor.visible_diffuse=False
    point=bpy.data.lights.new('Isotropic numeric I1, total power4pi','POINT')
    point.energy=4*math.pi;point.color=(1,1,1);point.shadow_soft_size=0
    if hasattr(point,'normalize'):point.normalize=True
    if hasattr(point,'use_custom_distance'):point.use_custom_distance=False
    emitter=bpy.data.objects.new(point.name,point);scene.collection.objects.link(emitter)
    emitter.location=rotate((0,2,0))
    scene.render.bake.use_pass_direct=False;scene.render.bake.use_pass_indirect=True
    scene.render.bake.use_pass_color=False;scene.render.bake.margin=0
    scene.render.bake.use_selected_to_active=False
    n=64
    q=((np.arange(n)+.5)/n-.5)*.02
    qz,qx=np.meshgrid(q,q,indexing='ij')
    expected=analytical(qx,qz,128).reshape(n,n)/math.pi
    center128=float(analytical([0],[0],128)[0]);center256=float(analytical([0],[0],256)[0])
    roi=np.zeros((n,n),dtype=bool);roi[16:48,16:48]=True
    center=np.zeros((n,n),dtype=bool);center[31:33,31:33]=True
    expected.astype('<f8').tofile(out/'analytic_sensor_EOverPi.f64')
    state={'protocolVersion':1,'startedUtc':utc(),'phase':'prepared','completed':False,
           'blenderVersion':bpy.app.version_string,'buildHash':bpy.app.build_hash.decode(),
           'scriptSHA256':sha(Path(__file__)),'device':'CPU','threads':args.threads,'samples':args.samples,'seed':20261027,
           'geometryUnity':{'reflector':'2mx2m, y0, normal up','reflectorRho':.5,'sensor':'2cmx2cm, y1, normal down','sensorRho':.5,
                            'sensorVisibleShadow':bool(sensor.visible_shadow),'sensorVisibleDiffuse':bool(sensor.visible_diffuse),
                            'pointPosition':[0,2,0],'pointNumericI':1,'normalizedPointPower':float(point.energy),'pointRadius':0},
           'coordinateMapping':'Unity(x,y,z)->Blender(x,-z,y); proper rotation det+1',
           'bake':'DIFFUSE INDIRECT, receiver color off, no denoise/clamp/margin, black world; exact float32 image pixels',
           'formulaEQ':'(.5*2/pi)*integral[-1,1]^2 1/[(x*x+z*z+4)^1.5*(x*x+z*z+1)^2] dx dz',
           'expectedDomain':'Cycles color-off = E_Q/pi; native one-bounce should be compared in its independently calibrated raw domain',
           'analyticCenterE':center256,'analyticCenterEOverPi':center256/math.pi,
           'analyticCenterQuadrature128Vs256Relative':abs(center128/center256-1),
           'analyticInteriorMeanEOverPi':float(expected[roi].mean()),
           'analyticGridSHA256':sha(out/'analytic_sensor_EOverPi.f64'),
           'roi':'Pixel indices x/y 16..47, inclusive, central half; center indices31..32',
           'controls':[]}
    dump(out/'state.json',state)
    if state['analyticCenterQuadrature128Vs256Relative']>1e-10:
        raise ValueError('Analytic quadrature has not converged')
    try:
        for bounces in (1,2,4):
            scene.cycles.max_bounces=bounces;scene.cycles.diffuse_bounces=bounces
            state['phase']='baking max_bounces='+str(bounces);dump(out/'state.json',state)
            image=bpy.data.images.new('Sensor indirect max_bounces'+str(bounces),width=n,height=n,alpha=True,float_buffer=True,is_data=True)
            image.colorspace_settings.name='Non-Color';image.generated_color=(0,0,0,0)
            nodes=sensor.data.materials[0].node_tree.nodes
            for node in nodes:node.select=False
            node=nodes.new('ShaderNodeTexImage');node.image=image;node.select=True;nodes.active=node
            bpy.ops.object.select_all(action='DESELECT');sensor.select_set(True);bpy.context.view_layer.objects.active=sensor
            print('ONE_BOUNCE_START',bounces,args.samples,flush=True)
            bpy.ops.object.bake(type='DIFFUSE',pass_filter={'INDIRECT'},use_clear=True,margin=0,use_selected_to_active=False,uv_layer='SensorUV')
            raw=np.asarray(image.pixels[:],dtype=np.float32).reshape(n,n,4)
            if not np.isfinite(raw).all() or (raw[:,:,:3]<-1e-9).any():raise ValueError('Non-finite/negative result')
            raw_path=out/f'sensor_bounces{bounces}.rgba32f';raw.astype('<f4').tofile(raw_path)
            image.file_format='OPEN_EXR';image.filepath_raw=str(out/f'sensor_bounces{bounces}.exr')
            if hasattr(image,'use_half_precision'):image.use_half_precision=False
            image.save_render(filepath=image.filepath_raw,scene=scene)
            value=raw[:,:,:3].mean(axis=2);mean=float(value[roi].mean(dtype=np.float64));reference=float(expected[roi].mean())
            residual=value[roi]-expected[roi]
            entry={'maxBounces':bounces,'diffuseBounces':bounces,'interiorPixels':int(roi.sum()),
                   'interiorMeanEOverPi':mean,'analyticInteriorMeanEOverPi':reference,
                   'relativeMeanError':mean/reference-1,'centerFourPixelMean':float(value[center].mean()),
                   'analyticCenterFourPixelMean':float(expected[center].mean()),'maximum':float(value.max()),
                   'allZero':bool(value.max()<=1e-12),'interiorLuminanceMAE':float(np.abs(residual).mean()),
                   'interiorResidualStd':float(residual.std()),'raw':raw_path.name,'rawSHA256':sha(raw_path),
                   'exr':Path(image.filepath_raw).name,'exrSHA256':sha(Path(image.filepath_raw)),
                   'interpretation':'Zero can indicate insufficient path depth, not a unit failure' if value.max()<=1e-12 else 'Nonzero one-bounce estimate; compare with analytic E_Q/pi'}
            state['controls'].append(entry);dump(out/'state.json',state)
            print('ONE_BOUNCE_RESULT',json.dumps(entry),flush=True)
        nonzero=[c for c in state['controls'] if not c['allZero']]
        state['minimumNonzeroMaxBounces']=min((c['maxBounces'] for c in nonzero),default=None)
        state['nonzeroControlsWithinOnePercentOfAnalytic']=bool(nonzero) and all(abs(c['relativeMeanError'])<.01 for c in nonzero)
        state['completed']=True;state['phase']='complete';state['completedUtc']=utc()
        state['valid']=state['nonzeroControlsWithinOnePercentOfAnalytic']
        dump(out/'state.json',state)
        bpy.ops.wm.save_as_mainfile(filepath=str(out/'GI_OneBounce_Cycles.blend'))
        lines=[f"Cycles {state['blenderVersion']}, build {state['buildHash']}, CPU threads={args.threads}",
               'Pure Lambert INDIRECT/coloroff, 2048spp, fixed seed, no denoise/clamp/margin.',
               'Analytic sensor center E='+format(center256,'.12g')+'; E/pi='+format(center256/math.pi,'.12g'),
               'Same interior texel-center reference E/pi='+format(float(expected[roi].mean()),'.12g'),
               'bounces | measured inner mean E/pi | relative mean error | all zero']
        for entry in state['controls']:
            lines.append(f"{entry['maxBounces']} | {entry['interiorMeanEOverPi']:.12g} | {entry['relativeMeanError']:.6%} | {entry['allZero']}")
        lines.append('Minimum nonzero max_bounces='+str(state['minimumNonzeroMaxBounces']))
        lines.append('Raw state valid='+str(state['valid'])+'; nonzero controls within explicit1% analytic tolerance. A zero low-depth control is not interpreted as a unit mismatch.')
        (out/'RESULTS.txt').write_text('\n'.join(lines)+'\n',encoding='utf-8')
        print('\n'.join(lines),flush=True)
        if not state['valid']:raise AssertionError('Nonzero one-bounce bake differs from independent analytic integral beyond1%; investigate')
    except Exception as error:
        state['error']=str(error);state['failedUtc']=utc();state['phase']='failed';state['valid']=False
        dump(out/'state.json',state);raise


if __name__=='__main__':
    main()
