# -*- coding: utf-8 -*-
"""
从 app_source.jpg 生成 app.ico（多尺寸）。

原图是 1920x1920 的 P5R 标志，右下角有「豆包AI生成」水印，需要先抹掉再裁切。
裁两套：
  · full  : 120,180 - 1860,1920  整个标志（P5 + R + 星星），用于 32px 及以上
  · tight : 300,690 - 1580,1920  聚焦 P5 白字，用于 16/20/24px（小尺寸笔画保不住，放大主体更可读）

用法：python make_icon.py
"""
import os
import struct
from PIL import Image, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "app_source.jpg")
ICO = os.path.join(HERE, "app.ico")

FULL = (120, 180, 1860, 1920)     # 1740 见方
TIGHT = (300, 690, 1580, 1920)    # 1280 x 1230，缩到正方形时裁掉底部 50px


def clean_watermark(im):
    """右下角水印压在纯红渐变上：按列在上下两侧取色做线性插值填掉。"""
    im = im.convert("RGB")
    px = im.load()
    w, h = im.size
    x0, x1, y0, y1 = 1540, w - 1, 1785, 1900
    for x in range(x0, x1 + 1):
        c1 = px[x, y0 - 6]
        c2 = px[x, min(h - 1, y1 + 8)]
        for y in range(y0, y1 + 1):
            t = (y - y0) / (y1 - y0)
            px[x, y] = tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(3))
    return im


def square(im, box):
    """裁成正方形（宽高不等时以中心为准，多出来的部分从底部裁）。"""
    crop = im.crop(box)
    w, h = crop.size
    if w != h:
        side = min(w, h)
        left = (w - side) // 2
        top = h - side                     # 底部对齐：保住星星
        crop = crop.crop((left, top, left + side, top + side))
    return crop


def render(master, size, sharpen):
    im = master.resize((size, size), Image.LANCZOS)
    if sharpen and size <= 48:
        im = im.filter(ImageFilter.UnsharpMask(radius=0.7, percent=55, threshold=2))
    return im.convert("RGBA")


def ico_bytes(frames):
    """按 ICO 规范写 32 位 DIB 帧（比 PNG 帧兼容性好，资源管理器/GDI+ 都认）。"""
    blobs = []
    for size, im in frames:
        px = im.load()
        hdr = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
        body = bytearray()
        for y in range(size - 1, -1, -1):          # DIB 自下而上
            for x in range(size):
                r, g, b, a = px[x, y]
                body += bytes((b, g, r, a))
        row_bytes = ((size + 31) // 32) * 4        # AND 掩膜，1bpp，行 4 字节对齐
        mask = bytearray()
        for y in range(size - 1, -1, -1):
            bits = bytearray(row_bytes)
            for x in range(size):
                if px[x, y][3] < 128:
                    bits[x // 8] |= 0x80 >> (x % 8)
            mask += bits
        blobs.append(hdr + bytes(body) + bytes(mask))

    n = len(frames)
    offset = 6 + 16 * n
    out = bytearray(struct.pack("<HHH", 0, 1, n))
    for (size, _), blob in zip(frames, blobs):
        wh = 0 if size >= 256 else size
        out += struct.pack("<BBBBHHII", wh, wh, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    for blob in blobs:
        out += blob
    return bytes(out)


def main():
    src = clean_watermark(Image.open(SRC))
    full = square(src, FULL)
    tight = square(src, TIGHT)

    frames = []
    for size in (16, 20, 24, 32, 40, 48, 64, 128, 256):
        src_img = tight if size <= 24 else full
        frames.append((size, render(src_img, size, sharpen=True)))

    with open(ICO, "wb") as f:
        f.write(ico_bytes(frames))
    print("app.ico", os.path.getsize(ICO), "bytes,", len(frames), "frames")


if __name__ == "__main__":
    main()
