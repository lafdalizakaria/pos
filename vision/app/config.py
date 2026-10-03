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
    #: Registers: DPAPI-protected file (machine scope) written by the installer, used when GEMINI_API_KEY is absent.
    gemini_api_key_file: str | None = None
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

    camera_index: int = 0
    camera_width: int = 1920
    camera_height: int = 1080
    #: Fixed exposure (driver units, e.g. -6) and white balance (K) for stable images; None = automatic.
    camera_exposure: float | None = -6
    camera_white_balance: int | None = 4500
    #: Tray area kept from the frame, "x,y,width,height" in pixels. Set it so that no face can be in the image.
    camera_crop: str | None = None
    camera_skip_frames: int = 2
    #: Reference photos are resized to this size before being sent to the model.
    reference_photo_max_side: int = 512
    max_reference_photos_total: int = 40

    mock_latency_ms: int = 150
    #: Folder of ``<sha256>.json`` files giving the exact mock answer for a given image (tests, demos).
    mock_scenarios_dir: str | None = None

    @field_validator(
        "gemini_api_key",
        "dataset_upload_url",
        "mock_scenarios_dir",
        "camera_crop",
        "gemini_api_key_file",
        "camera_exposure",
        "camera_white_balance",
        mode="before",
    )
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
