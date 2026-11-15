"""Bake the published Maladetas DEM into the independent 1 m mesh layout.

Requires numpy and Pillow. No Unity Terrain, colour conversion, AI upscaling,
or invented metre-scale noise. All coarse levels decimate one quantized base.
"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image

WORLD, PADDED, TILE, SIDE, SCALE = 10000, 10240, 512, 513, 2048.0
RECIPE = "maladetas-dem-1m-v1"


def digest(path):
    with open(path, "rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


def cubic(a, b, c, d, t):
    result = b + .5 * t * (c - a + t * (2*a - 5*b + 4*c - d + t*(3*(b-c) + d-a)))
    # Limit cubic ringing without blurring ridges into surrounding valleys.
    return np.clip(result, np.minimum(b, c), np.maximum(b, c))


def indices(coordinate, size):
    i = np.floor(coordinate).astype(np.int32)
    return [np.clip(i + d, 0, size-1) for d in (-1, 0, 1, 2)], (coordinate-i).astype(np.float32)


def verify_written_tiles(directory):
    """Read files independently and compare every shared tile edge."""
    edges = mismatches = 0
    for level in range(5):
        stride = 1 << level
        count = (WORLD+TILE*stride-1)//(TILE*stride)
        def read(x, z):
            return np.fromfile(directory/f"height-l{level}-x{x:02d}-z{z:02d}.r16", dtype="<u2").reshape(SIDE,SIDE)
        for z in range(count):
            for x in range(count):
                tile = read(x,z)
                if x+1 < count:
                    mismatches += int(np.count_nonzero(tile[:,-1] != read(x+1,z)[:,0]))
                    edges += 1
                if z+1 < count:
                    mismatches += int(np.count_nonzero(tile[-1,:] != read(x,z+1)[0,:]))
                    edges += 1
    assert mismatches == 0
    return dict(sharedTileEdgesChecked=edges, sharedEdgeSamplesChecked=edges*SIDE,
                sharedEdgeMismatches=mismatches)


def mirrored_z(world_z):
    # Original 7.6 km lies in [1200,8800]. Repeat by reflection, with a C1
    # transition in 64 m edge bands so the reflected slope has no sharp fold.
    z = world_z.astype(np.float64) - 1200
    z = 7600 - np.abs(np.mod(z, 15200)-7600)
    edge = np.minimum(z, 7600-z)
    t = np.clip(edge/64, 0, 1)
    rounded = 64*(2*t*t-t*t*t)
    z = np.where(edge < 64, np.where(z < 3800, rounded, 7600-rounded), z)
    return z


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    args.evidence.mkdir(parents=True, exist_ok=True)
    manifest = args.out / "metadata.json"
    if manifest.exists():
        raise RuntimeError("Choose a fresh output directory; a valid dataset already exists")
    image = Image.open(args.source)
    raw = np.asarray(image)
    assert image.size == (5000, 3800) and raw.max() > 255 and raw.ndim == 2
    # PNG top row is north. Unity +Z points toward the original top row.
    source = raw[::-1].astype(np.float32) * np.float32((3404-1452.8)/65535)
    temporary = args.out / "base-1m.building"
    base = np.memmap(temporary, dtype="<u2", mode="w+", shape=(WORLD+1, WORLD+1))
    xi, tx = indices(np.arange(WORLD+1) * (4999/WORLD), 5000)
    for first in range(0, WORLD+1, 128):
        last = min(first+128, WORLD+1)
        zi, tz = indices(mirrored_z(np.arange(first, last))*(3799/7600), 3800)
        vertical = cubic(*(source[i, :] for i in zi), tz[:, None])
        height = cubic(*(vertical[:, i] for i in xi), tx[None, :])
        base[first:last] = np.rint(height * (65535/SCALE)).astype("<u2")
    base.flush()
    metadata = dict(version=1, worldSize=WORLD, paddedSize=PADDED,
        tileIntervals=TILE, tileResolution=SIDE, clipLevels=5, clipSize=1295,
        leafIntervals=32, leafAxis=320, heightScale=SCALE, elevationOffset=1452.8,
        sourceSpacing=2.0, recipe=RECIPE,
        sourceDescription="Real Maladetas DEM; published nominal 2m, 5000x3800, 10km x 7.6km. Bounded separable cubic reconstruction onto a 1m lattice; original height range preserved. 64m C1 mirror transitions extend Z to 10km. No measured 1m detail is claimed.",
        encoding="unsigned-16-little-endian; original-grid decimation; shared endpoints",
        heightFileCount=0, heightFileBytes=0)
    tile_hashes = {}
    for level in range(5):
        stride = 1 << level
        count = (WORLD+TILE*stride-1)//(TILE*stride)
        for z in range(count):
            iz = np.minimum((z*TILE + np.arange(SIDE))*stride, WORLD)
            for x in range(count):
                ix = np.minimum((x*TILE + np.arange(SIDE))*stride, WORLD)
                tile = np.asarray(base[np.ix_(iz, ix)], dtype="<u2")
                name = f"height-l{level}-x{x:02d}-z{z:02d}.r16"
                path = args.out / name
                path.write_bytes(tile.tobytes())
                tile_hashes[name] = digest(path)
                metadata["heightFileCount"] += 1
                metadata["heightFileBytes"] += tile.nbytes
        print(f"Baked height level {level}, stride {stride}m", flush=True)
    # Include every vertex of every padded 32m leaf, including shared edges.
    bounds = np.empty((320, 320, 2), dtype="<u2")
    xs = np.minimum(np.arange(321)*32, WORLD)
    for z in range(320):
        rows = base[np.minimum(z*32+np.arange(33), WORLD)]
        for x in range(320):
            values = rows[:, xs[x]:min(xs[x]+33, WORLD+1)]
            bounds[z, x] = values.min(), values.max()
    (args.out/"leaf-bounds.bin").write_bytes(bounds.tobytes())
    report = dict(passed=True, sourceSha256=digest(args.source), sourceWidth=5000,
        sourceHeight=3800, sourceBitDepth=16, outputWidth=10001, outputHeight=10001,
        outputSpacingMetres=1, measuredSourceSpacingMetres=2,
        nativeExtentMetres=[10000,7600], heightScale=SCALE, elevationOffset=1452.8,
        minimumLocalHeight=float(base.min())*SCALE/65535,
        maximumLocalHeight=float(base.max())*SCALE/65535,
        quantizationHalfStepMetres=SCALE/65535/2,
        tileCount=metadata["heightFileCount"], tileBytes=metadata["heightFileBytes"],
        materialDataIncluded=False, addedProceduralDetail=False,
        reconstruction="bounded separable Catmull-Rom; source axes from published physical extent",
        extension="mirror Z outside [1200,8800]m; C1 transition in 64m edge bands",
        measuredOneMetreResolution=False)
    # Independent file-read checks across all levels and shared tile edges.
    mismatches = 0
    random = np.random.default_rng(20261104)
    for level in range(5):
        stride = 1 << level
        for _ in range(128):
            gx, gz = random.integers(0, WORLD//stride+1, size=2)
            txi, tzi = int(gx)//512, int(gz)//512
            path = args.out/f"height-l{level}-x{txi:02d}-z{tzi:02d}.r16"
            with path.open("rb") as f:
                f.seek(((int(gz)%512)*513+int(gx)%512)*2)
                word = int.from_bytes(f.read(2), "little")
            mismatches += word != base[int(gz)*stride, int(gx)*stride]
    report["independentSampleMismatches"] = int(mismatches)
    assert mismatches == 0
    report.update(verify_written_tiles(args.out))
    # A data-derived hillshade for reviewing the actual imported landform.
    preview = np.asarray(base[::8,::8], dtype=np.float32)*(SCALE/65535)
    dz, dx = np.gradient(preview, 8)
    light = np.clip((.8 - .45*dx + .3*dz)/np.sqrt(1+dx*dx+dz*dz),0,1)
    shade = np.uint8(np.clip(.18+.82*light, 0, 1)*255)
    Image.fromarray(shade[::-1]).save(args.evidence/"maladetas-height-hillshade.png")
    (args.evidence/"height-bake.json").write_text(json.dumps(report,indent=2),encoding="utf8")
    (args.evidence/"height-tile-sha256.json").write_text(json.dumps(tile_hashes,indent=2))
    manifest.write_text(json.dumps(metadata,indent=2),encoding="utf8")
    del base
    temporary.unlink()
    print(json.dumps(report), flush=True)


if __name__ == "__main__":
    main()
