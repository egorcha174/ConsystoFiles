"""Read supported Parasolid profiles and return a bounded triangle-only preview."""

import argparse
import json
import math
from pathlib import Path
import struct
import sys

MAX_FILE_BYTES = 32 * 1024 * 1024
MAX_TRIANGLES = 100_000
MAX_OUTPUT_BYTES = 8 * 1024 * 1024


def mesh_bytes(glb):
    if len(glb) > MAX_OUTPUT_BYTES or len(glb) < 20:
        raise ValueError('Invalid mesh size')
    magic, version, length = struct.unpack_from('<4sII', glb)
    if magic != b'glTF' or version != 2 or length != len(glb):
        raise ValueError('Invalid mesh header')
    offset, document, binary = 12, None, None
    while offset < length:
        size, kind = struct.unpack_from('<I4s', glb, offset)
        offset += 8
        if offset + size > length:
            raise ValueError('Invalid mesh chunk')
        chunk = glb[offset:offset + size]
        if kind == b'JSON':
            document = json.loads(chunk)
        elif kind == b'BIN\0':
            binary = chunk
        offset += size
    if document is None or binary is None:
        raise ValueError('Missing mesh data')

    def accessor(index, component, shape, width, code):
        item = document['accessors'][index]
        view = document['bufferViews'][item['bufferView']]
        if item['componentType'] != component or item['type'] != shape or 'sparse' in item:
            raise ValueError('Unexpected mesh accessor')
        count = item['count']
        stride = view.get('byteStride', width)
        start = view.get('byteOffset', 0) + item.get('byteOffset', 0)
        end = start + (count - 1) * stride + width if count else start
        if count < 0 or count > MAX_TRIANGLES * 3 or stride < width or start < 0 or end > len(binary):
            raise ValueError('Mesh accessor exceeds limits')
        return [struct.unpack_from('<' + code, binary, start + i * stride) for i in range(count)]

    triangles = bytearray()
    count = 0
    for mesh in document['meshes']:
        for primitive in mesh['primitives']:
            if primitive.get('mode', 4) != 4:
                continue
            points = accessor(primitive['attributes']['POSITION'], 5126, 'VEC3', 12, '3f')
            indices = accessor(primitive['indices'], 5125, 'SCALAR', 4, 'I')
            if len(indices) % 3:
                raise ValueError('Incomplete triangle')
            count += len(indices) // 3
            if count > MAX_TRIANGLES:
                raise ValueError('Too many triangles')
            for (index,) in indices:
                if index >= len(points) or not all(math.isfinite(v) for v in points[index]):
                    raise ValueError('Invalid triangle coordinates')
                triangles.extend(struct.pack('<3f', *points[index]))
    if not count:
        raise ValueError('No supported faces')
    return struct.pack('<8sII', b'CSMESH1\0', count, 0) + triangles


def convert(source, target):
    from parasolid_kit import ParseLimits, read_brep
    from parasolid_kit.interop import InteropLimits
    from parasolid_kit.interop.occt import to_occt
    from parasolid_kit.interop.preview import PreviewOptions, tessellate_preview

    if source.stat().st_size > MAX_FILE_BYTES:
        raise ValueError('Input exceeds limit')
    encoding = 'x-b' if source.suffix.lower() in ('.x_b', '.xmt_bin') else 'x-t'
    parsed = read_brep(source, source_format=encoding, limits=ParseLimits(
        max_file_size=MAX_FILE_BYTES, max_nodes=200_000, max_schema_types=4096,
        max_fields_per_type=512, max_string_bytes=1024*1024,
        max_variable_elements=500_000, max_diagnostics=100))
    if not parsed.complete:
        raise ValueError('Incomplete source geometry')
    limits = InteropLimits(max_entities=200_000, max_occt_subshapes=400_000,
                          max_curve_samples=100_000, max_triangles=MAX_TRIANGLES,
                          max_vertices=MAX_TRIANGLES*3, max_output_bytes=MAX_OUTPUT_BYTES,
                          max_diagnostics=100)
    # Uniform scaling is irrelevant for an unmeasured preview; no units are inferred or displayed.
    converted = to_occt(parsed.brep, source_unit='m', limits=limits)
    mesh = tessellate_preview(converted, parsed.brep, limits=limits,
        options=PreviewOptions(linear_deflection=0.5, angular_deflection=0.8, include_edges=False))
    data = mesh_bytes(mesh.glb)
    with target.open('xb') as stream:
        stream.write(data)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('source', type=Path)
    parser.add_argument('target', type=Path)
    args = parser.parse_args()
    try:
        convert(args.source, args.target)
        return 0
    except Exception:
        # Source diagnostics and names are never forwarded to the UI or interpreted as commands.
        return 2


if __name__ == '__main__':
    sys.exit(main())
