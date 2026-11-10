"""Draw a depth heatmap from the actual per-block CSV; no model inference needed."""
import argparse
import csv
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont


def draw_heatmap(folder: Path, output: Path) -> None:
    meta = json.loads((folder / "metadata.json").read_text(encoding="utf-8"))
    width, height = meta["Width"], meta["Height"]
    values = np.full((height, width), np.nan, dtype=np.float32)
    with (folder / "blocks.csv").open(encoding="utf-8", newline="") as handle:
        blocks = list(csv.DictReader(handle))
    if len(blocks) != meta["Blocks"]:
        raise ValueError("Block count differs from metadata")
    for block in blocks:
        x, y = int(block["cell_x"]), int(block["cell_y"])
        w, h = int(block["cell_width"]), int(block["cell_height"])
        values[y:y + h, x:x + w] = float(block["depth"])
    if not np.isfinite(values).all() or values.min() < 0 or values.max() > 1:
        raise ValueError("Depth must cover every source pixel and stay in [0, 1]")

    # A fixed 0..1 scale: blue = far, red = near. Never renormalize this image's range.
    anchors = np.array([[36, 57, 156], [29, 135, 209], [40, 191, 187],
                        [147, 210, 107], [248, 218, 64], [247, 135, 43],
                        [204, 42, 42]], dtype=np.float32)

    def colours(depth):
        position = np.clip(depth, 0, 1) * (len(anchors) - 1)
        lower = np.minimum(position.astype(np.int32), len(anchors) - 2)
        fraction = (position - lower)[..., None]
        return np.rint(anchors[lower] * (1 - fraction) + anchors[lower + 1] * fraction).astype(np.uint8)

    margin, header, footer = 48, 124, 144
    canvas = Image.new("RGB", (width + margin * 2, height + header + footer), (17, 22, 33))
    canvas.paste(Image.fromarray(colours(values)), (margin, header))
    draw = ImageDraw.Draw(canvas)
    font_path = Path("C:/Windows/Fonts/msyh.ttc")
    font = lambda size: ImageFont.truetype(str(font_path), size)
    white, muted = (243, 246, 251), (180, 194, 213)
    draw.text((margin, 20), "深度热力图", font=font(36), fill=white)
    draw.text((margin, 76), f"{width} × {height}  ·  {meta['CellSize']}px 方块  ·  {len(blocks)} 块  ·  原始深度数据", font=font(23), fill=muted)
    draw.rectangle((margin - 1, header - 1, margin + width, header + height), outline=(70, 81, 102))

    left, right = margin + 220, margin + width - 220
    top, bar_height = header + height + 35, 26
    bar = np.broadcast_to(np.linspace(0, 1, right - left + 1, dtype=np.float32), (bar_height, right - left + 1))
    canvas.paste(Image.fromarray(colours(bar)), (left, top))
    draw.text((margin, top - 5), "远  /  蓝色", font=font(25), fill=white)
    draw.text((margin + width, top - 5), "红色  /  近", font=font(25), fill=white, anchor="ra")
    for tick in range(11):
        x = left + (right - left) * tick / 10
        draw.line((x, top + bar_height, x, top + bar_height + 7), fill=muted)
        draw.text((x, top + bar_height + 11), f"{tick / 10:.1f}", font=font(20), fill=muted, anchor="mt")
    draw.text((margin, header + height + 108), "相对逆深度：0 远，1 近。颜色按模型数值映射，未叠加原图或逐块数字。", font=font(20), fill=muted)
    output.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output)
    print(f"Saved {output} ({canvas.width}x{canvas.height}); {len(blocks)} cells, depth {values.min():.6f}..{values.max():.6f}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path, help="Folder containing blocks.csv and metadata.json")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    draw_heatmap(args.folder, args.output or args.folder / "depth-heatmap.png")
