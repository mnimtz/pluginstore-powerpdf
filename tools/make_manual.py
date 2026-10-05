#!/usr/bin/env python3
"""make_manual.py - the Add-on Store manual as one English PDF.

Everything rule-related comes from the running server, so the manual never
drifts from what the store enforces:
    /api/agent-guide      the developer and agent guide (authoritative text)
    /api/rules            every rule grouped by area, with the store's own rules
    /api/openapi.json     the API reference
    /api/schema/manifest  the manifest schema
Part I (introduction and principles) is written here.

    python tools/make_manual.py                         # local test server
    python tools/make_manual.py --base https://addon.power-pdf.de
    -> docs/manual/AddonStore-Manual-<server version>.pdf

Fonts: Arial and Consolas from Windows (the brand's fallback for Red Hat Display).
"""
import argparse
import datetime
import json
import os
import re
import sys
import urllib.request
from xml.sax.saxutils import escape

from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (BaseDocTemplate, CondPageBreak, Frame, KeepTogether, NextPageTemplate, PageBreak,
                                PageTemplate, Paragraph, Preformatted, Spacer, Table, TableStyle)
from reportlab.platypus.tableofcontents import TableOfContents

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..'))
PUBLIC = 'https://addon.power-pdf.de'

NAVY = colors.HexColor('#002854')
BLUE = colors.HexColor('#00A0FB')
GREEN = colors.HexColor('#00EB86')
INK = colors.HexColor('#231F20')
SLATE = colors.HexColor('#5B6770')
SOFT = colors.HexColor('#EEF6FC')
GRID = colors.HexColor('#D5DCE3')
SEV = {'error': colors.HexColor('#B3261E'), 'warning': colors.HexColor('#8A6D00'), 'info': colors.HexColor('#0072B5'),
       'api': colors.HexColor('#5B6770')}

FONTS = r'C:\Windows\Fonts'
pdfmetrics.registerFont(TTFont('Body', os.path.join(FONTS, 'arial.ttf')))
pdfmetrics.registerFont(TTFont('Body-Bold', os.path.join(FONTS, 'arialbd.ttf')))
pdfmetrics.registerFont(TTFont('Body-Italic', os.path.join(FONTS, 'ariali.ttf')))
pdfmetrics.registerFont(TTFont('Body-BoldItalic', os.path.join(FONTS, 'arialbi.ttf')))
pdfmetrics.registerFont(TTFont('Mono', os.path.join(FONTS, 'consola.ttf')))
pdfmetrics.registerFont(TTFont('Mono-Bold', os.path.join(FONTS, 'consolab.ttf')))
from reportlab.pdfbase.pdfmetrics import registerFontFamily
registerFontFamily('Body', normal='Body', bold='Body-Bold', italic='Body-Italic', boldItalic='Body-BoldItalic')
registerFontFamily('Mono', normal='Mono', bold='Mono-Bold', italic='Mono', boldItalic='Mono-Bold')

ST = {
    'body': ParagraphStyle('body', fontName='Body', fontSize=9.6, leading=13.4, textColor=INK, spaceAfter=5, alignment=TA_LEFT),
    'small': ParagraphStyle('small', fontName='Body', fontSize=8.2, leading=11, textColor=SLATE),
    'cell': ParagraphStyle('cell', fontName='Body', fontSize=8.2, leading=10.6, textColor=INK),
    'cellb': ParagraphStyle('cellb', fontName='Body-Bold', fontSize=8.2, leading=10.6, textColor=colors.white),
    'code': ParagraphStyle('code', fontName='Mono', fontSize=7.6, leading=9.6, textColor=INK, backColor=SOFT,
                           borderPadding=(4, 5, 4, 5), leftIndent=4, rightIndent=4, spaceBefore=3, spaceAfter=7),
    'part': ParagraphStyle('part', fontName='Body-Bold', fontSize=24, leading=30, textColor=NAVY, spaceAfter=10),
    'h1': ParagraphStyle('h1', fontName='Body-Bold', fontSize=16, leading=20, textColor=NAVY, spaceBefore=6, spaceAfter=8),
    'h2': ParagraphStyle('h2', fontName='Body-Bold', fontSize=12, leading=15.5, textColor=NAVY, spaceBefore=10, spaceAfter=5),
    'h3': ParagraphStyle('h3', fontName='Body-Bold', fontSize=10.2, leading=13.5, textColor=NAVY, spaceBefore=7, spaceAfter=3),
    'lead': ParagraphStyle('lead', fontName='Body', fontSize=10.6, leading=15, textColor=SLATE, spaceAfter=8),
    'toc1': ParagraphStyle('toc1', fontName='Body-Bold', fontSize=10.5, leading=15, textColor=NAVY, spaceBefore=6),
    'toc2': ParagraphStyle('toc2', fontName='Body', fontSize=9.2, leading=12.5, leftIndent=12, textColor=INK),
    'toc3': ParagraphStyle('toc3', fontName='Body', fontSize=8.6, leading=11.5, leftIndent=26, textColor=SLATE),
}


def fetch(base, path):
    req = urllib.request.Request(base + path, headers={'Accept': 'text/markdown, application/json'})
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read().decode('utf-8')


def nodash(s):
    """House style for published text: no em dashes."""
    return s.replace(' \u2014 ', ', ').replace('\u2014', ', ')


def inline(text):
    """Markdown inline subset -> reportlab markup (escape first)."""
    text = escape(nodash(text))
    text = re.sub(r'`([^`]+)`', lambda m: '<font name="Mono" size="8.2">%s</font>' % m.group(1), text)
    text = re.sub(r'\*\*([^*]+)\*\*', r'<b>\1</b>', text)
    text = re.sub(r'\[([^\]]+)\]\((https?://[^)\s]+)\)', r'<link href="\2" color="#0072B5">\1</link>', text)
    # bare URLs; a sentence's closing punctuation is not part of the link
    text = re.sub(r'(?<![">])(https?://[^\s<)]+?)([.,;:]?)(?=\s|$|<)', r'<link href="\1" color="#0072B5">\1</link>\2', text)
    return text


# ---------------------------------------------------------------- document
def gradient_bar(c, x, y, w, h):
    """The brand's PDF gradient (#00EB86 -> #00A0FB) in a rectangle only:
    linearGradient paints the whole clip region, so clip first."""
    c.saveState()
    path = c.beginPath()
    path.rect(x, y, w, h)
    c.clipPath(path, stroke=0, fill=0)
    c.linearGradient(x, y, x + w, y, (GREEN, BLUE), extend=False)
    c.restoreState()


