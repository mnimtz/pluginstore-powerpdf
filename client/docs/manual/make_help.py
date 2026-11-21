"""make_help.py - the help of an add-on in the format of the central help (addon-help-format.md, version 1).

    python make_help.py <folder with *.src.html and help_config.json> <out folder docs/help> <version>

Reads the manual sources (handbuch.de.src.html / manual.<lang>.src.html, the same as for the PDF manuals),
drops cover, table of contents, styles and kit references, gives every chapter (h1) and section (h2) an
id made from the English headings (the same id in every language, by position) and writes
<out>/<lang>.html and <out>/help.json. help_config.json: {"addon": "<package id>", "topics": {"name": <chapter
number, 1-based>}}. Only the elements of the allow list are written; anything else is dropped.
Exit code 1 when a language has another structure than English or English is missing.
"""
import html
import json
import os
import re
import sys
from html.parser import HTMLParser

LANGS = ['en', 'de', 'fr', 'it', 'es', 'nl']
ALLOWED = {'h1', 'h2', 'h3', 'p', 'ul', 'ol', 'li', 'table', 'thead', 'tbody', 'tr', 'th', 'td', 'b', 'strong',
           'i', 'em', 'code', 'pre', 'br', 'div', 'span', 'a'}
VOID = {'br'}
HTML_VOID = {'area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'source', 'track', 'wbr'}
CLASSES = {'callout', 'steps'}


def src_of(folder, lang):
    for name in (('handbuch.de.src.html',) if lang == 'de' else ()) + ('manual.%s.src.html' % lang,):
        p = os.path.join(folder, name)
        if os.path.exists(p):
            return p
    return None


class Clean(HTMLParser):
    """Writes the body without cover, contents, scripts and styles; collects the h1/h2 texts."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.out, self.skip, self.lang, self.title = [], 0, 'en', ''
        self.in_title = False
        self.heads = []          # (tag, text) of h1/h2 in order
        self.cur = None          # heading being read
        self.open = []           # written start tags

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == 'html':
            self.lang = a.get('lang', 'en')
        if tag == 'title':
            self.in_title = True
        cls = set((a.get('class') or '').split())
        if self.skip:
            if tag not in HTML_VOID:
                self.skip += 1
            return
        if (tag == 'section' and 'cover' in cls) or (tag == 'h1' and 'first' in cls) or (tag == 'div' and 'toc' in cls) \
                or tag in ('script', 'style', 'head', 'iframe', 'form', 'object', 'svg'):
            self.skip = 1
            return
        if tag not in ALLOWED:
            return
        attr = ''
        if tag in ('h1', 'h2'):
            self.cur = [tag, '', len(self.out)]
            attr = ' id="%%ID%d%%"' % len(self.heads)
        keep = sorted(cls & CLASSES)
        if keep and tag in ('div', 'ol', 'p', 'span'):
            attr += ' class="%s"' % ' '.join(keep)
        if tag == 'a':
            href = a.get('href', '')
            if href.startswith('#') or href.startswith('https://'):
                attr += ' href="%s"' % html.escape(href, quote=True)
        self.out.append('<%s%s>' % (tag, attr))
        if tag not in VOID:
            self.open.append(tag)

    def handle_endtag(self, tag):
        if tag == 'title':
            self.in_title = False
        if tag in HTML_VOID:
            return
        if self.skip:
            self.skip -= 1
            return
        if tag not in ALLOWED or tag in VOID:
            return
        if self.open and self.open[-1] == tag:
            self.open.pop()
            self.out.append('</%s>' % tag)
            if self.cur and tag == self.cur[0]:
                self.heads.append((self.cur[0], self.cur[1].strip()))
                self.cur = None

    def handle_data(self, data):
        if self.in_title:
            self.title += data
        if self.skip:
            return
        if self.cur:
            self.cur[1] += data
        self.out.append(html.escape(data, quote=False))


def slug(text):
    t = re.sub(r'^\s*\d+\s*', '', text).lower()
    t = re.sub(r'[^a-z0-9]+', '-', t).strip('-')
    return t or 'section'


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        return 2
    folder, out, version = sys.argv[1], sys.argv[2], sys.argv[3]
    cfg = json.load(open(os.path.join(folder, 'help_config.json'), encoding='utf-8'))
    docs = {}
    for lang in LANGS:
        p = src_of(folder, lang)
        if not p:
            continue
        c = Clean()
        c.feed(open(p, encoding='utf-8').read().replace('{VERSION}', version))
        docs[lang] = c
    if 'en' not in docs:
        print('make_help: English source missing')
        return 1
    # ids from the English headings: chapter slug, section = chapter slug + "-" + section slug (unique)
    ids, used, chapter = [], set(), ''
    for tag, text in docs['en'].heads:
        s = slug(text)
        if tag == 'h1':
            chapter = s
        else:
            s = chapter + '-' + s
        base, k = s, 2
        while s in used:
            s = '%s-%d' % (base, k)
            k += 1
        used.add(s)
        ids.append(s)
    shape = [t for t, _ in docs['en'].heads]
    os.makedirs(out, exist_ok=True)
    errors = 0
    for lang, c in docs.items():
        if [t for t, _ in c.heads] != shape:
            print('make_help: %s has another structure than English (%d headings, English %d)' % (lang, len(c.heads), len(shape)))
            errors += 1
            continue
        body = ''.join(c.out)
        for i, s in enumerate(ids):
            body = body.replace('%%ID%d%%' % i, s, 1)
        assert '—' not in body, 'em dash in ' + lang
        doc = ('<!doctype html>\n<html lang="%s">\n<head>\n<meta charset="utf-8">\n<title>%s</title>\n</head>\n<body>\n%s\n</body>\n</html>\n'
               % (lang, html.escape(c.title.strip(), quote=False), body.strip()))
        with open(os.path.join(out, lang + '.html'), 'w', encoding='utf-8', newline='\n') as f:
            f.write(doc)
    chapters = [s for (t, _), s in zip(docs['en'].heads, ids) if t == 'h1']
    topics = {}
    for name, n in cfg.get('topics', {}).items():
        if isinstance(n, int) and 1 <= n <= len(chapters):
            topics[name] = chapters[n - 1]
        else:
            print('make_help: topic %s points to no chapter' % name)
            errors += 1
    meta = {'format': 'tungsten-addon-help', 'version': 1, 'addon': cfg['addon'],
            'langs': [l for l in LANGS if l in docs], 'topics': topics}
    with open(os.path.join(out, 'help.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(meta, f, ensure_ascii=False, indent=1)
        f.write('\n')
    print('make_help: %s, %d languages, %d chapters, %d topics' % (cfg['addon'], len(meta['langs']), len(chapters), len(topics)))
    return 1 if errors else 0


if __name__ == '__main__':
    sys.exit(main())
