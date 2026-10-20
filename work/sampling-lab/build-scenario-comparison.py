"""Build content-based comparison figures from exported GPU pixels.

Both filters share each complete tile and its fixed detail ROI. Native panels
are pasted at 1:1; details use only 4x nearest-neighbor enlargement.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
WIDTH, MARGIN, COLUMN, GAP = 1280, 18, 610, 24
DETAIL_SCALE = 4
SCENES = {
    "reading": "Reading",
    "code": "Code",
    "lines": "Lines & curves",
    "texture": "Fine textures",
    "icons": "Icons",
    "photos": "Photos",
}
COLORS = {
    "page": "#f7f8fa",
    "ink": "#202124",
    "muted": "#626874",
    "line": "#d9dde4",
}


def box(rect: dict) -> tuple[int, int, int, int]:
    return (
        rect["x"],
        rect["y"],
        rect["x"] + rect["width"],
        rect["y"] + rect["height"],
    )


def pack(items: list[dict], start_y: int, gap: int = 16) -> tuple[list[dict], int]:
    """Pack already sized items without changing any image dimensions."""
    placed: list[dict] = []
    x, y, row_height = 0, start_y, 0
    for item in items:
        width, height = item["cell_width"], item["cell_height"]
        if width > COLUMN:
            raise ValueError(f'{item["tile"]["id"]}: panel exceeds {COLUMN} px')
        if x and x + width > COLUMN:
            x, y, row_height = 0, y + row_height + gap, 0
        placed.append({**item, "x": x, "y": y})
        x += width + gap
        row_height = max(row_height, height)
    return placed, y + row_height


def text_lines(draw: ImageDraw.ImageDraw, label: str, font, width: int) -> list[str]:
    words, lines, current = label.split(), [], ""
    for word in words:
        candidate = f"{current} {word}".strip()
        if current and draw.textlength(candidate, font=font) > width:
            lines.append(current)
            current = word
        else:
            current = candidate
    if current:
        lines.append(current)
    return lines or [label]


def details_for(scene: str, tiles: list[dict]) -> list[dict]:
    if scene == "icons":
        return [tile for tile in tiles if tile.get("detail")]
    return tiles[:2]


def build(scene: str, tiles: list[dict], atlas: Image.Image, manifest: dict, font_dir: Path):
    regular = lambda size: ImageFont.truetype(str(font_dir / "segoeui.ttf"), size)
    semibold = lambda size: ImageFont.truetype(str(font_dir / "segoeuib.ttf"), size)
    small_font, label_font = regular(13), regular(15)
    measure = ImageDraw.Draw(Image.new("RGB", (1, 1)))
    native_items = []
    for tile in tiles:
        width, height = tile["width"], tile["height"]
        cell_width = 190 if scene == "icons" else width
        if scene in ("reading", "code", "lines", "texture"):
            cell_width = COLUMN
        labels = text_lines(measure, tile["label"], label_font, cell_width)
        label_height = len(labels) * 20 + 9
        native_items.append({
            "tile": tile,
            "cell_width": cell_width,
            "cell_height": label_height + height + 1,
            "label_lines": labels,
            "label_height": label_height,
            "image_width": width,
            "image_height": height,
        })
    native, native_end = pack(native_items, 164, 18)

    detail_header = native_end + 28
    detail_items = []
    for tile in details_for(scene, tiles):
        detail = tile["detail"]
        width, height = detail["width"] * DETAIL_SCALE, detail["height"] * DETAIL_SCALE
        labels = text_lines(measure, tile.get("detailLabel", tile["label"]), small_font, width)
        label_height = len(labels) * 18 + 8
        detail_items.append({
            "tile": tile,
            "cell_width": width,
            "cell_height": label_height + height + 1,
            "label_lines": labels,
            "label_height": label_height,
            "image_width": width,
            "image_height": height,
        })
    details, detail_end = pack(detail_items, detail_header + 28, 18)
    height = detail_end + 69
    image = Image.new("RGB", (WIDTH, height), COLORS["page"])
    draw = ImageDraw.Draw(image)
    checks = []

    title = manifest.get("sceneTitles", {}).get(scene, SCENES[scene])
    draw.text((MARGIN, 20), title, font=semibold(28), fill=COLORS["ink"])
    source = " × ".join(map(str, manifest["sourceSize"]))
    output = " × ".join(map(str, manifest["outputSize"]))
    draw.text(
        (MARGIN, 61), f"{source} → {output}",
        font=regular(15), fill=COLORS["muted"],
    )
    draw.line((MARGIN, 94, WIDTH - MARGIN, 94), fill=COLORS["line"])

    release = manifest["release"]["tag"].removeprefix("v")
    side_labels = (
        ("sbms", f"SBMS {release} · Released filter"),
        ("final", "New filter · Experimental"),
    )
    for column, (side, side_label) in enumerate(side_labels):
        origin_x = MARGIN + column * (COLUMN + GAP)
        draw.text((origin_x, 108), side_label, font=semibold(20), fill=COLORS["ink"])
        draw.text((origin_x, 140), "1:1 output", font=small_font, fill=COLORS["muted"])
        draw.text(
            (origin_x, detail_header), "Detail · 4× nearest",
            font=regular(15), fill=COLORS["muted"],
        )
        for placements, detail_mode in ((native, False), (details, True)):
            for item in placements:
                tile = item["tile"]
                pixels = atlas.crop(box(tile[side]))
                if pixels.size != (tile["width"], tile["height"]):
                    raise AssertionError(f'{tile["id"]}: native tile size mismatch')
                if detail_mode:
                    rect = tile["detail"]
                    if not (
                        0 <= rect["x"] < rect["x"] + rect["width"] <= pixels.width
                        and 0 <= rect["y"] < rect["y"] + rect["height"] <= pixels.height
                    ):
                        raise ValueError(f'{tile["id"]}: detail outside native output')
                    pixels = pixels.crop(box(rect)).resize(
                        (item["image_width"], item["image_height"]),
                        Image.Resampling.NEAREST,
                    )
                x = origin_x + item["x"]
                y = item["y"]
                item_font = small_font if detail_mode else label_font
                step = 18 if detail_mode else 20
                for line_index, label in enumerate(item["label_lines"]):
                    draw.text(
                        (x, y + line_index * step), label,
                        font=item_font, fill=COLORS["muted"],
                    )
                image_x = x + (item["cell_width"] - pixels.width) // 2
                image_y = y + item["label_height"]
                draw.rectangle(
                    (
                        image_x - 1, image_y - 1,
                        image_x + pixels.width, image_y + pixels.height,
                    ),
                    outline=COLORS["line"],
                )
                image.paste(pixels, (image_x, image_y))
                checks.append((image_x, image_y, pixels))

    footer_y = height - 45
    draw.line((MARGIN, footer_y - 13, WIDTH - MARGIN, footer_y - 13), fill=COLORS["line"])
    scale_x, scale_y = (
        source_size / output_size
        for source_size, output_size in zip(manifest["sourceSize"], manifest["outputSize"])
    )
    ratio = f"{scale_x:g}:1" if scale_x == scale_y else f"{scale_x:g}:1 × {scale_y:g}:1"
    note = f"WebGL reproduction · Source/output {ratio} · Detail 4× nearest"
    if scene in ("reading", "code"):
        note = f'Text {manifest["sourceDpi"]:g} → {manifest["outputDpi"]:g} DPI · ' + note
    draw.text((MARGIN, footer_y), note, font=small_font, fill=COLORS["muted"])

    for x, y, pixels in checks:
        pasted = image.crop((x, y, x + pixels.width, y + pixels.height))
        if pasted.tobytes() != pixels.tobytes():
            raise AssertionError("Figure decoration changed exported comparison pixels")
    return image


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prefix", default="scenario")
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--output-dir", type=Path, default=ROOT / "assets" / "readme" / "sampling")
    parser.add_argument("--font-dir", type=Path, default=Path("C:/Windows/Fonts"))
    args = parser.parse_args()
    if args.manifest is None:
        args.manifest = HERE / f"{args.prefix}-exports" / "manifest.json"
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    atlas = Image.open(args.manifest.parent / "pixels.png").convert("RGB")
    for tile in manifest["tiles"]:
        for side in ("sbms", "final"):
            rect = tile[side]
            if (rect["width"], rect["height"]) != (tile["width"], tile["height"]):
                raise ValueError(f'{tile["id"]}: inconsistent {side} dimensions')
            if not (
                0 <= rect["x"] < rect["x"] + rect["width"] <= atlas.width
                and 0 <= rect["y"] < rect["y"] + rect["height"] <= atlas.height
            ):
                raise ValueError(f'{tile["id"]}: {side} outside exported atlas')
    args.output_dir.mkdir(parents=True, exist_ok=True)
    for scene in SCENES:
        tiles = [tile for tile in manifest["tiles"] if tile["scene"] == scene]
        if not tiles:
            continue
        image = build(scene, tiles, atlas, manifest, args.font_dir)
        path = args.output_dir / f"{args.prefix}-{scene}.png"
        image.save(path)
        if Image.open(path).convert("RGB").tobytes() != image.tobytes():
            raise AssertionError(f"Saved figure differs: {path}")
        print(f"{path}  {image.width}x{image.height}; native 1:1, detail 4x nearest")


if __name__ == "__main__":
    main()