class Doc(BaseDocTemplate):
    def __init__(self, path, meta):
        super().__init__(path, pagesize=A4, leftMargin=20 * mm, rightMargin=20 * mm, topMargin=24 * mm, bottomMargin=20 * mm,
                         title='Add-on Store for Tungsten Power PDF: ' + meta.get('doc', 'Manual'), author='Tungsten Automation',
                         subject='API, rules and publishing guide', creator='tools/make_manual.py')
        self.meta = meta
        w, h = A4
        frame = Frame(self.leftMargin, self.bottomMargin, w - self.leftMargin - self.rightMargin, h - self.topMargin - self.bottomMargin, id='f')
        self.addPageTemplates([PageTemplate('cover', [frame], onPage=self.cover), PageTemplate('page', [frame], onPage=self.page)])
        self._hseq = 0

    def cover(self, c, doc):
        w, h = A4
        c.saveState()
        c.setFillColor(NAVY); c.rect(0, h - 120 * mm, w, 120 * mm, stroke=0, fill=1)
        c.restoreState()
        gradient_bar(c, 0, h - 123 * mm, w, 3 * mm)
        logo = os.path.join(ROOT, 'assets', 'tungsten-logo-white.png')
        if os.path.exists(logo):
            c.drawImage(logo, 20 * mm, h - 30 * mm, width=48 * mm, height=12 * mm, preserveAspectRatio=True, mask='auto', anchor='sw')
        c.setFillColor(colors.white)
        c.setFont('Body-Bold', 30); c.drawString(20 * mm, h - 70 * mm, 'Add-on Store')
        c.setFont('Body', 15); c.drawString(20 * mm, h - 80 * mm, 'for Tungsten Power PDF')
        c.setFont('Body-Bold', 13); c.drawString(20 * mm, h - 100 * mm, self.meta.get('subtitle', 'Manual: API, rules and publishing guide'))
        c.setFillColor(INK); c.setFont('Body', 10)
        y = h - 145 * mm
        for line in (f"Server {self.meta['server']}  ·  Client {self.meta['client']}", self.meta['date'],
                     f"Generated from {self.meta['source']}"):
            c.drawString(20 * mm, y, line); y -= 6 * mm
        c.setFillColor(SLATE); c.setFont('Body', 8.5)
        c.drawString(20 * mm, 22 * mm, 'Tungsten Automation. Internal and partner use. The live guide at')
        c.drawString(20 * mm, 17.5 * mm, f'{PUBLIC}/api/agent-guide is authoritative; this manual is a dated copy of it.')

    def page(self, c, doc):
        w, h = A4
        c.saveState()
        logo = os.path.join(ROOT, 'assets', 'tungsten-logo-navy.png')
        if os.path.exists(logo):
            c.drawImage(logo, 20 * mm, h - 15 * mm, width=26 * mm, height=6.5 * mm, preserveAspectRatio=True, mask='auto', anchor='sw')
        c.setFont('Body', 8); c.setFillColor(SLATE)
        c.drawRightString(w - 20 * mm, h - 13 * mm, 'Add-on Store for Tungsten Power PDF  ·  ' + self.meta.get('doc', 'Manual'))
        gradient_bar(c, 20 * mm, h - 18 * mm, w - 40 * mm, 0.8 * mm)
        c.setStrokeColor(GRID); c.line(20 * mm, 14 * mm, w - 20 * mm, 14 * mm)
        c.drawString(20 * mm, 9.5 * mm, f"Server {self.meta['server']}  ·  {self.meta['date']}")
        c.drawRightString(w - 20 * mm, 9.5 * mm, f'Page {doc.page}')
        c.restoreState()

    def beforeDocument(self):
        self._hseq = 0   # multiBuild runs several passes: keys must be the same in each

    def afterFlowable(self, f):
        lvl = getattr(f, 'toc_level', None)
        if lvl is None:
            return
        text = f.getPlainText()
        self._hseq += 1
        key = "h%d" % self._hseq
        self.canv.bookmarkPage(key)
        self.canv.addOutlineEntry(text, key, level=lvl, closed=lvl > 0)
        self.notify('TOCEntry', (lvl, text, self.page, key))


def heading(text, level):
    style = {0: 'part', 1: 'h1', 2: 'h2', 3: 'h3'}[level]
    p = Paragraph(inline(text), ST[style])
    p.toc_level = min(level, 2)
    return p


def para(text, style='body', indent=0):
    s = ST[style] if not indent else ParagraphStyle('i%d' % indent, parent=ST[style], leftIndent=indent)
    return Paragraph(inline(text), s)


def bullets(items, indent=0):
    out = []
    for it in items:
        s = ParagraphStyle('b', parent=ST['body'], leftIndent=12 + indent, bulletIndent=2 + indent, spaceAfter=2.5)
        out.append(Paragraph(inline(it), s, bulletText='\u2022'))
    out.append(Spacer(1, 3))
    return out


def table(rows, widths, header=True, sev_col=None, zebra=True):
    data = []
    for i, r in enumerate(rows):
        cells = []
        for j, c in enumerate(r):
            if header and i == 0:
                cells.append(Paragraph(escape(str(c)), ST['cellb']))
            elif isinstance(c, (Paragraph, Table)):
                cells.append(c)
            else:
                cells.append(Paragraph(inline(str(c)), ST['cell']))
        data.append(cells)
    t = Table(data, colWidths=widths, repeatRows=1 if header else 0, hAlign='LEFT')
    style = [('VALIGN', (0, 0), (-1, -1), 'TOP'), ('LEFTPADDING', (0, 0), (-1, -1), 4), ('RIGHTPADDING', (0, 0), (-1, -1), 4),
             ('TOPPADDING', (0, 0), (-1, -1), 3), ('BOTTOMPADDING', (0, 0), (-1, -1), 3),
             ('LINEBELOW', (0, 0), (-1, -1), 0.4, GRID)]
    if header:
        style += [('BACKGROUND', (0, 0), (-1, 0), NAVY)]
    if zebra:
        for i in range(1 if header else 0, len(data)):
            if i % 2 == 0:
                style.append(('BACKGROUND', (0, i), (-1, i), colors.HexColor('#F6F9FC')))
    t.setStyle(TableStyle(style))
    return t


