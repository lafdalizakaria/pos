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

    #: Installed models: ``<models_dir>/<version>/model.onnx`` + ``manifest.json`` (pushed by the register).
    models_dir: str = "data/models"
    #: Active model version (normally set by the register from the server's site settings).
    yolo_model_version: str | None = None
    #: Development only: an ONNX file used when no version is active (its classes come from ``<file>.json``).
    yolo_model_path: str | None = None
    #: Minimum class score kept by YOLO, and IoU of the non-maximum suppression (class-agnostic: one object, one box).
    yolo_min_confidence: float = 0.25
    yolo_nms_iou: float = 0.5
    yolo_threads: int = 2
    #: Hybrid: Gemini is asked when a YOLO detection is below this confidence, when YOLO sees nothing, or when
    #: today's menu has articles the model was not trained on.
    hybrid_min_confidence: float = 0.6
    hybrid_ask_gemini_for_unknown_articles: bool = True
    #: Number of installed model versions kept (rollback).
    models_kept: int = 3
    #: Written by the service when the register pushes the site settings (provider, model): between the environment
    #: and the local override file (``VISION_CONFIG_FILE`` always wins: emergency switch on site).
    runtime_file: str = "data/runtime.json"

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
        "yolo_model_version",
        "yolo_model_path",
        "camera_exposure",
        "camera_white_balance",
        mode="before",
    )
    @classmethod
    def _empty_is_none(cls, value: object) -> object:
        """``VAR=`` in a .env file means "not configured"."""
        return None if isinstance(value, str) and not value.strip() else value


class SettingsProvider:
    """Current settings = environment < runtime file (pushed by the register) < local override file.

    Both files are re-read when they change: the provider and the model are switched without restarting anything.
    """

    def __init__(self, base: Settings | None = None, config_file: str | None = None, runtime_file: str | None = None) -> None:
        self._base = base or Settings()
        local = config_file or os.environ.get("VISION_CONFIG_FILE") or None
        self._runtime = Path(runtime_file or self._base.runtime_file)
        self._files = [self._runtime, *([Path(local)] if local else [])]
        self._stamps: tuple[float | None, ...] | None = None
        self._layers: dict[Path, dict] = {}
        self._current = self._base
        self._lock = threading.Lock()

    @property
    def runtime_path(self) -> Path:
        return self._runtime

    def local_override_keys(self) -> set[str]:
        """Keys forced by the local override file (it wins over what the register pushes)."""
        self.get()
        return set().union(*(self._layers.get(f, {}).keys() for f in self._files[1:]))

    def get(self) -> Settings:
        stamps = tuple(_mtime(f) for f in self._files)
        if stamps != self._stamps:
            with self._lock:
                if stamps != self._stamps:
                    self._stamps = stamps
                    self._reload()
        return self._current

    def write_runtime(self, values: dict) -> Settings:
        """Validates then atomically writes the runtime layer (provider, model version) and returns the new settings."""
        with self._lock:
            candidate = {**self._layers.get(self._runtime, {}), **values}
            Settings.model_validate({**self._base.model_dump(), **_coerce(candidate)})  # raises on invalid values
            self._runtime.parent.mkdir(parents=True, exist_ok=True)
            temporary = self._runtime.with_suffix(".tmp")
            temporary.write_text(json.dumps(candidate, indent=1), encoding="utf-8")
            os.replace(temporary, self._runtime)
            self._stamps = None
        return self.get()

    def _reload(self) -> None:
        merged = self._base.model_dump()
        for path in self._files:
            if _mtime(path) is None:
                self._layers.pop(path, None)
                continue
            try:
                layer = json.loads(path.read_text(encoding="utf-8"))
                if not isinstance(layer, dict):
                    raise ValueError("un objet JSON est attendu")
                Settings.model_validate({**merged, **_coerce(layer)})
                self._layers[path] = layer
            except (OSError, ValueError) as ex:
                # A bad edit must not stop the service: keep the last valid content of this layer.
                log.error("Ignoring invalid %s: %s", path, ex)
            merged.update(_coerce(self._layers.get(path, {})))
        # Without any layer the base settings are used as given (tests inject providers outside the Literal).
        self._current = Settings.model_validate(merged) if self._layers else self._base


def _mtime(path: Path) -> float | None:
    try:
        return path.stat().st_mtime_ns
    except OSError:
        return None


def _coerce(overrides: dict) -> dict:
    """Secrets given in the JSON file are wrapped like the environment ones."""
    result = dict(overrides)
    for key in ("gemini_api_key", "dataset_upload_url"):
        if isinstance(result.get(key), str):
            result[key] = SecretStr(result[key])
    return result
