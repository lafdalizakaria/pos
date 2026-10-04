"""Provider selection by configuration (re-evaluated on every request)."""

from __future__ import annotations

from app.config import Settings
from app.providers.base import RecognitionProvider
from app.providers.gemini import GeminiProvider
from app.providers.hybrid import HybridProvider
from app.providers.mock import MockProvider
from app.providers.yolo import YoloProvider


class ProviderRegistry:
    def __init__(self, providers: dict[str, RecognitionProvider] | None = None) -> None:
        if providers is None:
            yolo, gemini = YoloProvider(), GeminiProvider()
            providers = {"mock": MockProvider(), "gemini": gemini, "yolo": yolo, "hybrid": HybridProvider(yolo, gemini)}
        self._providers: dict[str, RecognitionProvider] = providers

    def get(self, settings: Settings) -> RecognitionProvider:
        return self._providers[settings.provider]

    def register(self, name: str, provider: RecognitionProvider) -> None:
        self._providers[name] = provider
