"""Real training (Ultralytics, CPU) on synthetic trays, ONNX export, installation and recognition by the service.

Needs the ``training`` extra: ``uv sync --extra training`` then ``uv run pytest -m training``.
"""

from __future__ import annotations

import json

import cv2
import numpy as np
import pytest

pytest.importorskip("ultralytics")

from training.dataset import build  # noqa: E402
from training.synthetic import draw_tray, generate  # noqa: E402
from training.train import QualityGateError, train  # noqa: E402

pytestmark = pytest.mark.training


def test_train_export_install_and_recognize(tmp_path, settings):
    from tests.conftest import client_for

    generate(tmp_path / "src", 120, seed=1)
    build([tmp_path / "src"], tmp_path / "ds", val_ratio=0.2)
    package = train(tmp_path / "ds", tmp_path / "out", base="yolo11n.yaml", epochs=40, imgsz=320, batch=16,
                    version="test-1", min_map50=0.5, patience=0)  # fmt: skip
    manifest = json.loads((package / "manifest.json").read_text())
    assert manifest["classes"] == ["CSC-VND", "EAU-50", "FRU-SAI"]
    assert manifest["metrics"]["map50"] >= 0.5
    assert manifest["imgsz"] == 320

    client = client_for(settings)
    pushed = client.put("/models/test-1", files={"model": ("model.onnx", (package / "model.onnx").read_bytes())},
                        data={"manifest": (package / "manifest.json").read_text()})  # fmt: skip
    assert pushed.status_code == 201, pushed.text
    assert client.put("/runtime", json={"provider": "yolo", "model_version": "test-1"}).json()["provider_ready"] is True

    image, labels = draw_tray(np.random.default_rng(99), codes=["CSC-VND", "FRU-SAI"])
    ok, jpeg = cv2.imencode(".jpg", image)
    candidates = [{"article_code": c, "label": c} for c in ["CSC-VND", "FRU-SAI", "EAU-50"]]
    response = client.post("/recognize", files={"image": ("t.jpg", jpeg.tobytes(), "image/jpeg")},
                           data={"candidates": json.dumps(candidates)})  # fmt: skip
    body = response.json()
    assert body["provider"] == "yolo:test-1"
    assert sorted(i["article_code"] for i in body["items"] if i["confidence"] >= 0.5) == ["CSC-VND", "FRU-SAI"]


def test_quality_gate_refuses_a_poor_model(tmp_path):
    generate(tmp_path / "src", 12, seed=2)
    build([tmp_path / "src"], tmp_path / "ds", val_ratio=0.3)
    with pytest.raises(QualityGateError):
        train(tmp_path / "ds", tmp_path / "out", base="yolo11n.yaml", epochs=1, imgsz=160, batch=4, version="poor", min_map50=0.9)
    assert not (tmp_path / "out" / "poor" / "model.onnx").exists()
