"""Provider contract. Providers return raw detections; validation against the candidates is done centrally."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Protocol

from app.config import Settings
from app.images import PreparedImage
from app.models import Candidate


@dataclass
class RawDetection:
    article_code: str
    confidence: float
    #: Pixel box in the coordinates of ``PreparedImage.width/height`` (the resized image).
    bbox: tuple[int, int, int, int] | None = None
    alternatives: list[tuple[str, float]] = field(default_factory=list)


@dataclass
class ProviderResult:
    """Detections plus the label reported to the register (e.g. ``yolo:20261003-2213`` or ``hybrid:...+gemini``)."""

    detections: list[RawDetection]
    label: str


class ProviderUnavailableError(RuntimeError):
    """Configuration or dependency missing (no API key, no model file...)."""


class RecognitionProvider(Protocol):
    name: str

    async def recognize(
        self, image: PreparedImage, candidates: list[Candidate], settings: Settings
    ) -> list[RawDetection] | ProviderResult: ...

    def readiness(self, settings: Settings) -> tuple[bool, str | None]: ...
