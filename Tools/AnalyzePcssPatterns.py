"""Analyze same-frame visibility PNGs, not final color or a converged reference."""
from pathlib import Path
import json
import numpy as np
from PIL import Image, ImageFilter

root = Path(__file__).resolve().parent.parent / "OfflineBaseline/ShadowSamplingReview"
labels = ["legacy16", "legacy32", "sobol16raw", "sobol32raw", "sobol32quad",
          "sobol32fineRaw", "sobol32fineQuad", "sobol64quad"]
reference = np.asarray(Image.open(root / "sobol64quad.png"), dtype=np.float64)[..., 0] / 255
mask = (reference > .1) & (reference < .9)
if not np.any(mask):
    raise RuntimeError("No penumbra pixels; capture may have failed.")
report = {"scope": "8-bit linear visibility; reference is 64+64 quad, not ground truth. "
                   "High-pass metric uses a sigma=2px Gaussian on 8-bit images, not a denoising pass.",
          "penumbra_pixels": int(mask.sum()), "variants": {}}
for label in labels:
    image = Image.open(root / (label + ".png")).convert("L")
    values = np.asarray(image, dtype=np.float64) / 255
    blurred = np.asarray(image.filter(ImageFilter.GaussianBlur(2)), dtype=np.float64) / 255
    report["variants"][label] = {
        "rmse_against_64_quad": float(np.sqrt(np.mean((values[mask] - reference[mask]) ** 2))),
        "highpass_rms": float(np.sqrt(np.mean((values[mask] - blurred[mask]) ** 2))),
        "mean_penumbra_visibility": float(values[mask].mean()),
    }
baseline = report["variants"]["legacy16"]["highpass_rms"]
current = report["variants"]["sobol32fineQuad"]["highpass_rms"]
report["highpass_rms_reduction_from_legacy16"] = 1 - current / baseline
(root / "metrics.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report, indent=2))
