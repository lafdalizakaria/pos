from __future__ import annotations

import json

import cv2
import numpy as np

from app.models import RecognizeResponse
from app.providers.base import ProviderUnavailableError, RawDetection
from tests.conftest import CANDIDATES, StubProvider, client_for, make_jpeg, post_recognize


def test_health_reports_provider(settings):
    response = client_for(settings).get("/health")
    assert response.status_code == 200
    assert response.json() == {
        "status": "ok",
        "provider": "mock",
        "provider_ready": True,
        "detail": None,
        "dataset_items": 0,
    }


def test_health_is_degraded_when_gemini_has_no_key(settings):
    settings = settings.model_copy(update={"provider": "gemini"})
    body = client_for(settings).get("/health").json()
    assert body["status"] == "degraded"
    assert body["provider_ready"] is False
    assert "GEMINI_API_KEY" in body["detail"]


def test_mock_recognition_matches_the_contract(settings):
    response = post_recognize(client_for(settings))
    assert response.status_code == 200
    body = RecognizeResponse.model_validate(response.json())
    assert body.provider == "mock"
    assert (body.image_width, body.image_height) == (1600, 1200)
    assert 1 <= len(body.items) <= 3
    codes = {c["article_code"] for c in CANDIDATES}
    for item in body.items:
        assert item.article_code in codes
        assert 0 <= item.confidence <= 1
        x0, y0, x1, y1 = item.bbox
        assert 0 <= x0 < x1 <= 1600 and 0 <= y0 < y1 <= 1200
        assert all(0 <= v <= 1 for v in item.bbox_yolo)
        assert all(a.article_code in codes and a.article_code != item.article_code for a in item.alternatives)
    confidences = [i.confidence for i in body.items]
    assert confidences == sorted(confidences, reverse=True)


def test_mock_is_deterministic_for_the_same_image(settings):
    client = client_for(settings)
    image = make_jpeg(seed=7)
    first = post_recognize(client, image).json()
    second = post_recognize(client, image).json()
    assert [i["article_code"] for i in first["items"]] == [i["article_code"] for i in second["items"]]
    assert first["recognition_id"] != second["recognition_id"]


def test_codes_outside_the_menu_are_rejected(settings):
    stub = StubProvider(
        [
            RawDetection("PLT-COUS", 0.93, (10, 10, 200, 200), [("PIZZA", 0.5), ("PLT-POUL", 0.4)]),
            RawDetection("PIZZA", 0.99, (300, 10, 500, 200), []),
            RawDetection("des-fruit", 1.7, None, []),
        ]
    )
    settings = settings.model_copy(update={"provider": "stub"})
    body = post_recognize(client_for(settings, stub=stub)).json()
    codes = [i["article_code"] for i in body["items"]]
    assert codes == ["DES-FRUIT", "PLT-COUS"]
    assert body["rejected_codes"] == ["PIZZA"]
    assert body["items"][0]["confidence"] == 1.0
    assert body["items"][0]["bbox"] is None
    assert body["items"][1]["alternatives"] == [{"article_code": "PLT-POUL", "confidence": 0.4}]


def test_boxes_are_returned_in_original_image_pixels(settings):
    # 1600x1200 is resized to 1024x768 for the model: a box on the resized image is scaled back by 1.5625.
    stub = StubProvider([RawDetection("BOI-EAU", 0.9, (512, 384, 1024, 768), [])])
    settings = settings.model_copy(update={"provider": "stub"})
    body = post_recognize(client_for(settings, stub=stub)).json()
    assert stub.calls[0][0].width == 1024 and stub.calls[0][0].height == 768
    item = body["items"][0]
    assert item["bbox"] == [800, 600, 1600, 1200]
    assert item["bbox_yolo"] == [0.75, 0.75, 0.5, 0.5]


def test_candidates_are_deduplicated_and_uppercased(settings):
    stub = StubProvider()
    settings = settings.model_copy(update={"provider": "stub"})
    candidates = [{"article_code": "plt-cous", "label": "A"}, {"article_code": "PLT-COUS", "label": "B"}]
    post_recognize(client_for(settings, stub=stub), candidates=candidates)
    assert [c.article_code for c in stub.calls[0][1]] == ["PLT-COUS"]


def test_slow_provider_times_out(settings):
    stub = StubProvider([RawDetection("BOI-EAU", 0.9, None, [])], delay_s=2)
    settings = settings.model_copy(update={"provider": "stub", "request_timeout_s": 0.2})
    response = post_recognize(client_for(settings, stub=stub))
    assert response.status_code == 504
    assert response.json()["detail"]["code"] == "timeout"


