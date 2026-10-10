"""check_ui.py - checks plug-in pages against the rules of the Tungsten Plug-in UI.

    python tools/check_ui.py <page.html> [more pages] [--strings strings.json]

Errors (exit code 1):
  COLOR_OUTSIDE_KIT   a colour value (#hex, rgb(), hsl()) in the page's own CSS or inline
                      styles; use the kit variables (var(--tp-...)). Colours of a domain
                      (for example change types of Smart Compare) are allowed between
                      /* tpui:domain-colors */ and /* /tpui:domain-colors */.
  BROWSER_POPUP       alert(), confirm() or prompt(); use TPUI.alert/confirm/prompt
  REMOTE_RESOURCE     a script, style, image or frame from the network
  STRINGS_MISSING     with --strings: a language of the 21 is missing, or keys differ from en
Warnings:
  NO_KIT              the page does not use the kit (no tpui.css)
The kit itself (between /*tpui:begin*/ and /*tpui:end*/) is not checked, so pages can be
checked before or after tools/inline.py. Also checks the kit's own text colours for a
contrast of at least 4.5:1. MIT License, see LICENSE.
"""
import io, json, os, re, sys

KIT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
LANGS = ['en', 'de', 'fr', 'it', 'es', 'nl', 'pt', 'da', 'fi', 'nb', 'sv', 'pl', 'cs', 'hu', 'ru', 'tr',
         'zh-Hans', 'zh-Hant', 'ja', 'ko', 'ar']
COLOR = re.compile(r'#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(')


def strip_kit(text):
    return re.sub(r'/\*tpui:begin\*/.*?/\*tpui:end\*/', '', text, flags=re.S)


def strip_domain(text):
    return re.sub(r'/\*\s*tpui:domain-colors\s*\*/.*?/\*\s*/tpui:domain-colors\s*\*/', '', text, flags=re.S)


def lum(h):
    h = h.lstrip('#')
    c = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    c = [x / 12.92 if x <= 0.03928 else ((x + 0.055) / 1.055) ** 2.4 for x in c]
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]


def contrast(a, b):
    la, lb = sorted([lum(a), lum(b)], reverse=True)
    return (la + 0.05) / (lb + 0.05)


def check_kit_contrast():
    css = io.open(os.path.join(KIT, 'web', 'tpui.css'), encoding='utf-8').read()
    v = dict(re.findall(r'--(tp-[a-z0-9-]+):\s*(#[0-9A-Fa-f]{6})', css))
    pairs = [('tp-ink', None), ('tp-navy', None), ('tp-text-muted', None), ('tp-link', None),
             ('tp-ok', 'tp-ok-bg'), ('tp-warn', 'tp-warn-bg'), ('tp-err', 'tp-err-bg'), ('tp-navy', 'tp-info-bg'),
             ('tp-text-muted', 'tp-gray-50'), ('tp-navy', 'tp-gray-100'), (None, 'tp-navy'), (None, 'tp-navy-deep'), (None, 'tp-err')]
    bad = []
    for fg, bg in pairs:
        f = v[fg] if fg else '#FFFFFF'
        b = v[bg] if bg else '#FFFFFF'
        c = contrast(f, b)
        if c < 4.5:
            bad.append(f'{fg or "white"} on {bg or "white"}: {c:.2f}')
    return bad


def check_page(path):
    errors, warnings = [], []
    raw = io.open(path, encoding='utf-8-sig').read()
    text = strip_domain(strip_kit(raw))
    if 'tpui.css' not in raw and '/*tpui:begin*/' not in raw:
        warnings.append('NO_KIT the page does not use the kit (tpui.css)')
    styles = re.findall(r'<style[^>]*>(.*?)</style>', text, flags=re.S | re.I)
    inline_styles = re.findall(r'\sstyle="([^"]*)"', text)
    for chunk in styles + inline_styles:
        for line in chunk.splitlines():
            if COLOR.search(line):
                errors.append('COLOR_OUTSIDE_KIT ' + line.strip()[:120])
    scripts = re.findall(r'<script[^>]*>(.*?)</script>', text, flags=re.S | re.I)
    for chunk in scripts:
        code = re.sub(r'//[^\n]*|/\*.*?\*/', '', chunk, flags=re.S)
        for m in re.finditer(r'(?<![\w.$])(?:window\.)?(alert|confirm|prompt)\s*\(', code):
            errors.append(f'BROWSER_POPUP {m.group(1)}() in a script')
    for m in re.finditer(r'<(script|link|img|iframe|audio|video|source)\b[^>]*\b(src|href)\s*=\s*"((?:https?:)?//[^"]*)"', text, re.I):
        errors.append('REMOTE_RESOURCE ' + m.group(3))
    return errors, warnings


def check_strings(path):
    data = json.load(io.open(path, encoding='utf-8-sig'))
    errors = []
    missing = [l for l in LANGS if l not in data]
    if missing:
        errors.append('STRINGS_MISSING languages ' + ', '.join(missing))
    keys = set(data.get('en', {}))
    for l in LANGS:
        if l in data and set(data[l]) != keys:
            diff = sorted(keys ^ set(data[l]))[:6]
            errors.append(f'STRINGS_MISSING {l}: keys differ from en ({", ".join(diff)})')
    return errors


def main(argv):
    args = argv[1:]
    strings = None
    if '--strings' in args:
        i = args.index('--strings')
        strings = args[i + 1]
        del args[i:i + 2]
    total = 0
    for bad in check_kit_contrast():
        print('ERROR KIT_CONTRAST', bad)
        total += 1
    for p in args:
        e, w = check_page(p)
        for x in e:
            print(f'ERROR {os.path.basename(p)}: {x}')
        for x in w:
            print(f'WARN  {os.path.basename(p)}: {x}')
        total += len(e)
    if strings:
        for x in check_strings(strings):
            print('ERROR', x)
            total += 1
    print(f'{total} error(s)')
    return 1 if total else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
