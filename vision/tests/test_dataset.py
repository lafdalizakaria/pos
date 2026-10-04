from __future__ import annotations

import json

import httpx

from app.dataset import DatasetStore
from app.models import FeedbackRequest, RecognizedItem, RecognizeResponse


def response(recognition_id: str) -> RecognizeResponse:
    return RecognizeResponse(
        recognition_id=recognition_id,
        items=[RecognizedItem(article_code="A", confidence=0.9, bbox=[0, 0, 10, 10], bbox_yolo=[0.5, 0.5, 1, 1])],
        provider="mock",
        latency_ms=10,
        image_width=10,
        image_height=10,
    )


def feedback(recognition_id: str) -> FeedbackRequest:
    return FeedbackRequest.model_validate(
        {
            "recognition_id": recognition_id,
            "lines": [{"article_code": "A", "quantity": 2, "source": "VisionConfirmed", "prediction_index": 0}],
        }
    )


def test_quantity_above_the_detected_boxes_needs_annotation(tmp_path):
    store = DatasetStore(str(tmp_path))
    store.save(response("abcdef0123456789"), b"jpeg", [], "REG")
    assert store.apply_feedback(feedback("abcdef0123456789")) == (True, 1, True)


def test_rotation_keeps_the_most_recent_items(tmp_path):
    store = DatasetStore(str(tmp_path), max_items=2)
    for i in range(4):
        store.save(response(f"id{i:014d}"), b"jpeg", [], None)
    assert store.count() == 2
    assert {p.stem for p in tmp_path.glob("*/*.json")} == {"id00000000000002", "id00000000000003"}
    assert len(list(tmp_path.glob("*/*.jpg"))) == 2


def test_find_refuses_path_traversal(tmp_path):
    assert DatasetStore(str(tmp_path)).find("../../etc/passwd") is None


def test_only_validated_items_are_uploaded_once(tmp_path):
    store = DatasetStore(str(tmp_path))
    store.save(response("aaaaaaaaaaaaaaaa"), b"jpeg", [], None)
    store.save(response("bbbbbbbbbbbbbbbb"), b"jpeg", [], None)
    store.apply_feedback(feedback("aaaaaaaaaaaaaaaa"))
    requests: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        requests.append(request)
        return httpx.Response(201)

    client = httpx.Client(transport=httpx.MockTransport(handler))
    assert store.upload_pending("https://acct.blob.example/dataset?sv=1&sig=abc", client) == 1
    assert [r.url.path.rsplit("/", 1)[1] for r in requests] == ["aaaaaaaaaaaaaaaa.json", "aaaaaaaaaaaaaaaa.jpg"]
    assert all(r.url.query == b"sv=1&sig=abc" and r.headers["x-ms-blob-type"] == "BlockBlob" for r in requests)
    assert store.upload_pending("https://acct.blob.example/dataset?sv=1", client) == 0
    record = json.loads(next(tmp_path.glob("*/aaaaaaaaaaaaaaaa.json")).read_text())
    assert record["uploaded"] is True


def test_failed_upload_is_retried_later(tmp_path):
    store = DatasetStore(str(tmp_path))
    store.save(response("aaaaaaaaaaaaaaaa"), None, [], None)
    store.apply_feedback(feedback("aaaaaaaaaaaaaaaa"))
    failing = httpx.Client(transport=httpx.MockTransport(lambda r: httpx.Response(503)))
    assert store.upload_pending("https://x/c", failing) == 0
    assert len(store.pending_uploads()) == 1
