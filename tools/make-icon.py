#!/usr/bin/env python3
"""Generate the PadShift application icon (original artwork, no third-party assets).

Design: a rounded-square tile with a dark navy gradient and a cyan glow, a white gamepad
silhouette sitting on top of a cyan "shifted layer" copy of itself (the shift/layer motif), a soft
drop shadow for depth, and two cyan chevrons in the middle as the "shift" accent.

Outputs (relative to the repository root):
    assets/icon/padshift.ico        16, 24, 32, 48, 64, 128, 256 (PNG-compressed entries)
    assets/icon/padshift-256.png    store / marketing image

Usage:  python tools/make-icon.py
"""

from __future__ import annotations

import io
import struct
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageOps

SIZES = [16, 24, 32, 48, 64, 128, 256]
ROOT = Path(__file__).resolve().parent.parent
OUT_DIR = ROOT / "assets" / "icon"

# Palette ---------------------------------------------------------------------------------------
TILE_TOP = (18, 30, 54)       # #121E36
TILE_BOTTOM = (5, 9, 18)      # #050912
GLOW = (40, 128, 214)
LAYER_BACK = ((62, 199, 245), (12, 126, 200))   # cyan shift layer
PAD_TOP = (255, 255, 255)
PAD_BOTTOM = (198, 213, 235)
DETAIL = (124, 147, 180)
HOLE = (8, 14, 26)


def _vgrad(size: int, top, bottom) -> Image.Image:
    strip = Image.new("RGB", (1, size))
    for y in range(size):
        t = y / max(1, size - 1)
        strip.putpixel(
            (0, y),
            tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)),
        )
    return strip.resize((size, size), Image.Resampling.NEAREST)


def _chevron(draw: ImageDraw.ImageDraw, x0: float, y0: float, w: float, h: float, t: float, fill):
    """Right-pointing chevron band, used as the "shift" accent."""
    ymid = y0 + h / 2
    draw.polygon(
        [
            (x0, y0),
            (x0 + w, ymid),
            (x0, y0 + h),
            (x0 + t, y0 + h),
            (x0 + w - t, ymid),
            (x0 + t, y0),
        ],
        fill=fill,
    )


