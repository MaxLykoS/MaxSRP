"""Compare Blender world probes, source sky integrals and owned Unity GPU readbacks.

All values are sky-only, before material Fresnel, albedo, exposure and fog.
The project's shared RGB photometric scale is converted to lux with 100000.
"""
import json
import math
from pathlib import Path
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'OfflineBaseline/SkyboxScene/irradiance_comparison'
NORMALS = {'up':[0,1,0], 'down':[0,-1,0], 'positive_x':[1,0,0], 'negative_x':[-1,0,0],
           'positive_z':[0,0,1], 'negative_z':[0,0,-1]}
FACE_FOR_NORMAL = {'positive_x':0,'negative_x':1,'up':2,'down':3,'positive_z':4,'negative_z':5}
Y = np.array([.2126,.7152,.0722])
SCALE = 100000


def bilinear(image,u,v,repeat_u=False):
    h,w = image.shape[:2]
    x,y = np.asarray(u)*w-.5,np.asarray(v)*h-.5
    if not repeat_u:
        x = np.clip(x,0,w-1)
    y = np.clip(y,0,h-1)
    x0,y0 = np.floor(x).astype(int),np.floor(y).astype(int)
    tx,ty = (x-x0)[...,None],(y-y0)[...,None]
    x1,y1 = x0+1,np.minimum(y0+1,h-1)
    if repeat_u:
        x0,x1 = x0%w,x1%w
    else:
        x1 = np.minimum(x1,w-1)
    return ((1-tx)*image[y0,x0]+tx*image[y0,x1])*(1-ty)+((1-tx)*image[y1,x0]+tx*image[y1,x1])*ty


def directions(w,h,blender=False):
    u,v = np.meshgrid((np.arange(w)+.5)/w,(np.arange(h)+.5)/h)
    phi = ((.5-u) if blender else (u-.5))*2*math.pi
    lat = (v-.5)*math.pi
    d = np.stack([np.cos(lat)*np.cos(phi),np.sin(lat),np.cos(lat)*np.sin(phi)],axis=-1)
    weights = np.cos(lat)*math.pi/h*2*math.pi/w
    return d,weights


def integral(image,blender=False):
    h,w = image.shape[:2]
    d,weights = directions(w,h,blender)
    result = {}
    for name,n in NORMALS.items():
        rgb = np.sum(image*np.maximum(0,d@np.array(n))[...,None]*weights[...,None],axis=(0,1))*SCALE
        result[name] = {'irradiance_rgb_lux':rgb.tolist(),'illuminance_lux':float(rgb@Y)}
    return result


def cube_direction(face,u,v):
    a,b = 2*u-1,2*v-1
    one = np.ones_like(a)
    xyz = [(one,-b,-a),(-one,-b,a),(a,one,b),(a,-one,-b),(a,-b,one),(-a,-b,-one)][face]
    d = np.stack(xyz,axis=-1)
    return d/np.linalg.norm(d,axis=-1,keepdims=True)


def sample_cube(mips,d,lod):
    d = np.asarray(d)
    axis = np.argmax(np.abs(d),axis=-1)
    major = np.max(np.abs(d),axis=-1)
    face = 2*axis+(np.take_along_axis(d,axis[...,None],axis=-1)[...,0]<0)
    x,y,z = d[...,0],d[...,1],d[...,2]
    a = np.choose(face,[-z,z,x,x,x,-x])/major
    b = np.choose(face,[-y,-y,z,-z,-y,-y])/major
    u,v = (a+1)/2,(b+1)/2
    lod = np.broadcast_to(np.asarray(lod),face.shape)
    lod = np.clip(lod,0,len(mips)-1)
    first = np.floor(lod).astype(int)
    second = np.minimum(first+1,len(mips)-1)
    values = []
    for levels in [first,second]:
        value = np.zeros(d.shape,np.float64)
        for level in np.unique(levels):
            for f in range(6):
                mask = (levels==level)&(face==f)
                if mask.any():
                    value[mask] = bilinear(mips[level][f],u[mask],v[mask])
        values.append(value)
    t = (lod-first)[...,None]
    return values[0]*(1-t)+values[1]*t


