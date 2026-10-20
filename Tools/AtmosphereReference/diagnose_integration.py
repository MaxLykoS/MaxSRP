"""Isolate ray-step bias with the independent medium and solar transmission."""
import json
from pathlib import Path
import numpy as np
import reference_transport as rt

ROOT=Path(__file__).resolve().parents[2];OUT=ROOT/'OfflineBaseline/AtmosphereReference'
r=json.loads((OUT/'independent_results.json').read_text());p=np.array(r['physical_parameters'])
solar=np.array(r['solar_irradiance_internal_RGB']);Y=np.array([.2126,.7152,.0722])
table=np.load(OUT/('independent_solar_tau_'+r['validation']['table_parameter_hash'][:12]+'.npy'))
u=json.loads((OUT/'unity_probes.json').read_text())['probes']
def vec(v):
    a=np.array([v[k] for k in ['x','y','z']]);return a/np.linalg.norm(a)
def constant_cell_integral(probe,steps):
    view,sun=vec(probe['viewDirection']),vec(probe['sunDirection']);radius=p[0]+probe['height']
    distance=rt.boundary(radius,view[1],p);ds=distance/steps
    pos=np.arange(steps)[:,None]*ds*view+view*ds*.5+np.array([0,radius,0])
    radii=np.linalg.norm(pos,axis=1);h=radii-p[0]
    br=np.exp(-h[:,None]/p[2])*p[7:10]
    bm=np.exp(-h[:,None]/p[3])*p[10]
    extinction=br+bm+np.exp(-h[:,None]/p[3])*p[11]+np.maximum(0,1-abs(h[:,None]-p[5])/p[6])*p[12:15]
    optical=extinction*ds
    before=np.cumsum(optical,axis=0)-optical
    mus=pos@sun/radii
    ts=np.array([[rt.solar_t(rad,mu,c,p,table) for c in range(3)] for rad,mu in zip(radii,mus)])
    phase=br*rt.phase_r(view@sun)+bm*rt.phase_m(view@sun,p[4])
    weight=np.where(extinction>0,-np.expm1(-optical)/np.maximum(extinction,1e-100),ds)
    return (np.exp(-before)*phase*ts*weight).sum(axis=0)*solar
rows=[];refs={x['name']:x for x in r['probes']}
for probe in u:
    if probe['viewLabel'] not in ['sunward','zenith']:continue
    true=np.array(refs[probe['name']]['single'])@Y
    if true<1e-10:continue
    cases={str(n):float(constant_cell_integral(probe,n)@Y/true) for n in [32,64,128,256,1024]}
    rows.append({'name':probe['name'],'Unity_single_over_reference':sum(probe['singleSky'][c]*Y[i] for i,c in enumerate('xyz'))/true,
                 'independent_constant_cell_over_reference_by_step_count':cases})
(OUT/'integration_ablation.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
print('INTEGRATION_ABLATION',json.dumps(rows[:2]))

# Refine only the current-case angular integral; MC multiple scattering is held
# fixed. This records deterministic quadrature error separately from its CI.
probe=next(x for x in u if x['sunLabel']=='current' and x['height']==2)
mu,w=np.polynomial.legendre.leggauss(64)
single=rt.single_irradiance(2,vec(probe['sunDirection']),p,table,mu,w,256,8192)*solar
old=np.array(next(x for x in r['irradiance'] if x['sunLabel']=='current' and x['height']==2)['single'])
refinement={'current_single_irradiance_refined':single.tolist(),'previous_single':old.tolist(),
            'luminance_absolute_change_internal':float(abs((single-old)@Y)),
            'luminance_change_lux':float(abs((single-old)@Y)*1e5),
            'angular_grid':[64,256],'ray_steps':8192}
(OUT/'irradiance_refinement.json').write_text(json.dumps(refinement,indent=2),encoding='utf-8')
print('IRRADIANCE_REFINEMENT',json.dumps(refinement))
