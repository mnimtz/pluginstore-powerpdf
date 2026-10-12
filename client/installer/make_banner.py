#!/usr/bin/env python3
"""make_banner.py - the banner of the setup dialogs (C1.9.13): Tungsten navy with the brand
gradient line (Brand Book: navy #002854, PDF gradient #00EB86 -> #00A0FB). Our own image; the
title is drawn by the dialog. Writes installer/banner.bmp (493 x 58, 24 bit).

    python installer/make_banner.py
"""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
W, H = 493, 58
NAVY = (0x00, 0x28, 0x54)
G0, G1 = (0x00, 0xEB, 0x86), (0x00, 0xA0, 0xFB)

img = Image.new('RGB', (W, H), NAVY)
px = img.load()
for x in range(W):
    t = x / (W - 1)
    c = tuple(round(G0[i] + (G1[i] - G0[i]) * t) for i in range(3))
    for y in range(H - 3, H):
        px[x, y] = c
img.save(os.path.join(HERE, 'banner.bmp'), 'BMP')
print('banner.bmp written')
