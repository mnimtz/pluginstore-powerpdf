"""inline.py - puts the kit into a plug-in page, so the page is one self-contained file
(for an RCDATA resource and NavigateToString).

    python tools/inline.py <page.html> <out.html>

The page references the kit as during development, relative to its own folder:
    <link rel="stylesheet" href=".../tpui.css">
    <script src=".../tpui.js"></script>   (also tpui-icons.js)
Every such reference is replaced by the file's content; other local <link>/<script src>
references are inlined the same way. Remote references (http, https, //) are refused:
a plug-in page loads nothing from the network. The output starts with a comment naming
the kit version. MIT License, see LICENSE.
"""
import io, os, re, sys

KIT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))


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
        if '</script' in text.lower() and p.lower().endswith('.js'):
            raise SystemExit(f'inline.py: {ref} contains "</script", it cannot be inlined safely')
        return text

    def css(m):
        return '<style>\n' + read(m.group(1)) + '\n</style>'

    def js(m):
        return '<script>\n' + read(m.group(1)) + '\n</script>'

    html = re.sub(r'<link\s+rel="stylesheet"\s+href="([^"]+)"\s*/?>', css, html)
    html = re.sub(r'<script\s+src="([^"]+)"\s*>\s*</script>', js, html)
    if re.search(r'<(script|link|img|iframe)[^>]+(src|href)="(https?:)?//', html, re.I):
        raise SystemExit('inline.py: the page references the network')
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
