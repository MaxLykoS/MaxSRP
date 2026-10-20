"""Quantify the helmet-only HDR comparison, masking silhouette antialiasing edges.

Screening thresholds are declared before evaluation: 10% mean luminance difference
for combined, direct-unshadowed and unoccluded IBL; silhouette IoU at least .99.
These thresholds compare approximate renderers, not mathematical BSDF identity.
"""
import json
import hashlib
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT/'OfflineBaseline/HelmetOnly'
meta = json.loads((OUT/'unity_helmet.json').read_text(encoding='utf-8'))
source_hash = hashlib.sha256((ROOT/'Assets/MaxRendererPipeline/ShaderLibrary/Realtime/BRDF.hlsl').read_bytes()).hexdigest()
if meta.get('brdfSourceSHA256'):
    assert meta['brdfSourceSHA256']==source_hash, 'Unity BRDF capture is stale; recapture after compilation'
W,H = meta['width'],meta['height']
baseline = json.loads((OUT/'blender_helmet.json').read_text(encoding='utf-8'))
assert baseline['camera_from_actual_unity_capture'], 'Render Blender from the actual Unity capture first'
assert baseline['resolution']==[W,H] and abs(baseline['vertical_fov']-meta['verticalFov'])<1e-5
q = meta['cameraRotation']
x,y,z,w = (q[k] for k in ['x','y','z','w'])
rotation = np.array([[1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)],
                     [2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)],
                     [2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)]])
expected_camera = np.eye(4)
expected_camera[:3,:3] = rotation
expected_camera[:3,3] = [meta['cameraPosition'][k] for k in ['x','y','z']]
coordinate_swap = np.eye(4)[[0,2,1,3]]
expected_camera = coordinate_swap@expected_camera@np.diag([1,1,-1,1])
camera_error = float(np.max(np.abs(np.array(baseline['camera_matrix_world'])-expected_camera)))
assert camera_error<1e-5, f'Camera mismatch: {camera_error}'
Y = np.array([.2126,.7152,.0722])


def read(prefix,name):
    return np.fromfile(OUT/(prefix+'_'+name+'.f32'),np.float32).reshape(H,W,4)


unity = {n:read('unity',n) for n in ['combined','direct','ibl','ibl_diffuse','ibl_specular','direct_unshadowed']}
blender = {n:read('blender',n) for n in ['combined_matched','combined_delta_matched','combined_physical','direct','direct_delta','ibl','ibl_physical','direct_unshadowed','direct_delta_unshadowed','albedo','normal','roughness_metallic']}
mask_u = unity['combined'][:,:,3]>.5
mask_b = blender['combined_matched'][:,:,3]>.5
def iou(a,b):
    return float((a&b).sum()/max(1,(a|b).sum()))
score = iou(mask_u,mask_b)
flip_score = iou(mask_u[::-1],mask_b)
flip = flip_score>score+.02
if flip:
    unity = {k:v[::-1].copy() for k,v in unity.items()}
    mask_u = mask_u[::-1]
# Compare silhouettes at 50% coverage: Unity is single-sample rasterization,
# while Cycles filters alpha. A .99 contour erodes only the Cycles silhouette.
mask = mask_u&mask_b&(blender['combined_matched'][:,:,3]>.99)
# A three-pixel interior excludes rasterization vs path-tracer coverage differences.
for _ in range(3):
    previous = mask.copy()
    mask[1:-1,1:-1] = previous[1:-1,1:-1]&previous[:-2,1:-1]&previous[2:,1:-1]&previous[1:-1,:-2]&previous[1:-1,2:]
    mask[0] = mask[-1] = False
    mask[:,0] = mask[:,-1] = False
assert mask.sum()>1000, 'No reliable common helmet pixels'
report = {'resolution':[W,H],'comparison_before_exposure':True,'GI_enabled':False,
          'capturedUtc':meta['capturedUtc'],'brdfSourceSHA256':meta.get('brdfSourceSHA256'),
          'camera_matrix_max_absolute_error':camera_error,
          'silhouette_coverage_threshold':.5,'interior_blender_coverage_threshold':.99,
          'unity_rows_flipped_for_readback_alignment':bool(flip),'silhouette_IoU':iou(mask_u,mask_b),
          'common_interior_pixels':int(mask.sum()),'mean_luminance_ratio_tolerance':[.9,1.1],
          'comparisons':{}}
