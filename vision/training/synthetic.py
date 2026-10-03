"""Synthetic trays (coloured shapes = articles) in the exact format of the registers' dataset.

Used by the training tests and for demonstrations: the whole chain (dataset → training → ONNX → register) can be
exercised without real photos.
"""

from __future__ import annotations

import json
import uuid
from pathlib import Path

import cv2
import numpy as np

#: Article code → (shape, BGR colour). Distinct enough to be learnt in a few epochs from scratch.
SHAPES = {
    "CSC-VND": ("circle", (30, 60, 200)),
    "FRU-SAI": ("square", (40, 180, 40)),
    "EAU-50": ("triangle", (200, 120, 30)),
}


def draw_tray(rng: np.random.Generator, size: int = 480, codes: list[str] | None = None):
    image = np.full((size, size, 3), (215, 215, 210), np.uint8)
    cv2.rectangle(image, (10, 10), (size - 10, size - 10), (170, 170, 165), 4)
    codes = codes or list(rng.choice(list(SHAPES), size=int(rng.integers(1, 4)), replace=False))
    cells = rng.permutation(4)[: len(codes)]
    labels = []
    for code, cell in zip(codes, cells, strict=True):
        shape, colour = SHAPES[code]
        half = int(rng.integers(45, 70))
        cx = (cell % 2) * size // 2 + size // 4 + int(rng.integers(-20, 20))
        cy = (cell // 2) * size // 2 + size // 4 + int(rng.integers(-20, 20))
        if shape == "circle":
            cv2.circle(image, (cx, cy), half, colour, -1)
        elif shape == "square":
            cv2.rectangle(image, (cx - half, cy - half), (cx + half, cy + half), colour, -1)
        else:
            points = np.array([(cx, cy - half), (cx - half, cy + half), (cx + half, cy + half)], np.int32)
            cv2.fillPoly(image, [points], colour)
        labels.append((code, cx / size, cy / size, 2 * half / size, 2 * half / size))
    noise = rng.normal(0, 6, image.shape)
    return np.clip(image + noise, 0, 255).astype(np.uint8), labels


def generate(output: Path, count: int, seed: int = 0, needs_annotation_every: int = 0) -> list[str]:
    """Writes ``count`` validated records (as after a sale) under ``output/<date>/``. Returns their identifiers."""
    rng = np.random.default_rng(seed)
    folder = output / "2026-10-01"
    folder.mkdir(parents=True, exist_ok=True)
    ids = []
    for i in range(count):
        image, labels = draw_tray(rng)
        rid = uuid.UUID(int=int(rng.integers(0, 2**63)) << 64 | i).hex
        cv2.imwrite(str(folder / f"{rid}.jpg"), image)
        flagged = needs_annotation_every and i % needs_annotation_every == 0
        record = {
            "recognition_id": rid,
            "image": f"{rid}.jpg",
            "image_width": image.shape[1],
            "image_height": image.shape[0],
            "predictions": [{"article_code": c, "confidence": 0.9, "bbox_yolo": [x, y, w, h]} for c, x, y, w, h in labels],
            "validated_lines": [{"article_code": c, "quantity": 1, "source": "VisionAuto", "prediction_index": k}
                                for k, (c, *_) in enumerate(labels)],
            "labels": [{"article_code": c, "bbox_yolo": [x, y, w, h], "corrected": False} for c, x, y, w, h in labels],
            "needs_annotation": bool(flagged),
            "uploaded": False,
        }  # fmt: skip
        (folder / f"{rid}.json").write_text(json.dumps(record), encoding="utf-8")
        ids.append(rid)
    return ids
