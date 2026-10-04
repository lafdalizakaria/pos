from __future__ import annotations

import json

import cv2
import numpy as np
import pytest
from fastapi.testclient import TestClient

from app.config import Settings, SettingsProvider
from app.main import create_app
from app.providers.base import RawDetection
from app.providers.registry import ProviderRegistry


def make_jpeg(width: int = 1600, height: int = 1200, seed: int = 1) -> bytes:
    rng = np.random.default_rng(seed)
    image = rng.integers(0, 255, size=(height, width, 3), dtype=np.uint8)
    ok, encoded = cv2.imencode(".jpg", image)
    assert ok
    return encoded.tobytes()


CANDIDATES = [
    {"article_code": "PLT-COUS", "label": "Couscous viande", "category": "Plat", "visual_description": "semoule, légumes"},
    {"article_code": "PLT-POUL", "label": "Couscous poulet", "category": "Plat"},
    {"article_code": "DES-FRUIT", "label": "Fruit de saison", "category": "Dessert"},
    {"article_code": "BOI-EAU", "label": "Eau 50 cl", "category": "Boisson"},
]


class StubProvider:
    """Returns fixed detections (boxes in pixels of the resized image) or waits / raises."""

    name = "stub"

    def __init__(self, detections: list[RawDetection] | None = None, delay_s: float = 0, error: Exception | None = None):
        self.detections = detections or []
        self.delay_s = delay_s
        self.error = error
        self.calls: list[tuple] = []

    def readiness(self, settings: Settings) -> tuple[bool, str | None]:
        return True, None

    async def recognize(self, image, candidates, settings):
        import asyncio

        self.calls.append((image, candidates))
        if self.delay_s:
            await asyncio.sleep(self.delay_s)
        if self.error:
            raise self.error
        return self.detections


@pytest.fixture
def settings(tmp_path) -> Settings:
    return Settings(
        provider="mock",
        dataset_dir=str(tmp_path / "dataset"),
        models_dir=str(tmp_path / "models"),
        runtime_file=str(tmp_path / "runtime.json"),
        mock_latency_ms=0,
        gemini_api_key=None,
    )


def client_for(settings: Settings, **providers) -> TestClient:
    registry = ProviderRegistry()
    for name, provider in providers.items():
        registry.register(name, provider)
    return TestClient(create_app(SettingsProvider(settings), registry))


def post_recognize(client: TestClient, image: bytes | None = None, candidates=None, register_id: str = "REG-1"):
    return client.post(
        "/recognize",
        files={"image": ("tray.jpg", image if image is not None else make_jpeg(), "image/jpeg")},
        data={"candidates": json.dumps(candidates if candidates is not None else CANDIDATES), "register_id": register_id},
    )