def pad_mask(S: int) -> Image.Image:
    """Union of the body and the two grips, as an L mask."""
    mask = Image.new("L", (S, S), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle(
        (0.145 * S, 0.300 * S, 0.855 * S, 0.672 * S),
        radius=0.160 * S,
        fill=255,
    )

    for side in ("left", "right"):
        grip = Image.new("L", (S, S), 0)
        gd = ImageDraw.Draw(grip)
        if side == "left":
            box = (0.150 * S, 0.470 * S, 0.352 * S, 0.870 * S)
            angle = -16.0
        else:
            box = (0.648 * S, 0.470 * S, 0.850 * S, 0.870 * S)
            angle = 16.0
        gd.rounded_rectangle(box, radius=0.100 * S, fill=255)
        # Rotate around the grip's own centre so it splays outward without drifting.
        cx = (box[0] + box[2]) / 2
        cy = (box[1] + box[3]) / 2
        grip = grip.rotate(angle, center=(cx, cy), resample=Image.Resampling.BICUBIC)
        mask = ImageChops.lighter(mask, grip)

    return mask


def render(size: int, scale: int = 4) -> Image.Image:
    """Draw the icon at size*scale and downsample to size (supersampled anti-aliasing)."""
    S = size * scale
    detailed = size >= 32

    # --- tile -------------------------------------------------------------------------------
    tile = _vgrad(S, TILE_TOP, TILE_BOTTOM).convert("RGBA")

    glow = ImageOps.invert(Image.radial_gradient("L").resize((S, S), Image.Resampling.BILINEAR))
    glow = glow.point(lambda v: int(v * 0.62))
    glow_layer = Image.new("RGB", (S, S), GLOW)
    tile.paste(glow_layer, (0, 0), glow)

    tile_alpha = Image.new("L", (S, S), 0)
    ImageDraw.Draw(tile_alpha).rounded_rectangle(
        (0, 0, S - 1, S - 1), radius=0.234 * S, fill=255
    )

    # --- pad silhouette ---------------------------------------------------------------------
    mask = pad_mask(S)

    # Soft contact shadow under the pad: depth without any 3D effect layers.
    shadow = mask.filter(ImageFilter.GaussianBlur(0.018 * S))
    shadow = shadow.point(lambda v: int(v * 0.55))
    shadow_layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    shadow_layer.paste((0, 0, 0, 255), (int(0.010 * S), int(0.016 * S)), shadow)
    tile = Image.alpha_composite(tile, shadow_layer)

    # The shifted cyan layer behind the pad: the "layer / shift" motif.
    back = mask
    back_layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    back_fill = _vgrad(S, LAYER_BACK[0], LAYER_BACK[1]).convert("RGBA")
    back_layer.paste(back_fill, (-int(0.052 * S), -int(0.056 * S)), back)
    tile = Image.alpha_composite(tile, back_layer)

    # --- front pad --------------------------------------------------------------------------
    front = mask.copy()
    front_draw = ImageDraw.Draw(front)
    details = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    detail_draw = ImageDraw.Draw(details)

    if detailed:
        # Two thumbstick wells (cut through the white pad so the dark tile shows) + light caps.
        wells = [(0.338, 0.470, 0.083), (0.662, 0.588, 0.077)]
        for cx, cy, r in wells:
            front_draw.ellipse(
                ((cx - r) * S, (cy - r) * S, (cx + r) * S, (cy + r) * S), fill=0
            )
            rr = r * 0.62
            detail_draw.ellipse(
                ((cx - rr) * S, (cy - rr) * S, (cx + rr) * S, (cy + rr) * S),
                fill=DETAIL + (255,),
            )

        # Face-button diamond (kept small: it must survive at 32 px).
        if size >= 64:
            for cx, cy in ((0.663, 0.379), (0.699, 0.415), (0.627, 0.415), (0.663, 0.451)):
                r = 0.020
                detail_draw.ellipse(
                    ((cx - r) * S, (cy - r) * S, (cx + r) * S, (cy + r) * S),
                    fill=DETAIL + (235,),
                )

    pad_fill = _vgrad(S, PAD_TOP, PAD_BOTTOM).convert("RGBA")
    pad_layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    pad_layer.paste(pad_fill, (0, 0), front)
    pad_layer = Image.alpha_composite(pad_layer, details)
    tile = Image.alpha_composite(tile, pad_layer)

    # --- shift chevrons ---------------------------------------------------------------------
    if size >= 48:
        accent = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        ad = ImageDraw.Draw(accent)
        _chevron(ad, 0.442 * S, 0.415 * S, 0.058 * S, 0.140 * S, 0.019 * S, (14, 165, 233, 255))
        _chevron(ad, 0.508 * S, 0.415 * S, 0.058 * S, 0.140 * S, 0.019 * S, (14, 165, 233, 255))
        tile = Image.alpha_composite(tile, accent)

    tile.putalpha(ImageChops.multiply(tile.getchannel("A"), tile_alpha))
    return tile.resize((size, size), Image.Resampling.LANCZOS)


def _dib(image: Image.Image) -> bytes:
    """Encode one ICO entry as a classic 32 bpp DIB (BITMAPINFOHEADER + BGRA + AND mask).

    GDI/System.Drawing cannot decode a PNG-compressed frame for every size, so only the 256 px
    entry uses PNG (the usual convention); the rest stay universally readable.
    """
    width, height = image.size
    pixels = image.convert("RGBA").load()

    header = struct.pack(
        "<IiiHHIIiiII",
        40,              # biSize
        width,           # biWidth
        height * 2,      # biHeight (XOR bitmap + AND mask)
        1,               # biPlanes
        32,              # biBitCount
        0,               # biCompression = BI_RGB
        width * height * 4,
        0, 0, 0, 0,
    )

    body = bytearray()
    for y in range(height - 1, -1, -1):          # bottom-up rows
        for x in range(width):
            r, g, b, a = pixels[x, y]
            body += bytes((b, g, r, a))

    mask_row = ((width + 31) // 32) * 4          # 1 bpp, padded to 4 bytes
    body += bytes(mask_row * height)             # AND mask: 0 = use the alpha channel

    return header + bytes(body)


def write_ico(path: Path, images: list[tuple[int, Image.Image]]) -> None:
    """Write a multi-size ICO: DIB entries up to 128 px, PNG for 256 px."""
    blobs = []
    for size, image in images:
        if size >= 256:
            buffer = io.BytesIO()
            image.save(buffer, format="PNG", optimize=True)
            blobs.append(buffer.getvalue())
        else:
            blobs.append(_dib(image))

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = bytearray()
    data = bytearray()
    for (size, _image), blob in zip(images, blobs):
        dimension = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
        data += blob

    path.write_bytes(header + bytes(entries) + bytes(data))


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    images = [(size, render(size)) for size in SIZES]
    write_ico(OUT_DIR / "padshift.ico", images)
    images[-1][1].save(OUT_DIR / "padshift-256.png")
    print(f"wrote {OUT_DIR / 'padshift.ico'} ({len(images)} sizes) and padshift-256.png")


if __name__ == "__main__":
    main()
