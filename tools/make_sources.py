#!/usr/bin/env python3
"""make_sources.py - builds the source-code ZIPs of our own store packages.

The store keeps every version's source next to the package (admins only).
This script collects, per package, exactly the folders its build uses and
leaves out build output. Output: dist/sources/<id>-<version>.source.zip

    python tools/make_sources.py
"""
import json
import os
import re
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
STORE = os.path.normpath(os.path.join(HERE, '..'))
FP1 = r'C:\Claude\EnhancedFeaturePack'
FP2 = r'C:\Claude\FeaturePack2'
OUT = os.path.join(STORE, 'dist', 'sources')

SKIP_DIRS = {'release', 'debug', 'x64', 'arm64', 'obj', '.vs', 'ipch', '__pycache__', 'zxing-build', 'delivery'}
SKIP_EXT = {'.pdb', '.obj', '.zxt', '.exe', '.dll', '.lib', '.exp', '.ilk', '.aps', '.user', '.res', '.tlog',
            '.msi', '.ppak', '.log', '.iobj', '.ipdb', '.pch', '.idb', '.wixobj', '.wixpdb', '.zip'}

FP1_COMMON = ['common', 'res', 'store/common', 'store/tools', 'store/build_store.cmd']
FP1_MODULES = {
    'OneClickSign': ['sign'],
    'QesSign': ['sign', 'qes', 'third_party/tpui', 'third_party/webview2', 'bookmarks/json.hpp'],   # 1.2.0: pick and verify windows on the Plug-in UI
    'SmartBookmarks': ['sign', 'bookmarks', 'third_party/tpui', 'third_party/webview2'],   # 1.5.0: panel page on the Tungsten Plug-in UI
    'BarcodeStamps': ['sign', 'stamps', 'third_party/zxing-cpp', 'third_party/tpui', 'third_party/webview2', 'bookmarks/json.hpp'],   # 1.2.0: assistant on the Plug-in UI
    'EInvoice': ['sign', 'invoice', 'third_party/tpui', 'third_party/webview2', 'bookmarks/json.hpp'],   # 1.2.0: invoice window on the Plug-in UI
    'ComplianceCheck': ['sign', 'invoice', 'compliance', 'third_party/tpui', 'third_party/webview2', 'bookmarks/json.hpp'],   # 1.2.0: report window on the Plug-in UI
    'MailMerge': ['sign', 'mailmerge', 'third_party/tpui', 'third_party/webview2', 'bookmarks/json.hpp'],   # 1.2.0: run window on the Plug-in UI
    'CommerzbankSign': ['sign'],   # private customer edition of OneClickSign
}
# FP2's common folder also holds code other modules use (OCR, conversion); each
# package gets only the common files its project compiles plus their headers.
FP2_COMMON = ['res', 'store/make_store_icons.py', 'store/make_store_layout.py', 'store/make_store_rc.py']
FP2_MODULES = {'StampAssistant': ['stamp'], 'PrintixSecurePrint': ['printix']}

README = """Source code of {name} {version} ({id})

Part of: {repo}
Build: Visual Studio 2022 (v143), Release|x64, Tungsten Power PDF Plugin SDK 2025.3.6
(not included; headers expected under C:\\Claude\\Documentation\\TungstenPowerPDFPluginSDK-2025.3.6).
{build}
Package: python tools/make_ppak.py packaging/<spec>.ppakspec.json dist (Add-on Store repository).
"""


def add_path(z, root, rel, prefix=''):
    full = os.path.join(root, rel)
    if os.path.isfile(full):
        z.write(full, prefix + rel.replace('\\', '/'))
        return
    for d, dirs, files in os.walk(full):
        dirs[:] = [x for x in dirs if x.lower() not in SKIP_DIRS]
        for f in files:
            if os.path.splitext(f)[1].lower() in SKIP_EXT:
                continue
            p = os.path.join(d, f)
            z.write(p, prefix + os.path.relpath(p, root).replace('\\', '/'))


