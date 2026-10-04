"""Training (Ultralytics YOLO), ONNX export and packaging for the back-office.

Output: ``<out>/<version>/model.onnx`` + ``manifest.json`` (classes in index order, input size, metrics, dataset
report, SHA-256). That pair is what the back-office accepts and the registers install. A model below the quality
gate (mAP50 on the validation split) is not packaged.
"""

from __future__ import annotations

import hashlib
import json
import shutil
from datetime import UTC, datetime
from pathlib import Path

from app.model_store import ModelManifest, validate_onnx


class QualityGateError(RuntimeError):
    pass


def default_version() -> str:
    return datetime.now(UTC).strftime("%Y%m%d-%H%M")


def train(
    dataset: Path,
    output: Path,
    base: str = "yolo11n.pt",
    epochs: int = 100,
    imgsz: int = 640,
    batch: int = 16,
    device: str = "cpu",
    version: str | None = None,
    min_map50: float | None = 0.5,
    patience: int = 30,
    seed: int = 0,
) -> Path:
    from ultralytics import YOLO, __version__

    version = version or default_version()
    classes = json.loads((dataset / "classes.json").read_text(encoding="utf-8"))
    report = json.loads((dataset / "report.json").read_text(encoding="utf-8"))
    runs = output / "runs"
    model = YOLO(base)
    model.train(
        data=str(dataset / "data.yaml"), epochs=epochs, imgsz=imgsz, batch=batch, device=device, patience=patience,
        project=str(runs.resolve()), name=version, exist_ok=True, plots=False, seed=seed, deterministic=True,
        workers=0, verbose=False, amp=device != "cpu",
    )  # fmt: skip
    best = YOLO(str(model.trainer.best))
    validation = best.val(data=str(dataset / "data.yaml"), imgsz=imgsz, batch=batch, device=device, plots=False, verbose=False)
    box = validation.box
    per_class = {classes[int(c)]: round(float(ap), 4) for c, ap in zip(box.ap_class_index, box.ap50, strict=False)}
    metrics = {"map50": round(float(box.map50), 4), "map50_95": round(float(box.map), 4), "ap50_per_class": per_class,
               "precision": round(float(box.mp), 4), "recall": round(float(box.mr), 4)}  # fmt: skip
    (runs / version).mkdir(parents=True, exist_ok=True)
    (runs / version / "metrics.json").write_text(json.dumps(metrics, indent=1), encoding="utf-8")
    if min_map50 is not None and metrics["map50"] < min_map50:
        raise QualityGateError(f"mAP50 {metrics['map50']:.3f} < {min_map50} : modèle non empaqueté (voir {runs / version}).")

    exported = Path(best.export(format="onnx", imgsz=imgsz, dynamic=False, simplify=False, opset=17))
    package = output / version
    package.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(exported, package / "model.onnx")
    data = (package / "model.onnx").read_bytes()
    manifest = ModelManifest(
        version=version, classes=classes, imgsz=imgsz, sha256=hashlib.sha256(data).hexdigest(), size_bytes=len(data),
        created_at=datetime.now(UTC).isoformat(), framework=f"ultralytics {__version__}", base_model=base,
        metrics=metrics, dataset={"train": report["train"], "val": report["val"], "instances": report["instances"]},
    )  # fmt: skip
    validate_onnx(package / "model.onnx", manifest)  # the registers will run exactly this check
    (package / "manifest.json").write_text(manifest.model_dump_json(indent=1), encoding="utf-8")
    return package
