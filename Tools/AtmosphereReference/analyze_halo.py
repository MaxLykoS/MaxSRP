"""Compare the four actual GPU sky-halo sampling ablations, without changing inputs."""
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
OUT = ROOT / 'OfflineBaseline/SkyHalo'
capture = json.loads((OUT / 'capture.json').read_text(encoding='utf-8-sig'))
for source in capture['sources']:
    assert hashlib.sha256((ROOT / source['path']).read_bytes()).hexdigest() == source['sha256'], source['path']
n = capture['size']
labels = ['256 x 128 LUT', 'Per-pixel: 32 uniform', 'Per-pixel: 128 uniform', 'Per-pixel: 128 quadratic']
raw, mapped, images = [], [], []
for name in capture['variants']:
    for suffix, target in [('_sky.f32', raw), ('_mapped.f32', mapped)]:
        a = np.fromfile(OUT / (name + suffix), np.float32).reshape(n, n, 4)[..., :3]
        assert np.isfinite(a).all() and a.min() >= 0, name + suffix
        target.append(a.astype(np.float64))
    image = Image.open(OUT / (name + '.png')).convert('RGB')
    assert image.size == (n, n)
    images.append(image)

y, x = np.mgrid[:n, :n]
t = np.tan(np.deg2rad(capture['verticalFov']) * .5)
xx = ((x + .5) / n * 2 - 1) * t
yy = ((y + .5) / n * 2 - 1) * t
angle = np.rad2deg(np.arctan(np.sqrt(xx * xx + yy * yy)))
forward = np.array([capture['forward'][c] for c in 'xyz'])
right = np.array([capture['right'][c] for c in 'xyz'])
up = np.array([capture['up'][c] for c in 'xyz'])
view = forward + xx[..., None] * right + yy[..., None] * up
view /= np.linalg.norm(view, axis=-1)[..., None]
settings = json.loads(capture['atmosphereJson'])
disk = settings['_SunDiskAngle']
mask = (angle > disk + .5) & (angle < 18) & (view[..., 1] > .02)
assert mask.sum() > 1000, 'Need enough above-horizon pixels outside the disk.'
Y = np.array([.2126, .7152, .0722])

def compare(a, b):
    ar, br = raw[a][mask], raw[b][mask]
    difference = abs((mapped[a] - mapped[b])[mask]) * 255
    return {
        'a': capture['variants'][a], 'b': capture['variants'][b],
        'sky_luminance_ratio': float((ar @ Y).mean() / (br @ Y).mean()),
        'sky_luminance_relative_L1': float(abs((ar - br) @ Y).sum() / abs(br @ Y).sum()),
        'encoded_RGB_absolute_error_codes_mean': float(difference.mean()),
        'encoded_RGB_absolute_error_codes_p95': float(np.quantile(difference, .95)),
        'encoded_RGB_absolute_error_codes_max': float(difference.max()),
    }

report = {
    'capture': capture['capturedUtc'], 'cameraY': capture['cameraY'],
    'effectiveAltitude': capture['effectiveAltitude'],
    'mask': 'above horizon by >~1.15 deg; sun radius + 0.5 < angular distance < 18 deg',
    'pixels_in_mask': int(mask.sum()),
    'comparisons': [compare(0, 1), compare(1, 2), compare(2, 3)],
    'limitations': 'Four ablations, not a converged reference or a proof that the screenshot bands have one cause. Display images synthesize the production FP16/exposure/ACES/dither path and manual sRGB UAV encoding, not a capture of the editor compositor or monitor. Single captured Sun/altitude. Mapped differences are measured before dither and 8-bit quantization; raw differences exclude the Sun disk.',
}
output_settings = json.loads(capture['colorOutputJson'])
exposure = 100000 / (1.2 * 2 ** output_settings['CameraEV100']) * 2 ** output_settings['ExposureEV']

def display_codes(value):
    c = np.maximum(value * exposure, 0)
    if output_settings['ToneMapping'] == 1:
        c = c * (2.51 * c + .03) / (c * (2.43 * c + .59) + .14)
    c = np.clip(c, 0, 1)
    return np.where(c <= .0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - .055) * 255

