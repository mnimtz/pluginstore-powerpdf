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
  "uninstall": { "registryKeys": [ ... ], "extraPaths": [ ... ] },
  "visibility": "private"                   // optional: customer add-on, never in the catalog
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


def msi_product_version(path):
    """ProductVersion from the MSI Property table (Windows Installer COM via PowerShell)."""
    import subprocess
    ps = (
        "$wi=New-Object -ComObject WindowsInstaller.Installer;"
        f"$db=$wi.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$wi,@('{os.path.abspath(path)}',0));"
        "$v=$db.GetType().InvokeMember('OpenView','InvokeMethod',$null,$db,@(\"SELECT Value FROM Property WHERE Property='ProductVersion'\"));"
        "$v.GetType().InvokeMember('Execute','InvokeMethod',$null,$v,$null);"
        "$r=$v.GetType().InvokeMember('Fetch','InvokeMethod',$null,$v,$null);"
        "$r.GetType().InvokeMember('StringData','GetProperty',$null,$r,1)")
    out = subprocess.run(['powershell', '-NoProfile', '-Command', ps], capture_output=True, text=True)
    return out.stdout.strip()


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
    # License declaration is the author's own statement; never invent defaults.
    for key in ('thirdParty', 'complianceAudit'):
        if key in spec:
            manifest[key] = spec[key]
    # "private": a customer add-on that never appears in the catalog (only
    # with a customer code). Takes effect on the FIRST upload of the id.
    if spec.get('visibility') in ('public', 'private'):
        manifest['visibility'] = spec['visibility']

    # Guard: an included MSI must carry exactly the package version, otherwise
    # users get "Repair/Remove" instead of an upgrade (happened with 0.3.1).
    for item in spec.get('include', []):
        src = os.path.join(base, item['src'])
        if src.lower().endswith('.msi'):
            msi_ver = msi_product_version(src)
            if msi_ver != version:
                raise SystemExit(f'ABORT: {src} has ProductVersion {msi_ver}, package version is {version}. '
                                 'Rebuild the MSI (client\\installer\\build_msi.cmd).')

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