def test_provider_errors_never_crash_the_service(settings):
    settings = settings.model_copy(update={"provider": "stub"})
    for error, status, code in [
        (ProviderUnavailableError("no key"), 503, "provider_unavailable"),
        (json.JSONDecodeError("bad", "x", 0), 502, "invalid_provider_answer"),
        (ConnectionError("network"), 502, "provider_error"),
    ]:
        response = post_recognize(client_for(settings, stub=StubProvider(error=error)))
        assert response.status_code == status
        assert response.json()["detail"]["code"] == code


def test_phase5_providers_answer_503(settings):
    settings = settings.model_copy(update={"provider": "yolo"})
    response = post_recognize(client_for(settings))
    assert response.status_code == 503


def test_invalid_inputs(settings):
    client = client_for(settings)
    assert post_recognize(client, image=b"not an image").json()["detail"]["code"] == "invalid_image"
    assert post_recognize(client, candidates=[]).json()["detail"]["code"] == "no_candidates"
    response = post_recognize(client, candidates=[{"label": "sans code"}])
    assert response.status_code == 422
    assert response.json()["detail"]["code"] == "invalid_candidates"


def test_recognition_and_feedback_are_stored_in_the_dataset(settings, tmp_path):
    stub = StubProvider(
        [
            RawDetection("PLT-COUS", 0.95, (0, 0, 512, 384), []),
            RawDetection("DES-FRUIT", 0.7, (512, 384, 1024, 768), [("BOI-EAU", 0.2)]),
        ]
    )
    settings = settings.model_copy(update={"provider": "stub"})
    client = client_for(settings, stub=stub)
    body = post_recognize(client).json()

    files = list((tmp_path / "dataset").glob("*/*"))
    assert sorted(f.suffix for f in files) == [".jpg", ".json"]
    feedback = {
        "recognition_id": body["recognition_id"],
        "ticket_id": "T-1",
        "lines": [
            {"article_code": "PLT-COUS", "quantity": 1, "source": "VisionAuto", "prediction_index": 0},
            {"article_code": "BOI-EAU", "quantity": 1, "source": "VisionCorrected", "prediction_index": 1},
        ],
    }
    response = client.post("/feedback", json=feedback)
    assert response.json() == {
        "recognition_id": body["recognition_id"],
        "stored": True,
        "labels": 2,
        "needs_annotation": False,
    }
    record = json.loads(next(f for f in files if f.suffix == ".json").read_text(encoding="utf-8"))
    assert record["register_id"] == "REG-1"
    assert record["labels"][1] == {"article_code": "BOI-EAU", "bbox_yolo": [0.75, 0.75, 0.5, 0.5], "corrected": True}
    assert client.get("/health").json()["dataset_items"] == 1

    # A line added by hand (no box) flags the image for manual annotation.
    feedback["lines"].append({"article_code": "DES-FRUIT", "quantity": 1, "source": "Manual"})
    assert client.post("/feedback", json=feedback).json()["needs_annotation"] is True


def test_feedback_for_an_unknown_recognition_is_404(settings):
    response = client_for(settings).post("/feedback", json={"recognition_id": "0123456789abcdef", "lines": []})
    assert response.status_code == 404
    assert response.json()["detail"]["code"] == "unknown_recognition"


def test_dataset_can_be_disabled(settings, tmp_path):
    settings = settings.model_copy(update={"dataset_enabled": False})
    client = client_for(settings)
    body = post_recognize(client).json()
    assert not (tmp_path / "dataset").exists()
    response = client.post("/feedback", json={"recognition_id": body["recognition_id"], "lines": []})
    assert response.json()["stored"] is False


def test_images_with_a_face_are_not_stored(settings, tmp_path, monkeypatch):
    monkeypatch.setattr("app.main.contains_face", lambda pixels: True)
    body = post_recognize(client_for(settings)).json()
    files = list((tmp_path / "dataset").glob("*/*"))
    assert [f.suffix for f in files] == [".json"]
    record = json.loads(files[0].read_text(encoding="utf-8"))
    assert record["image"] is None
    assert record["image_skipped_reason"] == "face_detected"
    assert record["recognition_id"] == body["recognition_id"]


def test_face_detector_ignores_a_tray_like_image():
    from app.images import contains_face

    image = np.full((600, 800, 3), 200, dtype=np.uint8)
    cv2.circle(image, (400, 300), 150, (30, 120, 200), -1)
    assert contains_face(image) is False


def test_upload_endpoint_requires_configuration(settings):
    response = client_for(settings).post("/dataset/upload")
    assert response.status_code == 409
