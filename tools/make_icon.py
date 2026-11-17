# -*- coding: utf-8 -*-
"""
MiniLaunch 应用图标生成器（离线脚本，不参与程序构建）。

图标源（美术在 newIcon/ 下维护，本脚本只做「取源 + 抠底 + 打包」）：
  newIcon/MiniLaunch-icon-final.svg   1024x1024 母版（内嵌 PNG base64）
  newIcon/MiniLaunch-icon-final.ico   同源的多尺寸 ICO（各帧均为母版的 LANCZOS 缩放）
两个源任选其一即可：优先读 SVG 的内嵌 PNG，退化时读 ico 里的 256 帧。

注意：母版是「不透明」的 —— 底层是一整块方形近黑蓝画布（#010514），上面才是圆角的深蓝 tile。
直接缩放会得到方形黑底，所以这里先**自动抠掉方形底色**：
  1) 检测 tile 包围盒 + 圆角半径
  2) 用圆角矩形算覆盖率 c（4x 超采样）
  3) 母版是画在平底色上的：观测色 = 底色*(1-c) + tile色*c  →  用 c 反解 tile 真色，消掉边缘黑边
  4) 裁到 tile 边界（满幅方案 B），再放进正方形画布（tile 837x824，竖向居中补透明）

输出：
  MiniLaunch/app.ico    多尺寸图标（16/20/24/32/40/48/64/96/128 用 32 位 BMP，256 用 PNG）
  tools/icon-preview.png 预览图（256px）
  tools/icon-sizes.png   各尺寸放大并排（检查小尺寸可读性）

用法：python tools/make_icon.py
依赖：Pillow + numpy（仅开发机需要）
"""
import base64
import io
import os
import re
import struct
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ICO_PATH = os.path.join(ROOT, "MiniLaunch", "app.ico")
PREVIEW_PATH = os.path.join(ROOT, "tools", "icon-preview.png")
SIZES_PATH = os.path.join(ROOT, "tools", "icon-sizes.png")

SRC_SVG = os.path.join(ROOT, "newIcon", "MiniLaunch-icon-final.svg")
SRC_ICO = os.path.join(ROOT, "newIcon", "MiniLaunch-icon-final.ico")

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def load_raw():
    """读取 1024 母版（RGBA，不透明）。"""
    if os.path.exists(SRC_SVG):
        with open(SRC_SVG, "r", encoding="utf-8") as f:
            svg = f.read()
        m = re.search(r'xlink:href="data:image/png;base64,([^"]+)"', svg)
        if not m:
            raise SystemExit("SVG 里找不到内嵌 PNG：" + SRC_SVG)
        img = Image.open(io.BytesIO(base64.b64decode(m.group(1))))
        return img.convert("RGBA")

    if os.path.exists(SRC_ICO):
        im = Image.open(SRC_ICO)
        img = Image.open(io.BytesIO(im.ico.getimage((256, 256)).tobytes()))
        return img.convert("RGBA")

    raise SystemExit("找不到图标源：%s 或 %s" % (SRC_SVG, SRC_ICO))


def detect_geometry(a):
    """检测 tile 包围盒与圆角半径（母版 1024 坐标系）。"""
    rgb = a[:, :, :3].astype(int)
    mask = rgb[:, :, 2] > 32          # 底色平坦近黑，tile 的蓝通道明显更高
    ys, xs = np.nonzero(mask)
    x0, x1, y0, y1 = int(xs.min()), int(xs.max()), int(ys.min()), int(ys.max())

    # 左上角「内缩量 vs 深度」拟合圆角：inset = r - sqrt(2*r*d - d*d)
    samples = []
    for d in (6, 12, 20, 30, 45, 60):
        row = np.nonzero(mask[y0 + d])[0]
        if len(row):
            samples.append((d, int(row.min()) - x0))
    best = None
    for r in range(60, 320):
        err = sum((r - np.sqrt(max(0.0, 2 * r * d - d * d)) - inset) ** 2 for d, inset in samples)
        if best is None or err < best[1]:
            best = (r, err)
    return x0, x1, y0, y1, best[0]


def coverage_mask(size, box, radius, ss=4):
    """超采样圆角矩形覆盖率（0~1）。"""
    w, h = size
    m = Image.new("L", (w * ss, h * ss), 0)
    d = ImageDraw.Draw(m)
    x0, x1, y0, y1 = box
    d.rounded_rectangle([x0 * ss, y0 * ss, (x1 + 1) * ss - 1, (y1 + 1) * ss - 1],
                        radius=int(radius * ss), fill=255)
    return np.asarray(m.resize((w, h), Image.Resampling.BOX)).astype(float) / 255.0


def cutout(master):
    """抠掉方形底色，返回满幅（裁到 tile 边界）的正方形 RGBA 母版。"""
    w, h = master.size
    a = np.asarray(master).astype(float)
    bg = np.median(a[0:12, 0:12, :3].reshape(-1, 3), axis=0)   # 底色估计

    box = detect_geometry(a.astype(int))
    x0, x1, y0, y1, radius = box
    print("tile bbox:", box[:4], "radius:", radius)

    cov = np.clip(coverage_mask((w, h), (x0, x1, y0, y1), radius), 0.0, 1.0)
    rgb = a[:, :, :3]
    c = np.clip(cov, 1e-3, 1.0)[:, :, None]
    tile = np.clip((rgb - bg[None, None, :] * (1 - c)) / c, 0, 255)   # 反解真实 tile 色
    inside = (cov >= 0.999)[:, :, None]
    tile = np.where(inside, rgb, tile)
    med = np.asarray(Image.fromarray(tile.astype(np.uint8)).convert("RGB")
                     .filter(ImageFilter.MedianFilter(3))).astype(float)
    tile = np.where(inside, rgb, med)                                # 边缘去噪

    out = Image.fromarray(np.dstack([tile, cov * 255.0]).astype(np.uint8), "RGBA")

    # 裁到 tile 边界（满幅），再放进正方形画布（保持比例，透明补边）
    tile_img = out.crop((x0, y0, x1 + 1, y1 + 1))
    tw, th = tile_img.size
    side = max(tw, th)
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.alpha_composite(tile_img, ((side - tw) // 2, (side - th) // 2))
    return canvas


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
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def build_ico(master, sizes=None, png256=True):
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
    master = cutout(load_raw())
    print("母版:", master.size, "（已抠掉方形底色）")
    master.resize((256, 256), Image.LANCZOS).save(PREVIEW_PATH)
    data = build_ico(master, png256=True)
    with open(ICO_PATH, "wb") as f:
        f.write(data)
    print("ICO  :", ICO_PATH, f"{len(data)} bytes")
    print("预览 :", PREVIEW_PATH)

    # 小尺寸可读性检查图：各尺寸放大后并排（浅底 + 深底两行，看边缘是否干净）
    sheet = Image.new("RGBA", (740, 460), (248, 248, 250, 255))
    for row, bgc in ((0, (248, 248, 250, 255)), (1, (40, 43, 50, 255))):
        x = 20
        for size in (16, 20, 24, 32, 48, 64, 96, 128):
            small = master.resize((size, size), Image.LANCZOS)
            big = small.resize((size * 2, size * 2), Image.NEAREST)
            cell = Image.new("RGBA", big.size, bgc)
            cell.alpha_composite(big)
            sheet.alpha_composite(cell, (x, 30 + row * 210))
            x += size * 2 + 20
    sheet.save(SIZES_PATH)
    print("尺寸 :", SIZES_PATH)


if __name__ == "__main__":
    main()
