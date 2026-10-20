"""Frozen old vs current output shader, replaying the same real camera HDR."""
import hashlib
import json
import os
from pathlib import Path
import sys

import numpy as np
from PIL import Image

extra = Path(os.environ.get('TEMP', '.')) / 'maxsrp-atmosphere-python'
if extra.is_dir():
    sys.path.insert(0, str(extra))
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

def gaussian_filter(a, sigma):
    """Separable float64 Gaussian, reflected edges, truncated at four sigma."""
    radius = int(np.ceil(4*sigma))
    kernel = np.exp(-np.arange(-radius, radius+1, dtype=float)**2/(2*sigma*sigma))
    kernel /= kernel.sum()
    for axis in (0, 1):
        padding = [(0, 0)]*a.ndim
        padding[axis] = (radius, radius)
        padded = np.pad(a, padding, mode='reflect')
        a = np.apply_along_axis(lambda row: np.convolve(row, kernel, mode='valid'), axis, padded)
    return a

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'OfflineBaseline/OutputTransfer/SkyReplay'
r = json.loads((OUT / 'capture.json').read_text())
source_path = Path(r['sourceFile'])
assert hashlib.sha256(source_path.read_bytes()).hexdigest() == r['sourceSha256']
assert hashlib.sha256((ROOT/'Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl').read_bytes()).hexdigest() == r['shaderSha256']
w, h = r['width'], r['height']
source = np.fromfile(source_path, np.float32).reshape(h, w, 4)[..., :3].astype(float)
color = np.maximum(source * r['parameters']['x'], 0)
if r['parameters']['y'] > .5:
    color = np.clip(color*(2.51*color+.03)/(color*(2.43*color+.59)+.14), 0, 1)
base = np.where(color <= .0031308, 12.92*color, 1.055*np.maximum(color, 0)**(1/2.4)-.055)*255
images = {name: np.flipud(np.array(Image.open(OUT/(name+'_srgb8.png')).convert('RGB'), dtype=float)) for name in ['legacy', 'quantized']}
if np.mean(abs(images['legacy']-np.flipud(base))) < np.mean(abs(images['legacy']-base)):
    base = np.flipud(base)
    orientation = 'flipped HDR vertically to match replay raster convention'
else:
    orientation = 'HDR native bottom-left rows match replay raster convention'
mask = (base[..., 0] > 10) & (base[..., 2] > base[..., 0]+10) & (base.max(axis=-1) < 200)
assert mask.sum() > 100000
sigma = 12
weight = gaussian_filter(mask.astype(float), sigma)
valid = (weight > .999) & mask
result = {'capture': r['capturedUtc'], 'source_capture': r['sourceCaptureUtc'], 'size': [w,h], 'orientation': orientation,
          'sky_pixels': int(mask.sum()), 'lowpass_sigma_pixels': sigma, 'lowpass_ROI_pixels': int(valid.sum()), 'paths': {}}
fig, axes = plt.subplots(2, 2, figsize=(12, 7), layout='constrained')
for row, (name, image) in enumerate(images.items()):
    error = image-base
    low = np.stack([gaussian_filter(error[...,c]*mask, sigma)/np.maximum(weight, 1e-9) for c in range(3)], axis=-1)
    stats = {'mean_error_codes_RGB': error[mask].mean(axis=0).tolist(), 'std_error_codes_RGB': error[mask].std(axis=0).tolist(),
             'lowpass_error_abs_p95_codes_RGB': np.quantile(abs(low[valid]), .95, axis=0).tolist(),
             'lowpass_error_RMS_codes_RGB': np.sqrt(np.mean(low[valid]**2, axis=0)).tolist()}
    bins = []
    for c in range(3):
        idx = np.floor(base[...,c][mask]*8).astype(int)
        count = np.bincount(idx, minlength=2048)
        sums = np.bincount(idx, weights=error[...,c][mask], minlength=2048)
        used = count >= 1500
        means = sums[used]/count[used]
        bins.append({'bins': int(used.sum()), 'bias_abs_p95_codes': float(np.quantile(abs(means), .95)),
                     'bias_RMS_codes': float(np.sqrt(np.mean(means**2)))})
    stats['color_bin_error_RGB'] = bins
    result['paths'][name] = stats
    axes[row,0].imshow(np.flipud(np.clip(image,0,255).astype('uint8')))
    axes[row,0].set_title(name + ': same actual HDR -> GPU output')
    display_error = low.mean(axis=-1)
    shown = axes[row,1].imshow(np.flipud(np.where(valid, display_error, np.nan)), vmin=-.1, vmax=.1, cmap='RdBu_r')
    axes[row,1].set_title('Low-frequency RGB mean error (display codes)')
    for axis in axes[row]: axis.axis('off')
fig.colorbar(shown, ax=axes[:,1], shrink=.75)
fig.savefig(OUT/'sky_output_replay.png', dpi=150)
plt.close(fig)
result['limitations'] = 'Frozen same-HDR replay, not current Scene screenshot or monitor. Low-pass errors include finite-sample dither noise. Sky mask excludes disk/clipping. GI/atmosphere/lighting are held in the source data.'
old = np.array(result['paths']['legacy']['lowpass_error_abs_p95_codes_RGB'])
new = np.array(result['paths']['quantized']['lowpass_error_abs_p95_codes_RGB'])
assert (new < old*.5).all(), 'Same-HDR replay did not reduce low-frequency error sufficiently.'
(OUT/'analysis.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result,indent=2))
