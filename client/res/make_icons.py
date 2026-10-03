# make_icons.py - regenerates the ribbon button bitmaps (24-bit opaque BMP,
# per the ribbon icon rule; panel strips would need 32-bit alpha instead).
# The repository stores this script instead of the binary BMPs; run it once
# after cloning:  python make_icons.py   (requires Pillow)
from PIL import Image, ImageDraw


def draw(size):
    img = Image.new('RGB', (size, size), (255, 255, 255))
    d = ImageDraw.Draw(img)
    navy = (0, 40, 84)        # Tungsten primary #002854
    green = (0, 235, 134)     # PDF & eSignature accent #00EB86
    blue = (0, 160, 251)      # accent blue #00A0FB
    s = size / 32.0

    def S(v):
        return int(round(v * s))

    for i in range(4):  # store awning, alternating navy/blue
        color = navy if i % 2 == 0 else blue
        d.rectangle([S(3 + i * 6.5), S(4), S(3 + (i + 1) * 6.5), S(11)], fill=color)
    d.rectangle([S(5), S(11), S(27), S(28)], outline=navy, width=max(1, S(2)))
    d.rectangle([S(9), S(17), S(15), S(28)], fill=navy)
    d.rectangle([S(20), S(14), S(23), S(21)], fill=green)   # install arrow
    d.polygon([(S(17.5), S(20)), (S(25.5), S(20)), (S(21.5), S(25))], fill=green)
    return img


if __name__ == '__main__':
    draw(32).save('icon_store32.bmp')
    draw(16).save('icon_store16.bmp')
    print('icon_store32.bmp / icon_store16.bmp written')
