# -*- coding: utf-8 -*-
"""生成 Lynx（灵犀）品牌图标：猞猁耳 + 双摇杆眼。

母版 1024px、4 倍超采样抗锯齿；输出 assets/icon/padshift-256.png 与混合格式 padshift.ico
（256 帧为 PNG、小帧为经典 32bpp DIB —— GDI/WPF/资源管理器全兼容）。文件名保留：
csproj 与测试按路径引用，品牌更名不迁移资产路径。
"""
import io
import os
import struct

from PIL import Image, ImageDraw

S = 4096


def k(v):
    return int(round(v * (S / 1024.0)))


top, bottom = (27, 44, 71), (10, 14, 21)
grad = Image.new('RGB', (2, S))
for y in range(S):
    t = y / (S - 1)
    grad.putpixel((0, y), tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    grad.putpixel((1, y), grad.getpixel((0, y)))
grad = grad.resize((S, S))
mask = Image.new('L', (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=k(232), fill=255)
img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
img.paste(grad, (0, 0), mask)
d = ImageDraw.Draw(img)

ACCENT = (111, 182, 255, 255)
EAR = (51, 80, 122, 255)
EAR_INNER = (16, 25, 40, 255)
GREEN = (63, 161, 127, 255)
LIGHT = (232, 242, 255, 255)
DARK = (10, 14, 21, 255)


def ear(cx, tip_x):
    d.polygon([(k(cx - 150), k(360)), (k(tip_x), k(96)), (k(cx + 118), k(288))], fill=EAR)
    d.polygon([(k(cx - 74), k(330)), (k(tip_x + (6 if tip_x > cx else -6)), k(168)), (k(cx + 52), k(292))], fill=EAR_INNER)


def stick_eye(cx, cy):
    r = k(150)
    d.ellipse([k(cx) - r, k(cy) - r, k(cx) + r, k(cy) + r], outline=ACCENT, width=k(34))
    ri = k(108)
    d.ellipse([k(cx) - ri, k(cy) - ri, k(cx) + ri, k(cy) + ri], fill=DARK)
    rp = k(50)
    d.ellipse([k(cx) - rp, k(cy) - rp, k(cx) + rp, k(cy) + rp], fill=LIGHT)


def build():
    ear(300, 176)
    ear(724, 848)
    stick_eye(330, 596)
    stick_eye(694, 596)
    cx, cy, r = 512, 828, 72
    d.polygon([(k(cx), k(cy - r)), (k(cx + r), k(cy)), (k(cx), k(cy + r)), (k(cx - r), k(cy))], fill=GREEN)
    return img.resize((1024, 1024), Image.LANCZOS)


def frames_of(master, sizes):
    frames = []
    for s in sizes:
        im = master.resize((s, s), Image.LANCZOS)
        if s == 256:
            buf = io.BytesIO()
            im.save(buf, 'PNG')
            data = buf.getvalue()
        else:
            buf = io.BytesIO()
            im.save(buf, 'ICO', sizes=[(s, s)], bitmap_format='bmp')
            raw = buf.getvalue()
            assert struct.unpack('<H', raw[4:6])[0] == 1
            data = raw[6 + 16:]
            assert struct.unpack('<i', data[:4])[0] == 40
        frames.append((s, data))
    return frames


def write_ico(path, frames):
    header = struct.pack('<HHH', 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries = b''
    blob = b''
    for s, data in frames:
        w = 0 if s >= 256 else s
        entries += struct.pack('<BBBBHHII', w, w, 0, 0, 1, 32, len(data), offset)
        blob += data
        offset += len(data)
    open(path, 'wb').write(header + entries + blob)


def main():
    master = build()
    out_dir = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'assets', 'icon'))
    os.makedirs(out_dir, exist_ok=True)
    master.resize((256, 256), Image.LANCZOS).save(os.path.join(out_dir, 'padshift-256.png'))
    write_ico(os.path.join(out_dir, 'padshift.ico'), frames_of(master, [16, 24, 32, 48, 64, 128, 256]))
    print('icon written to', out_dir)


if __name__ == '__main__':
    main()
