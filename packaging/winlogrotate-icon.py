#!/usr/bin/env python3
"""Generates winlogrotate.ico and winlogrotate.png from geometry.

Original artwork by construction - no glyph, no font, no emoji - so there are no licensing
constraints on the shipped icon. Everything is rounded rectangles and an arc.

Requires Pillow >= 9.1 for the multi-size .ico (`append_images`). Older versions IGNORE unknown
save keywords rather than raising, so there is no exception to catch: they would silently
resize the 256px frame for every size and the small icons would look terrible for no visible
reason. Hence the explicit check below.

    python3 winlogrotate-icon.py
"""

import math

from PIL import Image, ImageDraw
import PIL

BG_TOP = (30, 41, 59)      # slate
BG_BOTTOM = (51, 65, 85)
MARK = (245, 158, 11)      # amber - reads as "logs", and stands out in a mostly-blue tray

SIZES = [256, 128, 64, 48, 32, 24, 16]
SUPERSAMPLE = 4


def render(size: int) -> Image.Image:
    """Draws one icon at `size` pixels."""
    # 16px is the size users actually see - tray, taskbar, Explorer list view - and it is the
    # size a single downsampled geometry serves worst. Measured rather than assumed: rendering
    # the full motif at 16px and magnifying it produced an amber blob in which neither the bars
    # nor the arc were distinguishable. There is simply no room for both, so the small variant
    # drops the arc entirely and keeps the log lines, which are the more legible half.
    small = size <= 20

    s = size * SUPERSAMPLE
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    radius = int(s * 0.22)
    for y in range(s):
        blend = y / max(1, s - 1)
        colour = tuple(
            int(BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * blend) for i in range(3)
        )
        draw.line([(0, y), (s, y)], fill=colour + (255,))

    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=radius, fill=255)
    image.putalpha(mask)

    draw = ImageDraw.Draw(image)
    centre = s / 2

    # Log lines, of decreasing width.
    bars = (
        [(-0.32, -0.17, 0.32), (-0.32, 0.03, 0.32), (-0.32, 0.23, 0.16)]
        if small
        else [(-0.30, -0.14, 0.30), (-0.30, 0.02, 0.22), (-0.30, 0.18, 0.14)]
    )

    height = s * (0.07 if small else 0.045)
    for x0, y, x1 in bars:
        draw.rounded_rectangle(
            [centre + x0 * s, centre + y * s - height,
             centre + x1 * s, centre + y * s + height],
            radius=height,
            fill=MARK + (255,),
        )

    # The rotation arc, sweeping clockwise around the lines. Omitted below 20px: see above.
    if small:
        return image.resize((size, size), Image.LANCZOS)

    inset = s * 0.10
    width = int(s * (0.075 if small else 0.05))
    gap = 30 if small else 18      # a wider mouth so it still reads as an arc when tiny
    end_angle = 35 - gap
    draw.arc(
        [inset, inset, s - inset, s - inset],
        start=125 + gap, end=end_angle,
        fill=MARK + (255,), width=width,
    )

    # An arrowhead at the sweeping end. Without it the arc reads as a circle, and the icon says
    # "logs" but not "rotation" - which is half the name.
    radius = (s - 2 * inset) / 2
    theta = math.radians(end_angle)
    tip_x = centre + radius * math.cos(theta)
    tip_y = centre + radius * math.sin(theta)
    head = width * (2.1 if small else 1.8)

    # Perpendicular to the tangent, so the head points the way the arc is travelling.
    draw.polygon(
        [
            (tip_x + head * math.cos(theta - math.pi / 2),
             tip_y + head * math.sin(theta - math.pi / 2)),
            (tip_x + head * math.cos(theta + math.pi / 2),
             tip_y + head * math.sin(theta + math.pi / 2)),
            (tip_x + head * 1.5 * math.cos(theta + math.pi),
             tip_y + head * 1.5 * math.sin(theta + math.pi)),
        ],
        fill=MARK + (255,),
    )

    return image.resize((size, size), Image.LANCZOS)


def main() -> None:
    major, minor = (int(p) for p in PIL.__version__.split(".")[:2])
    if (major, minor) < (9, 1):
        raise SystemExit(
            f"Pillow {PIL.__version__} is too old. Below 9.1 the multi-size .ico save keyword "
            "is silently ignored, producing an icon that looks wrong only at small sizes."
        )

    frames = [render(size) for size in SIZES]
    frames[0].save(
        "winlogrotate.ico",
        format="ICO",
        sizes=[(s, s) for s in SIZES],
        append_images=frames[1:],
    )
    render(512).save("winlogrotate.png")
    print("wrote winlogrotate.ico and winlogrotate.png")


if __name__ == "__main__":
    main()