def sev_cell(sev):
    names = {'error': 'Error', 'warning': 'Warning', 'info': 'Note', 'api': 'API'}
    parts = [p for p in sev.split('/') if p in names] or ['info']   # "error/warning": depends on the file
    label = ' / '.join(names[p] for p in parts)
    return Paragraph('<font color="%s"><b>%s</b></font>' % (SEV[parts[0]].hexval().replace('0x', '#'), label), ST['cell'])


# ---------------------------------------------------------------- markdown (agent guide)
def render_markdown(md, skip_sections=()):
    """The guide's markdown subset: ## / ### headings, paragraphs, - and 1. lists
    (with continuation lines), code blocks (indented after a blank line), tables."""
    out = []
    lines = md.replace('\r\n', '\n').split('\n')
    i, n = 0, len(lines)
    skipping = False

    def flush_para(buf, indent):
        if buf:
            out.append(para(' '.join(x.strip() for x in buf), indent=indent))

    while i < n:
        line = lines[i]
        if line.startswith('## '):
            title = line[3:].strip()
            skipping = any(title.startswith(s) for s in skip_sections)
            if not skipping:
                out.append(CondPageBreak(40 * mm))
                out.append(heading(title, 1))
            i += 1
            continue
        if skipping:
            i += 1
            continue
        if line.startswith('# '):          # the guide's own title
            i += 1
            continue
        if line.startswith('### '):
            out.append(heading(line[4:].strip(), 2)); i += 1; continue
        if not line.strip():
            i += 1; continue
        # table
        if line.lstrip().startswith('|'):
            rows = []
            while i < n and lines[i].lstrip().startswith('|'):
                cells = [c.strip() for c in lines[i].strip().strip('|').split('|')]
                if not all(re.fullmatch(r':?-{2,}:?', c) for c in cells if c):
                    rows.append(cells)
                i += 1
            if rows:
                k = len(rows[0])
                total = 170 * mm
                widths = [total / k] * k if k != 3 else [42 * mm, 26 * mm, total - 68 * mm]
                out.append(table(rows, widths))
                out.append(Spacer(1, 6))
            continue
        indent = len(line) - len(line.lstrip(' '))
        # code block: indented >= 4 after a blank line (or at the very start)
        if indent >= 4 and (i == 0 or not lines[i - 1].strip()) and not re.match(r'\s*(- |\d+\. |\d+b\. )', line):
            block = []
            base = indent
            while i < n and (not lines[i].strip() or (len(lines[i]) - len(lines[i].lstrip(' '))) >= min(base, 4)):
                if not lines[i].strip() and (i + 1 >= n or not lines[i + 1].strip() or
                                             (len(lines[i + 1]) - len(lines[i + 1].lstrip(' '))) < min(base, 4)):
                    break
                block.append(lines[i][min(base, len(lines[i]) - len(lines[i].lstrip(' '))):] if lines[i].strip() else '')
                i += 1
            out.append(Preformatted(nodash('\n'.join(block)), ST['code'], maxLineLength=104, newLineChars=''))
            continue
        m = re.match(r'(\s*)(- \[ \] |- |\d+b?\. )(.*)', line)
        if m:
            lead = len(m.group(1))
            marker = m.group(2)
            buf = [m.group(3)]
            i += 1
            while i < n and lines[i].strip() and not re.match(r'\s*(- |\d+b?\. )', lines[i]) \
                    and not lines[i].lstrip().startswith('|') and not lines[i].startswith('#'):
                buf.append(lines[i]); i += 1
            box = marker.startswith('- [')
            bullet = '[ ]' if box else '\u2022' if marker.startswith('-') else marker.strip()   # Arial has no empty-box glyph
            s = ParagraphStyle('li', parent=ST['body'], leftIndent=(20 if box else 14) + lead * 3, bulletIndent=2 + lead * 3,
                               spaceAfter=2.5, bulletFontName='Mono' if box else 'Body', bulletFontSize=8.6 if box else 9.6)
            out.append(Paragraph(inline(' '.join(x.strip() for x in buf)), s, bulletText=bullet))
            continue
        buf = [line]
        i += 1
        while i < n and lines[i].strip() and not lines[i].startswith('#') and not lines[i].lstrip().startswith('|') \
                and not re.match(r'\s*(- |\d+b?\. )', lines[i]):
            buf.append(lines[i]); i += 1
        flush_para(buf, indent * 3 if indent else 0)
    return out