for un,bn in [('combined','combined_matched'),('direct','direct'),('ibl','ibl'),('direct_unshadowed','direct_unshadowed'),
              ('combined','combined_physical'),('ibl','ibl_physical'),('direct_unshadowed','direct_delta_unshadowed'),
              ('combined','combined_delta_matched'),('direct','direct_delta')]:
    a,b = unity[un][:,:,:3][mask],blender[bn][:,:,:3][mask]
    ay,by = a@Y,b@Y
    assert np.isfinite(a).all() and np.isfinite(b).all()
    ratio = float(ay.mean()/max(by.mean(),1e-12))
    valid = by>max(by.mean()*.01,1e-6)
    stats = {'unity_mean_HDR_luminance':float(ay.mean()),'blender_mean_HDR_luminance':float(by.mean()),
             'unity_to_blender_mean_ratio':ratio,'unity_to_blender_median_pixel_ratio':float(np.median(ay[valid]/by[valid])),
             'relative_L1_luminance_error':float(np.abs(ay-by).sum()/max(by.sum(),1e-12)),
             'unity_mean_RGB':a.mean(axis=0).tolist(),'blender_mean_RGB':b.mean(axis=0).tolist()}
    key = un+'_vs_'+bn
    report['comparisons'][key] = stats
    print(key,json.dumps(stats))
for name in ['albedo','normal','roughness_metallic']:
    index = {'albedo':0,'normal':1,'roughness_metallic':2}[name]
    g = read('unity','gbuffer'+str(index))
    if flip:
        g = g[::-1]
    if name=='albedo':
        a,b = g[:,:,:3][mask],blender[name][:,:,:3][mask]
        report['albedo_mean_absolute_RGB_error'] = float(np.abs(a-b).mean())
    elif name=='roughness_metallic':
        a,b = g[:,:,[2,3]][mask],blender[name][:,:,[0,1]][mask]
        report['roughness_metallic_mean_absolute_error'] = np.abs(a-b).mean(axis=0).tolist()
    else:
        # Unity world normal to Blender world normal, both decoded from [0,1].
        a,b = (g[:,:,[0,2,1]]*2-1)[mask],(blender[name][:,:,:3]*2-1)[mask]
        a /= np.maximum(np.linalg.norm(a,axis=1,keepdims=True),1e-8)
        b /= np.maximum(np.linalg.norm(b,axis=1,keepdims=True),1e-8)
        angle = np.degrees(np.arccos(np.clip(np.sum(a*b,axis=1),-1,1)))
        report['shading_normal_angle_degrees'] = {'mean':float(angle.mean()),'median':float(np.median(angle)),'p90':float(np.percentile(angle,90))}
residual = unity['combined'][:,:,:3]-unity['direct'][:,:,:3]-unity['ibl'][:,:,:3]
report['unity_additivity_mean_absolute_error'] = float(np.abs(residual[mask]).mean())
screened = ['combined_vs_combined_matched','direct_unshadowed_vs_direct_unshadowed','ibl_vs_ibl']
report['brightness_screening_pass'] = bool(report['silhouette_IoU']>=.99 and
    all(.9<=report['comparisons'][k]['unity_to_blender_mean_ratio']<=1.1 for k in screened))
report['screening_scope'] = 'Whole-helmet mean HDR brightness at current inputs; not per-pixel BRDF equivalence'
report['per_pixel_equivalence_verified'] = False
report['direct_delta_note'] = 'Additional zero-angular-size Sun diagnostic; excluded from the original 2-degree Sun screening. Shows unresolved BRDF/highlight differences.'
report['matched_delta_light_screening_pass'] = bool(report['silhouette_IoU']>=.99 and
    all(.9<=report['comparisons'][k]['unity_to_blender_mean_ratio']<=1.1 for k in
        ['combined_vs_combined_delta_matched','direct_unshadowed_vs_direct_delta_unshadowed','ibl_vs_ibl']))
report['overall_comparison_pass'] = report['matched_delta_light_screening_pass']
quality_file=OUT/'sampling_quality_applied.json'
quality_applied=False
if quality_file.exists():
    quality=json.loads(quality_file.read_text(encoding='utf-8'))
    quality_applied=quality['appliedUtc']<=meta['capturedUtc']
    if quality_applied:report['input_texture_quality']=quality
old_report = OUT/'BeforeSmithGGX/comparison.json'
if old_report.exists():
    previous = json.loads(old_report.read_text(encoding='utf-8'))
    keys = ['direct_unshadowed_vs_direct_delta_unshadowed','combined_vs_combined_delta_matched','ibl_vs_ibl']
    report['before_after_smith_ggx'] = {k:{
        'before_unity_to_cycles':previous['comparisons'][k]['unity_to_blender_mean_ratio'],
        'after_unity_to_cycles':report['comparisons'][k]['unity_to_blender_mean_ratio'],
        'unity_after_to_before':report['comparisons'][k]['unity_mean_HDR_luminance']/previous['comparisons'][k]['unity_mean_HDR_luminance']}
        for k in keys}
