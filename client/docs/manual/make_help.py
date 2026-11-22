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
# fixed style of the page when it opens in the browser (no fonts or pictures loaded; brand colors only)
FALLBACK_STYLE = """<style>
:root { color-scheme: light dark; --ink: #1d1d1b; --muted: #5b6770; --line: #d9dee2; --head: #00a0fb; --bg: #ffffff; --soft: #f2f7fb; }
@media (prefers-color-scheme: dark) { :root { --ink: #e8eaec; --muted: #a9b3ba; --line: #3a434a; --head: #4dbefc; --bg: #15191c; --soft: #1e252a; } }
html { background: var(--bg); }
body { max-width: 860px; margin: 0 auto; padding: 24px 20px 64px; font: 15px/1.55 "Segoe UI", system-ui, sans-serif; color: var(--ink); background: var(--bg); }
h1 { font-weight: 300; font-size: 28px; color: var(--head); margin: 40px 0 12px; padding-top: 16px; border-top: 1px solid var(--line); }
h1:first-child { border-top: 0; margin-top: 0; }
h2 { font-weight: 600; font-size: 19px; margin: 28px 0 8px; }
h3 { font-weight: 600; font-size: 16px; margin: 20px 0 6px; }
p, li { margin: 6px 0; }
table { border-collapse: collapse; width: 100%; margin: 12px 0; font-size: 14px; }
th, td { border: 1px solid var(--line); padding: 6px 8px; text-align: start; vertical-align: top; }
th { background: var(--soft); font-weight: 600; }
code, pre { font-family: Consolas, "Cascadia Mono", monospace; font-size: 13px; background: var(--soft); border-radius: 3px; }
code { padding: 1px 4px; }
pre { padding: 10px; overflow-x: auto; }
.callout { border-inline-start: 3px solid var(--head); background: var(--soft); padding: 8px 12px; margin: 12px 0; }
a { color: var(--head); }
</style>"""


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
            # the help navigates by its own outline: no chapter numbers of the PDF manual ("1 Overview" -> "Overview")
            if not self.cur[1].strip():
                data = re.sub(r'^(\s*)\d+(?:\.\d+)*\.?\s+', r'\1', data)
            self.cur[1] += data
        self.out.append(html.escape(data, quote=False))


# "chapter 16" in the text of the manual -> a jump to that chapter, named by its title (the help shows no numbers)
CHAPTER_WORDS = {'de': 'Kapitel|Kapiteln', 'en': 'chapters|chapter|Chapters|Chapter', 'fr': 'chapitres|chapitre|Chapitres|Chapitre',
                 'it': 'capitoli|capitolo|Capitoli|Capitolo', 'es': 'capítulos|capítulo|Capítulos|Capítulo',
                 'nl': 'hoofdstukken|hoofdstuk|Hoofdstukken|Hoofdstuk'}
CHAPTER_JOIN = r'(?:\s*,\s*|\s*[-–]\s*|\s+(?:und|bis|oder|and|to|or|et|à|ou|e|a|o|y|en|tot|of)\s+)'


def chapter_links(body, lang, chapters):
    """chapters: [(id, title)] of the h1 headings in order; numbers outside 1..n stay as they are."""
    words = CHAPTER_WORDS.get(lang)
    if not words:
        return body

    def link(num):
        n = int(num)
        if 1 <= n <= len(chapters):
            cid, title = chapters[n - 1]
            return '<a href="#%s">%s</a>' % (cid, html.escape(title, quote=False))
        return num

    def repl(m):
        rest = re.sub(r'(%s)(\d+)' % CHAPTER_JOIN, lambda r: r.group(1) + link(r.group(2)), m.group(3))
        return m.group(1) + ' ' + link(m.group(2)) + rest

    return re.sub(r'\b(%s)\s+(\d+)((?:%s\d+)*)\b' % (words, CHAPTER_JOIN), repl, body)


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
        body = chapter_links(body, lang, [(s, txt) for (t, txt), s in zip(c.heads, ids) if t == 'h1'])
        assert '—' not in body, 'em dash in ' + lang
        # the central help takes only <body>; the <style> is for the browser fallback (readable, Tungsten look)
        doc = ('<!doctype html>\n<html lang="%s"%s>\n<head>\n<meta charset="utf-8">\n'
               '<meta name="viewport" content="width=device-width, initial-scale=1">\n<title>%s</title>\n%s\n</head>\n<body>\n%s\n</body>\n</html>\n'
               % (lang, ' dir="rtl"' if lang == 'ar' else '', html.escape(c.title.strip(), quote=False), FALLBACK_STYLE, body.strip()))
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