def on_disk(path):
    """The path with the file name spelled as on disk (includes differ in case: StdAfx.h, stdafx.h)."""
    d, name = os.path.split(path)
    try:
        return os.path.join(d, next(n for n in os.listdir(d) if n.lower() == name.lower()))
    except (OSError, StopIteration):
        return path


def project_common(root, proj_rel):
    """common/ files a project compiles (ClCompile/ClInclude) plus the local headers they include."""
    proj = os.path.join(root, proj_rel)
    base = os.path.dirname(proj)
    with open(proj, encoding='utf-8-sig') as f:
        text = f.read()
    files = set()
    for inc in re.findall(r'<(?:ClCompile|ClInclude)\s+Include="([^"]+)"', text):
        p = on_disk(os.path.normpath(os.path.join(base, inc)))
        if os.path.isfile(p):
            files.add(p)
    todo = list(files)
    while todo:
        with open(todo.pop(), encoding='utf-8', errors='replace') as f:
            src, here = f.read(), os.path.dirname(f.name)
        for h in re.findall(r'#\s*include\s+"([^"]+)"', src):
            for d in (here, os.path.join(root, 'common'), os.path.join(root, 'res'), base):
                p = on_disk(os.path.normpath(os.path.join(d, h)))
                if os.path.isfile(p) and p.startswith(root):
                    if p not in files:
                        files.add(p)
                        todo.append(p)
                    break
    common = os.path.join(root, 'common') + os.sep
    return sorted(os.path.relpath(p, root) for p in files if p.startswith(common))


def spec(name):
    with open(os.path.join(STORE, 'packaging', name + '.ppakspec.json'), encoding='utf-8') as f:
        return json.load(f)


def version_of(s):
    if 'version' in s:
        return s['version']
    vf = s['versionFrom']
    with open(os.path.normpath(os.path.join(STORE, 'packaging', vf['file'])), encoding='utf-8') as f:
        return re.search(vf['regex'], f.read()).group(1)


def build(pkg_id, version, name, repo, root, paths, build_hint):
    os.makedirs(OUT, exist_ok=True)
    out = os.path.join(OUT, f'{pkg_id}-{version}.source.zip')
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
        for rel in paths:
            add_path(z, root, rel)
        z.writestr('SOURCE-README.txt', README.format(name=name, version=version, id=pkg_id, repo=repo, build=build_hint))
    print(f'{out}  ({os.path.getsize(out) // 1024} KB)')


def main():
    for module, dirs in FP1_MODULES.items():
        sname = {'OneClickSign': 'oneclicksign', 'QesSign': 'qessign', 'SmartBookmarks': 'smartbookmarks',
                 'BarcodeStamps': 'barcodestamps', 'EInvoice': 'einvoice', 'ComplianceCheck': 'compliancecheck',
                 'MailMerge': 'mailmerge', 'CommerzbankSign': 'commerzbanksign'}[module]
        s = spec(sname)
        build(s['id'], version_of(s), module, 'Enhanced Feature Pack (C:\\Claude\\EnhancedFeaturePack), store build', FP1,
              FP1_COMMON + dirs + [f'store/{module}'],
              f'Run: python store/tools/gen_store.py, then store/build_store.cmd (builds store/{module}/{module}.vcxproj).')
    for module, dirs in FP2_MODULES.items():
        s = spec(module.lower())
        build(s['id'], version_of(s), module, 'Enhanced Feature Pack 2 (C:\\Claude\\FeaturePack2), store build', FP2,
              project_common(FP2, f'store/{module}/{module}.vcxproj') + FP2_COMMON + dirs + [f'store/{module}'],
              f'Run: msbuild store/{module}/{module}.vcxproj /p:Configuration=Release /p:Platform=x64.')
    s = spec('pluginstore')
    build(s['id'], version_of(s), 'Add-on Store client', 'Add-on Store (github.com/mnimtz/pluginstore-powerpdf)', STORE,
          ['client', 'packaging/pluginstore.ppakspec.json', 'tools/make_ppak.py'],
          'Run: client\\build.cmd, then client\\installer\\build_msi.cmd (WiX v3).')


if __name__ == '__main__':
    main()
