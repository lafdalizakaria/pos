"""Bounding box conversions.

* Gemini returns ``box_2d = [ymin, xmin, ymax, xmax]`` normalised to 0-1000.
* The API returns pixel boxes ``[x_min, y_min, x_max, y_max]`` in the coordinates of the image sent by the register.
* The dataset stores YOLO labels ``(x_center, y_center, width, height)`` normalised to 0-1.
"""

from __future__ import annotations

Box = tuple[int, int, int, int]


def gemini_to_pixels(box_2d: list[float] | tuple[float, ...], width: int, height: int) -> Box | None:
    if box_2d is None or len(box_2d) != 4 or width <= 0 or height <= 0:
        return None
    ymin, xmin, ymax, xmax = (min(max(float(v), 0.0), 1000.0) for v in box_2d)
    if xmax <= xmin or ymax <= ymin:
        return None
    return (
        round(xmin / 1000 * width),
        round(ymin / 1000 * height),
        round(xmax / 1000 * width),
        round(ymax / 1000 * height),
    )


def scale_box(box: Box, from_size: tuple[int, int], to_size: tuple[int, int]) -> Box:
    """Rescales a pixel box from a resized image back to the original image."""
    (fw, fh), (tw, th) = from_size, to_size
    sx, sy = tw / fw, th / fh
    x0, y0, x1, y1 = box
    return clamp((round(x0 * sx), round(y0 * sy), round(x1 * sx), round(y1 * sy)), tw, th)


def clamp(box: Box, width: int, height: int) -> Box:
    x0, y0, x1, y1 = box
    return (min(max(x0, 0), width), min(max(y0, 0), height), min(max(x1, 0), width), min(max(y1, 0), height))


def pixels_to_yolo(box: Box, width: int, height: int) -> tuple[float, float, float, float]:
    x0, y0, x1, y1 = clamp(box, width, height)
    return (
        round((x0 + x1) / 2 / width, 6),
        round((y0 + y1) / 2 / height, 6),
        round((x1 - x0) / width, 6),
        round((y1 - y0) / height, 6),
    )


def yolo_to_pixels(cx: float, cy: float, w: float, h: float, width: int, height: int) -> Box:
    return clamp(
        (round((cx - w / 2) * width), round((cy - h / 2) * height), round((cx + w / 2) * width), round((cy + h / 2) * height)),
        width,
        height,
    )