def simulated_diffuse(blender_image):
    # Expected source chain. CPU box mips/face-clamped bilinear sampling are an
    # approximation of the GPU cube sampler, not a claim of actual GPU readback.
    u,v = np.meshgrid((np.arange(128)+.5)/128,(np.arange(128)+.5)/128)
    cube = []
    for face in range(6):
        d = cube_direction(face,u,v)
        sky_u = .5-np.arctan2(d[...,2],d[...,0])/(2*math.pi)
        sky_v = np.arcsin(np.clip(d[...,1],-1,1))/math.pi+.5
        cube.append(bilinear(blender_image,sky_u,sky_v,True))
    mips = [np.array(cube).astype(np.float16).astype(float)]
    while mips[-1].shape[1]>1:
        last = mips[-1]
        size = last.shape[1]//2
        mips.append(last.reshape(6,size,2,size,2,3).mean(axis=(2,4)).astype(np.float16).astype(float))
    sample_count = 128
    i = np.arange(sample_count,dtype=np.uint32)
    bits = i.copy()
    bits = (bits<<16)|(bits>>16)
    bits = ((bits&0x55555555)<<1)|((bits&0xAAAAAAAA)>>1)
    bits = ((bits&0x33333333)<<2)|((bits&0xCCCCCCCC)>>2)
    bits = ((bits&0x0F0F0F0F)<<4)|((bits&0xF0F0F0F0)>>4)
    bits = ((bits&0x00FF00FF)<<8)|((bits&0xFF00FF00)>>8)
    xi_y = bits.astype(float)*2.3283064365386963e-10
    phi = 2*math.pi*i/sample_count
    cos_theta,sin_theta = np.sqrt(1-xi_y),np.sqrt(xi_y)
    lod = np.clip(.5*np.log2(1/(sample_count*np.maximum(cos_theta/math.pi,1e-6))/(4*math.pi/(6*128*128))),0,7)
    result = {}
    for name,n in NORMALS.items():
        n = np.array(n,dtype=float)
        up = np.array([0,1,0] if abs(n[1])<.999 else [1,0,0])
        tangent = np.cross(up,n)
        tangent /= np.linalg.norm(tangent)
        bitangent = np.cross(n,tangent)
        d = tangent*np.cos(phi[:,None])*sin_theta[:,None]+bitangent*np.sin(phi[:,None])*sin_theta[:,None]+n*cos_theta[:,None]
        rgb = sample_cube(mips,d,lod).mean(axis=0)*math.pi*SCALE
        no_lod_rgb = sample_cube(mips,d,0).mean(axis=0)*math.pi*SCALE
        result[name] = {'irradiance_rgb_lux':rgb.tolist(),'illuminance_lux':float(rgb@Y),
                        'mip_zero_illuminance_lux':float(no_lod_rgb@Y),'source_mip_range':[float(lod.min()),float(lod.max())]}
    return result


blender = json.loads((OUT/'blender_world_measurement.json').read_text(encoding='utf-8'))
env_node = next(n for n in blender['world_nodes'] if n['type']=='TEX_ENVIRONMENT')
w,h = env_node['image']['size']
image = np.fromfile(OUT/env_node['image']['raw_file'],np.float32).reshape(h,w,4)[:,:,:3]
report = {'units':'RGB photometric embedding, lux; excludes the analytic solar disk',
          'blender_cycles_probe':blender['probes'],'blender_environment_integral':integral(image,True),
          'unity_algorithm_CPU_approximation':simulated_diffuse(image),
          'unity_GPU_readback_available':False}
