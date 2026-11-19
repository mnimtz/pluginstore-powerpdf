"""inline.py - puts the kit into a plug-in page, so the page is one self-contained file
(for an RCDATA resource and NavigateToString).

    python tools/inline.py <page.html> <out.html>

The page references the kit as during development, relative to its own folder:
    <link rel="stylesheet" href=".../tpui.css">
    <script src=".../tpui.js"></script>   (also tpui-icons.js)
Every such reference is replaced by the file's content; other local <link>/<script src>
references are inlined the same way. 1.1.0: local url(...) inside an inlined stylesheet
(fonts of tpui-fonts.css, images) and <img src="..."> of local files (the brand logo) become
data: URIs. Remote references (http, https, //) are refused: a plug-in page loads nothing
from the network. The output starts with a comment naming the kit version. MIT License, see
LICENSE.
"""
import base64, io, os, re, sys

KIT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
# 1.1.0: what may become a data: URI, by extension
TYPES = {'.ttf': 'font/ttf', '.woff2': 'font/woff2', '.woff': 'font/woff', '.svg': 'image/svg+xml',
         '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.gif': 'image/gif'}
REMOTE = re.compile(r'^(https?:)?//', re.I)


def data_uri(base, ref, where):
    """data: URI of the local file ref (relative to base); remote or unknown types are refused."""
    if REMOTE.match(ref):
        raise SystemExit(f'inline.py: remote reference refused: {ref} ({where})')
    p = os.path.normpath(os.path.join(base, ref.split('?')[0].split('#')[0]))
    ext = os.path.splitext(p)[1].lower()
    if ext not in TYPES:
        raise SystemExit(f'inline.py: {ref} ({where}): only {", ".join(sorted(TYPES))} can be embedded')
    if not os.path.isfile(p):
        raise SystemExit(f'inline.py: missing file {ref} ({p})')
    return 'data:%s;base64,%s' % (TYPES[ext], base64.b64encode(open(p, 'rb').read()).decode('ascii'))


def kit_version():
    return io.open(os.path.join(KIT, 'VERSION'), encoding='utf-8').read().strip()


def inline(page_path):
    base = os.path.dirname(os.path.abspath(page_path))
    html = io.open(page_path, encoding='utf-8-sig').read()

    def read(ref):
        if re.match(r'^(https?:)?//', ref, re.I):
            raise SystemExit(f'inline.py: remote reference refused: {ref}')
        p = os.path.normpath(os.path.join(base, ref.split('?')[0]))
        if not os.path.isfile(p):
            raise SystemExit(f'inline.py: missing file {ref} ({p})')
        text = io.open(p, encoding='utf-8-sig').read()
        low = text.lower()
        if p.lower().endswith('.js') and ('</script' in low or '<!--' in low):
            raise SystemExit(f'inline.py: {ref} contains "</script" or "<!--", it cannot be inlined safely')
        if p.lower().endswith('.css') and '</style' in low:
            raise SystemExit(f'inline.py: {ref} contains "</style", it cannot be inlined safely')
        return text

    def css(m):
        ref = m.group(1)
        text = read(ref)
        css_dir = os.path.dirname(os.path.normpath(os.path.join(base, ref.split('?')[0])))

        def url(u):
            target = u.group(2)
            if target.startswith('data:'):
                return u.group(0)
            return 'url("%s")' % data_uri(css_dir, target, ref)
        text = re.sub(r'url\(\s*(["\']?)([^"\')]+)\1\s*\)', url, text)
        return '<style>\n' + text + '\n</style>'

    def js(m):
        return '<script>\n' + read(m.group(1)) + '\n</script>'

    def img(m):
        if m.group(2).startswith('data:'):
            return m.group(0)
        return m.group(1) + data_uri(base, m.group(2), 'img') + m.group(3)

    # images first, and only plain file paths: a script that builds '<img src="' + x + '"' stays untouched
    html = re.sub(r'(<img\b[^>]*?\bsrc=")([^"\'+<>\s]+\.(?:svg|png|jpe?g|gif))(")', img, html, flags=re.I)
    html = re.sub(r'<link\s+rel="stylesheet"\s+href="([^"]+)"\s*/?>', css, html)
    html = re.sub(r'<script\s+src="([^"]+)"\s*>\s*</script>', js, html)
    if re.search(r'<(script|link|img|iframe)[^>]+(src|href)="(https?:)?//', html, re.I):
        raise SystemExit('inline.py: the page references the network')
    # 1.0.2: a <script src> or stylesheet link in another form would stay external and fail silently
    left = re.search(r'<script\b[^>]*\bsrc\s*=|<link\b[^>]*\bstylesheet\b', html, re.I)
    if left:
        raise SystemExit(f'inline.py: reference not inlined (write it as <link rel="stylesheet" href="..."> or <script src="..."></script>): {left.group(0)}')
    return f'<!-- Tungsten Plug-in UI {kit_version()} -->\n' + html


def main(argv):
    if len(argv) != 3:
        print(__doc__)
        return 2
    out = inline(argv[1])
    io.open(argv[2], 'w', encoding='utf-8', newline='\n').write(out)
    print(f'{argv[2]}: {len(out.encode("utf-8")) // 1024} KB, kit {kit_version()}')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
