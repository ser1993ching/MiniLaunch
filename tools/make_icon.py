# -*- coding: utf-8 -*-
"""
MiniLaunch 应用图标生成器（离线脚本，不参与程序构建）。

设计：深色圆角方块（沿用主窗口配色 #33353F -> #1C1D22）+ 亮蓝「呼出」箭头（#7C9CFF -> #5B7CF6）。
输出：
  MiniLaunch/app.ico    多尺寸图标（16/20/24/32/40/48/64/96/128 用 32 位 BMP，256 用 PNG）
  tools/icon-preview.png 预览图（256px）

用法：python tools/make_icon.py
依赖：Pillow（仅开发机需要）
"""
import math
import os
import struct
import zlib
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ICO_PATH = os.path.join(ROOT, "MiniLaunch", "app.ico")
PREVIEW_PATH = os.path.join(ROOT, "tools", "icon-preview.png")

S = 2048  # 作画画布（输出最大 256px，8 倍超采样）

BG_TOP = (0x33, 0x35, 0x3F)
BG_BOT = (0x1C, 0x1D, 0x22)
BORDER = (0x3E, 0x42, 0x51)
ACC_TOP = (0x7C, 0x9C, 0xFF)
ACC_BOT = (0x5B, 0x7C, 0xF6)

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def vgradient(size, top, bottom):
    """竖向线性渐变 RGBA 图。"""
    col = Image.new("RGBA", (1, size))
    px = col.load()
    for y in range(size):
        t = y / max(1, size - 1)
        px[0, y] = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
    return col.resize((size, size), Image.BILINEAR)


def make_tile():
    """深色圆角方块底板。"""
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    mask = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(mask)
    margin = int(S * 0.045)
    radius = int((S - 2 * margin) * 0.235)
    d.rounded_rectangle([margin, margin, S - margin, S - margin], radius=radius, fill=255)
    img.paste(vgradient(S, BG_TOP, BG_BOT), (0, 0), mask)

    edge = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    de = ImageDraw.Draw(edge)
    de.rounded_rectangle([margin, margin, S - margin, S - margin], radius=radius,
                         outline=BORDER + (255,), width=max(2, int(S * 0.0035)))
    img.alpha_composite(edge)
    return img


def make_arrow_mask():
    """右上方向「呼出」箭头：三段圆头笔画拼成，缩到 16px 仍可辨。"""
    m = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(m)
    u = float(S)
    th = 0.128 * u          # 笔画粗细
    r = th / 2.0            # 圆头半径

    tip = (0.775 * u, 0.225 * u)
    arm_h = (0.395 * u, 0.225 * u)   # 箭头左臂（水平）
    arm_v = (0.775 * u, 0.605 * u)   # 箭头下臂（垂直）
    shaft_a = (0.235 * u, 0.765 * u)  # 杆起点
    shaft_b = (0.640 * u, 0.360 * u)  # 杆终点

    def stroke(p0, p1):
        d.line([p0, p1], fill=255, width=int(th))
        for p in (p0, p1):
            d.ellipse([p[0] - r, p[1] - r, p[0] + r, p[1] + r], fill=255)

    stroke(shaft_a, shaft_b)
    stroke(arm_h, tip)
    stroke(tip, arm_v)
    return m


def make_icon():
    tile = make_tile()
    arrow = make_arrow_mask()

    # 箭头外发光（弱，模拟主题高光）
    glow = Image.new("RGBA", (S, S), ACC_TOP + (0,))
    glow.putalpha(arrow.filter(ImageFilter.GaussianBlur(S * 0.012)).point(lambda v: int(v * 0.42)))
    tile.alpha_composite(glow)

    grad = vgradient(S, ACC_TOP, ACC_BOT)
    grad.putalpha(arrow)
    tile.alpha_composite(grad)
    return tile


def ico_bmp_frame(img):
    """32 位 BMP 图标帧（BGRA + AND 掩码），ICO 内为自下而上的位图。"""
    w, h = img.size
    data = img.convert("RGBA").tobytes()
    rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        base = y * w * 4
        for x in range(w):
            r, g, b, a = data[base + x * 4: base + x * 4 + 4]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    xor = b"".join(rows)
    mask_row = ((w + 31) // 32) * 4
    and_mask = bytes(mask_row * h)  # 32 位帧用 alpha，AND 全 0
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(and_mask), 0, 0, 0, 0)
    return header + xor + and_mask


def png_bytes(img):
    import io
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def build_ico(master, sizes=None, png256=False):
    sizes = sizes or SIZES
    frames = []
    for size in sizes:
        img = master.resize((size, size), Image.LANCZOS)
        if size >= 256 and png256:
            frames.append(png_bytes(img))
        else:
            frames.append(ico_bmp_frame(img))

    count = len(frames)
    out = bytearray(struct.pack("<HHH", 0, 1, count))
    offset = 6 + 16 * count
    for size, data in zip(sizes, frames):
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for data in frames:
        out += data
    return bytes(out)


def main():
    master = make_icon()
    master.resize((256, 256), Image.LANCZOS).save(PREVIEW_PATH)
    data = build_ico(master, png256=True)
    with open(ICO_PATH, "wb") as f:
        f.write(data)
    print("ICO  :", ICO_PATH, f"{len(data)} bytes")
    print("预览 :", PREVIEW_PATH)

    # 小尺寸可读性检查图：各尺寸放大后并排
    sheet = Image.new("RGBA", (640, 200), (250, 250, 252, 255))
    x = 16
    for size in (16, 20, 24, 32, 48, 64):
        small = master.resize((size, size), Image.LANCZOS)
        big = small.resize((size * 2, size * 2), Image.NEAREST)
        sheet.alpha_composite(big, (x, 40))
        x += size * 2 + 24
    sheet.save(os.path.join(ROOT, "tools", "icon-sizes.png"))
    print("尺寸 :", os.path.join(ROOT, "tools", "icon-sizes.png"))


if __name__ == "__main__":
    main()
