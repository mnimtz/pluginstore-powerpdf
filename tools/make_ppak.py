#!/usr/bin/env python3
"""make_ppak.py - builds a .ppak store package from a spec JSON.

Usage:  python make_ppak.py <spec.json> <output-folder>

The spec is a JSON file:
{
  "id": "com.tungsten.example",
  "version": "1.0.0",                       // omit to read from "versionFrom" regex
  "versionFrom": { "file": "...", "regex": "L\\\"([0-9.]+)\\\"" },
  "name": { "en": "...", ... },
  "description": { "en": "...", ... },
  "changelog": { "en": "...", ... },
  "minPowerPdfVersion": "5.0",
  "ribbonAtomNamespace": "FeaturePack::Example",
  "zxt": { "x64": "relative/path/Example.zxt" },   // paths relative to the spec file
  "include": [ { "src": "relative/path", "dst": "zip/path" }, ... ],
  "uninstall": { "registryKeys": [ ... ], "extraPaths": [ ... ] }
}

sha256 values and the architectures list are computed automatically.
"""
import hashlib
import json
import os
import re
import sys
import zipfile


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def main():
    spec_path, out_dir = sys.argv[1], sys.argv[2]
    base = os.path.dirname(os.path.abspath(spec_path))
    spec = json.load(open(spec_path, encoding='utf-8'))

    version = spec.get('version')
    if not version and 'versionFrom' in spec:
        vf = spec['versionFrom']
        text = open(os.path.join(base, vf['file']), encoding='utf-8', errors='ignore').read()
        version = re.search(vf['regex'], text).group(1)
    assert version, 'no version'

    zxt_name = os.path.basename(spec['zxt']['x64'])
    manifest = {
        'id': spec['id'],
        'version': version,
        'name': spec['name'],
        'description': spec.get('description', {}),
        'changelog': spec.get('changelog', {}),
        'minPowerPdfVersion': spec.get('minPowerPdfVersion', '5.0'),
        'category': spec.get('category', 'other'),
        'architectures': sorted(spec['zxt'].keys()),
        'files': {arch: f'{arch}/{zxt_name}' for arch in spec['zxt']},
        'sha256': {arch: sha256(os.path.join(base, p)) for arch, p in spec['zxt'].items()},
        'ribbonAtomNamespace': spec.get('ribbonAtomNamespace', ''),
        'uninstall': spec.get('uninstall', {}),
    }

    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, f"{spec['id']}-{version}.ppak")
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('manifest.json', json.dumps(manifest, indent=2, ensure_ascii=False))
        for arch, p in spec['zxt'].items():
            z.write(os.path.join(base, p), f'{arch}/{zxt_name}')
        for item in spec.get('include', []):
            src = os.path.join(base, item['src'])
            if os.path.isdir(src):
                for root, _, files in os.walk(src):
                    for f in files:
                        full = os.path.join(root, f)
                        rel = os.path.relpath(full, src).replace('\\', '/')
                        z.write(full, f"{item['dst']}/{rel}")
            else:
                z.write(src, item['dst'])
    print(f'{out}  ({os.path.getsize(out)} bytes, version {version})')


if __name__ == '__main__':
    main()
