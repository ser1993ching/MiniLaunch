import sys
from PIL import Image

path = sys.argv[1] if len(sys.argv) > 1 else r"C:\temp\ml-6.png"
im = Image.open(path).convert("RGB")
W, H = im.size
px = im.load()


def bg(y):
    return px[W - 6, y]


def diff(c, b):
    return abs(c[0] - b[0]) + abs(c[1] - b[1]) + abs(c[2] - b[2])


list_top, row_h, rows = 88, 54, 8
print("size", W, H)
print("row | iconBox x1-x2 / y1-y2 (h) | textX | textTop-textBot")
for i in range(rows):
    top = list_top + i * row_h
    bot = top + row_h - 1
    ix1, ix2, iy1, iy2 = 9999, -1, 9999, -1
    tx, ty1, ty2 = 9999, 9999, -1
    for y in range(top, bot + 1):
        b = bg(y)
        for x in range(24, 67):
            if diff(px[x, y], b) > 30:
                ix1, ix2 = min(ix1, x), max(ix2, x)
                iy1, iy2 = min(iy1, y), max(iy2, y)
        for x in range(70, 691):
            c = px[x, y]
            if sum(c) > 330 and diff(c, b) > 40:
                tx = min(tx, x)
                ty1, ty2 = min(ty1, y), max(ty2, y)
    print(f"{i+1} | ({ix1}-{ix2}, {iy1}-{iy2}) h={iy2-iy1+1} dy_top={iy1-top} | {tx} | {ty1}-{ty2}")
