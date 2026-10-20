"""Run the independent atmosphere reference; values never come from Unity LUTs."""
import argparse,hashlib,json,time
from pathlib import Path
import numpy as np
import reference_transport as rt

ROOT=Path(__file__).resolve().parents[2];OUT=ROOT/'OfflineBaseline/AtmosphereReference'
OUT.mkdir(parents=True,exist_ok=True)
parser=argparse.ArgumentParser()
parser.add_argument('--samples',type=int,default=262144)
parser.add_argument('--only-current',action='store_true')
parser.add_argument('--validate-only',action='store_true')
args=parser.parse_args()
input_path=OUT/'reference_inputs.json'
inputs=json.loads(input_path.read_text(encoding='utf-8'))
settings=inputs['settings'];p=rt.parameters(settings)
solar=np.array(inputs['solar_irradiance_RGB'])
code_hash=hashlib.sha256((Path(__file__).parent/'reference_transport.py').read_bytes()).hexdigest()
parameter_hash=hashlib.sha256(p.tobytes()+code_hash.encode()).hexdigest()
nodes,weights=np.polynomial.legendre.leggauss(128)
fine_nodes,fine_weights=np.polynomial.legendre.leggauss(512)
table_path=OUT/('independent_solar_tau_'+parameter_hash[:12]+'.npy')
start=time.time()
if table_path.exists():table=np.load(table_path)
else:
    print('BUILD_INDEPENDENT_SOLAR_TABLE',flush=True)
    table=rt.build_solar_tau_table(p,nodes,weights);np.save(table_path,table)

mus,ws=np.polynomial.legendre.leggauss(1024)
phase_integrals=[float((3*(1+mus*mus)/(16*np.pi)*ws).sum()*2*np.pi),
    float((3*(1-p[4]**2)*(1+mus*mus)/(8*np.pi*(2+p[4]**2)*(1+p[4]**2-2*p[4]*mus)**1.5)*ws).sum()*2*np.pi)]
assert max(abs(v-1) for v in phase_integrals)<1e-9
# Exponential columns and the triangular ozone profile have analytic integrals.
column=p[7:10]*p[2]*(1-np.exp(-p[1]/p[2]))+(p[10]+p[11])*p[3]*(1-np.exp(-p[1]/p[3]))
assert p[5]-p[6]>=0 and p[5]+p[6]<=p[1]
column+=p[12:15]*p[6]
analytic=np.exp(-column)
vertical=rt.direct_transmission(0,1,p,fine_nodes,fine_weights)
assert np.max(np.abs(analytic-vertical))<1e-5
table_errors=[];quadrature_errors=[]
for h in [2,10,100,1000,10000,30000,59999]:
    r=p[0]+h;horizon=-np.sqrt((r-p[0])*(r+p[0]))/r
    for mu in [1,.5,.05,.005,horizon+.0001]:
        exact=rt.direct_transmission(h,mu,p,fine_nodes,fine_weights)
        coarse=rt.direct_transmission(h,mu,p,nodes,weights)
        sampled=np.array([rt.solar_t(r,mu,c,p,table) for c in range(3)])
        table_errors.append(float(np.max(np.abs(sampled-exact))))
        quadrature_errors.append(float(np.max(np.abs(coarse-exact))))
assert max(table_errors)<.002,(max(table_errors),'Independent solar table is not accurate enough')
validation={'phase_integrals':phase_integrals,'vertical_analytic':analytic.tolist(),'vertical_quadrature':vertical.tolist(),
    'max_solar_table_absolute_error':max(table_errors),'max_128_vs_512_quadrature_absolute_error':max(quadrature_errors),
    'table_shape':list(table.shape),'table_parameter_hash':parameter_hash,'reference_code_sha256':code_hash}
(OUT/'independent_validation.json').write_text(json.dumps(validation,indent=2),encoding='utf-8')
print('INDEPENDENT_VALIDATION',json.dumps(validation),flush=True)
if args.validate_only:raise SystemExit(0)

probes=inputs['probes']
if args.only_current:probes=[v for v in probes if v['sunLabel']=='current' and v['height']==2]
def vec(v):
    a=np.array([v[k] for k in ['x','y','z']],np.float64);return a/np.linalg.norm(a)
def summary(batches):
    return {'mean':batches.mean(axis=0).tolist(),'standard_error':(batches.std(axis=0,ddof=1)/np.sqrt(len(batches))).tolist()}
