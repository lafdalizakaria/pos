"""YOLO models installed on the register: ``<models_dir>/<version>/model.onnx`` + ``manifest.json``.

Models are trained centrally (``training/``), uploaded to the back-office and pushed here by the register. A model
is installed only if its SHA-256 matches its manifest and if ONNX Runtime loads it with the expected output shape
(4 box values + one score per class).
"""

from __future__ import annotations

import hashlib
import json
import logging
import shutil
import threading
import uuid
from collections.abc import Iterable
from datetime import UTC, datetime
from pathlib import Path

import numpy as np
from pydantic import BaseModel, Field, field_validator

log = logging.getLogger("vision.models")

VERSION_PATTERN = r"^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$"


class ModelManifest(BaseModel):
    """Written by ``python -m training train``; travels with the ONNX file (back-office → register → here)."""

    version: str = Field(pattern=VERSION_PATTERN)
    #: Article code of each class index, in order.
    classes: list[str] = Field(min_length=1, max_length=2000)
    imgsz: int = Field(default=640, ge=32, le=2048)
    sha256: str = Field(pattern=r"^[0-9a-f]{64}$")
    size_bytes: int = Field(gt=0)
    created_at: str | None = None
    framework: str | None = None
    base_model: str | None = None
    metrics: dict = Field(default_factory=dict)
    dataset: dict = Field(default_factory=dict)

    @field_validator("classes")
    @classmethod
    def _unique_codes(cls, classes: list[str]) -> list[str]:
        codes = [c.strip().upper() for c in classes]
        if len(set(codes)) != len(codes) or any(not c for c in codes):
            raise ValueError("classes must be unique, non-empty article codes")
        return codes


class ModelInstallError(ValueError):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


class ModelStore:
    def __init__(self, root: str | Path) -> None:
        self.root = Path(root)
        self._lock = threading.Lock()

    def get(self, version: str) -> tuple[Path, ModelManifest] | None:
        folder = self.root / version
        if not folder.name or folder.parent != self.root:
            return None
        model, manifest = folder / "model.onnx", folder / "manifest.json"
        if not model.is_file() or not manifest.is_file():
            return None
        try:
            return model, ModelManifest.model_validate_json(manifest.read_text(encoding="utf-8"))
        except ValueError:
            return None

    def installed(self) -> list[ModelManifest]:
        if not self.root.is_dir():
            return []
        found = [(f.stat().st_mtime, self.get(f.name)) for f in self.root.iterdir() if f.is_dir() and not f.name.startswith(".")]
        return [m for _, entry in sorted(found, key=lambda x: x[0]) if entry is not None for m in [entry[1]]]

    def install(self, manifest: ModelManifest, chunks: Iterable[bytes]) -> bool:
        """Installs the model; returns False when the same version is already installed with the same content."""
        existing = self.get(manifest.version)
        if existing is not None:
            if existing[1].sha256 == manifest.sha256:
                _drain(chunks)
                return False
            raise ModelInstallError("version_conflict", f"La version {manifest.version} existe avec un autre contenu.")

        self.root.mkdir(parents=True, exist_ok=True)
        staging = self.root / f".staging-{uuid.uuid4().hex}"
        staging.mkdir()
        try:
            digest, size = hashlib.sha256(), 0
            with (staging / "model.onnx").open("wb") as out:
                for chunk in chunks:
                    digest.update(chunk)
                    size += len(chunk)
                    out.write(chunk)
            if size != manifest.size_bytes or digest.hexdigest() != manifest.sha256:
                raise ModelInstallError(
                    "checksum_mismatch", "Le fichier du modèle ne correspond pas à son manifeste (taille ou SHA-256)."
                )
            validate_onnx(staging / "model.onnx", manifest)
            stamped = manifest.model_copy(update={"dataset": {**manifest.dataset, "installed_at": datetime.now(UTC).isoformat()}})
            (staging / "manifest.json").write_text(stamped.model_dump_json(indent=1), encoding="utf-8")
            with self._lock:
                staging.rename(self.root / manifest.version)
        finally:
            if staging.exists():
                shutil.rmtree(staging, ignore_errors=True)
        log.info("Model %s installed (%d classes)", manifest.version, len(manifest.classes))
        return True

    def prune(self, keep: int, protect: set[str]) -> list[str]:
        """Removes the oldest versions beyond ``keep`` (never the protected ones: active, previous)."""
        removed = []
        versions = [m.version for m in self.installed()]
        for version in versions[: max(0, len(versions) - keep)]:
            if version not in protect:
                shutil.rmtree(self.root / version, ignore_errors=True)
                removed.append(version)
        return removed


def validate_onnx(path: Path, manifest: ModelManifest) -> None:
    import onnxruntime as ort

    try:
        session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
    except Exception as ex:  # noqa: BLE001 - onnxruntime raises its own exception types
        raise ModelInstallError("invalid_model", f"Modèle ONNX illisible : {ex}") from ex
    shape = session.get_inputs()[0].shape
    size = shape[2] if isinstance(shape[2], int) else manifest.imgsz
    output = session.run(None, {session.get_inputs()[0].name: np.zeros((1, 3, size, size), np.float32)})[0]
    if output.ndim != 3 or output.shape[1] != 4 + len(manifest.classes):
        raise ModelInstallError(
            "invalid_model",
            f"Sortie {list(output.shape)} incompatible avec {len(manifest.classes)} classes (attendu [1, 4+classes, N]).",
        )


def _drain(chunks: Iterable[bytes]) -> None:
    for _ in chunks:
        pass


def read_dev_manifest(model_path: Path) -> ModelManifest:
    """Development models (``VISION_YOLO_MODEL_PATH``): classes in ``<model>.json`` (``{"classes": [...]}``)."""
    data = json.loads(model_path.with_suffix(".json").read_text(encoding="utf-8"))
    content = model_path.read_bytes()
    return ModelManifest.model_validate(
        {"version": "dev", "imgsz": 640, **data, "sha256": hashlib.sha256(content).hexdigest(), "size_bytes": len(content)}
    )
