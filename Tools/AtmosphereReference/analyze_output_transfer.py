"""Check coherent bias through the actual GPU dither/FP16/sRGB8 path."""
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

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'OfflineBaseline/OutputTransfer'
capture = json.loads((OUT / 'capture.json').read_text())
assert hashlib.sha256((ROOT / 'Assets/MaxRendererPipeline/Shaders/Realtime/ColorOutputPass.hlsl').read_bytes()).hexdigest() == capture['shaderSha256']
w, h = capture['width'], capture['height']
start = np.array([capture['startCodes'][c] for c in 'xyz'])
base = start + capture['rangeCodes'] * (np.arange(w)[:, None] + .5) / w

def encode(v):
    return np.where(v <= .0031308, 12.92 * v, 1.055 * np.maximum(v, 0)**(1/2.4) - .055)

linear = {}
encoded = {}
for name in ['fp32', 'fp16']:
    a = np.fromfile(OUT / (name + '.f32'), np.float32).reshape(h, w, 4)[..., :3]
    assert np.isfinite(a).all() and (a >= 0).all()
    linear[name] = encode(a.astype(float)) * 255
    image = np.array(Image.open(OUT / (name + '_srgb8.png')).convert('RGB'), dtype=float)
    assert image.shape == (h, w, 3)
    encoded[name] = np.flipud(image)
encoded['direct'] = np.flipud(np.array(Image.open(OUT / 'direct_srgb8.png').convert('RGB'), dtype=float))
encoded['quantized'] = np.flipud(np.array(Image.open(OUT / 'quantized_srgb8.png').convert('RGB'), dtype=float))

fig, axes = plt.subplots(3, 1, figsize=(12, 9), layout='constrained')
results = {'capture': capture['capturedUtc'], 'samples': h*w, 'paths': {}}
for name, samples in [('shader_fp32', linear['fp32']), ('shader_fp16', linear['fp16']),
                      ('hardware_srgb8_direct', encoded['direct']),
                      ('quantized_srgb8_fp16', encoded['quantized']),
                      ('hardware_srgb8_fp32', encoded['fp32']), ('hardware_srgb8_fp16', encoded['fp16'])]:
    error = samples - base
    # Each ramp period spans 64 columns; combine repeated phases across 16 codes.
    phase = error.reshape(h, 16, 64, 3).mean(axis=(0, 1))
    results['paths'][name] = {
        'mean_error_codes_RGB': error.mean(axis=(0, 1)).tolist(),
        'std_error_codes_RGB': error.std(axis=(0, 1)).tolist(),
        'phase_mean_error_min_RGB': phase.min(axis=0).tolist(),
        'phase_mean_error_max_RGB': phase.max(axis=0).tolist(),
        'phase_bias_peak_to_peak_codes_RGB': np.ptp(phase, axis=0).tolist(),
        'phase_mean_standard_error_RGB': (error.std(axis=(0, 1)) / np.sqrt(h*16)).tolist(),
        'integer_level_mean_error_codes_RGB': error.reshape(h, 16, 64, 3).mean(axis=(0, 2)).tolist(),
    }
    for c, axis in enumerate(axes):
        axis.plot(base[:, c], error.mean(axis=0)[:, c], label=name, alpha=.8)
        axis.set(title='RGB'[c] + ': conditional mean error vs display color', ylabel='Display codes', xlabel='Input display code')
        axis.grid(alpha=.2)
axes[0].legend(ncol=2)
fig.savefig(OUT / 'transfer_bias.png', dpi=150)
plt.close(fig)
for name in ['fp32', 'fp16']:
    expected = np.rint(linear[name])
    error = encoded[name] - expected
    results['paths']['hardware_srgb8_' + name]['different_from_exact_round_fraction'] = np.mean(error != 0, axis=(0, 1)).tolist()
    results['paths']['hardware_srgb8_' + name]['mean_hardware_minus_exact_round_codes_RGB'] = error.mean(axis=(0, 1)).tolist()
results['limitations'] = 'Controlled blue-sky-colored ramps; no tone curve, camera, atmosphere or monitor. Errors include finite-sample noise. Phase bin combines 16 integer display levels. Actual sRGB8 framebuffer conversion, not compute UAV encoding.'
results['quantized_hardware_vs_exact_round_fraction'] = np.mean(encoded['quantized'] != np.rint(linear['fp32']), axis=(0, 1)).tolist()
quantized_stats = results['paths']['quantized_srgb8_fp16']
assert max(abs(np.array(quantized_stats['mean_error_codes_RGB']))) < .002, 'Quantized output has unexpected mean bias.'
assert max(np.ptp(quantized_stats['integer_level_mean_error_codes_RGB'], axis=0)) < .02, 'Quantized output retains coherent color-level bias.'
(OUT / 'analysis.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
print(json.dumps(results, indent=2))
