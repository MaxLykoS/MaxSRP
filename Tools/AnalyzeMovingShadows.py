"""Analyze CaptureMovingShadows output; no external scientific stack beyond NumPy/Pillow.

Usage: python Tools/AnalyzeMovingShadows.py OfflineBaseline/MovingShadows/CurrentSoft
Reference is phase-averaged PCSS from the SAME rasterized shadow maps, not a path-traced truth.
"""
import argparse
import csv
import json
from pathlib import Path

import numpy as np
from PIL import Image


def halton(index, base):
    value, fraction = 0.0, 1.0
    while index:
        fraction /= base
        value += (index % base) * fraction
        index //= base
    return value


def shift(image, dx, dy):
    height, width = image.shape
    y, x = np.mgrid[:height, :width]
    x, y = np.clip(x + dx, 0, width - 1.001), np.clip(y + dy, 0, height - 1.001)
    ix, iy = x.astype(int), y.astype(int)
    u, v = x - ix, y - iy
    return (image[iy, ix] * (1-u) * (1-v) + image[iy, ix+1] * u * (1-v)
            + image[iy+1, ix] * (1-u) * v + image[iy+1, ix+1] * u * v)


def analyze(folder):
    count = len(list(folder.glob('*_referenceVisibility.f32')))
    if count != 48:
        raise ValueError('Expected a complete 48-frame capture')
    n = 192

    def read(label):
        values = np.stack([np.fromfile(folder / f'{f:03}_{label}.f32', np.float32)
                           .reshape(n, n, 4)[..., 0] for f in range(count)])
        if not np.isfinite(values).all():
            raise ValueError(f'Nonfinite {label}')
        return values

    source, resolved = read('source'), read('resolved')
    raw, reference = read('rawVisibility'), read('referenceVisibility')
    meta = folder / 'frames.csv'
    if meta.exists():
        rows = list(csv.DictReader(meta.open(encoding='utf-8-sig')))
        jitters = [(float(r['jitterX']), float(r['jitterY'])) for r in rows]
    else:
        # Early A/B captures predate metadata. Their 32 warmup renders and
        # unchanged camera use this exact Halton phase; explicitly mark inference.
        jitters = [(halton((f & 7)+1, 2)-.5, halton((f & 7)+1, 3)-.5) for f in range(count)]

    # Uniform Lambert-like ground: recover its light/dark red-channel endpoints.
    # Restrict calibration to the shadow region, away from the visible caster.
    y, x = np.mgrid[:n, :n]
    calibration = (y > 85) & (y < 190) & (x > 5) & (x < 150)
    dark = np.median(source[:, calibration][raw[:, calibration] < .001])
    light = np.median(source[:, calibration][raw[:, calibration] > .999])
    if light - dark < .01:
        raise ValueError('Fixture lacks direct illumination or ground/shadow coverage')
    result_visibility = (resolved - dark) / (light - dark)
    # Raw reference is jittered raster data; resolved history is canonical.
    # ReadPixels and this D3D11 fixture use positive offsets in both readback axes.
    canonical_reference = np.stack([shift(reference[f], *jitters[f]) for f in range(count)])
    report = {'folder': str(folder), 'frames': count, 'jitter_metadata': meta.exists(),
              'ground_dark': float(dark), 'ground_lit': float(light), 'phases': {}}
    for name, lo, hi in [('static', 0, 4), ('moving', 4, 28), ('settled', 40, 48)]:
        mask = (canonical_reference[lo:hi] > .05) & (canonical_reference[lo:hi] < .95)
        error = (result_visibility[lo:hi] - canonical_reference[lo:hi])[mask]
        report['phases'][name] = {'mae': float(np.abs(error).mean()),
                                 'mse': float(np.square(error).mean()),
                                 'p95': float(np.quantile(np.abs(error), .95))}
    moving = (reference[4:28] > .05) & (reference[4:28] < .95)
    raw_error = np.abs(raw[4:28] - reference[4:28])[moving]
    report['moving_raw_outliers_over_0.2'] = int((raw_error > .2).sum())
    report['moving_raw_false_lit_below_0.9_reference'] = int(((raw[4:28] > .999) & (reference[4:28] < .9)).sum())
    # Four columns: raw PCSS, final TAA in visibility units, canonical reference,
    # absolute TAA error x3. Presentation only, never used as a rendering input.
    images = []
    for a in [raw[16], result_visibility[16], canonical_reference[16],
              np.abs(result_visibility[16] - canonical_reference[16]) * 3]:
        pixels = (np.clip(a, 0, 1) * 255).astype(np.uint8)[::-1]
        images.append(Image.fromarray(pixels).convert('RGB').resize((384, 384)))
    canvas = Image.new('RGB', (1536, 384))
    for i, im in enumerate(images):
        canvas.paste(im, (i*384, 0))
    canvas.save(folder / 'comparison.png')
    (folder / 'analysis.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('folders', nargs='+', type=Path)
    args = parser.parse_args()
    for folder in args.folders:
        print(json.dumps(analyze(folder), indent=2))
