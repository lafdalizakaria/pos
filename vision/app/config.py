"""Configuration of the local vision service.

Values come from environment variables (prefix ``VISION_``) and, optionally, from a JSON file
(``VISION_CONFIG_FILE``) that is re-read when it changes: switching provider (mock → gemini → yolo → hybrid)
or thresholds needs no redeployment of the register nor restart of the service.
"""

from __future__ import annotations

import json
import logging
import os
import threading
from pathlib import Path
from typing import Literal

from pydantic import Field, SecretStr, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

log = logging.getLogger("vision.config")
ProviderName = Literal["mock", "gemini", "yolo", "hybrid"]


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="VISION_", env_file=".env", extra="ignore")

    provider: ProviderName = "mock"
    host: str = "127.0.0.1"
    port: int = 8765
    #: Listening on anything else than the loopback interface must be explicitly allowed.
    allow_remote: bool = False

    #: Hard limit for one recognition (the register gives up after its own timeout, 6 s by default).
    request_timeout_s: float = 5.0
    image_max_side: int = 1024
    jpeg_quality: int = 90
    max_candidates: int = 80
    max_reference_photos_per_candidate: int = 2

    gemini_api_key: SecretStr | None = Field(default=None, validation_alias="GEMINI_API_KEY")
    gemini_model: str = "gemini-2.5-flash"
    gemini_temperature: float = 0.1
    #: 0 disables "thinking" on flash models: lower latency, enough for closed-set recognition.
    gemini_thinking_budget: int | None = 0

    yolo_model_path: str = "models/tray.onnx"
    #: Hybrid: below this YOLO confidence (or for an unknown class), Gemini is asked.
    hybrid_min_confidence: float = 0.6

    dataset_enabled: bool = True
    dataset_dir: str = "data/dataset"
    dataset_max_items: int = 20000
    #: Container URL with a SAS / signed query (e.g. Azure Blob ``https://acct.blob.core.windows.net/dataset?sv=...``).
    dataset_upload_url: SecretStr | None = None
    #: Images in which a face is detected are never stored in the dataset.
    face_guard: bool = True

    mock_latency_ms: int = 150
    #: Folder of ``<sha256>.json`` files giving the exact mock answer for a given image (tests, demos).
    mock_scenarios_dir: str | None = None

    @field_validator("gemini_api_key", "dataset_upload_url", "mock_scenarios_dir", mode="before")
    @classmethod
    def _empty_is_none(cls, value: object) -> object:
        """``VAR=`` in a .env file means "not configured"."""
        return None if isinstance(value, str) and not value.strip() else value


class SettingsProvider:
    """Returns current settings, reloading the optional JSON override file when it changes."""

    def __init__(self, base: Settings | None = None, config_file: str | None = None) -> None:
        self._base = base or Settings()
        self._file = (
            Path(config_file or os.environ.get("VISION_CONFIG_FILE", ""))
            if (config_file or os.environ.get("VISION_CONFIG_FILE"))
            else None
        )
        self._mtime: float | None = None
        self._current = self._base
        self._lock = threading.Lock()

    def get(self) -> Settings:
        if self._file is None:
            return self._current
        try:
            mtime = self._file.stat().st_mtime
        except FileNotFoundError:
            return self._current
        if mtime != self._mtime:
            with self._lock:
                if mtime != self._mtime:
                    self._mtime = mtime
                    try:
                        overrides = json.loads(self._file.read_text(encoding="utf-8"))
                        merged = {**self._base.model_dump(), **_coerce(overrides)}
                        self._current = Settings.model_validate(merged)
                    except (OSError, ValueError) as ex:
                        # A bad edit must not stop the service: keep the last valid configuration.
                        log.error("Ignoring invalid %s: %s", self._file, ex)
        return self._current


def _coerce(overrides: dict) -> dict:
    """Secrets given in the JSON file are wrapped like the environment ones."""
    result = dict(overrides)
    for key in ("gemini_api_key", "dataset_upload_url"):
        if isinstance(result.get(key), str):
            result[key] = SecretStr(result[key])
    return result
