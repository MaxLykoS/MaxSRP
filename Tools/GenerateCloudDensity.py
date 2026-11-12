"""Prepare the main repeating world-plane cloud density map.

Default: adapt Unity's authored Cloud Layer cumulus channel (see source license).
--procedural: experiment with offline 3D density projection; additionally needs SciPy.
No light direction or color is baked.
"""
from pathlib import Path
import argparse
import sys
import time
import numpy as np
from PIL import Image
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "OfflineBaseline/CloudLayer/PythonDeps"))


def smoothstep(a, b, x):
    t = np.clip((x-a)/(b-a), 0, 1)
    return t*t*(3-2*t)


def build_volume(seed):
    from scipy.ndimage import zoom
    rng = np.random.default_rng(seed)
    shape = (96, 384, 384)

    def noise(grid_shape, output_shape):
        grid = rng.random(grid_shape, dtype=np.float32)
        return zoom(grid, np.array(output_shape)/grid_shape, order=3,
                    mode="grid-wrap", grid_mode=True, prefilter=True).astype(np.float32)

    weather = .6*noise((8, 8), shape[1:])+.28*noise((16, 16), shape[1:])
    weather += .12*noise((32, 32), shape[1:])
    weather = np.clip((weather-.3)*1.9, 0, 1)[None, :, :]
    base = .4*noise((8, 24, 24), shape)
    base += .3*noise((16, 48, 48), shape)
    base += .2*noise((24, 96, 96), shape)
    base += .1*noise((48, 192, 192), shape)
    height = np.linspace(0, 1, shape[0], dtype=np.float32)[:, None, None]
    profile = smoothstep(0, .12, height)*(1-smoothstep(.32+.25*weather, .65+.35*weather, height))
    density = np.clip((base-.68+.3*weather)*3.5, 0, 1)
    density *= profile*smoothstep(.08, .52, weather)
    del base, profile
    detail = noise((24, 96, 96), shape)
    return np.maximum(0, density-.15*detail*(1-density)).astype(np.float32)


def bake(volume, width, height, steps):
    from scipy.ndimage import map_coordinates
    # Equal angular spacing: PNG top=zenith, texture v=0=horizon.
    longitude = ((np.arange(width, dtype=np.float32)+.5)/width-.5)*2*np.pi
    result = np.zeros((height, width), np.float32)
    samples = (np.arange(steps, dtype=np.float32)+.5)/steps
    domain = 32000.0
    bottom, top = 1600.0, 4600.0
    ny, nz, nx = volume.shape
    for row in range(0, height, 8):
        end = min(row+8, height)
        latitude = (1-(np.arange(row, end, dtype=np.float32)+.5)/height)*np.pi*.5
        dy = np.sin(latitude)[:, None]
        dx = np.cos(latitude)[:, None]*np.cos(longitude)[None, :]
        dz = np.cos(latitude)[:, None]*np.sin(longitude)[None, :]
        near = bottom/np.maximum(dy, .01)
        far = np.minimum(top/np.maximum(dy, .01), 100000)
        length = np.maximum(far-near, 0)
        distance = near[:, :, None]+length[:, :, None]*samples[None, None, :]
        x = dx[:, :, None]*distance
        z = dz[:, :, None]*distance
        y = np.broadcast_to(dy[:, :, None]*distance, x.shape)
        coords = np.array([(y-bottom)/(top-bottom)*(ny-1),
                           (z/domain % 1)*nz, (x/domain % 1)*nx], dtype=np.float32)
        values = map_coordinates(volume, coords, order=1, mode="grid-wrap", prefilter=False)
        mass = values.mean(axis=-1)*length
        result[row:end] = np.clip(mass/1100, 0, 1)
        if row % 128 == 0:
            print(f"Projected {row}/{height} rows", flush=True)
    pole_weight = smoothstep(.985, 1, 1-(np.arange(height)+.5)/height)[:, None]
    result = result*(1-pole_weight)+result.mean(axis=1)[:, None]*pole_weight
    # Square-root data encoding dedicates R8 precision to faint, transparent
    # cloud edges. This is data encoding, not an sRGB texture.
    return np.uint8(np.sqrt(np.clip(result, 0, 1))*255+.5)


