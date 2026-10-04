# make_icons.py - renders the Plugin-Store icon in every size we ship.
#
#   icon_store32(_upd).bmp / icon_store16(_upd).bmp  ribbon (_upd: with update badge)
#                                         (24-bit, OPAQUE: Power PDF needs
#                                        that; the background is the ribbon's own
#                                        colour #F1F2F4 so the icon looks transparent)
#   ../../packaging/icon.png             128 px catalog icon, real alpha
#
# Motif: shopping bag in the PDF & eSignature gradient (#00EB86 -> #00A0FB) with
# a navy handle and a white download arrow. Drawn here (no third-party asset),
# supersampled 8x and downscaled for smooth edges. Requires Pillow.
import os
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
RIBBON_BG = (241, 242, 244)
NAVY = (0, 40, 84)
GREEN = (0, 235, 134)
BLUE = (0, 160, 251)
WHITE = (255, 255, 255)
AMBER = (255, 198, 0)


def render(size, badge=False):
    ss = 8
    S = size * ss
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0))

    def u(v):  # 0..32 design grid -> pixels
        return int(round(v * S / 32.0))

    # bag body with vertical-diagonal gradient
    body = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    grad = Image.new('RGBA', (S, S))
    gp = grad.load()
    for y in range(S):
        for x in range(S):
            t = min(1.0, max(0.0, (x * 0.35 + y * 0.65) / S))
            gp[x, y] = (int(GREEN[0] + (BLUE[0] - GREEN[0]) * t),
                        int(GREEN[1] + (BLUE[1] - GREEN[1]) * t),
                        int(GREEN[2] + (BLUE[2] - GREEN[2]) * t), 255)
    mask = Image.new('L', (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([u(5), u(10), u(27), u(29)], radius=u(3.2), fill=255)
    body.paste(grad, (0, 0), mask)
    img = Image.alpha_composite(img, body)

    d = ImageDraw.Draw(img)
    # handle: navy arc above the bag
    w = max(1, u(2.2))
    d.arc([u(10), u(3), u(22), u(15)], start=180, end=360, fill=NAVY + (255,), width=w)
    d.line([u(10), u(9), u(10), u(11.5)], fill=NAVY + (255,), width=w)
    d.line([u(22) - w + 1, u(9), u(22) - w + 1, u(11.5)], fill=NAVY + (255,), width=w)
    # download arrow
    d.rectangle([u(14.6), u(14), u(17.4), u(21)], fill=WHITE + (255,))
    d.polygon([(u(11.2), u(19.5)), (u(20.8), u(19.5)), (u(16), u(25))], fill=WHITE + (255,))

    if badge:
        # update badge: amber dot with a white ring, top right (C0.6.0)
        d.ellipse([u(19.5), u(0.5), u(31.5), u(12.5)], fill=WHITE + (255,))
        d.ellipse([u(21.5), u(2.5), u(29.5), u(10.5)], fill=AMBER + (255,))
    return img.resize((size, size), Image.LANCZOS)


def on_ribbon(img):
    bg = Image.new('RGBA', img.size, RIBBON_BG + (255,))
    return Image.alpha_composite(bg, img).convert('RGB')


if __name__ == '__main__':
    on_ribbon(render(32)).save(os.path.join(HERE, 'icon_store32.bmp'))
    on_ribbon(render(16)).save(os.path.join(HERE, 'icon_store16.bmp'))
    on_ribbon(render(32, True)).save(os.path.join(HERE, 'icon_store32_upd.bmp'))
    on_ribbon(render(16, True)).save(os.path.join(HERE, 'icon_store16_upd.bmp'))
    root = os.path.normpath(os.path.join(HERE, '..', '..'))
    render(128).save(os.path.join(root, 'packaging', 'icon.png'))
    print('icons written')
