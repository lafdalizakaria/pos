"""Provider selection by configuration (re-evaluated on every request)."""

from __future__ import annotations

from app.config import Settings
from app.images import PreparedImage
from app.models import Candidate
from app.providers.base import ProviderUnavailableError, RawDetection, RecognitionProvider
from app.providers.gemini import GeminiProvider
from app.providers.mock import MockProvider


class _NotYetAvailable:
    """YOLO and Hybrid are delivered in phase 5 (local model trained on the collected dataset)."""

    def __init__(self, name: str) -> None:
        self.name = name

    def readiness(self, settings: Settings) -> tuple[bool, str | None]:
        return False, f"Provider {self.name} livré en phase 5"

    async def recognize(self, image: PreparedImage, candidates: list[Candidate], settings: Settings) -> list[RawDetection]:
        raise ProviderUnavailableError(f"Provider {self.name} non disponible")


class ProviderRegistry:
    def __init__(self, providers: dict[str, RecognitionProvider] | None = None) -> None:
        self._providers: dict[str, RecognitionProvider] = providers or {
            "mock": MockProvider(),
            "gemini": GeminiProvider(),
            "yolo": _NotYetAvailable("yolo"),
            "hybrid": _NotYetAvailable("hybrid"),
        }

    def get(self, settings: Settings) -> RecognitionProvider:
        return self._providers[settings.provider]

    def register(self, name: str, provider: RecognitionProvider) -> None:
        self._providers[name] = provider
