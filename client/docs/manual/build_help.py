"""build_help.py - the client's own help for the central help (C2.1.0).

    python -I client/docs/manual/build_help.py

Runs make_help.py on the manual sources of this folder (handbuch.de.src.html, manual.<lang>.src.html,
help_config.json) with the version of common/version.h, writes client/docs/help/ (help.json + six
<lang>.html; the MSI installs them into Plug-Ins\\PluginStore\\docs\\help) and checks the result the way
the store does (DOCS_ACTIVE_CONTENT: also plain text such as "ontbreekt = ..." matches on[a-z]+=) and
for dashes. Exit code 1 on any finding.
"""
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CLIENT = os.path.normpath(os.path.join(HERE, '..', '..'))
OUT = os.path.join(CLIENT, 'docs', 'help')


def version():
    txt = open(os.path.join(CLIENT, 'common', 'version.h'), encoding='utf-8').read()
    return re.search(r'FP_VERSION_A\s+"([0-9.]+)"', txt).group(1)


def main():
    if os.path.isdir(OUT):
        shutil.rmtree(OUT)   # no stale language files
    rc = subprocess.call([sys.executable, '-I', os.path.join(HERE, 'make_help.py'), HERE, OUT, version()])
    if rc != 0:
        return 1
    names = sorted(os.listdir(OUT))
    want = ['de.html', 'en.html', 'es.html', 'fr.html', 'help.json', 'it.html', 'nl.html']
    if names != want:
        print('build_help: unexpected files', names)
        return 1
    for n in names:
        txt = open(os.path.join(OUT, n), encoding='utf-8').read()
        active = re.search(r'<script\b|\bon[a-z]+\s*=|javascript:|<(?:iframe|object|embed|img)\b', txt, re.I)
        if '—' in txt or '–' in txt or active:
            print('build_help: forbidden content in', n, repr(active.group(0)) if active else '(dash)')
            rc = 1
    if rc == 0:
        print('build_help: ok, %d files in %s' % (len(names), OUT))
    return rc


if __name__ == '__main__':
    sys.exit(main())
