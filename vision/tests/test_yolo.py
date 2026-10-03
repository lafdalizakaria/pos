from __future__ import annotations

import json

import numpy as np
import pytest

from app.images import prepare
from app.model_store import ModelInstallError, ModelManifest, ModelStore
from app.models import Candidate
from app.providers.base import ProviderUnavailableError
from app.providers.yolo import YoloProvider, decode, letterbox
from tests.conftest import CANDIDATES, client_for, make_jpeg, post_recognize
from tests.onnx_models import manifest_for, yolo_like_model

CLASSES = ["PLT-COUS", "PLT-POUL", "DES-FRUIT", "BOI-EAU", "PIZZA"]
# Network input 320x320; a 1600x1200 image is resized to 1024x768 by the service, then letterboxed:
# ratio 0.3125, 320x240 placed at top=40. Input (160, 160) is pixel (512, 384) of the 1024x768 image.
BOXES = [
    (160.0, 160.0, 100.0, 80.0, {"PLT-COUS": 0.91, "PLT-POUL": 0.30}),
    (162.0, 158.0, 96.0, 82.0, {"PLT-POUL": 0.85}),  # same plate, other class: suppressed (class-agnostic NMS)
    (60.0, 100.0, 40.0, 40.0, {"PIZZA": 0.97, "BOI-EAU": 0.55}),  # off-menu best class: masked, water remains
    (280.0, 250.0, 30.0, 30.0, {"DES-FRUIT": 0.10}),  # below the minimum confidence
]


def install(settings, version="20261003-1200", classes=CLASSES, boxes=BOXES):
    data = yolo_like_model(classes, boxes)
    ModelStore(settings.models_dir).install(ModelManifest.model_validate(manifest_for(version, classes, data)), [data])
    return settings.model_copy(update={"yolo_model_version": version})


def candidates():
    return [Candidate.model_validate(c) for c in CANDIDATES]


def test_letterbox_keeps_the_aspect_ratio():
    canvas, ratio, left, top = letterbox(np.zeros((768, 1024, 3), np.uint8), 320)
    assert canvas.shape == (320, 320, 3)
    assert (ratio, left, top) == (0.3125, 0, 40)
    assert canvas[0, 0, 0] == 114


def test_decode_masks_off_menu_classes_and_suppresses_duplicates():
    data = np.zeros((1, 9, 8), np.float32)
    for i, (cx, cy, w, h, scores) in enumerate(BOXES):
        data[0, :4, i] = (cx, cy, w, h)
        for code, score in scores.items():
            data[0, 4 + CLASSES.index(code), i] = score
    detections = decode(data, CLASSES, {"PLT-COUS", "PLT-POUL", "BOI-EAU", "DES-FRUIT"}, 0.3125, 0, 40, 1024, 768, 0.25, 0.5)
    assert [(d.article_code, d.confidence) for d in detections] == [
        ("PLT-COUS", pytest.approx(0.91)),
        ("BOI-EAU", pytest.approx(0.55)),
    ]
    assert detections[0].bbox == (352, 256, 672, 512)
    assert detections[0].alternatives == [("PLT-POUL", pytest.approx(0.3))]


async def test_provider_answers_with_the_installed_model(settings):
    current = install(settings)
    provider = YoloProvider()
    assert provider.readiness(current) == (True, "modèle 20261003-1200 (5 articles)")
    result = await provider.recognize(prepare(make_jpeg()), candidates(), current)
    assert result.label == "yolo:20261003-1200"
    assert [d.article_code for d in result.detections] == ["PLT-COUS", "BOI-EAU"]


async def test_provider_without_model_is_unavailable(settings):
    provider = YoloProvider()
    current = settings.model_copy(update={"yolo_model_version": "absent"})
    assert provider.readiness(current)[0] is False
    with pytest.raises(ProviderUnavailableError):
        await provider.recognize(prepare(make_jpeg()), candidates(), current)


def test_development_model_path(settings, tmp_path):
    data = yolo_like_model(CLASSES, BOXES)
    (tmp_path / "dev.onnx").write_bytes(data)
    (tmp_path / "dev.json").write_text(json.dumps({"classes": CLASSES, "imgsz": 320}))
    current = settings.model_copy(update={"yolo_model_path": str(tmp_path / "dev.onnx")})
    assert YoloProvider().readiness(current)[0] is True


