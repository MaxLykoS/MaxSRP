"""Audit actual camera/Editor readbacks and the RGB dither correlation ablation."""
import hashlib
import json
import os
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

extra = Path(os.environ.get('TEMP', '.')) / 'maxsrp-atmosphere-python'
if extra.is_dir():
    sys.path.insert(0, str(extra))
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'OfflineBaseline/FinalOutput'

def encode(v):
    v = np.maximum(v, 0)
    return np.where(v <= .0031308, 12.92 * v, 1.055 * v ** (1 / 2.4) - .055)

def sun_center(image):
    """Largest compact white component, ignoring toolbars and white modal panels."""
    yy, xx = np.where(image.min(axis=-1) > 250)
    remaining = set(zip(yy.tolist(), xx.tolist()))
    candidates = []
    while remaining:
        start = remaining.pop()
        todo, component = [start], []
        while todo:
            y, x = todo.pop(); component.append((y, x))
            for neighbor in [(y-1, x), (y+1, x), (y, x-1), (y, x+1)]:
                if neighbor in remaining:
                    remaining.remove(neighbor); todo.append(neighbor)
        if 30 < len(component) < 5000:
            points = np.array(component)
            height, width = np.ptp(points, axis=0) + 1
            if max(width, height) < 100 and .8 < width / height < 1.25:
                candidates.append((len(component), points.mean(axis=0)))
    assert candidates, 'No unoccluded Sun disk to register the captured screen.'
    return max(candidates, key=lambda item: item[0])[1]

def audit(directory):
    r = json.loads((directory / 'capture.json').read_text())
    camera = next(c for c in r['cameras'] if c['type'] == 'SceneView')
    h, w = camera['source']['height'], camera['source']['width']
    def load(name):
        a = np.fromfile(directory / name, np.float32).reshape(h, w, 4)[..., :3].astype(float)
        assert np.isfinite(a).all()
        return a
    source, final = load('sceneview_hdr.f32'), load('sceneview_output.f32')
    color = np.maximum(source * camera['parameters']['x'], 0)
    if camera['parameters']['y'] > .5:
        color = np.clip(color * (2.51 * color + .03) / (color * (2.43 * color + .59) + .14), 0, 1)
    base, output = encode(color) * 255, encode(final) * 255
    mask = (base[..., 0] > 10) & (base[..., 2] > base[..., 0] + 10) & (base.max(axis=-1) < 200)
    error = (output - base)[mask]
    stats = {'capture': r['capturedUtc'], 'size': [w, h], 'parameters': camera['parameters'],
             'source_format': camera['source']['graphicsFormat'], 'target_format': camera['destination']['graphicsFormat'],
             'actual_noise_mean_codes_RGB': error.mean(axis=0).tolist(),
             'actual_noise_std_codes_RGB': error.std(axis=0).tolist(),
             'actual_noise_RGB_correlation': np.corrcoef(error.T).tolist()}
    view = np.flipud(np.rint(np.clip(output, 0, 255)).astype('uint8'))
    Image.fromarray(view).save(directory / 'scene_output.png')
    if (directory / 'scene_editor_ldr_native.f32').exists():
        ldr = np.rint(load('scene_editor_ldr_native.f32') * 255)
        assert abs(ldr - load('scene_editor_ldr_native.f32') * 255).max() < .001
        stats['editor_LDR_format'] = r['editorLdr']['graphicsFormat']
        stats['editor_LDR_quantization_MAE_codes'] = float(abs(ldr - output)[mask].mean())
        Image.fromarray(np.flipud(np.clip(ldr, 0, 255).astype('uint8'))).save(directory / 'scene_ldr.png')
    else:
        ldr = np.rint(output)
    screen = np.array(Image.open(directory / 'scene_screen.png').convert('RGB'))
    origin = sun_center(screen) - sun_center(view)
    expected = np.flipud(ldr)
    # Fit only a small translation around the detected disk. No resizing is permitted.
    best = None
    center = sun_center(view)
    for dy in range(round(origin[0]) - 3, round(origin[0]) + 4):
        for dx in range(round(origin[1]) - 3, round(origin[1]) + 4):
            top, left = max(0, -dy), max(0, -dx)
            bottom, right = min(h, screen.shape[0] - dy), min(w, screen.shape[1] - dx)
            a, b = expected[top:bottom, left:right], screen[top+dy:bottom+dy, left+dx:right+dx]
            yy2, xx2 = np.mgrid[top:bottom, left:right]
            # Ignore toolbars, gizmos, Navigation overlay, disk edges, and a possible
            # Unity busy panel on the right. Use the same sky ROI for every candidate.
            roi = (xx2 > 100) & (xx2 < min(1000, w-100)) & (yy2 > 180) & (yy2 < min(800, h-150))
            roi &= (xx2-center[1])**2 + (yy2-center[0])**2 > 40**2
            assert roi.sum() > 10000
            diff = b.astype(float) - a
            candidate = (float(abs(diff[roi]).mean()), dy, dx, diff, roi)
            if best is None or candidate[0] < best[0]: best = candidate
    mae, dy, dx, diff, roi = best
    stats['screen_registration'] = {'translation_yx': [dy, dx], 'scale': 1,
        'ROI_pixels': int(roi.sum()), 'RGB_MAE_codes': mae,
        'pixel_exact_fraction': float(np.all(diff[roi] == 0, axis=-1).mean()),
        'p99_absolute_error_codes': float(np.quantile(abs(diff[roi]), .99)),
        'mean_error_codes_RGB': diff[roi].mean(axis=0).tolist(),
        'note': 'Sun-aligned sky ROI, excludes overlays and busy panel. Small translation includes toolbar/crop origin and possible subpixel camera changes between the two frames.'}
    stats['screen_registration']['valid_for_stage_comparison'] = bool(mae < .05)
    return stats, base

