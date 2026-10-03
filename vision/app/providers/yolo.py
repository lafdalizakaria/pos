"""Local YOLO detector (Ultralytics export, run with ONNX Runtime: no PyTorch, no network, ~50-150 ms on a register CPU).

Output of a YOLOv8/YOLO11 detection export: ``[1, 4 + classes, anchors]`` (cx, cy, w, h in input pixels, then one
score per class). Classes that are not on today's menu are masked *before* choosing the best class: the detector
can only answer with today's articles.
"""

from __future__ import annotations

import asyncio
import threading
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

from app.config import Settings
from app.images import PreparedImage
from app.model_store import ModelManifest, ModelStore, read_dev_manifest
from app.models import Candidate
from app.providers.base import ProviderResult, ProviderUnavailableError, RawDetection


@dataclass
class LoadedModel:
    manifest: ModelManifest
    session: object
    input_name: str
    size: int


def letterbox(image: np.ndarray, size: int) -> tuple[np.ndarray, float, int, int]:
    """Resizes keeping the aspect ratio and pads to ``size``×``size`` (grey 114, as in training)."""
    height, width = image.shape[:2]
    ratio = min(size / height, size / width)
    new_w, new_h = round(width * ratio), round(height * ratio)
    resized = cv2.resize(image, (new_w, new_h), interpolation=cv2.INTER_LINEAR) if (new_w, new_h) != (width, height) else image
    left, top = (size - new_w) // 2, (size - new_h) // 2
    canvas = np.full((size, size, 3), 114, dtype=np.uint8)
    canvas[top : top + new_h, left : left + new_w] = resized
    return canvas, ratio, left, top


def decode(
    output: np.ndarray,
    classes: list[str],
    allowed: set[str],
    ratio: float,
    left: int,
    top: int,
    width: int,
    height: int,
    min_confidence: float,
    nms_iou: float,
) -> list[RawDetection]:
    predictions = output[0].T  # (anchors, 4 + classes)
    boxes, scores = predictions[:, :4], predictions[:, 4:].copy()
    mask = np.array([c in allowed for c in classes], dtype=bool)
    if not mask.any():
        return []
    scores[:, ~mask] = 0.0
    best = scores.argmax(axis=1)
    confidence = scores[np.arange(len(scores)), best]
    keep = confidence >= min_confidence
    if not keep.any():
        return []
    boxes, scores, best, confidence = boxes[keep], scores[keep], best[keep], confidence[keep]
    x0 = (boxes[:, 0] - boxes[:, 2] / 2 - left) / ratio
    y0 = (boxes[:, 1] - boxes[:, 3] / 2 - top) / ratio
    x1 = (boxes[:, 0] + boxes[:, 2] / 2 - left) / ratio
    y1 = (boxes[:, 1] + boxes[:, 3] / 2 - top) / ratio
    rects = [[float(a), float(b), float(c - a), float(d - b)] for a, b, c, d in zip(x0, y0, x1, y1, strict=True)]
    # Class-agnostic suppression: one physical item gets one line, its other classes become alternatives.
    indices = cv2.dnn.NMSBoxes(rects, confidence.astype(float).tolist(), min_confidence, nms_iou)
    detections = []
    for i in np.array(indices).reshape(-1):
        box = (
            int(np.clip(round(x0[i]), 0, width)),
            int(np.clip(round(y0[i]), 0, height)),
            int(np.clip(round(x1[i]), 0, width)),
            int(np.clip(round(y1[i]), 0, height)),
        )
        if box[2] <= box[0] or box[3] <= box[1]:
            continue
        order = np.argsort(-scores[i])
        alternatives = [(classes[k], round(float(scores[i][k]), 4)) for k in order[1:4] if scores[i][k] >= 0.05]
        detections.append(RawDetection(classes[best[i]], round(float(confidence[i]), 4), box, alternatives))
    return sorted(detections, key=lambda d: d.confidence, reverse=True)


class YoloProvider:
    name = "yolo"

    def __init__(self) -> None:
        self._cache: dict[tuple[str, int], LoadedModel] = {}
        self._lock = threading.Lock()

    def resolve(self, settings: Settings) -> tuple[Path, ModelManifest] | None:
        if settings.yolo_model_version:
            return ModelStore(settings.models_dir).get(settings.yolo_model_version)
        if settings.yolo_model_path and Path(settings.yolo_model_path).is_file():
            try:
                return Path(settings.yolo_model_path), read_dev_manifest(Path(settings.yolo_model_path))
            except (OSError, ValueError):
                return None
        return None

    def known_codes(self, settings: Settings) -> set[str]:
        resolved = self.resolve(settings)
        return set(resolved[1].classes) if resolved else set()

    def readiness(self, settings: Settings) -> tuple[bool, str | None]:
        resolved = self.resolve(settings)
        if resolved is None:
            wanted = settings.yolo_model_version or settings.yolo_model_path or "aucun"
            return False, f"Modèle YOLO non installé ({wanted})"
        try:
            self._load(*resolved, settings)
        except Exception as ex:  # noqa: BLE001 - surfaced in /health
            return False, f"Modèle YOLO illisible : {ex}"
        return True, f"modèle {resolved[1].version} ({len(resolved[1].classes)} articles)"

    async def recognize(self, image: PreparedImage, candidates: list[Candidate], settings: Settings) -> ProviderResult:
        resolved = self.resolve(settings)
        if resolved is None:
            raise ProviderUnavailableError("Modèle YOLO non installé")
        model = await asyncio.to_thread(self._load, *resolved, settings)
        detections = await asyncio.to_thread(self._infer, model, image, {c.article_code for c in candidates}, settings)
        return ProviderResult(detections, f"yolo:{model.manifest.version}")

    def _infer(self, model: LoadedModel, image: PreparedImage, allowed: set[str], settings: Settings) -> list[RawDetection]:
        canvas, ratio, left, top = letterbox(image.pixels, model.size)
        blob = np.ascontiguousarray(canvas[:, :, ::-1].transpose(2, 0, 1)[None], dtype=np.float32) / 255.0
        output = model.session.run(None, {model.input_name: blob})[0]  # type: ignore[attr-defined]
        return decode(
            output, model.manifest.classes, allowed, ratio, left, top, image.width, image.height,
            settings.yolo_min_confidence, settings.yolo_nms_iou,
        )  # fmt: skip

    def _load(self, path: Path, manifest: ModelManifest, settings: Settings) -> LoadedModel:
        key = (str(path), path.stat().st_mtime_ns)
        with self._lock:
            if key not in self._cache:
                import onnxruntime as ort

                options = ort.SessionOptions()
                options.intra_op_num_threads = settings.yolo_threads
                session = ort.InferenceSession(str(path), options, providers=["CPUExecutionProvider"])
                shape = session.get_inputs()[0].shape
                size = shape[2] if isinstance(shape[2], int) else manifest.imgsz
                self._cache.clear()  # one model in memory at a time
                self._cache[key] = LoadedModel(manifest, session, session.get_inputs()[0].name, size)
            return self._cache[key]
