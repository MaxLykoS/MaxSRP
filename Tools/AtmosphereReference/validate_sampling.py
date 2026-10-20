"""Independent checks of the phase samplers used beyond first scattering."""
import json
from pathlib import Path
import numpy as np
import reference_transport as rt
from numba import njit

ROOT=Path(__file__).resolve().parents[2];OUT=ROOT/'OfflineBaseline/AtmosphereReference'
r=json.loads((OUT/'independent_results.json').read_text());p=np.array(r['physical_parameters'])
@njit
def moments(rayleigh,g,count):
    np.random.seed(20261020)
    values=np.zeros(4);rotation_error=0.
    d=np.array([.3,.4,np.sqrt(.75)])
    for i in range(count):
        c=rt.sample_phase_cosine(rayleigh,g)
        values[0]+=c;values[1]+=c*c;values[2]+=c**3;values[3]+=c**4
        if i<1000:
            x,y,z=rt.scatter_direction(d[0],d[1],d[2],c)
            rotation_error=max(rotation_error,abs(x*x+y*y+z*z-1),abs(x*d[0]+y*d[1]+z*d[2]-c))
    return values/count,rotation_error
mu,w=np.polynomial.legendre.leggauss(1024);count=1000000;checks=[]
for rayleigh in [True,False]:
    pdf=3*(1+mu*mu)/(16*np.pi) if rayleigh else 3*(1-p[4]**2)*(1+mu*mu)/(8*np.pi*(2+p[4]**2)*(1+p[4]**2-2*p[4]*mu)**1.5)
    expected=np.array([((mu**i)*pdf*w).sum()*2*np.pi for i in range(1,5)])
    observed,rotation=moments(rayleigh,p[4],count)
    stderr=np.sqrt(np.maximum(0,np.array([expected[1]-expected[0]**2,expected[3]-expected[1]**2]))/count)
    assert np.all(abs(observed[:2]-expected[:2])<6*stderr)
    assert rotation<1e-12
    checks.append({'phase':'Rayleigh' if rayleigh else 'Cornette-Shanks','samples':count,
        'expected_first_two_moments':expected[:2].tolist(),'sampled_first_two_moments':observed[:2].tolist(),
        'standard_errors':stderr.tolist(),'rotation_max_absolute_error':rotation})
(OUT/'phase_sampling_validation.json').write_text(json.dumps(checks,indent=2),encoding='utf-8')
print('PHASE_SAMPLING_VALIDATED',json.dumps(checks))
