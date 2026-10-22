"""Restore locally supplied ORCA Bistro exterior, preserving committed Unity metas.

Run with Python 3, Pillow and NumPy: prepare_bistro.py C:/path/to/Bistro_v5_2.zip
No network access, Unity Library access, or overwrite of .meta files.
"""
import argparse
import binascii
import hashlib
import io
import json
from pathlib import Path
import struct
import zipfile

import numpy as np
from PIL import Image


def read_fbx(data):
    stream = io.BytesIO(data)
    if stream.read(23) != b"Kaydara FBX Binary  \x00\x1a\x00":
        raise ValueError("Expected binary FBX")
    version = struct.unpack("<I", stream.read(4))[0]
    wide = version >= 7500
    fmt, header_size = ("<QQQB", 25) if wide else ("<IIIB", 13)

    def prop():
        kind = stream.read(1).decode()
        scalar = {"Y": "h", "C": "?", "I": "i", "F": "f", "D": "d", "L": "q"}
        if kind in scalar:
            code = "<" + scalar[kind]
            return struct.unpack(code, stream.read(struct.calcsize(code)))[0]
        if kind in "SR":
            n = struct.unpack("<I", stream.read(4))[0]
            value = stream.read(n)
            return value.decode("utf-8", "replace") if kind == "S" else None
        if kind in "fdlibc":
            length, encoding, n = struct.unpack("<III", stream.read(12))
            stream.seek(n, 1)
            return {"array": kind, "count": length}
        raise ValueError("Unknown FBX property " + kind)

    def node():
        end, count, size, name_len = struct.unpack(fmt, stream.read(header_size))
        if end == 0:
            return None
        name = stream.read(name_len).decode()
        values = [prop() for _ in range(count)]
        children = []
        while stream.tell() < end:
            child = node()
            if child is None:
                break
            children.append(child)
        stream.seek(end)
        return {"name": name, "values": values, "children": children}

    nodes = []
    while True:
        item = node()
        if item is None:
            break
        nodes.append(item)
    return nodes


def child(node, name):
    return next((c for c in node["children"] if c["name"] == name), None)


def properties(node):
    ps = child(node, "Properties70")
    return {p["values"][0]: p["values"][4:] for p in ps["children"]} if ps else {}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    target = root / "Assets/ThirdParty/Bistro"
    with zipfile.ZipFile(args.archive) as archive:
        prefix = "Bistro_v5_2/"
        data = archive.read(prefix + "BistroExterior.fbx")
        nodes = read_fbx(data)
        objects = next(n for n in nodes if n["name"] == "Objects")["children"]
        connections = next(n for n in nodes if n["name"] == "Connections")["children"]
        textures = {}
        material_data = []
        for obj in objects:
            if obj["name"] == "Texture":
                filename = child(obj, "RelativeFilename") or child(obj, "FileName")
                if filename:
                    textures[obj["values"][0]] = filename["values"][0].replace("\\", "/").split("/")[-1]
            if obj["name"] == "Material":
                mid, name, *_ = obj["values"]
                maps = {}
                for conn in connections:
                    vs = conn["values"]
                    if len(vs) >= 4 and vs[2] == mid and vs[1] in textures:
                        maps[vs[3]] = textures[vs[1]]
                material_data.append({"id": mid, "name": name.split("\x00")[0], "properties": properties(obj), "maps": maps})
        # Textures may occur after materials in Objects; resolve all links after collecting them.
        for material in material_data:
            material["maps"] = {c["values"][3]: textures[c["values"][1]] for c in connections
                                if len(c["values"]) >= 4 and c["values"][2] == material["id"] and c["values"][1] in textures}
        selected = {"BistroExterior.fbx", "README.txt", "LICENSE.txt", "CHANGELOG.txt", "BistroExterior.pyscene"}
        selected.update("Textures/" + name for name in textures.values())
        target.mkdir(parents=True, exist_ok=True)
        for relative in sorted(selected):
            path = target / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            entry = archive.getinfo(prefix + relative)
            if path.exists() and path.stat().st_size == entry.file_size:
                checksum = 0
                with path.open("rb") as existing:
                    while block := existing.read(1024 * 1024):
                        checksum = binascii.crc32(block, checksum)
                if checksum == entry.CRC:
                    continue
            with archive.open(prefix + relative) as source, path.open("wb") as dest:
                while block := source.read(1024 * 1024):
                    dest.write(block)
        # Unity imports DDS with IHVImageFormatImporter, which cannot perform the
        # NormalImporter/sRGB/platform settings needed by this material workflow.
        # Restore PNGs from the supplied compressed data; keep all derivatives local.
        converted = target / "ConvertedTextures"
        converted.mkdir(exist_ok=True)
        for name in sorted(set(textures.values())):
            source = target / "Textures" / name
            destination = converted / (Path(name).stem + ".png")
            if destination.exists() and destination.stat().st_mtime >= source.stat().st_mtime:
                continue
            image = Image.open(source)
            if name.endswith("_Normal.dds"):
                # ORCA ATI2/BC5 stores only XY. Reconstruct Z before Unity's normal
                # import; flipGreenChannel in the committed meta handles DirectX Y.
                values = np.asarray(image.convert("RGB")).astype(np.float32) / 255
                xy = values[:, :, :2] * 2 - 1
                values[:, :, 2] = np.sqrt(np.maximum(0, 1 - np.sum(xy * xy, axis=2))) * .5 + .5
                image = Image.fromarray(np.rint(values * 255).astype(np.uint8), "RGB")
            image.save(destination, compress_level=3)
        # This local derivative is referenced by the committed label material.
        # Restore it too: a fresh checkout must not require rebuilding the scene.
        generated = target / "Generated"
        generated.mkdir(exist_ok=True)
        fallback = generated / "Fallback_Specular.png"
        if not fallback.exists():
            Image.new("RGBA", (1, 1), (255, 153, 0, 255)).save(fallback)
        summary = {
            "archive": args.archive.name,
            "fbxSha256": hashlib.sha256(data).hexdigest(),
            "materials": material_data,
            "lights": [{"name": o["values"], "properties": properties(o)} for o in objects if o["name"] == "NodeAttribute" and "Light" in o["values"]],
            "settings": [n for n in nodes if n["name"] == "GlobalSettings"],
            "textureCount": len(set(textures.values())),
        }
        report = root / "OfflineBaseline/Bistro/import_manifest.json"
        report.parent.mkdir(parents=True, exist_ok=True)
        report.write_text(json.dumps(summary, indent=2), encoding="utf-8")
        print(json.dumps({"target": str(target), "files": len(selected), "textures": summary["textureCount"], "materials": len(material_data), "lights": len(summary["lights"]), "report": str(report)}))


if __name__ == "__main__":
    main()