def test_install_checks_checksum_shape_and_conflicts(settings):
    store = ModelStore(settings.models_dir)
    data = yolo_like_model(CLASSES, BOXES)
    manifest = ModelManifest.model_validate(manifest_for("v1", CLASSES, data))
    with pytest.raises(ModelInstallError) as ex:
        store.install(manifest, [data[:-1] + b"x"])
    assert ex.value.code == "checksum_mismatch"
    wrong_classes = ModelManifest.model_validate(manifest_for("v1", CLASSES[:3], data))
    with pytest.raises(ModelInstallError) as ex:
        store.install(wrong_classes, [data])
    assert ex.value.code == "invalid_model"
    garbage = b"not an onnx model"
    with pytest.raises(ModelInstallError) as ex:
        store.install(ModelManifest.model_validate(manifest_for("v1", CLASSES, garbage)), [garbage])
    assert ex.value.code == "invalid_model"
    assert store.installed() == []

    assert store.install(manifest, [data]) is True
    assert store.install(manifest, [data]) is False, "same content: nothing to do"
    other = yolo_like_model(CLASSES, BOXES[:1])
    with pytest.raises(ModelInstallError) as ex:
        store.install(ModelManifest.model_validate(manifest_for("v1", CLASSES, other)), [other])
    assert ex.value.code == "version_conflict"
    assert [m.version for m in store.installed()] == ["v1"]
    assert store.get("../v1") is None


def test_manifest_validation():
    data = b"x"
    with pytest.raises(ValueError):
        ModelManifest.model_validate(manifest_for("../evil", CLASSES, data))
    with pytest.raises(ValueError):
        ModelManifest.model_validate(manifest_for("v1", ["A", "a"], data))


def test_register_pushes_a_model_then_switches_the_provider(settings):
    client = client_for(settings)
    data = yolo_like_model(CLASSES, BOXES)
    manifest = manifest_for("20261003-1200", CLASSES, data)

    refused = client.put("/runtime", json={"provider": "yolo", "model_version": "20261003-1200"})
    assert refused.status_code == 409 and refused.json()["detail"]["code"] == "model_not_installed"
    assert client.put("/runtime", json={"provider": "yolo"}).json()["detail"]["code"] == "model_required"

    def push(manifest_json, payload=data, version="20261003-1200"):
        return client.put(
            f"/models/{version}", files={"model": ("model.onnx", payload)}, data={"manifest": json.dumps(manifest_json)}
        )

    assert push(manifest).status_code == 201
    assert push(manifest).status_code == 200
    assert push(manifest, version="other").json()["detail"]["code"] == "invalid_manifest"
    assert (
        push(manifest_for("v2", CLASSES, data), payload=b"tampered", version="v2").json()["detail"]["code"] == "checksum_mismatch"
    )
    assert client.get("/models").json()["installed"][0]["version"] == "20261003-1200"

    health = client.put("/runtime", json={"provider": "yolo", "model_version": "20261003-1200"}).json()
    assert health["provider"] == "yolo" and health["provider_ready"] is True and health["model_version"] == "20261003-1200"
    assert health["models_installed"] == ["20261003-1200"]

    response = post_recognize(client)
    assert response.status_code == 200
    body = response.json()
    assert body["provider"] == "yolo:20261003-1200"
    assert [i["article_code"] for i in body["items"]] == ["PLT-COUS", "BOI-EAU"]
    assert body["items"][0]["bbox"] == [550, 400, 1050, 800], "box in pixels of the register's 1600x1200 image"

    # Back to Gemini from the back-office: no redeployment, the model stays installed for a rollback.
    health = client.put("/runtime", json={"provider": "gemini"}).json()
    assert health["provider"] == "gemini" and health["model_version"] is None and health["models_installed"] == ["20261003-1200"]


def test_local_override_file_wins_over_the_register(settings, tmp_path):
    from fastapi.testclient import TestClient

    from app.config import SettingsProvider
    from app.main import create_app

    override = tmp_path / "vision.json"
    override.write_text(json.dumps({"provider": "mock"}))
    client = TestClient(create_app(SettingsProvider(settings, str(override))))
    health = client.put("/runtime", json={"provider": "gemini"}).json()
    assert health["provider"] == "mock" and health["local_override"] is True


def test_old_models_are_pruned_but_active_and_previous_are_kept(settings):
    client = client_for(settings.model_copy(update={"models_kept": 2}))
    for i in range(4):
        data = yolo_like_model(CLASSES, BOXES[i : i + 1] or BOXES[:1])
        version = f"v{i}"
        client.put(
            f"/models/{version}",
            files={"model": ("m.onnx", data)},
            data={"manifest": json.dumps(manifest_for(version, CLASSES, data))},
        )
        client.put("/runtime", json={"provider": "yolo", "model_version": version})
    assert client.get("/health").json()["models_installed"] == ["v2", "v3"]
