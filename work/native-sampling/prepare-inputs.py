"""Native renderer fixtures: public GDI test windows, TikTok, and boundary probes."""
import json
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent
LAB = ROOT.parent / "sampling-lab"
ROOT.mkdir(exist_ok=True)
cases = []


def add(name, image, target, provenance, checks=()):
    image = image.convert("RGBA")
    image.putalpha(255)
    image.save(ROOT / f"{name}.input.png")
    (ROOT / f"{name}.input.rgba").write_bytes(image.tobytes())
    cases.append(dict(id=name, sw=image.width, sh=image.height,
                      tw=target[0], th=target[1], source=provenance,
                      input=f"{name}.input.rgba", checks=list(checks)))


for key, target, name in [
    ("100", (560, 240), "identity"),
    ("200", (560, 240), "down2"),
    ("240", (560, 240), "down2_4"),
    ("300", (560, 240), "down3"),
    ("50", (560, 240), "up2"),
    ("66_667", (560, 240), "up1_5"),
    ("200", (560, 480), "horizontal2"),
    ("200", (1120, 240), "vertical2"),
]:
    src = f"assets/text-capture/adaptive/windows-gdi-auto-{key}-cleartype.png"
    add(f"text-{name}", Image.open(LAB / src), target, src,
        ["identity"] if name == "identity" else [])

src = "assets/text-capture/windows-gdi-125-cleartype.png"
im = Image.open(LAB / src)
add("text-125", im, (round(im.width / 1.25), round(im.height / 1.25)), src)
src = "assets/text-capture/adaptive/windows-gdi-auto-200-cleartype.png"
im = Image.open(LAB / src).convert("RGBA")
r, g, b, a = im.split()
add("text-bgr-down2", Image.merge("RGBA", (b, g, r, a)), (560, 240),
    src + "; R/B swapped to exercise opposite subpixel order")
src = "assets/text-capture/adaptive/windows-gdi-auto-200-grayscale.png"
add("grayscale-down2", Image.open(LAB / src), (560, 240), src, ["achromatic"])

src = "assets/TikTok_Icon_Black_Square.png"
im = Image.open(LAB / src).convert("RGBA")
bg = Image.new("RGBA", im.size, "#181818")
bg.alpha_composite(im)
add("tiktok-down2", bg, (im.width // 2, im.height // 2), src)
add("tiktok-up1_5", bg, (round(im.width * 1.5), round(im.height * 1.5)), src)

# Unequal colored borders expose wraparound, channel swaps and vertical flips.
im = Image.new("RGBA", (400, 240), "#d7d7d7")
d = ImageDraw.Draw(im)
for x in range(8, 392):
    v = round((x - 8) * 255 / 383)
    d.line((x, 10, x, 55), fill=(v, v, v))
for y in range(62, 182):
    for x in range(8, 392):
        v = 255 if ((x + y) % 4 < 2) else 0
        d.point((x, y), fill=(v, v, v))
for i, color in enumerate(["#ff0000", "#00ff00", "#0000ff", "#ffff00",
                           "#00ffff", "#ff00ff", "#ff8800", "#8800ff"]):
    d.rectangle((8 + i * 48, 190, 53 + i * 48, 230), fill=color)
d.rectangle((0, 0, 399, 3), fill="#ff0000")
d.rectangle((0, 236, 399, 239), fill="#0000ff")
d.rectangle((0, 4, 3, 235), fill="#00ff00")
d.rectangle((396, 4, 399, 235), fill="#ff00ff")
for name, target in [("identity", im.size), ("down2_4", (167, 100)),
                     ("down4", (100, 60)), ("down5", (80, 48)),
                     ("up1_5", (600, 360)), ("mixed-axis", (200, 360))]:
    add("boundary-" + name, im, target, "Synthetic boundary, RGB/BGR, ramp, and stripes",
        ["identity"] if name == "identity" else [])

(ROOT / "inputs.json").write_text(json.dumps({
    "scope": "Actual D3D11 production pipeline vs independent browser final formula; no private desktop captures.",
    "cases": cases,
}, indent=2), encoding="utf-8")
print(f"Prepared {len(cases)} native test cases.")

benchmarks = []
for name, source_size, target_size, src in [
    ("1440p-from-4k", (3840, 2160), (2560, 1440),
     "assets/text-capture/windows-gdi-150-cleartype.png"),
    ("1440p-from-6k", (6144, 3456), (2560, 1440),
     "assets/text-capture/adaptive/windows-gdi-auto-240-cleartype.png"),
    ("4k-from-1440p", (2560, 1440), (3840, 2160),
     "assets/text-capture/adaptive/windows-gdi-auto-66_667-cleartype.png"),
]:
    tile = Image.open(LAB / src).convert("RGBA")
    full = Image.new("RGBA", source_size, "#ffffff")
    for y in range(0, full.height, tile.height):
        for x in range(0, full.width, tile.width):
            full.paste(tile, (x, y))
    filename = f"benchmark-{name}.input.rgba"
    (ROOT / filename).write_bytes(full.tobytes())
    benchmarks.append(dict(id=name, sw=full.width, sh=full.height,
                           tw=target_size[0], th=target_size[1],
                           source=src + "; tiled at native pixels, no image scaling",
                           input=filename))
(ROOT / "benchmark-inputs.json").write_text(
    json.dumps({"cases": benchmarks}, indent=2), encoding="utf-8")
print(f"Prepared {len(benchmarks)} full-frame benchmarks.")
