"""Convert current Unity GPU textures to UE source images, without Library access."""
import json
from pathlib import Path
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
source = ROOT / "OfflineBaseline/UnrealReference"
manifest = json.loads((source / "manifest.json").read_text(encoding="utf-8"))
prepared = source / "prepared"
prepared.mkdir(exist_ok=True)
roles = {"_AlbedoMap": "albedo", "_MetalnessMap": "metallic", "_RoughnessMap": "roughness", "_NormalMap": "normal"}
done = {}
for mat in manifest["materials"].values():
    mat["prepared_maps"] = {}
    for prop, role in roles.items():
        key = mat["maps"][prop]
        if key is None:
            mat["prepared_maps"][role] = None
            continue
        pair = (key, role)
        if pair not in done:
            info = manifest["textures"][key]
            # ReadPixels buffer starts at the bottom. Image files start at the top.
            a = np.fromfile(source / info["linear_gpu_rgba"], dtype="<f4").reshape(info["height"], info["width"], 4)[::-1]
            if not np.isfinite(a).all():
                raise ValueError("Nonfinite GPU texture " + key)
            path = prepared / f"{key}_{role}.png"
            if role == "albedo":
                rgb = np.clip(a[..., :3], 0, 1)
                rgb = np.where(rgb <= .0031308, 12.92 * rgb, 1.055 * rgb ** (1 / 2.4) - .055)
                Image.fromarray(np.rint(rgb * 255).astype(np.uint8)).save(path)
            elif role == "normal":
                # Production DXT5nm/BC5 path: X = R*A, Y = G, reconstruct Z.
                x, y = a[..., 0] * a[..., 3] * 2 - 1, a[..., 1] * 2 - 1
                z = np.sqrt(np.maximum(0, 1 - x*x - y*y))
                normal = np.stack([x, y, z], axis=-1) * .5 + .5
                Image.fromarray(np.rint(np.clip(normal, 0, 1) * 255).astype(np.uint8)).save(path)
            else:
                Image.fromarray(np.rint(np.clip(a[..., 0], 0, 1) * 65535).astype(np.uint16)).save(path)
            done[pair] = str(path)
        mat["prepared_maps"][role] = done[pair]
(source / "prepared_manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False), encoding="utf-8")
print(json.dumps({"objects": len(manifest["objects"]), "materials": len(manifest["materials"]), "images": len(done)}, indent=2))
