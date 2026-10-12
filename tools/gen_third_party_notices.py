"""Writes server/THIRD-PARTY-NOTICES.md: every NuGet package the server image ships, with its copyright
and full license text, taken from the restored packages (obj/project.assets.json and the NuGet cache).

    python tools/gen_third_party_notices.py          # write the file
    python tools/gen_third_party_notices.py --check  # exit 1 when the file is out of date

Run it after every package change (dotnet restore first). The Dockerfile copies the file to
/app/THIRD-PARTY-NOTICES.md (S1.25.0).
"""
import io, json, os, re, sys, glob, hashlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, 'server', 'src', 'AddonStore.Web', 'obj', 'project.assets.json')
OUT = os.path.join(ROOT, 'server', 'THIRD-PARTY-NOTICES.md')

MIT = '''Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.'''

# NOTICE files of Apache-2.0 packages that the NuGet package itself does not carry (Apache-2.0 section 4(d))
NOTICES = {
    'Apache.Arrow': '''Apache Arrow .NET
Copyright 2018-2025 The Apache Software Foundation

This product includes software developed at
The Apache Software Foundation (http://www.apache.org/).''',
}
# copyright holders missing from a package's metadata
COPYRIGHT = {'MaxMind.Db': 'Copyright (c) MaxMind, Inc.'}

BASE_IMAGE = '''## Base image

The image is built on `mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu 24.04 "noble"),
published by Microsoft. It contains the .NET and ASP.NET Core runtime (MIT, notices in
`/usr/share/dotnet/ThirdPartyNotices.txt` and `/usr/share/dotnet/LICENSE.txt`) and
Ubuntu packages, some of them under the GNU GPL or LGPL (for example the C library and
the shell). Those packages are passed on unchanged under their own licenses; their
copyright and license texts are in the image under `/usr/share/doc/<package>/copyright`.
The source code of each package is available from Ubuntu (https://launchpad.net/ubuntu,
or `apt-get source <package>`), and the image definition from Microsoft
(https://github.com/dotnet/dotnet-docker). Licensing of the image contents:
https://github.com/dotnet/dotnet-docker/blob/main/documentation/image-artifact-details.md'''


def read(p):
    b = open(p, 'rb').read()
    for enc in ('utf-8-sig', 'cp1252'):
        try:
            return b.decode(enc).replace('\r\n', '\n').strip('\n')
        except UnicodeDecodeError:
            pass
    return b.decode('latin-1')


def build():
    a = json.load(open(ASSETS, encoding='utf-8'))
    cache = list(a['packageFolders'])[0]
    pkgs, apache_text, shared = [], None, {}
    for key, lib in sorted(a['libraries'].items(), key=lambda kv: kv[0].lower()):
        if lib.get('type') != 'package':
            continue
        name, ver = key.split('/')
        d = os.path.join(cache, lib['path'])
        nuspec = read(glob.glob(os.path.join(d, '*.nuspec'))[0])
        tag = lambda t: (re.search(r'<%s>([^<]*)</%s>' % (t, t), nuspec) or [None, ''])[1].strip()
        m = re.search(r'<license type="(\w+)"[^>]*>([^<]+)</license>', nuspec)
        lic_type, lic = (m.group(1), m.group(2).strip()) if m else ('url', tag('licenseUrl'))
        copyright_ = COPYRIGHT.get(name) or tag('copyright').replace('©', '(c)').replace('  ', ' ')
        files = sorted(f for f in os.listdir(d) if re.match(r'(?i)(license|notice|copying)', f))
        texts = [(f, read(os.path.join(d, f))) for f in files]
        tpn = os.path.join(d, 'THIRD-PARTY-NOTICES.TXT')
        if os.path.exists(tpn):
            t = read(tpn)
            h = hashlib.sha256(t.encode()).hexdigest()[:8]
            shared.setdefault(h, [t, []])[1].append('%s %s' % (name, ver))
            tpn = h
        else:
            tpn = None
        if name == 'Apache.Arrow':
            full = dict(texts).get('LICENSE.txt', '')
            apache_text = full[:full.index('END OF TERMS AND CONDITIONS') + len('END OF TERMS AND CONDITIONS')]
        if lic_type == 'url' and 'corefx' in lic:
            lic = 'MIT'
        pkgs.append(dict(name=name, ver=ver, lic=lic if lic_type != 'file' else None, file=lic if lic_type == 'file' else None,
                         copyright=copyright_, url=tag('projectUrl'), texts=texts, tpn=tpn))
    assert apache_text, 'Apache-2.0 text (from Apache.Arrow LICENSE.txt)'

    o = ['# Third-party notices: Add-on Store server image', '',
         'The server image (`/app`) contains the components below. Each is used under its own',
         'license; the copyright notices and full license texts follow. The Add-on Store code',
         'itself is under the MIT License in `LICENSE` of the repository. Generated by',
         '`tools/gen_third_party_notices.py` from the restored NuGet packages.', '',
         '| Package | Version | License | Copyright |', '|---|---|---|---|']
    for p in pkgs:
        lic = p['lic'] or {'Apache.Arrow': 'Apache-2.0', 'SQLite': 'Public domain'}.get(p['name'], 'see below')
        o.append('| %s | %s | %s | %s |' % (p['name'], p['ver'], lic, p['copyright'] or 'see below'))
    o += ['', BASE_IMAGE, '', '## Packages', '']
    for p in pkgs:
        o += ['### %s %s' % (p['name'], p['ver']), '']
        if p['url']:
            o += ['Project: %s' % p['url'], '']
        if p['copyright']:
            o += [p['copyright'], '']
        if p['lic'] == 'MIT' and not p['texts']:
            o += ['MIT License', '', '```text', MIT, '```', '']
        elif p['lic'] == 'Apache-2.0':
            o += ['Licensed under the Apache License, Version 2.0; the full text is in the section', '"Apache License 2.0" at the end of this file.', '']
        for f, t in p['texts']:
            if f.upper() == 'THIRD-PARTY-NOTICES.TXT':
                continue
            o += ['%s of the package:' % f, '', '```text', t, '```', '']
        if p['name'] in NOTICES:
            o += ['NOTICE (from the project, Apache-2.0 section 4(d)):', '', '```text', NOTICES[p['name']], '```', '']
        if p['tpn']:
            o += ['The package carries third-party notices for code it contains; see "Notices of code', 'inside Microsoft packages", set %s.' % p['tpn'], '']
    o += ['## Notices of code inside Microsoft packages', '']
    for h, (t, users) in shared.items():
        o += ['### Set %s' % h, '', 'THIRD-PARTY-NOTICES.TXT of: %s' % ', '.join(users), '', '```text', t, '```', '']
    o += ['## Apache License 2.0', '', '```text', apache_text, '```', '']
    return '\n'.join(o)


if __name__ == '__main__':
    text = build()
    if '--check' in sys.argv:
        cur = io.open(OUT, encoding='utf-8').read() if os.path.exists(OUT) else ''
        if cur != text:
            print('server/THIRD-PARTY-NOTICES.md is out of date: run python tools/gen_third_party_notices.py')
            sys.exit(1)
        print('OK: third-party notices are up to date.')
    else:
        io.open(OUT, 'w', encoding='utf-8', newline='\n').write(text)
        print('wrote', OUT, len(text), 'chars')