def project_to_plane(density, size, tile_size, altitude):
    """Unproject the authored angular silhouette onto a repeating world plane.

    The fixed reference view retains the authored outline, while runtime wind
    can advect actual world XZ coordinates without rotating the panorama.
    """
    coordinates = ((np.arange(size, dtype=np.float32)+.5)/size-.5)*tile_size
    x, z = np.meshgrid(coordinates, -coordinates)
    u = (np.arctan2(z, x)/(2*np.pi)+.5) % 1
    v = np.arctan2(altitude, np.sqrt(x*x+z*z))/(np.pi*.5)
    h, w = density.shape
    px, py = u*w-.5, (1-v)*(h-1)
    ix, iy = np.floor(px).astype(np.int32), np.floor(py).astype(np.int32)
    fx, fy = px-ix, py-iy
    iy = np.clip(iy, 0, h-1)
    a, b = density[iy, ix % w], density[iy, (ix+1) % w]
    c, d = density[np.minimum(iy+1, h-1), ix % w], density[np.minimum(iy+1, h-1), (ix+1) % w]
    plane = (a+(b-a)*fx)*(1-fy)+(c+(d-c)*fx)*fy
    # Join both pairs of tile edges offline. No runtime crossfading or cycle reset.
    border = max(8, size//32)
    weight = 1-smoothstep(0, 1, np.arange(border, dtype=np.float32)/border)
    seam = (plane[:, 0]+plane[:, -1])*.5
    for i in range(border):
        plane[:, i] = plane[:, i]*(1-weight[i])+seam*weight[i]
        plane[:, -1-i] = plane[:, -1-i]*(1-weight[i])+seam*weight[i]
    seam = (plane[0]+plane[-1])*.5
    for i in range(border):
        plane[i] = plane[i]*(1-weight[i])+seam*weight[i]
        plane[-1-i] = plane[-1-i]*(1-weight[i])+seam*weight[i]
    return np.uint8(np.sqrt(np.clip(plane, 0, 1))*255+.5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--width", type=int, default=4096)
    parser.add_argument("--height", type=int, default=1024)
    parser.add_argument("--steps", type=int, default=128)
    parser.add_argument("--seed", type=int, default=917)
    parser.add_argument("--procedural", action="store_true")
    parser.add_argument("--plane-size", type=int, default=2048)
    parser.add_argument("--tile-size", type=float, default=16000)
    parser.add_argument("--altitude", type=float, default=2000)
    args = parser.parse_args()
    started = time.monotonic()
    output = ROOT/"Assets/MaxRendererPipeline/Res/MainCloudDensity.png"
    if not args.procedural:
        import hashlib
        source = ROOT/"Tools/CloudAssets/Unity/DefaultCloudMap.png"
        expected = "31adcdba4682a53dc03daea286b8c3d200b5013e06e5b641bbb07a3d6481404c"
        if hashlib.sha256(source.read_bytes()).hexdigest() != expected:
            raise ValueError("Unity cloud source does not match its pinned provenance")
        density = np.asarray(Image.open(source), dtype=np.float32)[:, :, 0]/255
        pixels = project_to_plane(density, args.plane_size, args.tile_size, args.altitude)
        Image.fromarray(pixels).save(output)
        print(f"Prepared planar cumulus density {args.plane_size}x{args.plane_size}, {args.tile_size:g}m tile")
        return
    cache = ROOT/"OfflineBaseline/CloudLayer"
    cache.mkdir(parents=True, exist_ok=True)
    volume_path = cache/f"density-volume-{args.seed}-v3.npy"
    if volume_path.exists():
        volume = np.load(volume_path)
    else:
        print("Building multiscale 3D density field", flush=True)
        volume = build_volume(args.seed)
        np.save(volume_path, volume)
    pixels = bake(volume, args.width, args.height, args.steps)
    density = (pixels.astype(np.float32)/255)**2
    Image.fromarray(project_to_plane(density, args.plane_size, args.tile_size, args.altitude)).save(output)
    print(f"Wrote {args.plane_size}x{args.plane_size} plane density ({time.monotonic()-started:.1f}s)")


if __name__ == "__main__":
    main()