# ---------------------------------------------------------------- part I (written here)
def part_intro(meta, rules):
    c = rules['counts']
    x = []
    x.append(heading('Part I. Introduction and principles', 0))
    x.append(para('What the Add-on Store is, why its rules exist and how they are enforced. The later parts are generated '
                  'from the running server: they are the rules, not a description of them.', 'lead'))

    x.append(heading('1. About this document', 1))
    x += bullets([
        '**Purpose.** One complete reference for everyone who builds, publishes, reviews or operates add-ons for '
        'Tungsten Power PDF through the Add-on Store, and the basis for further guidelines.',
        '**Audience.** Plug-in developers, AI coding assistants acting for them, reviewers who approve versions, and store administrators.',
        f"**Status.** Server {meta['server']}, client {meta['client']}, generated on {meta['date']} from {meta['source']}.",
        f'**Authority.** The live developer and agent guide at {PUBLIC}/api/agent-guide is authoritative. This manual is a '
        'dated copy of it plus the rule catalog, the API description and the manifest schema. When they differ, the live '
        'guide and the server checks win.',
        '**Language of the rules.** Rule texts are English on purpose: they are what AI assistants read and what the server '
        'reports. The web portal and the Power PDF client are available in 16 languages.',
        '**Keeping it current.** Regenerate with `python tools/make_manual.py --base https://addon.power-pdf.de` after every '
        'server release. A CI check (`tools/check_docs.py`) already keeps the guide complete: every finding code is '
        'documented and every hard rule is in the pre-flight checklist.',
    ])

    x.append(heading('2. The Add-on Store at a glance', 1))
    x.append(para('The store distributes plug-ins (`.zxt`, built with the Tungsten Power PDF Plugin SDK) as signed `.ppak` '
                  'packages. It has three parts:'))
    x.append(table([
        ['Part', 'What it does'],
        ['Store server', f'Web portal and HTTPS API at {PUBLIC}: accounts, uploads, automatic checks, review, catalog, '
                         'customer deliveries, statistics, backup.'],
        ['Power PDF client', 'The Add-on Store plug-in with its own ribbon tab "Store": catalog window, install, update '
                             'and removal of add-ons, customer codes, self-update. Installed once per machine as an MSI.'],
        ['Packages (.ppak)', 'ZIP container with manifest.json, the x64 plug-in binary, the UI layout in 16 languages, '
                             'icon, licenses and documentation. Signed by the store; the client verifies signature and SHA-256.'],
    ], [38 * mm, 132 * mm]))
    x.append(Spacer(1, 6))
    x.append(heading('Roles', 3))
    x.append(table([
        ['Role', 'May'],
        ['Developer', 'Upload and update own add-ons, upload their source code, manage own customers and deliver any '
                      'add-on to them, read problem reports of own add-ons, create personal API tokens.'],
        ['Reviewer', 'Review versions in the queue and approve or reject them (four-eyes rule when enabled), read customers.'],
        ['Admin', 'Everything: users, settings, rules, categories, backup and restore, the store client itself.'],
    ], [30 * mm, 140 * mm]))
    x.append(Spacer(1, 6))
    x.append(heading('Lifecycle of a version', 3))
    x += bullets([
        '**Submitted:** the upload is checked automatically. A package with an error is not stored at all.',
        '**Beta:** passed every automatic check; visible to clients with the beta option and in customer beta deliveries.',
        '**Live:** approved by a reviewer or admin; in the public catalog (public add-ons) or in live customer deliveries (private add-ons).',
        '**Rejected / withdrawn:** not offered. A live version can be set back to beta by an admin.',
    ])
    x.append(heading('Visibility', 3))
    x += bullets([
        '**Public:** in the catalog for every Power PDF user. Lives on the shared ribbon tab "Enhanced Features".',
        '**Private:** only for customers with a delivery and a customer code. May have its own ribbon tab; such an add-on stays private.',
    ])

    x.append(heading('3. Principles behind the rules', 1))
    x.append(para('Every rule serves one of these principles. New guidelines should name the principle they serve.'))
    x.append(table([
        ['Principle', 'What it means', 'Rules that serve it'],
        ['Safe machines', 'An add-on runs inside Power PDF with the user\'s rights. Nothing reaches a machine '
                          'unchecked or altered.', 'Signature and SHA-256, Release builds only, no foreign DLLs, no secrets, ZIP safety, size limits'],
        ['One consistent product', 'Add-ons feel like part of Power PDF.', 'Shared ribbon tab, large icons, Help stays last, 16 languages in the UI'],
        ['Legal clarity', 'Tungsten can distribute every package without license or trademark risk.',
         'MIT/BSD/Apache-2.0 only, truthful compliance audit, declared third-party code and services, no third-party brands'],
        ['Privacy', 'Users know which data leaves their machine.', 'Declared external services, no hidden telemetry, AI features optional'],
        ['Maintainability', 'Every version can be rebuilt, fixed and supported later.',
         'Source code per version, SemVer with a higher version per upload, changelog in 16 languages'],
        ['Transparency', 'Users see who stands behind an add-on and what changed.', 'Author and contact, categories, screenshots, ratings and problem reports'],
    ], [32 * mm, 62 * mm, 76 * mm]))
    x.append(Spacer(1, 6))

    x.append(heading('4. How the rules are enforced', 1))
    x.append(table([
        ['Level', 'Effect'],
        [sev_cell('error'), 'Blocks the upload; nothing is stored. Fix it with the hint of the finding and upload again.'],
        [sev_cell('warning'), 'Accepted, but shown in the check report and to the reviewer. A store can make a warning mandatory.'],
        [sev_cell('info'), 'A note for the submitter, for example a declared third-party library or a confirmed compliance audit.'],
        [sev_cell('api'), 'An answer of an API call (for example 401, 403, 404, 409, 422, 429) rather than a package check.'],
    ], [26 * mm, 144 * mm]))
    x.append(Spacer(1, 6))
    x += bullets([
        f"**Automatic checks** on every upload and dry run: {c['errors']} hard rules, {c['warnings']} warnings and {c['info']} notes "
        f"in 13 areas, plus {c['api']} API responses (Part III).",
        '**Review:** a reviewer or admin approves every live version; with the four-eyes rule never the uploader.',
        '**House rules:** guidelines a store adds in plain words. Reviewers check them before approval; recommendations only advise.',
        '**Mandatory warnings:** a store can treat chosen warnings as errors; the validator then refuses such uploads.',
        '**On the client:** it installs only packages signed by a trusted store key whose SHA-256 matches, refuses Power PDF '
        'versions and editions the SDK does not support, and honours machine policies set by IT.',
    ])
    x.append(heading('Extending the rules', 3))
    x.append(para('Admins maintain the store\'s own rules under Settings, Rules (`/Admin/Rules`): house rules per area and '
                  'mandatory warnings. Both appear at once in `GET /api/rules`, in section F of the pre-flight checklist '
                  'that every AI assistant reads, in the review queue and in the next edition of this manual. New standard '
                  'rules (new automatic checks) are added to the server and documented in the guide; the CI check refuses '
                  'a release whose guide misses one.'))
    house = [h for a in rules['areas'] for h in a['houseRules']]
    if house or rules['escalated']:
        x.append(heading('Rules of this store', 3))
        if rules['escalated']:
            x.append(para('Mandatory here (warnings treated as errors): ' + ', '.join('`%s`' % e for e in rules['escalated']) + '.'))
        for a in rules['areas']:
            for h in a['houseRules']:
                x.append(para('**%s** (%s, %s): %s' % (h['title'], a['name'], 'checked at approval' if h['kind'] == 'review' else 'recommendation', h['text'])))

    x.append(heading('5. Getting started', 1))
    x.append(para('Developers create a personal token on their profile page and keep it in the environment variable '
                  '`PPAK_TOKEN`, never in a chat or a file. Then one line is enough for an AI assistant:'))
    x.append(Preformatted('Read %s/agent-guide and publish the plugin in this folder.' % PUBLIC, ST['code']))
    x.append(para('When the assistant cannot reach the store (for example a cloud sandbox with a network block), it builds a '
                  'finished manual upload package instead, which the developer uploads on the website:'))
    x.append(Preformatted('Read %s/agent-guide/manual-upload and %s/agent-guide/checklist,\n'
                          'then build the finished upload ZIP for the plugin in this folder yourself.' % (PUBLIC, PUBLIC), ST['code']))
    x.append(para('Short pages for web readers that summarize long documents: `/api/agent-guide/checklist` (every hard rule) '
                  'and `/api/agent-guide/manual-upload` (package format and the one-file upload).'))
    return x


