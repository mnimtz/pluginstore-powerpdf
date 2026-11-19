#!/usr/bin/env python3
"""make_banner.py - the banner of the setup dialogs (C2.0.0, Brand Book November 2026): white
with the original Tungsten Automation logo 2026 (blue PNG from the brand package, only scaled)
on the right. The title is drawn by the dialog in navy on the left. Writes installer/banner.bmp
(493 x 58, 24 bit).

    python installer/make_banner.py [path to TungstenAutomationLogoBlue.png]
"""
import os
import sys
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = sys.argv[1] if len(sys.argv) > 1 else (
    r'C:\Claude\Tungsten-Branding\Logos & Lockups\Tungsten Logos'
    r'\Tungsten Automation Logo 2026\PNG\TungstenAutomationLogoBlue.png')
W, H = 493, 58
LOGO_H, MARGIN = 24, 16        # the title ends at about 300 px in the longest language (fr-FR)

logo = Image.open(SRC).convert('RGBA')
lw = round(logo.width * LOGO_H / logo.height)
logo = logo.resize((lw, LOGO_H), Image.LANCZOS)
img = Image.new('RGBA', (W, H), (255, 255, 255, 255))
img.alpha_composite(logo, (W - MARGIN - lw, (H - LOGO_H) // 2))
img.convert('RGB').save(os.path.join(HERE, 'banner.bmp'), 'BMP')
print('banner.bmp written (logo %dx%d)' % (lw, LOGO_H))
