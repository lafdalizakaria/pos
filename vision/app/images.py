"""Image decoding, resizing and the privacy guard (no face may enter the dataset)."""

from __future__ import annotations

from dataclasses import dataclass
from functools import lru_cache

import cv2
import numpy as np


class InvalidImageError(ValueError):
    pass


@dataclass(frozen=True)
class PreparedImage:
    """The register's image (original size) and the resized JPEG actually sent to the models."""

    original_width: int
    original_height: int
    jpeg: bytes
    width: int
    height: int
    pixels: np.ndarray


def prepare(data: bytes, max_side: int = 1024, quality: int = 90) -> PreparedImage:
    if not data:
        raise InvalidImageError("Image vide.")
    array = cv2.imdecode(np.frombuffer(data, dtype=np.uint8), cv2.IMREAD_COLOR)
    if array is None:
        raise InvalidImageError("Image illisible (JPEG attendu).")
    height, width = array.shape[:2]
    scale = min(1.0, max_side / max(width, height))
    resized = (
        array if scale >= 1.0 else cv2.resize(array, (round(width * scale), round(height * scale)), interpolation=cv2.INTER_AREA)
    )
    ok, encoded = cv2.imencode(".jpg", resized, [int(cv2.IMWRITE_JPEG_QUALITY), quality])
    if not ok:
        raise InvalidImageError("Encodage JPEG impossible.")
    return PreparedImage(width, height, encoded.tobytes(), resized.shape[1], resized.shape[0], resized)


@lru_cache(maxsize=1)
def _face_detector() -> cv2.CascadeClassifier:
    return cv2.CascadeClassifier(cv2.data.haarcascades + "haarcascade_frontalface_default.xml")


def contains_face(image: np.ndarray) -> bool:
    """Conservative face check (Haar cascade). The camera crop is the primary protection; this is a safety net."""
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    faces = _face_detector().detectMultiScale(gray, scaleFactor=1.1, minNeighbors=6, minSize=(48, 48))
    return len(faces) > 0
