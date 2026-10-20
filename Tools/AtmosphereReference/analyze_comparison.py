"""Compare actual GPU atmosphere probes to the independent RTE reference."""
import hashlib,json
from pathlib import Path
import numpy as np
import reference_transport as rt # Adds the isolated scientific dependency path.
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.colors import TwoSlopeNorm

ROOT=Path(__file__).resolve().parents[2];OUT=ROOT/'OfflineBaseline/AtmosphereReference'
u=json.loads((OUT/'unity_probes.json').read_text());snap=json.loads((OUT/'unity_snapshot.json').read_text())
r=json.loads((OUT/'independent_results.json').read_text());Y=np.array([.2126,.7152,.0722]);SCALE=1e5
assert r['reference_code_sha256']==hashlib.sha256((Path(__file__).parent/'reference_transport.py').read_bytes()).hexdigest()
assert r['physical_input_manifest_sha256']==hashlib.sha256((OUT/'reference_inputs.json').read_bytes()).hexdigest()
for source in u['sources']:assert hashlib.sha256((ROOT/source['path']).read_bytes()).hexdigest()==source['sha256']
assert len(r['probes'])==len(u['probes'])==48
by_name={p['name']:p for p in u['probes']};rows=[]
def xyz(v):return np.array([v[c] for c in 'xyz'])
for p in r['probes']:
    gpu=by_name[p['name']];single=np.array(p['single']);full=np.array(p['full']['mean']);stderr=np.array(p['full']['standard_error'])
    single_gpu=xyz(gpu['singleSky']);full_gpu=xyz(gpu['fullSky']);t=xyz(gpu['solarTransmittance']);ref_t=np.array(p['solar_transmittance'])
    sy=float(single@Y);fy=float(full@Y);se=float(np.sqrt(((stderr*Y)**2).sum()))
    row={'name':p['name'],'sunLabel':p['sunLabel'],'viewLabel':p['viewLabel'],'height':p['height'],
         'Unity_single_RGB':single_gpu.tolist(),'reference_single_RGB':single.tolist(),
         'single_luminance_ratio':float(single_gpu@Y/sy) if sy>1e-12 else None,
         'Unity_full_RGB':full_gpu.tolist(),'reference_full_RGB':full.tolist(),
         'full_luminance_ratio':float(full_gpu@Y/fy),'reference_full_95CI_halfwidth_luminance':2*se,
         'full_ratio_approx_95CI_halfwidth':float(full_gpu@Y/fy*2*se/fy),
         'Unity_multiple_luminance':float((full_gpu-single_gpu)@Y),
         'reference_multiple_luminance':float(np.array(p['multiple']['mean'])@Y),
         'Unity_solar_transmittance':t.tolist(),'reference_solar_transmittance':ref_t.tolist(),
         'max_absolute_transmittance_error':float(abs(t-ref_t).max()),
         'last_two_scattering_orders_fraction':float(np.array(p['last_two_orders_mean'])@Y/fy)}
    rows.append(row)

texture=next(t for t in snap['textures'] if t['file']=='unity_skyview.f32')
w,h=texture['width'],texture['height'];sky=np.fromfile(OUT/'unity_skyview.f32',np.float32).reshape(h,w,4)[...,:3]
lat=((np.arange(h)+.5)/h-.5)*np.pi
weights=np.maximum(np.sin(lat),0)*np.cos(lat)*np.pi/h*2*np.pi/w
gpu_e=(sky*weights[:,None,None]).sum(axis=(0,1))*SCALE
ref_e=next(p for p in r['irradiance'] if p['sunLabel']=='current' and p['height']==2)
ref_rgb=np.array(ref_e['full']['mean'])*SCALE;ref_sigma=np.array(ref_e['full']['standard_error'])*SCALE
e={'Unity_sky_irradiance_RGB':gpu_e.tolist(),'reference_irradiance_RGB':ref_rgb.tolist(),
   'Unity_illuminance_lux':float(gpu_e@Y),'reference_illuminance_lux':float(ref_rgb@Y),
   'reference_95CI_halfwidth_lux':float(2*np.sqrt(((ref_sigma*Y)**2).sum())),
   'Unity_over_reference':float(gpu_e@Y/(ref_rgb@Y)),
   'RGB_ratios':(gpu_e/ref_rgb).tolist(),
   'note':'Sky only. No solar disk; no exposure or material response. RGB uses the project photometric normalization, not a full spectral/CIE Earth benchmark.'}