# ---------------------------------------------------------------- parts III to V
def part_rules(rules):
    x = [PageBreak(), heading('Part III. Rule reference', 0),
         para('Every code the store can report, grouped by area as on the Rules page of the portal. Package rules are checked on '
              'every upload and dry run; API responses are answers of individual calls.', 'lead')]
    c = rules['counts']
    x.append(table([['Errors', 'Warnings', 'Notes', 'API responses', 'House rules'],
                    [str(c['errors']), str(c['warnings']), str(c['info']), str(c['api']), str(c['houseRules'])]],
                   [34 * mm] * 5, zebra=False))
    x.append(Spacer(1, 8))
    for a in rules['areas']:
        if not a['rules'] and not a['houseRules']:
            continue
        x.append(CondPageBreak(35 * mm))
        x.append(heading(a['name'], 1))
        rows = [['Code', 'Level', 'Rule']]
        for r in a['rules']:
            sev = 'api' if r['kind'] == 'api' else r['severity']
            text = r['description'] + (' **Mandatory in this store.**' if r['stricterHere'] else '')
            rows.append([Paragraph('<font name="Mono" size="7.6">%s</font>' % escape(r['code']), ST['cell']), sev_cell(sev), text])
        x.append(table(rows, [48 * mm, 18 * mm, 104 * mm]))
        for h in a['houseRules']:
            x.append(Spacer(1, 4))
            x.append(para('**House rule: %s** (%s). %s' % (h['title'], 'checked at approval' if h['kind'] == 'review' else 'recommendation', h['text'])))
        x.append(Spacer(1, 6))
    return x


AUTH_NOTE = re.compile(r'\s*(Requires [^.]+\.)\s*$')


def part_api(api):
    x = [PageBreak(), heading('Part IV. API reference', 0),
         para(nodash(api['info']['description']), 'lead')]
    x.append(heading('Conventions', 1))
    x += bullets([
        f'Base URL: `{PUBLIC}`. HTTPS only; JSON in and out unless an endpoint says otherwise.',
        'Every JSON answer uses the envelope `{ ok, error: { code, message, hint }, findings: [ { code, severity, message, hint } ], data }`. '
        'The codes are listed in Part III.',
        'Authentication: `Authorization: Bearer ppak_...` with a personal token from the profile page. Endpoints marked "token" need it; '
        'the description says when only the owner, a reviewer or an admin may call it.',
        'Rate limits: at most 60 package checks and submissions per account and hour (429 RATE_LIMITED); further limits for ratings, '
        'problem reports and customer codes protect the store.',
        'Machine-readable: `GET /api/openapi.json` (OpenAPI 3.1), `GET /llms.txt`, `GET /api`.',
    ])
    groups = [('Documentation and discovery', ('/api/agent-guide', '/api/openapi', '/api/schema', '/api/skill', '/api/agents-md', '/api/tools',
                                               '/api/rules', '/api/ping', '/api/devkit', '/api', '/llms', '/robots')),
              ('Account', ('/api/me',)),
              ('Packages and versions', ('/api/packages',)),
              ('Catalog and search', ('/api/catalog', '/api/categories', '/api/search', '/api/signing-key')),
              ('Customers and deliveries', ('/api/customers', '/api/deliveries', '/api/customer-code'))]
    ops = []
    for path, methods in api['paths'].items():
        for method, op in methods.items():
            ops.append((path, method.upper(), op))

    def group_of(path):
        for g, prefixes in groups:
            if any(path == p or (p != '/api' and path.startswith(p)) for p in prefixes):
                return g
        return 'Other'
    order = [g for g, _ in groups] + ['Other']
    for g in order:
        mine = [o for o in ops if group_of(o[0]) == g]
        if not mine:
            continue
        x.append(CondPageBreak(30 * mm))
        x.append(heading(g, 1))
        for path, method, op in mine:
            desc = op.get('description') or op.get('summary') or ''
            auth = 'token' if op.get('security') else 'none'
            m = AUTH_NOTE.search(desc)
            if m:
                desc = desc[:m.start()].strip()
                auth = 'token: ' + m.group(1)[len('Requires '):].rstrip('.')
            rows = [[Paragraph('<font name="Mono-Bold" size="8.2" color="#002854">%s %s</font>' % (method, escape(path)), ST['cell']), '']]
            body = [para(desc, 'cell')]
            info = ['**Authentication:** ' + auth]
            for p in op.get('parameters', []) or []:
                info.append('**%s** (%s%s): %s' % (p['name'], p['in'], ', required' if p.get('required') else '', p.get('description', '')))
            if op.get('requestBody'):
                ref = json.dumps(op['requestBody'])
                sm = re.search(r'#/components/schemas/(\w+)', ref)
                ctype = list(op['requestBody'].get('content', {}).keys())
                info.append('**Body:** %s%s' % (', '.join(ctype), ' (schema %s)' % sm.group(1) if sm else ''))
            resp = op.get('responses', {}).get('200', {})
            if resp:
                info.append('**Returns:** ' + ', '.join(resp.get('content', {}).keys() or ['-']) + ' (' + resp.get('description', '') + ')')
            t = Table([[rows[0][0]], [body[0]], [Paragraph('<br/>'.join(inline(i) for i in info), ST['cell'])]],
                      colWidths=[170 * mm], hAlign='LEFT')
            t.setStyle(TableStyle([('BACKGROUND', (0, 0), (-1, 0), SOFT), ('BOX', (0, 0), (-1, -1), 0.5, GRID),
                                   ('LEFTPADDING', (0, 0), (-1, -1), 5), ('RIGHTPADDING', (0, 0), (-1, -1), 5),
                                   ('TOPPADDING', (0, 0), (-1, -1), 3), ('BOTTOMPADDING', (0, 0), (-1, -1), 3)]))
            x.append(KeepTogether([t, Spacer(1, 5)]))
    schemas = api.get('components', {}).get('schemas', {})
    if schemas:
        x.append(CondPageBreak(40 * mm))
        x.append(heading('Request and response objects', 1))
        for name, sc in schemas.items():
            props = sc.get('properties', {})
            rows = [['Field', 'Type', 'Description']]
            for k, v in props.items():
                typ = v.get('type', '')
                if isinstance(typ, list):
                    typ = ' | '.join(typ)
                if 'enum' in v:
                    typ += ': ' + ', '.join(map(str, v['enum']))
                rows.append([Paragraph('<font name="Mono" size="7.6">%s</font>' % escape(k), ST['cell']), str(typ), v.get('description', '')])
            x.append(heading(name, 3))
            if sc.get('description'):
                x.append(para(sc['description'], 'small'))
            if len(rows) > 1:
                x.append(table(rows, [38 * mm, 34 * mm, 98 * mm]))
            x.append(Spacer(1, 4))
    return x


