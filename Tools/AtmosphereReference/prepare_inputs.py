"""Extract shared physical inputs only. Never pass GPU answers to the solver."""
import json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2];OUT=ROOT/'OfflineBaseline/AtmosphereReference'
snapshot=json.loads((OUT/'unity_snapshot.json').read_text(encoding='utf-8'))
probes=json.loads((OUT/'unity_probes.json').read_text(encoding='utf-8'))['probes']
settings=json.loads(snapshot['atmosphereJson'])
physical={k:v for k,v in settings.items() if isinstance(v,(int,float)) and not isinstance(v,bool)}
data={'scope':'Shared physical parameters and query geometry only; no GPU LUT, sky radiance or expected answers.',
    'settings':physical,'solar_irradiance_RGB':[snapshot['solarIrradiance'][k] for k in ['r','g','b']],
    'probes':[{k:p[k] for k in ['name','sunLabel','viewLabel','height','sunElevation','sunDirection','viewDirection']} for p in probes],
    'assumptions':{'ground_albedo':0,'sun':'delta-directional; visible solar disk excluded',
       'density':'exponential Rayleigh/Mie, triangular ozone','phase':'Rayleigh and Cornette-Shanks',
       'base_rayleigh_per_metre':[5.802e-6,13.558e-6,33.1e-6],'base_mie_scatter_per_metre':3.996e-6,
       'base_mie_absorption_per_metre':.444e-6,'base_ozone_absorption_per_metre':[.650e-6,1.881e-6,.085e-6]}}
(OUT/'reference_inputs.json').write_text(json.dumps(data,indent=2),encoding='utf-8')
print('PHYSICAL_INPUTS_ONLY',len(probes),'queries')
