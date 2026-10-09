#!/usr/bin/env python3
"""bundle_client.py <version> - puts the store client package into packaging/client-bundle/ (S1.15.0).

Run it at every client release, after make_ppak.py and make_sources.py, and commit the folder with the
"C1.x.y:" commit. The server image carries the folder; a server that knows no client yet, or an older one,
releases the bundled one at start (Services/ClientBundleWorker.cs). Only the newest version stays in the folder.
"""
import hashlib, os, shutil, sys, zipfile, json

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ID = 'com.tungsten.pluginstore'
DIST = os.path.join(ROOT, 'dist')
OUT = os.path.join(ROOT, 'packaging', 'client-bundle')


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    ver = sys.argv[1]
    ppak = os.path.join(DIST, f'{ID}-{ver}.ppak')
    src = os.path.join(DIST, 'sources', f'{ID}-{ver}.source.zip')
    for f in (ppak, src):
        if not os.path.isfile(f):
            sys.exit(f'missing: {f}')
    with zipfile.ZipFile(ppak) as z:
        man = json.loads(z.read('manifest.json'))
    if man.get('id') != ID or man.get('version') != ver:
        sys.exit(f'{ppak} is {man.get("id")} {man.get("version")}, not {ID} {ver}')
    os.makedirs(OUT, exist_ok=True)
    for name in os.listdir(OUT):
        if name.endswith('.ppak') or name.endswith('.source.zip'):
            os.remove(os.path.join(OUT, name))
    shutil.copy2(ppak, OUT)
    shutil.copy2(src, OUT)
    with open(os.path.join(OUT, 'SHA256SUMS.txt'), 'w', encoding='ascii', newline='\n') as f:
        for p in (ppak, src):
            f.write(hashlib.sha256(open(p, 'rb').read()).hexdigest() + '  ' + os.path.basename(p) + '\n')
    print(f'bundled {ID} {ver} in {OUT}')


if __name__ == '__main__':
    main()
