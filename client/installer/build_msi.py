#!/usr/bin/env python3
"""build_msi.py - builds the multi-language client MSI (C0.8.0).

1. candle once (Product.wxs and our own dialogs Ui.wxs), light once per language
   (l10n/<culture>.wxl with our dialog texts, the license of that language). No WiX
   extension and no WiX runtime part goes into the MSI (C1.9.13, license audit),
2. language transforms of the 15 other languages against the English MSI
   (torch -t language),
3. the transforms embedded into the English MSI as sub-storages named by
   their LCID, and the summary information lists all languages, so Windows
   applies the user's language, and msiexec TRANSFORMS=:<LCID> picks one.

Output: installer/Release/PluginStore-<version>.msi and PluginStore.msi.
Needs the WiX v3 binaries in C:\\Claude\\Tools\\wix314.
"""
import os, shutil, subprocess, sys, warnings

warnings.filterwarnings('ignore', category=DeprecationWarning)
import msilib   # Windows Installer API (Python up to 3.12)

WIX = r'C:\Claude\Tools\wix314'
HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.dirname(HERE)
OUT = os.path.join(HERE, 'Release')
LANGDIR = os.path.join(OUT, 'lang')
sys.path.insert(0, HERE)
from make_l10n import CULTURES, main as make_l10n   # noqa: E402


def run(args):
    r = subprocess.run(args, capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-3000:], r.stderr[-3000:])
        raise SystemExit(f'failed: {os.path.basename(args[0])} ({r.returncode})')


def main():
    if not os.path.exists(os.path.join(PROJ, 'Release', 'PluginStore.zxt')):
        raise SystemExit('Plug-in not built - run build.cmd first.')
    version = subprocess.run([sys.executable, os.path.join(HERE, 'get_version.py')], capture_output=True, text=True).stdout.strip()
    if not version:
        raise SystemExit('Version not found - check common\\version.h.')
    print('Version:', version)
    make_l10n()
    import make_banner   # noqa: F401  (writes banner.bmp)
    os.makedirs(LANGDIR, exist_ok=True)
    objdir = os.path.join(HERE, 'obj') + os.sep
    os.makedirs(objdir, exist_ok=True)
    run([os.path.join(WIX, 'candle.exe'), '-nologo', '-arch', 'x64',
         f'-dProjectDir={PROJ}', f'-dFPVersion={version}', '-out', objdir,
         os.path.join(HERE, 'Product.wxs'), os.path.join(HERE, 'Ui.wxs')])
    objs = [os.path.join(objdir, 'Product.wixobj'), os.path.join(objdir, 'Ui.wixobj')]

    msis = {}
    for culture, lcid, _ in CULTURES:
        target = os.path.join(LANGDIR, f'{culture}.msi')
        run([os.path.join(WIX, 'light.exe'), '-nologo', '-sw1076',
             '-cultures:' + (culture if culture == 'en-US' else f'{culture};en-US'), '-loc', os.path.join(HERE, 'l10n', f'{culture}.wxl'),
             f'-dTpLicenseRtf={os.path.join(HERE, "l10n", f"license-{culture}.rtf")}', '-out', target] + objs)
        msis[culture] = (lcid, target)

    base = os.path.join(OUT, f'PluginStore-{version}.msi')
    shutil.copyfile(msis['en-US'][1], base)
    transforms = []
    for culture, (lcid, path) in msis.items():
        if culture == 'en-US':
            continue
        mst = os.path.join(LANGDIR, f'{lcid}.mst')
        run([os.path.join(WIX, 'torch.exe'), '-nologo', '-t', 'language', msis['en-US'][1], path, '-out', mst])
        transforms.append((lcid, mst))

    db = msilib.OpenDatabase(base, msilib.MSIDBOPEN_TRANSACT)
    view = db.OpenView('SELECT `Name`, `Data` FROM `_Storages`')
    view.Execute(None)
    for lcid, mst in transforms:
        rec = msilib.CreateRecord(2)
        rec.SetString(1, str(lcid))
        rec.SetStream(2, mst)
        view.Modify(msilib.MSIMODIFY_ASSIGN, rec)
    view.Close()
    si = db.GetSummaryInformation(1)
    si.SetProperty(msilib.PID_TEMPLATE, 'x64;' + ','.join(str(l) for l, _ in [(1033, '')] + transforms))
    si.Persist()
    db.Commit()
    del db

    shutil.copyfile(base, os.path.join(OUT, 'PluginStore.msi'))
    print(f'OK. MSI: {base} ({len(transforms) + 1} languages)')


if __name__ == '__main__':
    main()
