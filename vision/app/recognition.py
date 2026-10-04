"""Post-processing shared by every provider: closed-set validation, clamping, ordering, coordinates."""

from __future__ import annotations

from app import geometry
from app.images import PreparedImage
from app.models import Alternative, Candidate, RecognizedItem
from app.providers.base import RawDetection


def finalize(
    raw: list[RawDetection], candidates: list[Candidate], image: PreparedImage
) -> tuple[list[RecognizedItem], list[str]]:
    """Drops codes outside today's candidates (the model may hallucinate), clamps confidences, converts boxes
    back to the register's image size and adds the YOLO form. Returns the items and the rejected codes."""
    allowed = {c.article_code for c in candidates}
    items: list[RecognizedItem] = []
    rejected: list[str] = []
    for detection in raw:
        code = detection.article_code.strip().upper() if detection.article_code else ""
        if code not in allowed:
            rejected.append(detection.article_code)
            continue
        bbox = bbox_yolo = None
        if detection.bbox is not None:
            original = geometry.scale_box(
                detection.bbox, (image.width, image.height), (image.original_width, image.original_height)
            )
            if original[2] > original[0] and original[3] > original[1]:
                bbox = list(original)
                bbox_yolo = list(geometry.pixels_to_yolo(original, image.original_width, image.original_height))
        alternatives = sorted(
            (
                Alternative(article_code=a.strip().upper(), confidence=_clamp(p))
                for a, p in detection.alternatives
                if a and a.strip().upper() in allowed and a.strip().upper() != code
            ),
            key=lambda a: a.confidence,
            reverse=True,
        )[:3]
        items.append(
            RecognizedItem(
                article_code=code,
                confidence=_clamp(detection.confidence),
                bbox=bbox,
                bbox_yolo=bbox_yolo,
                alternatives=alternatives,
            )
        )
    items.sort(key=lambda i: i.confidence, reverse=True)
    return items, rejected


def _clamp(value: float) -> float:
    try:
        return round(min(max(float(value), 0.0), 1.0), 4)
    except (TypeError, ValueError):
        return 0.0
