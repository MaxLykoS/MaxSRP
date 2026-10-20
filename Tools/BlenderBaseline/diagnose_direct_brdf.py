"""Offline ablations on actual GPU GBuffer values; no runtime shader changes.

The NumPy BRDF below is an explicit replica, not an execution of production HLSL.
Agreement with the captured GPU direct output is checked and reported first.
"""
import json
from pathlib import Path
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT/'OfflineBaseline/HelmetOnly'
meta = json.loads((OUT/'unity_helmet.json').read_text(encoding='utf-8'))
sky = json.loads((ROOT/'OfflineBaseline/SkyboxScene/sky_info.json').read_text())
source = (ROOT/'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/BRDF.hlsl').read_text(encoding='latin1')
current_geometry = 'smith_correlated' if 'float VisibilitySmithGGXCorrelated(' in source else 'schlick'
W,H = meta['width'],meta['height']
Y = np.array([.2126,.7152,.0722])
def read(name):
    return np.fromfile(OUT/(name+'.f32'),np.float32).reshape(H,W,4).astype(np.float64)
def unit(v):
    return v/np.maximum(np.linalg.norm(v,axis=-1,keepdims=True),1e-12)
u = read('unity_direct_unshadowed')
b = read('blender_direct_delta_unshadowed')
mask = (u[...,3]>.99)&(b[...,3]>.99)
for _ in range(3):
    old = mask.copy()
    mask[1:-1,1:-1] = old[1:-1,1:-1]&old[:-2,1:-1]&old[2:,1:-1]&old[1:-1,:-2]&old[1:-1,2:]
    mask[0] = mask[-1] = False
    mask[:,0] = mask[:,-1] = False
q = meta['cameraRotation']
x,y,z,w = [q[k] for k in ['x','y','z','w']]
R = np.array([[1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)],
              [2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)],
              [2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)]])
xx,yy = np.meshgrid((np.arange(W)+.5)/W*2-1,(np.arange(H)+.5)/H*2-1)
t = np.tan(np.radians(meta['verticalFov']/2))
V = -unit(np.stack([xx*t*W/H,yy*t,np.ones_like(xx)],axis=-1)@R.T)[mask]
L = unit(np.array([meta['sunDirection'][k] for k in ['x','y','z']]))
irradiance = np.array(sky['sun_ground_rgb'])
reference = b[mask,:3]
gpu = u[mask,:3]

def evaluate(N,albedo,roughness,metalness,geometry='schlick',epsilon=.001):
    N = unit(N)
    half = unit(L+V)
    nl = np.clip(N@L,0,1)
    nv = np.clip(np.sum(N*V,axis=-1),0,1)
    nh = np.clip(np.sum(N*half,axis=-1),0,1)
    vh = np.clip(np.sum(V*half,axis=-1),0,1)
    a2 = np.maximum(roughness,.05)**4
    D = a2/np.maximum(np.pi*((1-nh*nh)+nh*nh*a2)**2,1e-10)
    if geometry=='schlick':
        k = (np.maximum(roughness,.05)+1)**2/8
        G = nv/np.maximum(nv*(1-k)+k,1e-10)*nl/np.maximum(nl*(1-k)+k,1e-10)
    elif geometry=='smith_exact':
        G = 2*nv/np.maximum(nv+np.sqrt(a2+(1-a2)*nv*nv),1e-10)
        G *= 2*nl/np.maximum(nl+np.sqrt(a2+(1-a2)*nl*nl),1e-10)
    else:
        G = 2*nl*nv/np.maximum(nl*np.sqrt(a2+(1-a2)*nv*nv)+nv*np.sqrt(a2+(1-a2)*nl*nl),1e-10)
    f0 = .04*(1-metalness[:,None])+albedo*metalness[:,None]
    F = f0+(1-f0)*(1-vh[:,None])**5
    diffuse = (1-F)*(1-metalness[:,None])*albedo/np.pi
    spec = (D*G/np.maximum(4*nl*nv+epsilon,1e-10))[:,None]*F
    return (diffuse+spec)*irradiance*nl[:,None]

def stats(a,ref):
    ay,by = a@Y,ref@Y
    valid = by>max(by.mean()*.01,1e-6)
    return {'mean_luminance':float(ay.mean()),'mean_ratio_to_reference':float(ay.mean()/by.mean()),
            'median_pixel_ratio':float(np.median(ay[valid]/by[valid])),
            'relative_L1':float(np.abs(ay-by).sum()/by.sum())}

g0,g1,g2 = [read('unity_gbuffer'+str(i))[mask] for i in range(3)]
inputs = (g1[:,:3]*2-1,g0[:,:3],g2[:,2],g2[:,3])
original_replica = evaluate(*inputs)
replica = evaluate(*inputs,geometry=current_geometry,epsilon=0 if current_geometry=='smith_correlated' else .001)
report = {'note':'Offline replica and ablations on actual captured GBuffer. Constant 2m solar attenuation; not GPU executions of variants.',
          'current_geometry':current_geometry,'capturedUtc':meta['capturedUtc'],
          'pixels':int(mask.sum()),'gpu_vs_cycles':stats(gpu,reference),
          'replica_vs_gpu':stats(replica,gpu),'ablations':{}}
for geometry,eps in [('schlick',.001),('schlick',0),('smith_exact',.001),('smith_exact',0),('smith_correlated',0)]:
    a = evaluate(*inputs,geometry=geometry,epsilon=eps)
    report['ablations'][geometry+'_epsilon_'+str(eps)] = {
        'vs_cycles':stats(a,reference),'vs_original_replica':stats(a,original_replica)}
# Same model evaluated with Cycles' diagnostic texture/normal samples.
bn = read('blender_normal')[mask,:3][:,[0,2,1]]*2-1
ba = read('blender_albedo')[mask,:3]
brm = read('blender_roughness_metallic')[mask]
for geom in ['schlick','smith_exact','smith_correlated']:
    a = evaluate(bn,ba,brm[:,0],brm[:,1],geometry=geom,epsilon=0 if geom!='schlick' else .001)
    report['ablations']['blender_samples_'+geom] = {'vs_cycles':stats(a,reference)}
single_path = OUT/'blender_direct_delta_single_ggx.f32'
if single_path.exists():
    single = read('blender_direct_delta_single_ggx')[mask,:3]
    report['cycles_multi_vs_single_ggx'] = stats(reference,single)
    report['gpu_vs_cycles_single_ggx'] = stats(gpu,single)
    report['smith_ablation_vs_cycles_single_ggx'] = stats(evaluate(*inputs,geometry='smith_correlated',epsilon=0),single)
(OUT/'direct_brdf_diagnosis.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report,indent=2))