results=[];blocks=64;per_block=max(1,(args.samples+blocks-1)//blocks)
def monte_carlo(height,view,sun,name,hemisphere=False):
    digest=hashlib.sha256(p.tobytes()+view.tobytes()+sun.tobytes()+str((height,blocks,per_block,hemisphere,code_hash)).encode()).hexdigest()
    cache=OUT/('mc_'+name+'_'+digest[:12]+'.npy')
    if cache.exists():return np.load(cache)
    batches=rt.path_trace(height,view,sun,p,table,blocks,per_block,20261019,hemisphere)
    np.save(cache,batches);return batches
for i,probe in enumerate(probes):
    view,sun=vec(probe['viewDirection']),vec(probe['sunDirection']);height=probe['height']
    single=rt.single_scatter(height,view,sun,p,table,8192)
    lower=rt.single_scatter(height,view,sun,p,table,4096)
    trans=rt.direct_transmission(height,sun[1],p,fine_nodes,fine_weights)
    batches=monte_carlo(height,view,sun,probe['name'])
    mc_single=batches[:,:,0];higher=batches[:,:,1:].sum(axis=2)
    full=single[None,:]+higher
    stderr=mc_single.std(axis=0,ddof=1)/np.sqrt(blocks)
    # An independently sampled first-order estimator checks free-flight,
    # phase mixtures, albedo weighting and solar connections against quadrature.
    assert np.all(np.abs(mc_single.mean(axis=0)-single)<6*stderr+np.maximum(single*.005,1e-7)),(probe['name'],'MC single scatter disagrees with quadrature')
    item={'name':probe['name'],'height':height,'sunLabel':probe['sunLabel'],'viewLabel':probe['viewLabel'],
          'single':(single*solar).tolist(),'solar_transmittance':trans.tolist(),
          'full':summary(full*solar),'multiple':summary(higher*solar),'MC_single_check':summary(mc_single*solar),
          'single_4096_vs_8192_absolute_difference':(np.abs(single-lower)*solar).tolist(),
          'orders_mean':(batches.mean(axis=0)*solar[:,None]).tolist(),
          'last_two_orders_mean':(batches[:,:,-2:].sum(axis=2).mean(axis=0)*solar).tolist()}
    results.append(item)
    print('REFERENCE_PROBE',i+1,len(probes),probe['name'],'single',item['single'],'full',item['full']['mean'],'elapsed',round(time.time()-start,1),flush=True)
    (OUT/'independent_probes_partial.json').write_text(json.dumps(results,indent=2),encoding='utf-8')

irradiance=[];unique={}
for probe in probes:unique[(probe['sunLabel'],probe['height'])]=probe
gauss_mu,gauss_w=np.polynomial.legendre.leggauss(32)
for key,probe in unique.items():
    sun=vec(probe['sunDirection']);view=np.array([0.,1.,0.]);height=probe['height']
    single=rt.single_irradiance(height,sun,p,table,gauss_mu,gauss_w,128,4096)
    coarse_mu,coarse_w=np.polynomial.legendre.leggauss(16)
    coarse=rt.single_irradiance(height,sun,p,table,coarse_mu,coarse_w,64,2048)
    batches=monte_carlo(height,view,sun,'irradiance_'+probe['sunLabel']+'_h'+str(height),True)
    higher=batches[:,:,1:].sum(axis=2)
    item={'sunLabel':probe['sunLabel'],'height':height,'single':(single*solar).tolist(),
          'full':summary((single[None,:]+higher)*solar),'multiple':summary(higher*solar),
          'MC_single_check':summary(batches[:,:,0]*solar),
          'angular_and_spatial_refinement_absolute_change':(np.abs(single-coarse)*solar).tolist()}
    irradiance.append(item)
    print('REFERENCE_IRRADIANCE',key,item['full'],'elapsed',round(time.time()-start,1),flush=True)
output={'scope':'Independent 64-bit numerical RTE. Physical inputs only; no Unity LUT or sky radiance read. Black ground, delta Sun, Rayleigh + Cornette-Shanks; no scene GI.',
    'samples_per_channel':blocks*per_block,'independent_batches':blocks,'max_orders':24,
    'multiple_scattering_estimator':'Null-collision volume paths, phase importance sampling, next-event Sun connections; exact single scatter used as control variate.',
    'confidence':'Standard errors across independently seeded batches. Shared solar-table discretization error is separate.',
    'reference_code_sha256':code_hash,'physical_parameters':p.tolist(),'solar_irradiance_internal_RGB':solar.tolist(),
    'physical_input_manifest_sha256':hashlib.sha256(input_path.read_bytes()).hexdigest(),
    'validation':validation,'probes':results,'irradiance':irradiance,'elapsed_seconds':time.time()-start}
(OUT/('independent_results_pilot.json' if args.only_current else 'independent_results.json')).write_text(json.dumps(output,indent=2),encoding='utf-8')
print('INDEPENDENT_ATMOSPHERE_COMPLETE',round(time.time()-start,1),'seconds',flush=True)
