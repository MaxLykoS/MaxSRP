#!/usr/bin/env python3
"""Generate verified UV2 caches from a pre-UV2 Unity import snapshot.

Dependencies can be installed into a local directory and supplied using
--dependency-dir. This tool never changes model geometry or Unity settings.
Publication is atomic per file; manifest.json is always published last.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import struct
import sys
import time
import uuid

np = None
xatlas = None
ALGORITHM = "xatlas-0.0.11-index-components-verified-v1"
PADDING = 2
DENSITY = 4.0
MAX_SIDE = 2044  # Four native renderer-padding texels must fit a 2048 atlas.
LOSS_RATIO_LIMIT = 1e-4
PHASES = ((0.0, 0.0), (0.5, 0.0), (0.0, 0.5), (0.5, 0.5))


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, allow_nan=False), encoding="utf-8")
    replace_file(temporary, path)


def replace_file(source, destination):
    # Windows readers (including Editor import and status inspection) may hold
    # a short-lived handle without FILE_SHARE_DELETE. Preserve atomicity and
    # retry that transient sharing violation rather than discarding the run.
    for attempt in range(9):
        try:
            os.replace(source, destination)
            return
        except PermissionError:
            if attempt == 8:
                raise
            time.sleep(0.01 * 2 ** attempt)


def allowed_path(value):
    path = Path(value).resolve()
    if any(part.lower() == "library" for part in path.parts):
        raise ValueError("Library is outside this tool's permitted inputs and outputs.")
    return path


def read_source(directory, entry):
    for key in ("file", "attributes"):
        if Path(entry[key]).name != entry[key] or not re.fullmatch(r"\d+\.(?:attributes\.)?bin", entry[key]):
            raise ValueError("Source filenames must be numbered files within the snapshot.")
    geometry = (directory / entry["file"]).read_bytes()
    attributes = (directory / entry["attributes"]).read_bytes()
    digest = hashlib.sha256(geometry + attributes).hexdigest()
    if digest != entry["sourceGeometryHash"]:
        raise ValueError("Source geometry/attribute hash mismatch: " + entry["meshName"])
    if len(geometry) < 24:
        raise ValueError("Truncated geometry header.")
    n, submeshes, sx, sy, ox, oy = struct.unpack_from("<ii4f", geometry)
    if n <= 0 or submeshes <= 0 or (sx, sy, ox, oy) != (1, 1, 0, 0):
        raise ValueError("Expected a pre-UV2 source snapshot, not an atlas-space export.")
    if len(geometry) < 24 + n * 20 + submeshes * 4 or len(attributes) != 4 + n * 20:
        raise ValueError("Truncated source vertex/attribute data.")
    if struct.unpack_from("<i", attributes)[0] != n:
        raise ValueError("Attribute vertex count mismatch.")
    positions = np.frombuffer(geometry, "<f4", n * 3, 24).reshape(n, 3).copy()
    uv0 = np.frombuffer(geometry, "<f4", n * 2, 24 + n * 12).reshape(n, 2)
    normals = np.frombuffer(attributes, "<f4", n * 3, 4).reshape(n, 3)
    attribute_uv0 = np.frombuffer(attributes, "<f4", n * 2, 4 + n * 12).reshape(n, 2)
    if not all(np.isfinite(a).all() for a in (positions, uv0, normals)) or not np.array_equal(uv0, attribute_uv0):
        raise ValueError("Invalid or inconsistent source vertex attributes.")
    offset, faces, counts = 24 + n * 20, [], []
    for _ in range(submeshes):
        count = struct.unpack_from("<i", geometry, offset)[0]
        offset += 4
        if count < 0 or count % 3 or offset + count * 4 > len(geometry):
            raise ValueError("Invalid triangle submesh.")
        indices = np.frombuffer(geometry, "<i4", count, offset).reshape(-1, 3)
        if count and (indices.min() < 0 or indices.max() >= n):
            raise ValueError("Out-of-range applied source index.")
        faces.append(indices)
        counts.append(count)
        offset += count * 4
    faces = np.concatenate(faces).astype(np.uint32)
    if offset != len(geometry) or n != entry["vertices"] or len(faces) != entry["triangles"]:
        raise ValueError("Source count/length mismatch.")
    return positions, faces, counts


def index_components(vertex_count, faces):
    parents = np.arange(vertex_count)

    def find(i):
        while parents[i] != i:
            parents[i] = parents[parents[i]]
            i = parents[i]
        return i

    for a, b, c in faces:
        ra, rb, rc = find(a), find(b), find(c)
        parents[rb] = parents[rc] = ra
    groups = {}
    for face, vertex in enumerate(faces[:, 0]):
        groups.setdefault(int(find(vertex)), []).append(face)
    return [(i, np.asarray(group, dtype=np.int64)) for i, group in enumerate(groups.values())]


def pack(positions, faces, groups, texels_per_unit):
    atlas = xatlas.Atlas()
    inputs = []
    for _, face_ids in groups:
        vertices, inverse = np.unique(faces[face_ids], return_inverse=True)
        atlas.add_mesh(positions[vertices], inverse.reshape(-1, 3).astype(np.uint32))
        inputs.append((face_ids, vertices))
    options = xatlas.PackOptions()
    options.padding, options.bilinear = PADDING, True
    options.resolution, options.texels_per_unit, options.max_chart_size = 0, texels_per_unit, 0
    atlas.generate(pack_options=options)
    if atlas.atlas_count != 1 or not atlas.width or not atlas.height:
        raise ValueError("One mesh cache must contain exactly one nonempty atlas.")
    side = max(atlas.width, atlas.height)
    vertices_out, uv_out, mappings, charts_out, assignments, face_inputs = [], [], [], [], [], np.empty(len(faces), dtype=np.int64)
    output_faces = np.empty_like(faces)
    vertex_offset, chart_offset, chart_inputs = 0, 0, []
    wide_inputs = set()
    for mesh, (face_ids, vertices) in enumerate(inputs):
        mapping, indices, uv = atlas[mesh]
        mapping = vertices[mapping]
        if not np.array_equal(mapping[indices], faces[face_ids]):
            raise ValueError("xatlas changed triangle order, winding, or corner identity.")
        assigned, chart_ids = atlas.get_mesh_vertex_assignment(mesh)
        chart_ids = chart_ids.astype(np.int64)
        chart_ids[chart_ids == 0xffffffff] = -1
        pixel_uv = uv.astype(np.float64) * [atlas.width, atlas.height]
        local_face_charts = chart_ids[indices[:, 0]]
        for chart in np.unique(local_face_charts):
            if chart < 0:
                continue
            chart_vertices = np.unique(indices[local_face_charts == chart])
            if np.ptp(pixel_uv[chart_vertices], axis=0).max() + 4 > MAX_SIDE:
                wide_inputs.add(mesh)
        vertices_out.append(positions[mapping])
        uv_out.append((pixel_uv / side).astype(np.float32))
        mappings.append(mapping)
        # xatlas vertex assignments use atlas-global chart IDs. Remap each
        # input mesh's IDs to our own dense range rather than adding twice.
        unique_charts = np.unique(chart_ids[chart_ids >= 0])
        charts_out.append(np.where(chart_ids >= 0, np.searchsorted(unique_charts, chart_ids) + chart_offset, -1))
        assignments.append(assigned)
        output_faces[face_ids] = indices + vertex_offset
        face_inputs[face_ids] = mesh
        count = len(unique_charts)
        if count != atlas.get_mesh_chart_count(mesh):
            raise ValueError("Chart assignment count does not cover the output mesh.")
        chart_inputs.extend([mesh] * count)
        chart_offset += count
        vertex_offset += len(mapping)
    data = dict(width=int(atlas.width), height=int(atlas.height), side=int(side),
                positions=np.concatenate(vertices_out), uv=np.concatenate(uv_out),
                mapping=np.concatenate(mappings), charts=np.concatenate(charts_out),
                assignment=np.concatenate(assignments), faces=output_faces,
                chart_inputs=np.asarray(chart_inputs, dtype=np.int64), face_inputs=face_inputs,
                wide_inputs=wide_inputs, chart_count=chart_offset)
    del atlas
    if not np.array_equal(data["mapping"][output_faces], faces) or not np.array_equal(data["positions"][output_faces], positions[faces]):
        raise ValueError("Final cache no longer preserves original geometry corners.")
    if not np.isfinite(data["uv"]).all() or data["uv"].min() < 0 or data["uv"].max() > 1:
        raise ValueError("Generated UV coordinates are not finite unit-square coordinates.")
    return data


def triangle_area_2d(triangles):
    ab, ac = triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0]
    return np.abs(ab[:, 0] * ac[:, 1] - ab[:, 1] * ac[:, 0]) * 0.5


def cross2(a, b):
    return a[0] * b[1] - a[1] * b[0]


def triangle_intersection_area(a, b):
    polygon = list(a)
    if cross2(b[1] - b[0], b[2] - b[0]) < 0:
        b = b[::-1]
    for c, d in zip(b, np.roll(b, -1, axis=0)):
        if not polygon:
            return 0.0
        result, previous = [], polygon[-1]
        previous_side = cross2(d - c, previous - c)
        for current in polygon:
            current_side = cross2(d - c, current - c)
            if (current_side >= 0) != (previous_side >= 0):
                result.append(previous + (current - previous) * (-previous_side) / (current_side - previous_side))
            if current_side >= 0:
                result.append(current)
            previous, previous_side = current, current_side
        polygon = result
    if len(polygon) < 3:
        return 0.0
    polygon = np.asarray(polygon)
    return abs(np.sum(polygon[:, 0] * np.roll(polygon[:, 1], -1) - polygon[:, 1] * np.roll(polygon[:, 0], -1))) * 0.5


def self_overlaps(data):
    triangles = data["uv"][data["faces"]].astype(np.float64)
    lower, upper = triangles.min(1), triangles.max(1)
    chart_ids = data["charts"][data["faces"][:, 0]]
    groups = {}
    for face, chart in enumerate(chart_ids):
        if chart >= 0:
            groups.setdefault(int(chart), []).append(face)
    inputs, pair_count = set(), 0
    for chart, group in groups.items():
        if len(group) < 2:
            continue
        group = np.asarray(group)
        for k, face in enumerate(group):
            candidates = group[k + 1:]
            candidates = candidates[np.all(lower[candidates] < upper[face] - 1e-12, axis=1) & np.all(upper[candidates] > lower[face] + 1e-12, axis=1)]
            for other in candidates:
                if triangle_intersection_area(triangles[face], triangles[other]) > 1e-14:
                    pair_count += 1
                    inputs.add(int(data["chart_inputs"][chart]))
    return pair_count, inputs


def filter_overlaps(data, chunk_size=200000):
    """Exact triangle vs +/-1 texel support SAT at four subpixel phases.

    Numpy batches bound candidate memory. A pixel may be owned repeatedly by
    one chart; different chart owners are a real bilinear-footprint collision.
    """
    side, origin = data["side"], 3
    if side > MAX_SIDE:
        return 0, set(), []  # Oversized charts must be split before raster work.
    width = side + 8
    face_charts = data["charts"][data["faces"][:, 0]]
    chart_base = max(1, data["chart_count"])
    maximum, offending, phases = 0, set(), []
    for phase_x, phase_y in PHASES:
        triangles = data["uv"][data["faces"]].astype(np.float64) * side + [origin + phase_x, origin + phase_y]
        lower_xy, upper_xy = triangles.min(1), triangles.max(1)
        x0 = np.maximum(0, np.floor(lower_xy[:, 0] - 1.5).astype(np.int64))
        y0 = np.maximum(0, np.floor(lower_xy[:, 1] - 1.5).astype(np.int64))
        x1 = np.minimum(width - 1, np.ceil(upper_xy[:, 0] + 0.5).astype(np.int64))
        y1 = np.minimum(width - 1, np.ceil(upper_xy[:, 1] + 0.5).astype(np.int64))
        span_x, span_y = x1 - x0 + 1, y1 - y0 + 1
        determinant = triangle_area_2d(triangles) * 2
        counts = span_x * span_y
        counts[(determinant < 1e-16) | (face_charts < 0)] = 0
        offsets = np.concatenate(([0], np.cumsum(counts)))
        nx = np.stack((triangles[:, 1, 1] - triangles[:, 0, 1], triangles[:, 2, 1] - triangles[:, 1, 1], triangles[:, 0, 1] - triangles[:, 2, 1]), axis=1)
        ny = np.stack((triangles[:, 0, 0] - triangles[:, 1, 0], triangles[:, 1, 0] - triangles[:, 2, 0], triangles[:, 2, 0] - triangles[:, 0, 0]), axis=1)
        projections = [triangles[:, i, 0, None] * nx + triangles[:, i, 1, None] * ny for i in range(3)]
        lower, upper, radius = np.minimum.reduce(projections), np.maximum.reduce(projections), np.abs(nx) + np.abs(ny)
        owners = np.full(width * width, -1, dtype=np.int64)
        collisions = np.zeros(width * width, dtype=np.bool_)
        for start in range(0, int(offsets[-1]), chunk_size):
            flat = np.arange(start, min(start + chunk_size, int(offsets[-1])), dtype=np.int64)
            face = np.searchsorted(offsets[1:], flat, side="right")
            local = flat - offsets[face]
            px, py = x0[face] + local % span_x[face], y0[face] + local // span_x[face]
            qx, qy = px + 0.5, py + 0.5
            hit = (qx + 1 > lower_xy[face, 0]) & (qx - 1 < upper_xy[face, 0]) & (qy + 1 > lower_xy[face, 1]) & (qy - 1 < upper_xy[face, 1])
            for edge in range(3):
                value = qx * nx[face, edge] + qy * ny[face, edge]
                hit &= (value + radius[face, edge] > lower[face, edge] + 1e-10) & (value - radius[face, edge] < upper[face, edge] - 1e-10)
            if not np.any(hit):
                continue
            packed = np.unique((py[hit] * width + px[hit]) * chart_base + face_charts[face[hit]])
            pixels, charts = packed // chart_base, packed % chart_base
            repeated = pixels[1:] == pixels[:-1]
            if np.any(repeated):
                collisions[pixels[1:][repeated]] = True
                offending.update(int(x) for x in data["chart_inputs"][charts[1:][repeated]])
                offending.update(int(x) for x in data["chart_inputs"][charts[:-1][repeated]])
            previous = owners[pixels]
            different = (previous >= 0) & (previous != charts)
            if np.any(different):
                collisions[pixels[different]] = True
                offending.update(int(x) for x in data["chart_inputs"][charts[different]])
                offending.update(int(x) for x in data["chart_inputs"][previous[different]])
            owners[pixels] = charts
        count = int(collisions.sum())
        maximum = max(maximum, count)
        phases.append(dict(x=phase_x, y=phase_y, overlapPixels=count, footprintPixels=int((owners >= 0).sum())))
    return maximum, offending, phases


def generate_mesh(directory, entry, progress):
    positions, faces, submesh_counts = read_source(directory, entry)
    p64 = positions.astype(np.float64)
    areas = np.linalg.norm(np.cross(p64[faces[:, 1]] - p64[faces[:, 0]], p64[faces[:, 2]] - p64[faces[:, 0]]), axis=1) * 0.5
    world_area = float(entry["worldSurfaceArea"])
    if not math.isfinite(world_area) or world_area <= 0 or areas.sum() <= 0:
        raise ValueError("Mesh has no positive finite world-space area.")
    base_texels_per_unit = DENSITY * math.sqrt(world_area / float(areas.sum()))
    texels_per_unit = base_texels_per_unit
    groups = index_components(len(positions), faces)
    initial_components = len(groups)
    fallback_components = set()
    reasons = dict(selfOverlap=0, filterOverlap=0, loss=0, unassigned=0, oversize=0)
    for attempt in range(3):
        progress("packing", entry["meshName"], attempt)
        data = pack(positions, faces, groups, texels_per_unit)
        uv_areas = triangle_area_2d(data["uv"][data["faces"]].astype(np.float64))
        nondegenerate = areas > 1e-14
        lost = (uv_areas <= 1e-18) & nondegenerate
        unassigned = (data["assignment"][data["faces"]] != 0).any(1)
        positive_unassigned = unassigned & nondegenerate
        lost_area = float(areas[lost].sum() / areas.sum() * world_area)
        measured_density = math.sqrt(float(uv_areas.sum()) * data["side"] ** 2 / world_area)
        progress("validating", entry["meshName"], attempt)
        self_pairs, self_inputs = self_overlaps(data)
        filter_pixels, filter_inputs, phases = filter_overlaps(data)
        loss_inputs = set(int(x) for x in data["face_inputs"][lost])
        unassigned_inputs = set(int(x) for x in data["face_inputs"][positive_unassigned])
        split = data["wide_inputs"] | self_inputs | filter_inputs | loss_inputs | unassigned_inputs
        splittable = {i for i in split if len(groups[i][1]) > 1}
        if splittable:
            reasons["selfOverlap"] += len(self_inputs)
            reasons["filterOverlap"] += len(filter_inputs)
            reasons["loss"] += len(loss_inputs)
            reasons["unassigned"] += len(unassigned_inputs)
            reasons["oversize"] += len(data["wide_inputs"])
            fallback_components.update(groups[i][0] for i in splittable)
            groups = [sub for i, (base, group) in enumerate(groups) for sub in ([(base, np.asarray([face], dtype=np.int64)) for face in group] if i in splittable else [(base, group)])]
            continue
        if measured_density < DENSITY:
            # Correct only a measured shortage in UV texel budget. Padding is
            # held constant and no lighting/material value is changed.
            if measured_density <= 0:
                raise ValueError("Generated layout has no positive surface texel density.")
            texels_per_unit *= DENSITY / measured_density * 1.005
            continue
        if positive_unassigned.any() or self_pairs or filter_pixels or data["side"] > MAX_SIDE or lost_area / world_area > LOSS_RATIO_LIMIT:
            raise ValueError("UV validation failed for %s: unassigned=%d, selfPairs=%d, filterPixels=%d, side=%d, lostRatio=%.9g" % (entry["meshName"], positive_unassigned.sum(), self_pairs, filter_pixels, data["side"], lost_area / world_area))
        corners = data["uv"][data["faces"]].astype("<f4")
        payload = struct.pack("<4sii", b"MXUV", 1, len(faces)) + bytes.fromhex(entry["sourceGeometryHash"]) + corners.tobytes()
        if len(payload) != 44 + len(faces) * 24:
            raise ValueError("Internal payload size mismatch.")
        result = dict(meshName=entry["meshName"], file=Path(entry["file"]).stem + ".bytes",
                      sourceGeometryHash=entry["sourceGeometryHash"], payloadHash=hashlib.sha256(payload).hexdigest(),
                      squareSide=data["side"], vertices=len(positions), generatedVertices=len(data["uv"]), triangles=len(faces),
                      texelsPerUnit=texels_per_unit, texelsPerUnitCorrection=texels_per_unit / base_texels_per_unit,
                      crop=[0, 0, data["width"], data["height"]], padding=PADDING, bilinear=True,
                      lostWorldArea=lost_area, lostWorldRatio=lost_area / world_area, lostUVTriangles=int(lost.sum()),
                      unassignedNonDegenerateFaces=int(positive_unassigned.sum()), unassignedDegenerateFaces=int(unassigned.sum()),
                      selfOverlapPairs=self_pairs, filterOverlapPixels=filter_pixels, filterPhases=phases,
                      initialComponents=initial_components, finalComponents=len(groups), fallbackComponents=len(fallback_components), fallbackReasons=reasons,
                      exactCornerMapping=True, sourceHashVerified=True, surfaceDensity=measured_density)
        return payload, result, data, submesh_counts
    raise ValueError("UV fallback did not converge within three attempts: " + entry["meshName"])


def write_diagnostic(path, data, submesh_counts):
    with path.open("wb") as output:
        output.write(struct.pack("<ii4f", len(data["uv"]), len(submesh_counts), data["side"] / 2048, data["side"] / 2048, 0, 0))
        output.write(data["positions"].astype("<f4").tobytes())
        output.write(data["uv"].astype("<f4").tobytes())
        cursor = 0
        for count in submesh_counts:
            output.write(struct.pack("<i", count))
            output.write(data["faces"].ravel()[cursor:cursor + count].astype("<i4").tobytes())
            cursor += count


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, help="Source snapshot directory containing manifest.json.")
    parser.add_argument("--output", required=True, help="Cache directory; an existing cache is preserved until validation succeeds.")
    parser.add_argument("--dependency-dir", action="append", default=[], help="Optional local Python dependency directory.")
    parser.add_argument("--staging", default="OfflineBaseline/BistroLightmap/UVStaging")
    parser.add_argument("--progress", default="OfflineBaseline/BistroLightmap/uv_generation.json")
    parser.add_argument("--only", help="Regex selection for offline validation. A subset never publishes a complete manifest.")
    parser.add_argument("--diagnostics", action="store_true", help="Keep compatible geometry/UV binaries within the ignored staging directory.")
    args = parser.parse_args()
    for directory in args.dependency_dir:
        sys.path.insert(0, str(allowed_path(directory)))
    global np, xatlas
    import numpy as np
    import xatlas
    if getattr(xatlas, "__version__", "") != "0.0.11":
        raise ValueError("This verified algorithm requires xatlas 0.0.11.")
    source, destination = allowed_path(args.input), allowed_path(args.output)
    progress_path = allowed_path(args.progress)
    stage = allowed_path(args.staging) / (time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:8])
    stage.mkdir(parents=True, exist_ok=False)
    manifest = json.loads((source / "manifest.json").read_text(encoding="utf-8-sig"))
    if not manifest.get("complete") or manifest.get("version") != 1 or manifest.get("density") != DENSITY or manifest.get("padding") != PADDING:
        raise ValueError("Source snapshot is incomplete or uses unsupported settings.")
    if not re.fullmatch(r"[0-9a-f]{64}", manifest.get("recipeHash", "")):
        raise ValueError("Source recipe hash is missing or malformed.")
    entries = [entry for entry in manifest["meshes"] if not args.only or re.search(args.only, entry["meshName"])]
    if not entries or len({entry["meshName"] for entry in entries}) != len(entries) or len({entry["file"] for entry in entries}) != len(entries):
        raise ValueError("Empty or duplicate source mesh selection.")
    results, start = [], time.monotonic()

    def progress(phase, mesh="", attempt=0, error=""):
        atomic_json(progress_path, dict(utc=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), phase=phase,
                                       processed=len(results), total=len(entries), meshName=mesh, attempt=attempt,
                                       elapsedSeconds=time.monotonic() - start, recipeHash=manifest["recipeHash"],
                                       staging=str(stage), output=str(destination), error=error))
    try:
        for entry in entries:
            payload, result, data, submeshes = generate_mesh(source, entry, progress)
            (stage / result["file"]).write_bytes(payload)
            results.append(result)
            if args.diagnostics:
                diagnostic = stage / "diagnostics"
                diagnostic.mkdir(exist_ok=True)
                write_diagnostic(diagnostic / entry["file"], data, submeshes)
            atomic_json(stage / "partial_results.json", results)
            progress("mesh-complete", entry["meshName"])
            print("%d/%d %s: side=%d, fallback=%d, loss=%.8g m2" % (len(results), len(entries), entry["meshName"], result["squareSide"], result["fallbackComponents"], result["lostWorldArea"]), flush=True)
        complete = len(entries) == len(manifest["meshes"])
        result_manifest = dict(version=1, complete=complete, recipeHash=manifest["recipeHash"], algorithm=ALGORITHM,
                               density=DENSITY, padding=PADDING, rendererPadding=4, maximumSquareSide=MAX_SIDE,
                               sourceMeshCount=len(manifest["meshes"]), generatedMeshCount=len(results), meshes=results)
        atomic_json(stage / "manifest.json", result_manifest)
        progress("publishing")
        destination.mkdir(parents=True, exist_ok=True)
        for result in results:
            temporary = destination / (result["file"] + ".tmp")
            shutil.copyfile(stage / result["file"], temporary)
            if hashlib.sha256(temporary.read_bytes()).hexdigest() != result["payloadHash"]:
                raise ValueError("Payload copy verification failed.")
            replace_file(temporary, destination / result["file"])
        atomic_json(destination / "manifest.json", result_manifest)
        progress("complete" if complete else "subset-complete")
        print("Verified cache published to " + str(destination), flush=True)
    except Exception as error:
        progress("failed", error=str(error))
        raise


if __name__ == "__main__":
    main()
