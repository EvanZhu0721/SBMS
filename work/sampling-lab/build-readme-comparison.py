"""Build README figures from exported GPU pixels, entirely offline.

Native panels are pasted without resampling. Every detail uses the same fixed
48 x 18 output-pixel rectangle at (0, 0) of the exported text row, for both
algorithms, both backgrounds, and all scenarios. Only 4x nearest-neighbor
enlargement is allowed; labels and borders never overlap the measured pixels.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
SCENARIOS = {
    "up2": ("2× upscale", "upscale-2x.png"),
    "down75": ("0.75× downscale", "downscale-075.png"),
    "down33": ("⅓× downscale", "downscale-third.png"),
    "6k2k": ("6K → 1440p", "downscale-6k-1440p.png"),
}
SIDES = (("sbms", "SBMS current"), ("final", "Final"))
TYPES = (("english", "English"), ("chinese", "Chinese"), ("code", "Code"))

WIDTH, HEIGHT = 1280, 590
LEFT, GAP, COL_WIDTH = 28, 24, 600
NATIVE_Y = 163
DETAIL_Y, DARK_Y = 414, 490
DETAIL_WIDTH, DETAIL_HEIGHT, DETAIL_SCALE = 48, 18, 4
DETAIL_GAP = 12
COLORS = {
    "background": "#f7f8fa",
    "ink": "#202124",
    "muted": "#626874",
    "border": "#d9dde4",
}


def number(value: float) -> str:
    return f"{value:.3f}".rstrip("0").rstrip(".")


def rectangle(box: dict) -> tuple[int, int, int, int]:
    return box["x"], box["y"], box["x"] + box["width"], box["y"] + box["height"]


def build_figure(atlas: Image.Image, manifest: dict, case: dict, font_dir: Path) -> Image.Image:
    scenario = case["scenario"]
    if case["sourceMotion"] != [0, 0]:
        raise ValueError(f"{scenario}: expected the common source offset (0, 0)")

    image = Image.new("RGB", (WIDTH, HEIGHT), COLORS["background"])
    draw = ImageDraw.Draw(image)
    regular = lambda size: ImageFont.truetype(str(font_dir / "segoeui.ttf"), size)
    semibold = lambda size: ImageFont.truetype(str(font_dir / "segoeuib.ttf"), size)
    draw.text((LEFT, 20), SCENARIOS[scenario][0], font=semibold(27), fill=COLORS["ink"])

    dimensions = case["dimensions"]
    source = f'{dimensions["sourceWidth"]} × {dimensions["sourceHeight"]}'
    target = f'{dimensions["width"]} × {dimensions["height"]}'
    dpi = number(case["dpi"] * 96 / 100)
    percentage = number(case["dpi"])
    em = number(case["rows"][0]["outputEm"])
    caption = f"{source} → {target}   ·   Source text: {dpi} DPI ({percentage}%)   ·   Output em: {em} px"
    draw.text((LEFT, 61), caption, font=regular(17), fill=COLORS["muted"])
    draw.line((LEFT, 95, WIDTH - LEFT, 95), fill=COLORS["border"])

    rows = {row["id"]: row for row in case["rows"]}
    checks: list[tuple[tuple[int, int], Image.Image]] = []
    for column, (side, label) in enumerate(SIDES):
        x = LEFT + column * (COL_WIDTH + GAP)
        draw.text((x, 109), label, font=semibold(21), fill=COLORS["ink"])
        draw.text((x, 140), "Native output · 1:1", font=regular(14), fill=COLORS["muted"])
        panel = next(
            panel for panel in manifest["atlas"]["panels"]
            if panel["scenario"] == scenario and panel["side"] == side
        )
        native = atlas.crop(rectangle(panel))
        if native.width > COL_WIDTH or native.height != 192:
            raise ValueError(f"Unexpected native panel size: {scenario} {native.size}")
        native_x = x + (COL_WIDTH - native.width) // 2
        # Borders are outside the atlas pixels, and never replace a source pixel.
        draw.rectangle(
            (native_x - 1, NATIVE_Y - 1, native_x + native.width, NATIVE_Y + native.height),
            outline=COLORS["border"],
        )
        image.paste(native, (native_x, NATIVE_Y))
        checks.append(((native_x, NATIVE_Y), native))
        draw.text((x, 374), "Detail · 4× nearest", font=regular(14), fill=COLORS["muted"])

        for kind_index, (kind, kind_label) in enumerate(TYPES):
            detail_x = x + kind_index * (DETAIL_WIDTH * DETAIL_SCALE + DETAIL_GAP)
            draw.text((detail_x, 395), kind_label, font=regular(13), fill=COLORS["muted"])
            for tone, detail_y in (("light", DETAIL_Y), ("dark", DARK_Y)):
                row = rows[f"{tone}-{kind}"]["export"]
                if row["width"] < DETAIL_WIDTH or row["height"] < DETAIL_HEIGHT:
                    raise ValueError(f"Fixed detail ROI exceeds {scenario} {tone}-{kind}")
                start_x = panel["x"] + row["x"]
                start_y = panel["y"] + row["y"]
                detail = atlas.crop((
                    start_x, start_y, start_x + DETAIL_WIDTH, start_y + DETAIL_HEIGHT,
                )).resize(
                    (DETAIL_WIDTH * DETAIL_SCALE, DETAIL_HEIGHT * DETAIL_SCALE),
                    Image.Resampling.NEAREST,
                )
                image.paste(detail, (detail_x, detail_y))
                checks.append(((detail_x, detail_y), detail))

    # Assert the completed drawing retains every native/detail RGB pixel.
    for (x, y), source in checks:
        pasted = image.crop((x, y, x + source.width, y + source.height))
        if pasted.tobytes() != source.tobytes():
            raise AssertionError("Figure decoration changed exported comparison pixels")
    return image


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=HERE / "final-exports" / "manifest.json")
    parser.add_argument("--output-dir", type=Path, default=ROOT / "assets" / "readme" / "sampling")
    parser.add_argument("--font-dir", type=Path, default=Path("C:/Windows/Fonts"))
    parser.add_argument("--scenarios", nargs="+", choices=SCENARIOS, default=list(SCENARIOS))
    args = parser.parse_args()

    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    atlas = Image.open(args.manifest.parent / manifest["atlas"]["file"]).convert("RGB")
    cases = {case["scenario"]: case for case in manifest["cases"]}
    args.output_dir.mkdir(parents=True, exist_ok=True)
    for scenario in args.scenarios:
        image = build_figure(atlas, manifest, cases[scenario], args.font_dir)
        path = args.output_dir / SCENARIOS[scenario][1]
        image.save(path)
        # PNG encode/decode must also preserve the composed image exactly.
        if Image.open(path).convert("RGB").tobytes() != image.tobytes():
            raise AssertionError(f"Saved figure differs: {path}")
        print(f"{path}  {image.width}x{image.height}; native 1:1, fixed detail ROI 4x nearest")


if __name__ == "__main__":
    main()