before, _ = audit(OUT / 'Before')
after, base = audit(OUT)
assert after['screen_registration']['valid_for_stage_comparison'], 'Screen/RT captures do not match; capture after Present with a stable camera.'
capture = json.loads((OUT / 'capture.json').read_text())
shader = ROOT / 'Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl'
assert hashlib.sha256(shader.read_bytes()).hexdigest() == capture['colorOutputShaderSha256']

# Controlled identical-color tests isolate channel correlation from the spatial
# spectrum of IGN versus hashing. Both use the SAME triangular distribution.
rng = np.random.default_rng(18183)
u = rng.random((131072, 3)); s = u * 2 - 1
noise = np.sign(s) * (1 - np.sqrt(1 - abs(s)))
fractions = np.linspace(0, 1, 65)
rows = {}
for label, n in [('shared', noise[:, :1]), ('independent', noise)]:
    variances = []
    for f in fractions:
        color = np.array([80.2, 95.7, 160.2 + f])
        error = np.floor(color + n + .5) - color
        variances.append(float(np.var(error[:, 2] - error[:, 0])))
    rows[label] = variances

# Same ACTUAL HDR-derived display colors, same triangular amplitude, only change
# channel correlation. These are explicitly CPU ablations, not new screen captures.
u = rng.random((*base.shape[:2], 3)) * 2 - 1
n = np.sign(u) * (1 - np.sqrt(1 - abs(u)))
for label, offset in [('shared', n[..., :1]), ('independent', n)]:
    image = np.flipud(np.clip(np.floor(base + offset + .5), 0, 255).astype('uint8'))
    Image.fromarray(image).save(OUT / ('matched_' + label + '.png'))

# Bilinear half-scale at pixel centres averages 2x2 input pixels. This measures
# the existing noise field only; it is NOT a live GameView capture.
h, w = n.shape[:2]; n = n[:h//2*2, :w//2*2]
half = n.reshape(h//2, 2, w//2, 2, 3).mean(axis=(1, 3))
scale_test = {'method': 'controlled 2x2 averaging of independent triangular noise; not a live GameView capture',
              'native_std_codes_RGB': n.std(axis=(0, 1)).tolist(),
              'half_scale_std_codes_RGB': half.std(axis=(0, 1)).tolist()}

result = {'before_shared': before, 'after_independent': after,
          'chroma_variance_minmax': {k: [min(v), max(v)] for k, v in rows.items()},
          'scale_test': scale_test,
          'limitations': 'Before/after viewports differ; no pixelwise before-after image quality score is claimed. Each real output is compared against its own captured HDR. Shared/independent matched images use the same AFTER HDR on CPU. Scene at native 1:1 only; hidden Game tab was not captured. The numerical defect is proven, but subjective disappearance of every faint contour is not claimed.'}
(OUT / 'analysis.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
fig, axes = plt.subplots(1, 2, figsize=(11, 4), layout='constrained')
for label, variance in rows.items(): axes[0].plot(fractions, variance, label=label)
axes[0].set(xlabel='Fractional part of blue-minus-red display code', ylabel='Blue-red quantization error variance (codes squared)', title='Same triangular PDF, different RGB correlation')
axes[0].legend()
for label, stats in [('shared', before), ('independent', after)]:
    corr = np.array(stats['actual_noise_RGB_correlation'])
    axes[1].plot(['R-G', 'R-B', 'G-B'], corr[[0, 0, 1], [1, 2, 2]], marker='o', label=label)
axes[1].set(title='Actual GPU output noise correlation', ylabel='Correlation coefficient', ylim=(-.05, 1.05))
axes[1].legend(); fig.savefig(OUT / 'dither_correlation.png', dpi=160); plt.close(fig)
print(json.dumps(result, indent=2))
