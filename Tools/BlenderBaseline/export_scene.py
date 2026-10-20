"""Export source-only Unity scene data; never load Unity's Library cache.

Requires numpy, Pillow and PyYAML. Run with --out to choose the artifact directory.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import sys

extra = Path(os.environ.get('TEMP', '.')) / 'maxsrp-baseline-python'
if extra.is_dir():
    sys.path.insert(0, str(extra))
import numpy as np
from PIL import Image
import yaml

ROOT = Path(__file__).resolve().parents[2]


def documents(path):
    text = path.read_text(encoding='utf-8-sig')
    result = {}
    for match in re.finditer(r'^--- !u!(\d+) &(-?\d+)(?: stripped)?\s*\n(.*?)(?=^--- !u!|\Z)', text, re.M | re.S):
        body = yaml.safe_load(match[3])
        result[int(match[2])] = {'class': int(match[1]), **body}
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--out', type=Path, default=ROOT / 'OfflineBaseline' / 'SkyboxScene')
    args = parser.parse_args()
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    (out / 'textures').mkdir(exist_ok=True)
    assets = {}
    # Only Assets is traversed. Library is outside the traversal entirely.
    for directory, folders, files in os.walk(ROOT / 'Assets', followlinks=False):
        folders[:] = [f for f in folders if not f.startswith('.') and f != 'Library']
        for filename in files:
            if filename.endswith('.meta'):
                path = Path(directory) / filename
                with path.open(encoding='utf-8-sig', errors='replace') as source:
                    head = source.read(1024)
                match = re.search(r'^guid: (\w+)', head, re.M)
                if match:
                    assets[match[1]] = Path(str(path)[:-5])
    sources = {}

    def track(path):
        path = Path(path)
        sources[str(path.relative_to(ROOT)).replace('\\', '/')] = hashlib.sha256(path.read_bytes()).hexdigest()
        return str(path)

    def resolve(ref):
        guid = ref.get('guid')
        if not guid:
            return None
        if guid not in assets:
            raise ValueError(f'Unresolved source asset {guid}')
        return assets[guid]

    texture_cache = {}

    def texture(ref):
        path = resolve(ref)
        if not path:
            return None
        if str(path) in texture_cache:
            return texture_cache[str(path)]
        track(path)
        meta_path = Path(str(path) + '.meta')
        track(meta_path)
        meta = meta_path.read_text(encoding='utf-8-sig')
        srgb = bool(int((re.search(r'^\s*sRGBTexture: (\d+)', meta, re.M) or [None, '0'])[1]))
        flip = bool(int((re.search(r'^\s*flipGreenChannel: (\d+)', meta, re.M) or [None, '0'])[1]))
        result_path = path
        if path.suffix == '.asset':
            text = path.read_text(encoding='utf-8-sig')
            width = int(re.search(r'm_Width: (\d+)', text)[1])
            height = int(re.search(r'm_Height: (\d+)', text)[1])
            fmt = int(re.search(r'm_TextureFormat: (\d+)', text)[1])
            data = bytes.fromhex(re.search(r'_typelessdata: ([0-9a-fA-F]+)', text)[1])
            if fmt == 5:  # Unity ARGB32 is BGRA bytes on this little-endian source.
                image = np.frombuffer(data, np.uint8, count=width*height*4).reshape(height, width, 4)
                image = image[::-1, :, [2, 1, 0, 3]]
            elif fmt == 4:
                image = np.frombuffer(data, np.uint8, count=width*height*4).reshape(height, width, 4)[::-1]
            elif fmt == 3:
                image = np.frombuffer(data, np.uint8, count=width*height*3).reshape(height, width, 3)[::-1]
            else:
                raise ValueError(f'Unsupported serialized TextureFormat {fmt}: {path}')
            result_path = out / 'textures' / (path.stem + '.png')
            Image.fromarray(np.ascontiguousarray(image)).save(result_path)
            srgb = int(re.search(r'm_ColorSpace: (\d+)', text)[1]) == 1
        result = {'source': str(path), 'path': str(result_path), 'srgb': srgb, 'flip_green': flip}
        texture_cache[str(path)] = result
        return result

    materials = {}

    def material(ref):
        path = resolve(ref)
        if not path:
            raise ValueError('Built-in material needs an explicit mapping')
        key = ref['guid']
        if key in materials:
            return key
        track(path)
        data = next(iter(documents(path).values()))['Material']
        saved = data['m_SavedProperties']
        envs = {k: v for item in saved['m_TexEnvs'] for k, v in item.items()}
        maps = {}
        for prop in ['_AlbedoMap', '_MetalnessMap', '_RoughnessMap', '_NormalMap']:
            env = envs.get(prop, {})
            maps[prop] = texture(env.get('m_Texture', {}))
        # All four maps use _AlbedoMap_ST in the actual GBuffer shader.
        uv = envs.get('_AlbedoMap', {})
        materials[key] = {'name': data['m_Name'], 'source': str(path), 'maps': maps,
                          'uv_scale': uv.get('m_Scale', {'x': 1, 'y': 1}),
                          'uv_offset': uv.get('m_Offset', {'x': 0, 'y': 0})}
        return key

    scene_path = ROOT / 'Assets/Scenes/SkyboxScene.unity'
    track(scene_path)
    docs = documents(scene_path)
    transforms = {d['Transform']['m_GameObject']['fileID']: d['Transform'] for d in docs.values()
                  if 'Transform' in d and 'm_GameObject' in d['Transform']}
    go = {key: d['GameObject'] for key, d in docs.items() if 'GameObject' in d}
    objects, lights, cameras = [], [], []

    def transform(t):
        return {k: t[k] for k in ['m_LocalPosition', 'm_LocalRotation', 'm_LocalScale']}

    for key, obj in go.items():
        if not obj['m_IsActive']:
            continue
        components = [docs[c['component']['fileID']] for c in obj['m_Component']]
        t = transforms[key]
        base = {'name': obj['m_Name'], 'transform': transform(t), 'unity_id': key}
        # Parents in this test scene are identity groups. Fail if that changes.
        parent = t['m_Father']['fileID']
        if parent:
            pt = docs[parent]['Transform']
            assert pt['m_LocalPosition'] == dict(x=0, y=0, z=0) and pt['m_LocalScale'] == dict(x=1, y=1, z=1)
            assert pt['m_LocalRotation'] == dict(x=0, y=0, z=0, w=1)
        mesh = next((c['MeshFilter'] for c in components if 'MeshFilter' in c), None)
        renderer = next((c['MeshRenderer'] for c in components if 'MeshRenderer' in c), None)
        if mesh and renderer and renderer['m_Enabled']:
            objects.append({**base, 'mesh': mesh['m_Mesh'], 'kind': 'primitive',
                            'materials': [material(m) for m in renderer['m_Materials']]})
        light = next((c['Light'] for c in components if 'Light' in c), None)
        if light and light['m_Enabled']:
            unit = next((c['MonoBehaviour'].get('PointUnit', 0) for c in components
                         if 'MonoBehaviour' in c and 'PointUnit' in c['MonoBehaviour']), 0)
            lights.append({**base, 'type': light['m_Type'], 'intensity': light['m_Intensity'],
                           'unit': unit, 'color': light['m_Color'], 'range': light['m_Range'],
                           'temperature': light.get('m_ColorTemperature', 6500),
                           'use_temperature': light.get('m_UseColorTemperature', 0)})
        camera = next((c['Camera'] for c in components if 'Camera' in c), None)
        if camera and camera['m_Enabled']:
            cameras.append({**base, 'vfov': camera['field of view'], 'near': camera['near clip plane'],
                            'far': camera['far clip plane']})

    for d in docs.values():
        if 'PrefabInstance' not in d:
            continue
        prefab = d['PrefabInstance']
        path = resolve(prefab['m_SourcePrefab'])
        track(path)
        mods = {v['propertyPath']: v for v in prefab['m_Modification']['m_Modifications']}
        if int(mods.get('m_IsActive', {'value': 1})['value']) == 0:
            continue
        t = {'m_LocalPosition': dict(x=0, y=0, z=0), 'm_LocalScale': dict(x=1, y=1, z=1),
             'm_LocalRotation': dict(x=0, y=0, z=0, w=1)}
        for k in t:
            for axis in t[k]:
                prop = k + '.' + axis
                if prop in mods:
                    t[k][axis] = float(mods[prop]['value'])
        name = mods.get('m_Name', {'value': path.stem})['value']
        if path.suffix.lower() == '.fbx':
            ref = mods['m_Materials.Array.data[0]']['objectReference']
            objects.append({'name': name, 'transform': t, 'kind': 'fbx', 'path': str(path), 'materials': [material(ref)]})
        else:
            children = documents(path)
            mf = next(v['MeshFilter'] for v in children.values() if 'MeshFilter' in v)
            mr = next(v['MeshRenderer'] for v in children.values() if 'MeshRenderer' in v)
            mesh_path = resolve(mf['m_Mesh'])
            track(mesh_path)
            mesh = next(iter(documents(mesh_path).values()))['Mesh']
            vd = mesh['m_VertexData']
            raw = bytes.fromhex(vd['_typelessdata'])
            count = vd['m_VertexCount']
            channels = vd['m_Channels']
            stride = max(c['offset'] + c['dimension']*4 for c in channels if c['stream'] == 0)
            assert all(c['format'] == 0 and c['stream'] == 0 for c in channels if c['dimension'])
            assert len(raw) == stride*count
            def channel(index):
                c = channels[index]
                return np.ndarray((count, c['dimension']), dtype='<f4', buffer=raw, offset=c['offset'], strides=(stride, 4)).copy()
            indices = np.frombuffer(bytes.fromhex(mesh['m_IndexBuffer']), '<u2' if mesh['m_IndexFormat'] == 0 else '<u4').reshape(-1, 3)
            mesh_out = out / (path.stem + '.npz')
            np.savez(mesh_out, vertices=channel(0), normals=channel(1), uv=channel(4), indices=indices)
            objects.append({'name': name, 'transform': t, 'kind': 'unity_mesh', 'path': str(mesh_out),
                            'materials': [material(m) for m in mr['m_Materials']]})

    setting_path = ROOT / 'Assets/MaxRendererPipeline/Max Renderer Pipeline Asset.asset'
    track(setting_path)
    settings = next(iter(documents(setting_path).values()))['MonoBehaviour']
    result = {'source_scene': str(scene_path), 'objects': objects, 'lights': lights, 'cameras': cameras,
              'materials': materials, 'atmosphere': settings['m_atmosphereScatteringSetting'],
              'exposure': settings['m_colorOutputSettings'], 'resolution': [1920, 1080], 'source_sha256': sources}
    (out / 'manifest.json').write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding='utf-8')
    print(json.dumps({'objects': [o['name'] for o in objects], 'lights': len(lights), 'materials': len(materials),
                      'textures': list(texture_cache), 'output': str(out)}, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
