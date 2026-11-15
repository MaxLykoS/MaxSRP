"""Validate the embedded MaxSRP/MaxTerrain boundary without reading Unity's Library.

Run with a Python 3 interpreter from any directory. This checks source/package
structure and serialized references; it does not replace Unity compilation.
"""
import json
from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
SRP = "com.maxsrp.render-pipelines.maxsrp"
TERRAIN = "com.maxsrp.terrain"
PACKAGES = {name: ROOT / "Packages" / name for name in (SRP, TERRAIN)}


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def main():
    manifests = {
        name: json.loads((path / "package.json").read_text(encoding="utf-8-sig"))
        for name, path in PACKAGES.items()
    }
    for name, manifest in manifests.items():
        require(manifest["name"] == name, f"Package identity mismatch: {name}")
    require(
        manifests[TERRAIN]["dependencies"].get(SRP) == manifests[SRP]["version"],
        "MaxTerrain must depend on the current MaxSRP package version",
    )
    require(TERRAIN not in manifests[SRP]["dependencies"], "Reverse package dependency")

    assemblies = {}
    owners = {}
    for name, package in PACKAGES.items():
        for path in package.rglob("*.asmdef"):
            definition = json.loads(path.read_text(encoding="utf-8-sig"))
            assemblies[definition["name"]] = definition
            owners[definition["name"]] = name
    expected = {"MaxSRP.Runtime", "MaxSRP.Editor", "MaxTerrain", "MaxTerrain.Editor"}
    require(set(assemblies) == expected, f"Unexpected assemblies: {set(assemblies)}")
    for name in ("MaxTerrain", "MaxTerrain.Editor", "MaxSRP.Editor"):
        require("MaxSRP.Runtime" in assemblies[name]["references"], f"Missing SRP reference: {name}")
    for name in ("MaxSRP.Editor", "MaxTerrain.Editor"):
        require(assemblies[name].get("includePlatforms") == ["Editor"], f"Editor assembly exposed to Player: {name}")
    for name in ("MaxSRP.Runtime", "MaxSRP.Editor"):
        for reference in assemblies[name].get("references", []):
            require(owners.get(reference) != TERRAIN, f"Reverse assembly dependency: {name} -> {reference}")
    for package in PACKAGES.values():
        for path in package.rglob("*.cs"):
            require(any(path.parent == p or p in path.parents for p in
                        (package / "Runtime", package / "Editor")), f"Script outside package assemblies: {path}")
            if package == PACKAGES[SRP]:
                source = path.read_text(encoding="utf-8-sig")
                require(not re.search(r"\b(?:using\s+MaxTerrain\b|MaxTerrain\.)", source), f"SRP depends on terrain code: {path}")

    # Resolve GUID ownership only under Assets and embedded packages; never Library.
    guids = {}
    for base in (ROOT / "Assets", ROOT / "Packages"):
        for meta in base.rglob("*.meta"):
            match = re.search(r"(?m)^guid: ([a-f0-9]{32})\s*$", meta.read_text(encoding="utf-8-sig"))
            require(match is not None, f"Invalid metadata GUID: {meta}")
            if match:
                guid = match[1]
                require(guid not in guids, f"Duplicate GUID {guid}: {meta}, {guids.get(guid)}")
                guids[guid] = Path(str(meta)[:-5])
    serialized_count = 0
    for name, package in PACKAGES.items():
        for path in package.rglob("*"):
            if not path.is_file():
                continue
            require(Path(str(path) + ".meta").exists() or path.suffix == ".meta", f"Missing metadata: {path}")
            if path.suffix not in {".meta", ".asset", ".mat", ".prefab", ".unity"}:
                continue
            serialized_count += 1
            for guid in re.findall(r"guid: ([a-f0-9]{32})", path.read_text(encoding="utf-8-sig")):
                if guid.startswith("0000000000000000"):
                    continue  # Built-in Unity resources.
                require(guid in guids, f"Unresolved package asset GUID {guid}: {path}")
                target = guids[guid]
                require(target.is_relative_to(ROOT / "Packages"), f"Package asset depends on project data: {path} -> {target}")
                if name == SRP:
                    require(not target.is_relative_to(PACKAGES[TERRAIN]), f"Reverse serialized dependency: {path} -> {target}")

    require(not (ROOT / "Assets" / "MaxRendererPipeline").exists(), "Old pipeline source directory remains")
    for scene in ("DirectDeltaAuthoring10km", "DeltaPublished10km"):
        path = ROOT / "Assets" / "Samples" / "MaxTerrain" / "Scenes" / f"{scene}.unity"
        require(path.exists(), f"Missing writable terrain scene: {path}")
    print(f"PASS: 2 packages, 4 assemblies, one-way MaxTerrain -> MaxSRP dependency; {serialized_count} serialized package files resolve without project assets; GUIDs unique.")


if __name__ == "__main__":
    main()
