"""Draws the app/window/installer icons from the tray artwork, so taskbar and tray look the same.

Run with: python -I maintenance/make-icons.py   (needs Pillow)
The tray's hand-tuned 16 and 32 px images are used as they are; larger sizes repeat their geometry:
a 2x2 grid (first tile orange) with 1/16 margins and gaps, on a transparent background.
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ORANGE, GREY = (217, 119, 87, 255), (86, 92, 107, 255)


def draw(size):
    scale = 4  # supersampled, then reduced: smooth corners
    big = size * scale
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    pen = ImageDraw.Draw(image)
    margin = gap = big / 16
    tile = (big - 2 * margin - gap) / 2
    for row in range(2):
        for column in range(2):
            x = margin + column * (tile + gap)
            y = margin + row * (tile + gap)
            pen.rounded_rectangle((x, y, x + tile, y + tile), radius=tile * 0.18,
                                  fill=ORANGE if (row, column) == (0, 0) else GREY)
    return image.resize((size, size), Image.LANCZOS)


def main():
    tray = {16: Image.open(ROOT / "resources" / "tray.png").convert("RGBA"),
            32: Image.open(ROOT / "resources" / "tray@2x.png").convert("RGBA")}
    draw(512).save(ROOT / "resources" / "icon.png")
    draw(1024).save(ROOT / "build" / "icon.png")
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [tray.get(size) or draw(size) for size in sizes]
    # Pillow writes one ICO entry per frame given in append_images, largest first.
    frames[-1].save(ROOT / "build" / "icon.ico", format="ICO", sizes=[(s, s) for s in sizes],
                    append_images=frames[:-1])


if __name__ == "__main__":
    main()