quality_before=OUT/'BeforeSamplingQuality/comparison.json'
if quality_applied and quality_before.exists():
    previous=json.loads(quality_before.read_text(encoding='utf-8'))
    # Preserve the fixed-input GGX comparison instead of attributing texture changes to GGX.
    report['before_after_smith_ggx']=previous.get('before_after_smith_ggx')
    report['before_after_sampling_quality']={k:{
        'before_unity_to_cycles':previous['comparisons'][k]['unity_to_blender_mean_ratio'],
        'after_unity_to_cycles':report['comparisons'][k]['unity_to_blender_mean_ratio'],
        'before_relative_L1':previous['comparisons'][k]['relative_L1_luminance_error'],
        'after_relative_L1':report['comparisons'][k]['relative_L1_luminance_error']}
        for k in ['direct_unshadowed_vs_direct_delta_unshadowed','combined_vs_combined_delta_matched','ibl_vs_ibl']}


def preview(array):
    rgb = np.maximum(array[:,:,:3],0)*meta['exposureMultiplier']
    rgb = np.clip(rgb*(2.51*rgb+.03)/(rgb*(2.43*rgb+.59)+.14),0,1)
    rgb = np.where(rgb<=.0031308,12.92*rgb,1.055*np.power(rgb,1/2.4)-.055)
    alpha = np.clip(array[:,:,3:4],0,1)
    rgb = rgb*alpha+.055*(1-alpha)
    return Image.fromarray(np.uint8(np.clip(rgb[::-1],0,1)*255+.5))


for name,array in unity.items():
    preview(array).save(OUT/('unity_'+name+'.png'))
tile = 512
margin = 44
sheet = Image.new('RGB',(tile*2,(tile+margin)*3),(22,22,22))
draw = ImageDraw.Draw(sheet)
font = ImageFont.truetype('C:/Windows/Fonts/arial.ttf',20)
for row,(label,u,b) in enumerate([('Combined - delta Sun','combined','combined_delta_matched'),('Direct - delta Sun','direct','direct_delta'),('Sky IBL - unoccluded','ibl','ibl')]):
    for col,(name,array) in enumerate([('Unity',unity[u]),('Cycles',blender[b])]):
        top = row*(tile+margin)
        draw.text((col*tile+12,top+10),name+' | '+label,font=font,fill=(245,245,245))
        sheet.paste(preview(array).resize((tile,tile),Image.Resampling.LANCZOS),(col*tile,top+margin))
sheet.save(OUT/'helmet_comparison.png')
if old_report.exists():
    old_images = {n:np.fromfile(OUT/'BeforeSmithGGX'/('unity_'+n+'.f32'),np.float32).reshape(H,W,4)
                  for n in ['combined','direct','ibl']}
    if flip:
        old_images = {k:v[::-1].copy() for k,v in old_images.items()}
    before_after = Image.new('RGB',(tile*3,(tile+margin)*3),(22,22,22))
    labels = ImageDraw.Draw(before_after)
    for row,(label,u,b) in enumerate([('Combined','combined','combined_delta_matched'),('Direct','direct','direct_delta'),('Sky IBL','ibl','ibl')]):
        for col,(name,array) in enumerate([('Before',old_images[u]),('Smith-GGX + BC5/BC7' if quality_applied else 'Smith-GGX',unity[u]),('Cycles',blender[b])]):
            top = row*(tile+margin)
            labels.text((col*tile+12,top+10),name+' | '+label,font=font,fill=(245,245,245))
            before_after.paste(preview(array).resize((tile,tile),Image.Resampling.LANCZOS),(col*tile,top+margin))
    before_after.save(OUT/'helmet_before_after.png')
if quality_applied and quality_before.exists():
    old_quality={n:np.fromfile(OUT/'BeforeSamplingQuality'/('unity_'+n+'.f32'),np.float32).reshape(H,W,4)
                 for n in ['combined','direct','ibl']}
    quality_sheet=Image.new('RGB',(tile*3,(tile+margin)*3),(22,22,22))
    labels=ImageDraw.Draw(quality_sheet)
    for row,(label,u,b) in enumerate([('Combined','combined','combined_delta_matched'),('Direct','direct','direct_delta'),('Sky IBL','ibl','ibl')]):
        for col,(name,a) in enumerate([('Before: DXT5/DXT1',old_quality[u]),('BC5 / BC7',unity[u]),('Cycles native',blender[b])]):
            top=row*(tile+margin)
            labels.text((col*tile+12,top+10),name+' | '+label,font=font,fill='white')
            quality_sheet.paste(preview(a).resize((tile,tile),Image.Resampling.LANCZOS),(col*tile,top+margin))
    quality_sheet.save(OUT/'sampling_quality_before_after.png')
(OUT/'comparison.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print('ORIGINAL_2_DEGREE_BASELINE_MEAN_SCREENING_PASS',report['brightness_screening_pass'])
print('HELMET_OVERALL_COMPARISON_PASS',report['overall_comparison_pass'])
