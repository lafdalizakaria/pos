"""YOLO first (local, fast, free); Gemini only when YOLO is unsure.

Gemini is asked when a YOLO detection is below ``hybrid_min_confidence``, when YOLO sees nothing, or when today's
menu contains articles the model was not trained on (new dishes). Both answers are merged box by box:
* a confident YOLO detection is kept (Gemini's opinion, if different, becomes an alternative);
* an unsure YOLO detection takes Gemini's answer for the same box when Gemini is more confident;
* items seen by only one of them are kept.
If Gemini fails or runs out of time, the YOLO answer is returned: the hybrid mode is never worse than YOLO alone.
"""

from __future__ import annotations

import asyncio
import logging
import time

from app.config import Settings
from app.images import PreparedImage
from app.models import Candidate
from app.providers.base import ProviderResult, ProviderUnavailableError, RawDetection, RecognitionProvider
from app.providers.yolo import YoloProvider

log = logging.getLogger("vision.hybrid")
MATCH_IOU = 0.45
#: Kept for the rest of the request (merging, response) when Gemini is called.
SAFETY_MARGIN_S = 0.3


def iou(a: tuple[int, int, int, int] | None, b: tuple[int, int, int, int] | None) -> float:
    if a is None or b is None:
        return 0.0
    x0, y0, x1, y1 = max(a[0], b[0]), max(a[1], b[1]), min(a[2], b[2]), min(a[3], b[3])
    inter = max(0, x1 - x0) * max(0, y1 - y0)
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


def merge(yolo: list[RawDetection], gemini: list[RawDetection], min_confidence: float) -> list[RawDetection]:
    result: list[RawDetection] = []
    used: set[int] = set()
    for detection in yolo:
        match = max(
            ((i, iou(detection.bbox, g.bbox)) for i, g in enumerate(gemini) if i not in used),
            key=lambda x: x[1],
            default=(None, 0.0),
        )
        index, overlap = match
        if index is None or overlap < MATCH_IOU:
            result.append(detection)
            continue
        used.add(index)
        other = gemini[index]
        if other.article_code == detection.article_code:
            confidence = max(detection.confidence, other.confidence)
            result.append(RawDetection(detection.article_code, confidence, detection.bbox, detection.alternatives))
        elif detection.confidence >= min_confidence or detection.confidence >= other.confidence:
            alternatives = [(other.article_code, other.confidence), *detection.alternatives]
            result.append(RawDetection(detection.article_code, detection.confidence, detection.bbox, alternatives))
        else:
            alternatives = [(detection.article_code, detection.confidence), *other.alternatives]
            result.append(RawDetection(other.article_code, other.confidence, detection.bbox or other.bbox, alternatives))
    result.extend(g for i, g in enumerate(gemini) if i not in used)
    return sorted(result, key=lambda d: d.confidence, reverse=True)


class HybridProvider:
    name = "hybrid"

    def __init__(self, yolo: YoloProvider, gemini: RecognitionProvider) -> None:
        self._yolo = yolo
        self._gemini = gemini

    def readiness(self, settings: Settings) -> tuple[bool, str | None]:
        yolo_ready, yolo_detail = self._yolo.readiness(settings)
        gemini_ready, gemini_detail = self._gemini.readiness(settings)
        if not yolo_ready and not gemini_ready:
            return False, f"{yolo_detail} ; {gemini_detail}"
        detail = f"YOLO : {yolo_detail}" + ("" if gemini_ready else f" ; Gemini indisponible : {gemini_detail}")
        return True, detail

    async def recognize(self, image: PreparedImage, candidates: list[Candidate], settings: Settings) -> ProviderResult:
        started = time.perf_counter()
        yolo_ready, _ = self._yolo.readiness(settings)
        gemini_ready, _ = self._gemini.readiness(settings)
        if not yolo_ready:
            if not gemini_ready:
                raise ProviderUnavailableError("Ni modèle YOLO ni clé Gemini")
            return ProviderResult(_detections(await self._gemini.recognize(image, candidates, settings)), "hybrid:gemini")

        first = await self._yolo.recognize(image, candidates, settings)
        detections, label = first.detections, first.label.replace("yolo:", "hybrid:", 1)
        unknown = {c.article_code for c in candidates} - self._yolo.known_codes(settings)
        unsure = (
            not detections
            or any(d.confidence < settings.hybrid_min_confidence for d in detections)
            or (settings.hybrid_ask_gemini_for_unknown_articles and bool(unknown))
        )
        if not unsure or not gemini_ready:
            return ProviderResult(detections, label)

        remaining = settings.request_timeout_s - (time.perf_counter() - started) - SAFETY_MARGIN_S
        if remaining <= 0.5:
            return ProviderResult(detections, label)
        try:
            second = _detections(await asyncio.wait_for(self._gemini.recognize(image, candidates, settings), remaining))
        except Exception as ex:  # noqa: BLE001 - timeout, network, quota: keep the local answer
            log.warning("Hybrid: Gemini failed (%s), YOLO answer kept", type(ex).__name__)
            return ProviderResult(detections, label + "+gemini!")
        return ProviderResult(merge(detections, second, settings.hybrid_min_confidence), label + "+gemini")


def _detections(result: list[RawDetection] | ProviderResult) -> list[RawDetection]:
    return result.detections if isinstance(result, ProviderResult) else result