snapshot_path = OUT/'unity_snapshot.json'
if snapshot_path.exists():
    snapshot = json.loads(snapshot_path.read_text(encoding='utf-8'))
    textures = {t['file']:t for t in snapshot['textures']}
    def read(filename):
        t = textures[filename]
        return np.fromfile(OUT/filename,np.float32).reshape(t['height'],t['width'],4)[:,:,:3]
    sky = read('unity_skyview.f32')
    report['unity_GPU_readback_available'] = True
    report['unity_snapshot'] = snapshot
    baseline_manifest = json.loads((OUT.parent/'manifest.json').read_text(encoding='utf-8'))
    actual_atmosphere = json.loads(snapshot['atmosphereJson'])
    source_atmosphere = baseline_manifest['atmosphere']
    mismatches = {key:[value,actual_atmosphere.get(key)] for key,value in source_atmosphere.items()
                  if isinstance(value,(float,int,bool)) and not np.isclose(value,actual_atmosphere.get(key,np.nan),rtol=1e-6,atol=1e-6)}
    baseline_sky_info = json.loads((OUT.parent/'sky_info.json').read_text(encoding='utf-8'))
    source_sun = next(l for l in baseline_manifest['lights'] if l['type']==1)
    actual_direction = np.array([snapshot['sunDirection'][k] for k in ['x','y','z']])
    report['input_alignment'] = {'atmosphere_numeric_mismatches':mismatches,
        'sun_intensity_matches':bool(np.isclose(snapshot['sunIntensity'],source_sun['intensity'])),
        'sun_direction_matches':bool(np.allclose(actual_direction,baseline_sky_info['sun_direction_unity'],atol=1e-6)),
        'reference_height_matches':bool(np.isclose(json.loads(snapshot['iblSettingsJson'])['ReferenceHeight'],baseline_sky_info['reference_height']))}
    report['unity_GPU_skyview_integral'] = integral(sky,False)
    # ReadPixels' row packing for cube render targets is calibrated against the
    # separately read 2D source and the actual producer's face directions. Keep
    # both residuals in the report rather than silently assuming a Y convention.
    cubes = [read('unity_environment_'+str(f)+'.f32') for f in range(6)]
    size = cubes[0].shape[0]
    u,v = np.meshgrid((np.arange(size)+.5)/size,(np.arange(size)+.5)/size)
    residuals = {}
    for flip in [False,True]:
        errors,reference_energy = [],[]
        for face in range(6):
            d = cube_direction(face,u,1-v if flip else v)
            su = np.arctan2(d[...,2],d[...,0])/(2*math.pi)+.5
            sv = np.arcsin(np.clip(d[...,1],-1,1))/math.pi+.5
            expected = bilinear(sky,su,sv,True)
            errors.append(np.square(cubes[face]-expected).sum())
            reference_energy.append(np.square(expected).sum())
        residuals[str(flip)] = float(np.sqrt(sum(errors)/sum(reference_energy)))
    flip = residuals['True']<residuals['False']
    report['cube_readback_flip_v'] = bool(flip)
    report['cube_source_relative_RMSE_for_each_row_convention'] = residuals
    edges = np.linspace(-1,1,size+1)
    ex,ey = np.meshgrid(edges,edges)
    area = np.arctan2(ex*ey,np.sqrt(ex*ex+ey*ey+1))
    solid_angle = np.abs(np.diff(np.diff(area,axis=0),axis=1))
    report['unity_GPU_environment_cube_integral'] = {}
    for name,n in NORMALS.items():
        rgb = np.zeros(3)
        for face in range(6):
            d = cube_direction(face,u,1-v if flip else v)
            rgb += np.sum(cubes[face]*np.maximum(0,d@np.array(n))[...,None]*solid_angle[...,None],axis=(0,1))*SCALE
        report['unity_GPU_environment_cube_integral'][name] = {'irradiance_rgb_lux':rgb.tolist(),'illuminance_lux':float(rgb@Y)}
    report['unity_GPU_diffuse_cube'] = {}
    for name,face in FACE_FOR_NORMAL.items():
        data = read('unity_diffuse_'+str(face)+'.f32')
        size = data.shape[0]
        rgb = data[size//2-1:size//2+1,size//2-1:size//2+1].mean(axis=(0,1))*math.pi*SCALE
        report['unity_GPU_diffuse_cube'][name] = {'irradiance_rgb_lux':rgb.tolist(),'illuminance_lux':float(rgb@Y),
                                                'note':'center four cube texels average, normals within 2 degrees of axis'}
    report['nonfinite_components'] = {name:int((~np.isfinite(read(name))).sum()) for name in textures}
ground_path = OUT/'local_ground_bounce.json'
if ground_path.exists():
    report['blender_local_downward_probe'] = json.loads(ground_path.read_text(encoding='utf-8'))
(OUT/'irradiance_comparison.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
for name in NORMALS:
    values = {'normal':name, 'Blender_Cycles_lux':report['blender_cycles_probe'][name]['illuminance_lux'],
              'source_integral_lux':report['blender_environment_integral'][name]['illuminance_lux'],
              'Unity_CPU_approx_lux':report['unity_algorithm_CPU_approximation'][name]['illuminance_lux']}
    if report['unity_GPU_readback_available']:
        values['Unity_GPU_LUT_lux'] = report['unity_GPU_skyview_integral'][name]['illuminance_lux']
        values['Unity_GPU_cube_lux'] = report['unity_GPU_environment_cube_integral'][name]['illuminance_lux']
        values['Unity_GPU_diffuse_lux'] = report['unity_GPU_diffuse_cube'][name]['illuminance_lux']
    print(json.dumps(values))
print('UNITY_GPU_READBACK_AVAILABLE',report['unity_GPU_readback_available'])
