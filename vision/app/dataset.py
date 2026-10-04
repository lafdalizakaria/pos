"""Local dataset of recognitions: image + predictions, completed by the lines validated at the register.

Layout: ``<dataset_dir>/<yyyy-mm-dd>/<recognition_id>.jpg`` and ``.json``. The JSON holds the candidates, the
predictions, the validated lines and the YOLO labels derived from them (a corrected line keeps the box of the
prediction it replaces; a line added by hand has no box and flags the image for manual annotation).
Optionally pushed to a central container (HTTP PUT on a signed URL, e.g. Azure Blob SAS).
"""

from __future__ import annotations

import json
import logging
import threading
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import urlsplit, urlunsplit

import httpx

from app.models import Candidate, FeedbackRequest, RecognizeResponse

log = logging.getLogger("vision.dataset")


class DatasetStore:
    def __init__(self, root: str, max_items: int = 20000) -> None:
        self.root = Path(root)
        self.max_items = max_items
        self._lock = threading.Lock()

    def count(self) -> int:
        return sum(1 for _ in self.root.glob("*/*.json")) if self.root.exists() else 0

    def save(
        self,
        response: RecognizeResponse,
        jpeg: bytes | None,
        candidates: list[Candidate],
        register_id: str | None,
        skipped_reason: str | None = None,
    ) -> None:
        folder = self.root / datetime.now(UTC).strftime("%Y-%m-%d")
        folder.mkdir(parents=True, exist_ok=True)
        record = {
            "recognition_id": response.recognition_id,
            "captured_at": datetime.now(UTC).isoformat(),
            "register_id": register_id,
            "provider": response.provider,
            "latency_ms": response.latency_ms,
            "image": f"{response.recognition_id}.jpg" if jpeg else None,
            "image_skipped_reason": skipped_reason,
            "image_width": response.image_width,
            "image_height": response.image_height,
            "candidates": [c.article_code for c in candidates],
            "predictions": [i.model_dump() for i in response.items],
            "rejected_codes": response.rejected_codes,
            "validated_lines": None,
            "labels": [],
            "needs_annotation": None,
            "uploaded": False,
        }
        with self._lock:
            if jpeg:
                (folder / f"{response.recognition_id}.jpg").write_bytes(jpeg)
            (folder / f"{response.recognition_id}.json").write_text(
                json.dumps(record, ensure_ascii=False, indent=1), encoding="utf-8"
            )
        self._rotate()

    def find(self, recognition_id: str) -> Path | None:
        if not recognition_id.replace("-", "").isalnum():
            return None
        matches = list(self.root.glob(f"*/{recognition_id}.json")) if self.root.exists() else []
        return matches[0] if matches else None

    def apply_feedback(self, feedback: FeedbackRequest) -> tuple[bool, int, bool]:
        """Stores the validated lines and derives YOLO labels. Returns (stored, label count, needs annotation)."""
        path = self.find(feedback.recognition_id)
        if path is None:
            return False, 0, False
        with self._lock:
            record = json.loads(path.read_text(encoding="utf-8"))
            predictions = record["predictions"]
            labels = []
            needs_annotation = False
            for line in feedback.lines:
                index = line.prediction_index
                if index is not None and 0 <= index < len(predictions) and predictions[index].get("bbox_yolo"):
                    labels.append(
                        {
                            "article_code": line.article_code,
                            "bbox_yolo": predictions[index]["bbox_yolo"],
                            "corrected": predictions[index]["article_code"] != line.article_code,
                        }
                    )
                    extra = line.quantity - 1
                else:
                    extra = line.quantity
                if extra > 0:
                    needs_annotation = True
            record["ticket_id"] = feedback.ticket_id
            record["validated_lines"] = [line.model_dump() for line in feedback.lines]
            record["labels"] = labels
            record["needs_annotation"] = needs_annotation or record["image"] is None
            record["feedback_at"] = datetime.now(UTC).isoformat()
            path.write_text(json.dumps(record, ensure_ascii=False, indent=1), encoding="utf-8")
        return True, len(labels), record["needs_annotation"]

    def pending_uploads(self) -> list[Path]:
        if not self.root.exists():
            return []
        result = []
        for path in sorted(self.root.glob("*/*.json")):
            record = json.loads(path.read_text(encoding="utf-8"))
            if not record.get("uploaded") and record.get("validated_lines") is not None:
                result.append(path)
        return result

    def upload_pending(self, container_url: str, client: httpx.Client | None = None, limit: int = 200) -> int:
        """PUTs validated items (JSON + image) to ``<container>/<date>/<id>.<ext>?<signature>``. Returns the count sent."""
        sent = 0
        own = client is None
        client = client or httpx.Client(timeout=30)
        try:
            for path in self.pending_uploads()[:limit]:
                record = json.loads(path.read_text(encoding="utf-8"))
                blobs = [(path.name, path.read_bytes(), "application/json")]
                if record.get("image"):
                    image = path.with_suffix(".jpg")
                    if image.exists():
                        blobs.append((image.name, image.read_bytes(), "image/jpeg"))
                try:
                    for name, data, content_type in blobs:
                        response = client.put(
                            _blob_url(container_url, f"{path.parent.name}/{name}"),
                            content=data,
                            headers={"x-ms-blob-type": "BlockBlob", "Content-Type": content_type},
                        )
                        response.raise_for_status()
                except httpx.HTTPError as ex:
                    log.warning("Dataset upload failed for %s: %s", path.stem, ex)
                    break
                record["uploaded"] = True
                with self._lock:
                    path.write_text(json.dumps(record, ensure_ascii=False, indent=1), encoding="utf-8")
                sent += 1
        finally:
            if own:
                client.close()
        return sent

    def _rotate(self) -> None:
        records = sorted(self.root.glob("*/*.json"))
        for path in records[: max(0, len(records) - self.max_items)]:
            path.unlink(missing_ok=True)
            path.with_suffix(".jpg").unlink(missing_ok=True)


def _blob_url(container_url: str, name: str) -> str:
    parts = urlsplit(container_url)
    return urlunsplit((parts.scheme, parts.netloc, parts.path.rstrip("/") + "/" + name, parts.query, ""))