def part_schema(schema):
    x = [PageBreak(), heading('Part V. Manifest reference', 0),
         para('Every field of manifest.json, from `GET /api/schema/manifest`. Required fields are marked. The rules in Part III '
              'decide what a valid value is.', 'lead')]
    req = set(schema.get('required', []))
    rows = [['Field', 'Type', 'Description']]
    for k, v in schema.get('properties', {}).items():
        typ = v.get('type', 'object' if 'properties' in v else '')
        if isinstance(typ, list):
            typ = ' | '.join(typ)
        name = '<font name="Mono" size="7.6">%s</font>%s' % (escape(k), '<br/><font color="#B3261E" size="7">required</font>' if k in req else '')
        desc = v.get('description', '')
        if 'pattern' in v:
            desc += ' Pattern: `%s`.' % v['pattern']
        if 'enum' in v:
            desc += ' Values: %s.' % ', '.join('`%s`' % e for e in v['enum'])
        rows.append([Paragraph(name, ST['cell']), str(typ), desc])
    x.append(table(rows, [40 * mm, 26 * mm, 104 * mm]))
    return x


def appendix(meta):
    x = [PageBreak(), heading('Appendix. Glossary', 0)]
    x.append(table([
        ['Term', 'Meaning'],
        ['.zxt', 'A Power PDF plug-in binary (a native Windows DLL built with the Plugin SDK).'],
        ['.ppak', 'An Add-on Store package: ZIP with manifest.json, binaries, UILayout, assets, licenses.'],
        ['Upload package', 'One ZIP with the .ppak and the source ZIP of the same version, for a single manual upload.'],
        ['Shared tab', 'The ribbon tab "Enhanced Features" (toolbar atom FeaturePack) where all public add-ons put their group.'],
        ['Atom namespace', 'The prefix of every ribbon atom of an add-on, e.g. FeaturePack::QuickNote.'],
        ['UILayout', 'The ribbon layout files (Publish Mode.xml, NameAndTitle.xml per language) the host merges.'],
        ['Delivery', 'A private add-on given to one customer, unlocked in the client with a customer code.'],
        ['House rule', 'A guideline a store adds to the standard rules; checked by reviewers or advisory.'],
        ['Mandatory warning', 'A warning a store treats as an error, enforced by the automatic check.'],
        ['Pre-flight checklist', 'Every hard rule as a list an assistant checks before packaging (Part II).'],
        ['Four-eyes rule', 'The person who uploaded a version may not approve it.'],
    ], [38 * mm, 132 * mm]))
    x.append(Spacer(1, 10))
    x.append(para(f"Generated on {meta['date']} by tools/make_manual.py from {meta['source']} (server {meta['server']}).", 'small'))
    return x



# ---------------------------------------------------------------- mandatory requirements (second document)
CHECK_RX = re.compile(r'\(([A-Z][A-Z0-9_]+(?:, [A-Z][A-Z0-9_]+)*)\)')

GROUP_WHY = {
    'A': 'The binary runs inside Power PDF with the rights of the user. It must be the Release build that was checked, '
         'self-contained, and licensed so that Tungsten may redistribute it.',
    'B': 'The manifest is the contract between the package, the catalog and the client: identity, version, texts in all '
         '16 languages, category and the legal declarations.',
    'C': 'Add-ons have to look and behave like part of Power PDF and must never break the ribbon of Power PDF or of another add-on.',
    'D': 'The package must unpack safely and completely on every Windows machine.',
    'E': 'Every version can be rebuilt, fixed and supported later; the store keeps the source code of each version for its admins.',
    'F': 'Requirements this store adds to the standard ones (maintained by the store admins under Settings, Rules).',
    'G': 'What a reviewer confirms for every version before it goes live; the approval is refused until each one is ticked. '
         'The store admins maintain the wording and add conditions under Settings, Rules.',
}


def checklist_groups(guide):
    """The pre-flight checklist of the live guide: [(letter, title, intro codes, [(text, codes, online)])]."""
    md = guide.replace('\r\n', '\n')
    a = md.index('## Pre-flight checklist')
    b = md.index('## Compliance audit', a)
    groups, cur = [], None
    lines = md[a:b].split('\n')
    i = 0
    while i < len(lines):
        line = lines[i]
        m = re.match(r'\*\*([A-G])\. (.+?)\*\*(.*)', line)
        if m:
            intro = m.group(3)
            j = i + 1
            while j < len(lines) and lines[j].strip() and not lines[j].lstrip().startswith('- ['):
                intro += ' ' + lines[j].strip(); j += 1
            cur = [m.group(1), m.group(2), CHECK_RX.findall(intro), []]
            groups.append(cur)
            i = j
            continue
        if line.lstrip().startswith('- [ ]') and cur is not None:
            buf = [line.split('- [ ]', 1)[1].strip()]
            i += 1
            while i < len(lines) and lines[i].startswith('      ') and not lines[i].lstrip().startswith('- ['):
                buf.append(lines[i].strip()); i += 1
            text = ' '.join(buf)
            codes = []
            for c in CHECK_RX.findall(text):
                codes += [x.strip() for x in c.split(',')]
            clean = re.sub(r'\s+([.;,])(?=\s|$)', r'\1', CHECK_RX.sub('', text)).strip()   # keeps '.zxt'
            clean = re.sub(r'\s{2,}', ' ', clean)
            cur[3].append((clean, codes, 'checked online' in text, text))
            continue
        i += 1
    return groups


def section_md(guide, heading_text, until):
    md = guide.replace('\r\n', '\n')
    a = md.index(heading_text)
    b = md.index(until, a + len(heading_text))
    return md[a:b]