report['precision'] = []
for i, name in enumerate(capture['variants']):
    error = abs(display_codes(raw[i]) - display_codes(raw[i].astype(np.float16).astype(float)))
    # Check the saved 8-bit GPU result against its own pre-dither float output.
    quantization_error = np.flipud(np.array(images[i], dtype=float)) - mapped[i] * 255
    report['precision'].append({
        'variant': name, 'FP16_sky_error_codes_mean': float(error.mean()),
        'FP16_sky_error_codes_max': float(error.max()),
        'final_quantization_mean_error_codes_RGB': quantization_error[mask].mean(axis=0).tolist(),
        'final_quantization_error_std_codes_RGB': quantization_error[mask].std(axis=0).tolist(),
    })
(OUT / 'analysis.json').write_text(json.dumps(report, indent=2), encoding='utf-8')

board = Image.new('RGB', (2 * n, 2 * (n + 28)), '#151515')
draw = ImageDraw.Draw(board)
for i, (image, label) in enumerate(zip(images, labels)):
    left, top = (i % 2) * n, (i // 2) * (n + 28)
    draw.text((left + 8, top + 7), label, fill='white')
    board.paste(image, (left, top + 28))
board.save(OUT / 'halo_comparison.png')

fig, axes = plt.subplots(2, 2, figsize=(13, 8), layout='constrained')
row = n // 2
horizontal = np.rad2deg(np.arctan(xx[row]))
for i, label in enumerate(labels):
    # A common horizontal line displays interpolation slope changes and plateaus.
    color = mapped[i][row] * 255
    good = abs(horizontal) > disk + .5
    axes[0, 0].plot(horizontal, np.where(good, color @ Y, np.nan), label=label, lw=1)
axes[0, 0].set(title='Horizontal halo profile, before final dither', xlabel='Angle from Sun (deg)', ylabel='Encoded luma (8-bit code units)')
axes[0, 0].legend(fontsize=8)
for a, b, label in [(0, 1, 'LUT - direct32'), (1, 2, 'direct32 - direct128'), (2, 3, 'uniform128 - quadratic128')]:
    delta = (mapped[a] - mapped[b])[row] @ Y * 255
    axes[0, 1].plot(horizontal, np.where(abs(horizontal) > disk + .5, delta, np.nan), label=label, lw=1)
axes[0, 1].axhline(0, color='grey', lw=.5)
axes[0, 1].set(title='Differences before dither/8-bit quantization', xlabel='Angle from Sun (deg)', ylabel='Encoded luma difference (codes)')
axes[0, 1].legend(fontsize=8)
bound = max(.05, float(np.quantile(abs((mapped[0] - mapped[1])[mask] @ Y) * 255, .99)))
delta = (mapped[0] - mapped[1]) @ Y * 255
delta[~mask] = np.nan
im = axes[1, 0].imshow(np.flipud(delta), cmap='coolwarm', vmin=-bound, vmax=bound)
axes[1, 0].set(title='LUT - direct32: angular reconstruction error', xlabel='Pixel', ylabel='Pixel')
fig.colorbar(im, ax=axes[1, 0], label='Encoded luma difference (codes)')
lo = max(disk + .5, 1.5)
edges = np.arange(lo, 18.01, .15)
centres = (edges[1:] + edges[:-1]) * .5
for i, label in enumerate(labels):
    lum = mapped[i] @ Y * 255
    radial = [float(lum[mask & (angle >= a) & (angle < b)].mean()) for a, b in zip(edges[:-1], edges[1:])]
    axes[1, 1].plot(centres, radial, label=label, lw=1)
axes[1, 1].set(title='Azimuth-averaged halo profile (may hide grid artefacts)', xlabel='Angular distance from Sun (deg)', ylabel='Encoded luma (codes)')
axes[1, 1].legend(fontsize=8)
fig.savefig(OUT / 'halo_profiles.png', dpi=160)
plt.close(fig)
print(json.dumps(report, indent=2))