refine=json.loads((OUT/'irradiance_refinement.json').read_text())
e['deterministic_single_irradiance_refinement_change_lux']=refine['luminance_change_lux']
current=[p for p in rows if p['sunLabel']=='current' and p['height']==2]
report={'scope':'Independent RGB spherical-medium numerical reference; actual production GPU probes. Same physical coefficients/profiles/phase functions and delta Sun; black ground; no scene GI.',
        'source_capture':u['capturedUtc'],'reference_samples_per_channel':r['samples_per_channel'],
        'statistical_uncertainty':'Approximate 95% intervals = 2 standard errors across 64 independent batches; quadrature/table errors reported separately.',
        'current_sky_energy':e,'probes':rows,
        'current_full_error_percent_range':[(min(p['full_luminance_ratio'] for p in current)-1)*100,(max(p['full_luminance_ratio'] for p in current)-1)*100],
        'current_non_sunward_full_error_percent_range':[(min(p['full_luminance_ratio'] for p in current if p['viewLabel']!='sunward')-1)*100,(max(p['full_luminance_ratio'] for p in current if p['viewLabel']!='sunward')-1)*100],
        'maximum_solar_transmittance_absolute_error':max(p['max_absolute_transmittance_error'] for p in rows),
        'maximum_last_two_orders_fraction':max(p['last_two_scattering_orders_fraction'] for p in rows),
        'validation':r['validation'],'conclusion':'Current daytime integrated sky energy agrees within reference uncertainty; local near-Sun ray-step bias and larger twilight multiple-scattering errors remain. No runtime changes.'}
(OUT/'atmosphere_comparison.json').write_text(json.dumps(report,indent=2),encoding='utf-8')

plt.rcParams.update({'font.size':10,'axes.titlesize':12})
fig,axes=plt.subplots(2,2,figsize=(14,9),layout='constrained')
labels=['current','sun90','sun45','sun10','sun0','sun-3'];views=['zenith','toward45','away45','side10','toward1','away1','sunward']
matrix=np.array([[(next(p for p in rows if p['sunLabel']==s and p['height']==2 and p['viewLabel']==v)['full_luminance_ratio']-1)*100 for v in views] for s in labels])
ax=axes[0,0];im=ax.imshow(matrix,cmap='coolwarm',norm=TwoSlopeNorm(vmin=-8,vcenter=0,vmax=20),aspect='auto')
ax.set_xticks(range(7),views,rotation=30,ha='right');ax.set_yticks(range(6),['69.81 deg','90 deg','45 deg','10 deg','0 deg','-3 deg'])
ax.set_title('GPU full sky / independent reference - 1 (%)')
for i in range(6):
    for j in range(7):ax.text(j,i,f'{matrix[i,j]:+.1f}',ha='center',va='center',fontsize=9,color='white' if abs(matrix[i,j])>12 else 'black')
fig.colorbar(im,ax=ax,shrink=.8,extend='both',label='Relative luminance error (%)')
ax=axes[0,1];x=np.arange(7)
ax.bar(x-.18,[(p['single_luminance_ratio']-1)*100 for p in current],.36,label='Single scatter')
ax.bar(x+.18,[(p['full_luminance_ratio']-1)*100 for p in current],.36,
       yerr=[p['full_ratio_approx_95CI_halfwidth']*100 for p in current],capsize=2,label='Full sky + approx. 95% CI')