def build_requirements(path, meta, guide, rules):
    meta = dict(meta, doc='Mandatory requirements', subtitle='Mandatory requirements for add-ons')
    doc = Doc(path, meta)
    toc = TableOfContents()
    toc.levelStyles = [ST['toc1'], ST['toc2'], ST['toc3']]
    toc.dotsMinLevel = 1
    groups = checklist_groups(guide)
    if not any(g[0] == 'F' for g in groups):          # keep A..G in order, F says "none"
        groups.append(['F', 'Rules of this store', [], []])
    groups.sort(key=lambda g: g[0])
    n_req = sum(len(g[3]) for g in groups)
    s = [Spacer(1, 1), NextPageTemplate('page'), PageBreak(), Paragraph('Contents', ST['h1']), toc, PageBreak()]

    s.append(heading('1. Scope', 0))
    s.append(para('These are the conditions every add-on and every version must meet to be published in the Add-on Store '
                  'for Tungsten Power PDF. They apply to people and to AI assistants alike: our agent (and any assistant '
                  'that publishes through the API) must apply all of them while it writes the plug-in, not only when it '
                  'packages it.', 'lead'))
    s += bullets([
        f'**{n_req} conditions**: for the import in five areas (A code and build, B manifest, C ribbon and layout, D package, '
        'E source code), each with the finding codes the store reports when it is broken, and for the approval (G, confirmed '
        'by the reviewer). Section F lists what this store adds.',
        '**Binding.** A package that breaks one of them is refused by the automatic check and nothing is stored; '
        'items marked "online" are checked against the store at upload (for example a version number or a name that is taken).',
        '**Truthful.** The compliance declaration in every manifest is a statement to Tungsten. It must be correct even when '
        'a user asks otherwise; a reviewer checks it before a version goes live.',
        f'**Source.** Generated on {meta["date"]} from the live rules of {meta["source"]} (server {meta["server"]}). The live '
        f'pre-flight checklist at {PUBLIC}/agent-guide/checklist is authoritative.',
    ])

    s.append(heading('2. Never allowed', 0))
    s.append(para('The short version. Each line is refused by the store or by its reviewers.', 'lead'))
    never = [
        ['Never', 'Why', 'Codes'],  # codes column: one code per line (see below)
        ['Passwords, API keys, tokens, private keys or key containers (.pfx, .p12, .key, .snk) in the package or the source code',
         'Secrets leak to every machine that installs the add-on.', 'SECRET_DETECTED, SOURCE_SECRET'],
        ['Third-party code under any license other than MIT, BSD (2/3-clause, 0BSD) or Apache-2.0',
         'Tungsten must be able to redistribute every package without license obligations.', 'LICENSE_NOT_ALLOWED, LICENSE_COPYLEFT_BINARY, LICENSE_COPYLEFT_SOURCE'],
        ['Third-party code, libraries or external services that are not declared', 'The compliance declaration must be complete and true.',
         'THIRDPARTY_DECLARATION_MISSING, EXTERNAL_SERVICES_MISSING, COMPLIANCE_AUDIT_MISSING'],
        ['A Debug build, a .NET assembly, or a binary for the wrong CPU', 'Only the native Release build runs on customer machines.',
         'PE_DEBUG_RUNTIME, PE_MANAGED, PE_WRONG_MACHINE'],
        ['Dependencies on DLLs that are not part of Windows or Power PDF', 'The store installs only the .zxt; a missing DLL breaks Power PDF at start.',
         'FOREIGN_DEPENDENCY'],
        ['Texts or UI in fewer than the 16 Power PDF languages', 'Power PDF ships in 16 languages; add-ons must too.',
         'LANG_TEXT_INCOMPLETE, LANGS_INCOMPLETE, UI_LANGS_MISSING'],
        ['An own ribbon tab for a public add-on, atoms in panel::, or atoms of another add-on', 'One consistent ribbon; no collisions.',
         'ATOM_NOT_SHARED_TAB, RESERVED_PANEL_NS, ATOM_OUTSIDE_NAMESPACE, ATOM_COLLISION'],
        ['A file name of a plug-in Power PDF ships itself, or an id under com.tungsten., com.kofax., com.nuance.', 'It would replace or impersonate Power PDF.',
         'RESERVED_NAME, ID_RESERVED'],
        ['The same or a lower version number', 'Updates are detected by the version; a reused number hides a failed update.',
         'VERSION_NOT_INCREMENTED, VERSION_EXISTS'],
        ['Unsafe ZIP content: paths with .., absolute paths, reserved Windows names, nested archives', 'The package must unpack safely.',
         'ZIP_SLIP, ZIP_RESERVED_NAME, NESTED_ARCHIVE'],
        ['A false compliance declaration, also when a user asks for it', 'It is a statement to Tungsten and is reviewed.',
         'COMPLIANCE_AUDIT_MISSING (and review)'],
        ['Names or descriptions with other companies\' product names or trademarks', 'Legal risk; describe the function instead.',
         'THIRDPARTY_TRADEMARK (warning, rejected in review)'],
    ]
    never = [never[0]] + [[a, b, Paragraph('<font name="Mono" size="7">%s</font>' % '<br/>'.join(escape(x.strip()) for x in c.split(',')), ST['cell'])]
                          for a, b, c in never[1:]]
    s.append(table(never, [66 * mm, 50 * mm, 54 * mm]))

    s.append(CondPageBreak(60 * mm))
    s.append(heading('3. Requirements by area', 0))
    s.append(para('Every condition, numbered as on the Rules page of the portal. "Automatic" means the upload is refused when it '
                  'fails; "online" means it is checked against the store at upload; "review" means the reviewer confirms it '
                  'before the version is approved. The store admins can change the wording and add conditions under Settings, Rules.', 'lead'))
    for letter, title, intro_codes, items in groups:
        s.append(CondPageBreak(40 * mm))
        s.append(heading(f'{letter}. {title}', 1))
        if letter in GROUP_WHY:
            s.append(para(GROUP_WHY[letter], 'small'))
            s.append(Spacer(1, 3))
        if intro_codes:
            s.append(para('Applies to the whole area: ' + ', '.join(intro_codes) + '.', 'small'))
        if not items:
            s.append(para('This store currently adds no requirements of its own. Admins add them under Settings, Rules; '
                          'they then appear here and in the live checklist.', 'small'))
            continue
        rows = [['ID', 'Requirement', 'Checked', 'Codes']]
        for k, (text, codes, online, raw) in enumerate(items, 1):
            if letter in ('F', 'G'):
                how = 'Review' if 'checked by the reviewer' in raw else 'Automatic' if 'Mandatory here' in raw else 'Advice'
            else:
                how = 'Automatic, online' if online else 'Automatic'
            rows.append([f'{letter}{k}', text, how,
                         Paragraph('<font name="Mono" size="7">%s</font>' % '<br/>'.join(escape(c) for c in codes), ST['cell'])])
        s.append(table(rows, [11 * mm, 99 * mm, 20 * mm, 40 * mm]))
        s.append(Spacer(1, 6))

    s.append(PageBreak())
    s.append(heading('4. Licenses and secrets in detail', 0))
    s.append(heading('Third-party licenses', 1))
    s.append(table([
        ['Category', 'Licenses', 'Result'],
        ['Allowed', 'MIT, BSD-2-Clause, BSD-3-Clause, 0BSD, Apache-2.0', 'Accepted (declare the component in thirdParty and ship its text in LICENSES.md)'],
        ['Other permissive', 'For example Zlib, libpng, IJG, curl, OpenSSL (Apache-2.0 from 3.0)', 'Warning LICENSE_NEEDS_REVIEW; a reviewer decides'],
        ['Copyleft, declared', 'GPL, AGPL, LGPL, MPL, EPL, CDDL, EUPL, OSL, SSPL, CC-BY-SA, CC-BY-NC', 'Refused: LICENSE_NOT_ALLOWED'],
        ['GPL/AGPL found in files', 'License texts, SPDX tags or known libraries (MuPDF, Ghostscript, Poppler, Xpdf)', 'Refused: LICENSE_COPYLEFT_BINARY / LICENSE_COPYLEFT_SOURCE'],
        ['Weak copyleft found', 'LGPL code, FFmpeg, UnRAR', 'Warning; rejected in review unless it is a false positive'],
    ], [32 * mm, 66 * mm, 72 * mm]))
    s.append(Spacer(1, 6))
    s.append(heading('Secrets the check finds', 1))
    s.append(para('Text and binary files of the package and of the source ZIP are searched for these patterns; any hit refuses the upload. '
                  'Credentials are loaded at run time instead (per user, DPAPI-protected), never shipped.'))
    s += bullets(['Private keys (`-----BEGIN ... PRIVATE KEY-----`)', 'Add-on Store API tokens (`ppak_...`)',
                  'AWS access keys, Google API keys, GitHub, Slack and Azure storage keys', 'AI service API keys (`sk-...`), Stripe live keys',
                  'Key container files: `.pfx`, `.p12`, `.key`, `.snk`'])
    s.append(heading('5. Declarations in detail', 0))
    s += render_markdown(section_md(guide, '## Compliance audit', '## Categories'))
    s += render_markdown(section_md(guide, '## Source code (mandatory)', '## Changing an existing add-on'))
    s += render_markdown(section_md(guide, '## Languages (mandatory)', '## Ribbon governance'))
    s.append(heading('6. Enforcement', 0))
    s += bullets([
        '**Automatic check** on every upload and dry run (`POST /api/packages/validate`). An error stores nothing; each finding has a hint that says what to change.',
        '**Review** before a version goes live: house rules, the compliance declaration, warnings and the package as a whole. '
        'With the four-eyes rule the uploader cannot approve their own version.',
        '**After publication** a version can be withdrawn or set back to beta, and the add-on can be switched off in the store.',
        '**On every machine** the Power PDF client installs only packages signed by a trusted store key whose SHA-256 matches, '
        'and only on Power PDF versions and editions the Plugin SDK supports.',
    ])
    s.append(Spacer(1, 6))
    s.append(para(f'Generated on {meta["date"]} by tools/make_manual.py from {meta["source"]} (server {meta["server"]}).', 'small'))
    doc.multiBuild(s)
    return n_req

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--base', default='http://localhost:5191')
    ap.add_argument('--out', default=os.path.join(ROOT, 'docs', 'manual'))
    a = ap.parse_args()
    base = a.base.rstrip('/')
    guide = fetch(base, '/api/agent-guide').replace(base, PUBLIC)
    rules = json.loads(fetch(base, '/api/rules'))['data']
    api = json.loads(fetch(base, '/api/openapi.json').replace(base, PUBLIC))
    schema = json.loads(fetch(base, '/api/schema/manifest'))
    server = json.loads(fetch(base, '/api/ping'))['data']['version']
    vh = open(os.path.join(ROOT, 'client', 'common', 'version.h'), encoding='utf-8').read()
    client = re.search(r'FP_VERSION_A\s+"([\d.]+)"', vh).group(1)
    d = datetime.date.today()
    meta = {'server': server, 'client': client, 'date': d.strftime('%b ') + str(d.day) + d.strftime(', %Y'),
            'source': PUBLIC if base.startswith('http://localhost') else base}

    os.makedirs(a.out, exist_ok=True)
    path = os.path.join(a.out, f'AddonStore-Manual-{server}.pdf')
    doc = Doc(path, meta)
    toc = TableOfContents()
    toc.levelStyles = [ST['toc1'], ST['toc2'], ST['toc3']]
    toc.dotsMinLevel = 1
    story = [Spacer(1, 1), NextPageTemplate('page'), PageBreak(),
             Paragraph('Contents', ST['h1']), toc, PageBreak()]
    story += part_intro(meta, rules)
    story += [PageBreak(), heading('Part II. Developer and agent guide', 0),
              para('The complete guide as AI assistants and developers read it at /api/agent-guide (the rule reference is '
                   'in Part III). Section F of the pre-flight checklist lists the rules this store adds.', 'lead')]
    story += render_markdown(guide, skip_sections=('Complete rule reference',))
    story += part_rules(rules)
    story += part_api(api)
    story += part_schema(schema)
    story += appendix(meta)
    doc.multiBuild(story)
    print(path, os.path.getsize(path) // 1024, 'KB')

    req = os.path.join(a.out, f'AddonStore-Requirements-{server}.pdf')
    n = build_requirements(req, meta, guide, rules)
    print(req, os.path.getsize(req) // 1024, 'KB', n, 'requirements')

    # stable names for the download on the website's API page
    web = os.path.join(ROOT, 'server', 'src', 'AddonStore.Web', 'wwwroot', 'docs')
    os.makedirs(web, exist_ok=True)
    import shutil
    shutil.copyfile(path, os.path.join(web, 'AddonStore-Manual.pdf'))
    shutil.copyfile(req, os.path.join(web, 'AddonStore-Requirements.pdf'))
    print('copied to', web)


if __name__ == '__main__':
    sys.exit(main())
