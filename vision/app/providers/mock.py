"""Deterministic provider for tests and demos (no network, no key)."""

from __future__ import annotations

import asyncio
import hashlib
import json
from pathlib import Path

from app.config import Settings
from app.images import PreparedImage
from app.models import Candidate
from app.providers.base import RawDetection

#: Confidences spanning the three bands of the register (auto / to confirm / manual).
_PALETTE = (0.95, 0.78, 0.45)


class MockProvider:
    name = "mock"

    def readiness(self, settings: Settings) -> tuple[bool, str | None]:
        return True, None

    async def recognize(self, image: PreparedImage, candidates: list[Candidate], settings: Settings) -> list[RawDetection]:
        await asyncio.sleep(settings.mock_latency_ms / 1000)
        digest = hashlib.sha256(image.jpeg).hexdigest()
        scenario = Path(settings.mock_scenarios_dir) / f"{digest}.json" if settings.mock_scenarios_dir else None
        if scenario is not None and scenario.exists():
            return [
                RawDetection(
                    d["article_code"],
                    d["confidence"],
                    tuple(d["bbox"]) if d.get("bbox") else None,
                    [(a["article_code"], a["confidence"]) for a in d.get("alternatives", [])],
                )
                for d in json.loads(scenario.read_text(encoding="utf-8"))
            ]
        if not candidates:
            return []
        seed = int(digest[:8], 16)
        count = 1 + seed % min(3, len(candidates))
        detections = []
        for i in range(count):
            code = candidates[(seed + i * 7) % len(candidates)].article_code
            alt = candidates[(seed + i * 7 + 1) % len(candidates)].article_code
            w, h = image.width, image.height
            box = (w * i // (count + 1), h // 4, w * (i + 1) // (count + 1), h * 3 // 4)
            detections.append(
                RawDetection(code, _PALETTE[i % len(_PALETTE)], box, [(alt, round(1 - _PALETTE[i % len(_PALETTE)], 2))])
            )
        return detections