ax.axhline(0,color='black',lw=.7);ax.set_xticks(x,views,rotation=30,ha='right');ax.set_ylabel('GPU/reference - 1 (%)')
ax.set_title('Current demo: 2 m observer, Sun 69.81 deg');ax.legend(fontsize=8)
ax=axes[1,0];abl=json.loads((OUT/'integration_ablation.json').read_text())
for name in ['current_h2_zenith','current_h2_sunward']:
    item=next(t for t in abl if t['name']==name);pts=item['independent_constant_cell_over_reference_by_step_count']
    ax.plot([int(k) for k in pts],[(v-1)*100 for v in pts.values()],marker='o',label=name.replace('current_h2_',''))
ax.set_xscale('log',base=2);ax.set_xlabel('Independent constant-cell integration steps');ax.set_ylabel('Single-scatter bias (%)')
ax.set_title('Near-Sun deficit follows 32-step integration');ax.grid(alpha=.25);ax.legend()
ax=axes[1,1];ax.bar([0,1],[e['Unity_illuminance_lux'],e['reference_illuminance_lux']],color=['#4178b8','#e69b42'],
    yerr=[0,e['reference_95CI_halfwidth_lux']],capsize=6)
ax.set_xticks([0,1],['Unity GPU sky LUT','Independent volume paths']);ax.set_ylabel('Horizontal sky illuminance (lux)')
ax.set_ylim(0,7500);ax.set_title('Current integrated sky energy; Sun disk excluded')
for i,val in enumerate([e['Unity_illuminance_lux'],e['reference_illuminance_lux']]):ax.text(i,val+220,f'{val:.2f}',ha='center')
ax.text(.5,.1,f"Reference approx. 95% CI: +/- {e['reference_95CI_halfwidth_lux']:.2f} lux\nShared physical RGB inputs; no Unity sky/LUT in reference",transform=ax.transAxes,ha='center',fontsize=9)
fig.suptitle('MaxSRP atmosphere: independent numerical transport comparison',fontsize=16)
fig.savefig(OUT/'atmosphere_reference_comparison.png',dpi=160);fig.savefig(OUT/'atmosphere_reference_comparison.svg');plt.close(fig)

# Lower-sample beauty images are explicitly separate from the quantitative CI.
info_path=OUT/'independent_sky_info.json'
if info_path.exists():
    info=json.loads(info_path.read_text());iw,ih=info['width'],info['height']
    independent=np.fromfile(OUT/'independent_sky.f32',np.float32).reshape(ih,iw,4)[...,:3]
    assert w==iw*2 and h==ih*2
    gpu=sky.reshape(ih,2,iw,2,3).mean(axis=(1,3))
    def display(a):
        c=np.maximum(a,0)*2.54313135
        c=np.clip(c*(2.51*c+.03)/(c*(2.43*c+.59)+.14),0,1)
        return np.where(c<=.0031308,c*12.92,1.055*c**(1/2.4)-.055)
    fig,ax=plt.subplots(2,1,figsize=(12,7),layout='constrained')
    for a,rgb,title in zip(ax,[gpu,independent],['Unity GPU sky LUT','Independent volume transport (2048 paths/channel/pixel illustration)']):
        a.imshow(display(rgb)[::-1],interpolation='nearest');a.set_axis_off();a.set_title(title)
    fig.suptitle('Same fixed exposure / ACES; no Sun disk; independent sky is not a reused Unity HDR',fontsize=12)
    fig.savefig(OUT/'independent_sky_comparison.png',dpi=160);plt.close(fig)
print('CURRENT_SKY_ENERGY',json.dumps(e));print('CURRENT_FULL_ERROR_RANGE',report['current_full_error_percent_range'])
print('CURRENT_NON_SUNWARD_ERROR_RANGE',report['current_non_sunward_full_error_percent_range'])
print('MAX_LAST_ORDERS_FRACTION',report['maximum_last_two_orders_fraction'])
