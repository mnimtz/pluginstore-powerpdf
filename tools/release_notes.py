#!/usr/bin/env python3
"""release_notes.py <server|client> - prints the tag, title and notes for a GitHub Release.

Used by CI (container.yml) to tag every new server (S<version>) and client
(C<version>) release. Output: first line tag, second line title, rest notes.
"""
import json
import os
import re
import subprocess
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
STORE = 'https://addon.power-pdf.de'


def read(rel):
    with open(os.path.join(ROOT, rel), encoding='utf-8') as f:
        return f.read()


def commit_message():
    try:
        return subprocess.check_output(['git', 'log', '-1', '--format=%B'], cwd=ROOT, text=True).strip()
    except Exception:
        return ''


kind = sys.argv[1]
if kind == 'server':
    version = read('VERSION').strip()
    tag = f'S{version}'
    title = f'Server {tag}'
    notes = [
        f'Server release **{tag}** of the Add-on Store.',
        '',
        f'Container image: `ghcr.io/mnimtz/pluginstore-powerpdf:{tag}` (also `:latest`).',
        'The App Service pulls the new image on restart (or immediately when the deployment webhook is configured).',
        f'Check the running version in the page footer or at `{STORE}/api/ping`.',
        '',
        '### Commit',
        '',
        commit_message(),
    ]
elif kind == 'client':
    version = re.search(r'FP_VERSION_A\s+"([0-9.]+)"', read('client/common/version.h')).group(1)
    tag = f'C{version}'
    title = f'Client {tag}'
    spec = json.loads(read('packaging/pluginstore.ppakspec.json'))
    log = spec.get('changelog', {})
    notes = [
        f'Client release **{tag}** of the Add-on Store ribbon add-on for Tungsten Power PDF.',
        '',
        f'MSI download (newest live client): {STORE}/download/pluginstore.msi',
        'Installed clients offer the update in the store dialog.',
        '',
        '### Changes',
        '',
        f'- EN: {log.get("en", "")}',
        f'- DE: {log.get("de", "")}',
    ]
else:
    sys.exit('usage: release_notes.py <server|client>')

print(tag)
print(title)
print('\n'.join(notes))
