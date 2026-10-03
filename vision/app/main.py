"""Local recognition service of a register (FastAPI, listens on 127.0.0.1 only).

POST /recognize  multipart: ``image`` (JPEG) + ``candidates`` (JSON list) [+ ``register_id``]
POST /feedback   JSON: lines validated by the cashier
GET  /health
POST /dataset/upload  pushes validated dataset items to the central container (if configured)
"""

from __future__ import annotations

import asyncio
import ipaddress
import json
import logging
import time
import uuid
from typing import Annotated

from fastapi import BackgroundTasks, FastAPI, File, Form, HTTPException, UploadFile
from pydantic import TypeAdapter, ValidationError

from app.config import Settings, SettingsProvider
from app.dataset import DatasetStore
from app.images import InvalidImageError, contains_face, prepare
from app.models import Candidate, FeedbackRequest, FeedbackResponse, HealthResponse, RecognizeResponse
from app.providers.base import ProviderUnavailableError
from app.providers.registry import ProviderRegistry
from app.recognition import finalize

log = logging.getLogger("vision")
_candidates_adapter = TypeAdapter(list[Candidate])
MAX_IMAGE_BYTES = 8 * 1024 * 1024


def create_app(settings: SettingsProvider | None = None, registry: ProviderRegistry | None = None) -> FastAPI:
    settings = settings or SettingsProvider()
    registry = registry or ProviderRegistry()
    app = FastAPI(title="Newrest POS — service vision", version="0.4.0")

    def dataset(current: Settings) -> DatasetStore:
        return DatasetStore(current.dataset_dir, current.dataset_max_items)

    @app.get("/health", response_model=HealthResponse)
    async def health() -> HealthResponse:
        current = settings.get()
        ready, detail = registry.get(current).readiness(current)
        return HealthResponse(
            status="ok" if ready else "degraded",
            provider=current.provider,
            provider_ready=ready,
            detail=detail,
            dataset_items=dataset(current).count() if current.dataset_enabled else 0,
        )

    @app.post("/recognize", response_model=RecognizeResponse)
    async def recognize(
        image: Annotated[UploadFile, File(description="Photo JPEG du plateau (déjà recadrée par la caisse)")],
        candidates: Annotated[str, Form(description="Liste JSON des articles du menu du jour")],
        register_id: Annotated[str | None, Form()] = None,
    ) -> RecognizeResponse:
        current = settings.get()
        try:
            parsed = _candidates_adapter.validate_json(candidates)
        except ValidationError as ex:
            raise HTTPException(422, detail={"code": "invalid_candidates", "message": str(ex.errors()[:3])}) from ex
        if not parsed:
            raise HTTPException(422, detail={"code": "no_candidates", "message": "Aucun article candidat (menu du jour vide)."})
        parsed = _unique(parsed)[: current.max_candidates]

        data = await image.read(MAX_IMAGE_BYTES + 1)
        if len(data) > MAX_IMAGE_BYTES:
            raise HTTPException(413, detail={"code": "image_too_large", "message": "Image > 8 Mo."})
        try:
            prepared = prepare(data, current.image_max_side, current.jpeg_quality)
        except InvalidImageError as ex:
            raise HTTPException(400, detail={"code": "invalid_image", "message": str(ex)}) from ex

        provider = registry.get(current)
        started = time.perf_counter()
        try:
            raw = await asyncio.wait_for(provider.recognize(prepared, parsed, current), timeout=current.request_timeout_s)
        except TimeoutError as ex:
            raise HTTPException(
                504, detail={"code": "timeout", "message": f"Reconnaissance > {current.request_timeout_s} s"}
            ) from ex
        except ProviderUnavailableError as ex:
            raise HTTPException(503, detail={"code": "provider_unavailable", "message": str(ex)}) from ex
        except (json.JSONDecodeError, ValueError) as ex:
            log.warning("Provider %s returned an invalid answer: %s", provider.name, ex)
            raise HTTPException(502, detail={"code": "invalid_provider_answer", "message": "Réponse du modèle invalide."}) from ex
        except Exception as ex:  # noqa: BLE001 - provider SDK errors (network, quota): never block the sale
            log.warning("Provider %s failed: %s", provider.name, ex)
            raise HTTPException(502, detail={"code": "provider_error", "message": type(ex).__name__}) from ex
        latency = round((time.perf_counter() - started) * 1000)

        items, rejected = finalize(raw, parsed, prepared)
        if rejected:
            log.info("Rejected %d code(s) outside today's candidates: %s", len(rejected), rejected)
        response = RecognizeResponse(
            recognition_id=uuid.uuid4().hex,
            items=items,
            provider=provider.name,
            latency_ms=latency,
            image_width=prepared.original_width,
            image_height=prepared.original_height,
            rejected_codes=rejected,
        )
        if current.dataset_enabled:
            face = current.face_guard and contains_face(prepared.pixels)
            try:
                dataset(current).save(response, None if face else data, parsed, register_id, "face_detected" if face else None)
            except OSError as ex:
                log.warning("Dataset write failed: %s", ex)
        return response

    @app.post("/feedback", response_model=FeedbackResponse)
    async def feedback(request: FeedbackRequest, background: BackgroundTasks) -> FeedbackResponse:
        current = settings.get()
        if not current.dataset_enabled:
            return FeedbackResponse(recognition_id=request.recognition_id, stored=False, labels=0, needs_annotation=False)
        stored, labels, needs = dataset(current).apply_feedback(request)
        if not stored:
            raise HTTPException(404, detail={"code": "unknown_recognition", "message": "Reconnaissance inconnue ou expirée."})
        if current.dataset_upload_url is not None:
            background.add_task(dataset(current).upload_pending, current.dataset_upload_url.get_secret_value())
        return FeedbackResponse(recognition_id=request.recognition_id, stored=True, labels=labels, needs_annotation=needs)

    @app.post("/dataset/upload")
    async def upload() -> dict[str, int]:
        current = settings.get()
        if current.dataset_upload_url is None:
            raise HTTPException(409, detail={"code": "upload_not_configured", "message": "VISION_DATASET_UPLOAD_URL absente."})
        sent = await asyncio.to_thread(dataset(current).upload_pending, current.dataset_upload_url.get_secret_value())
        return {"uploaded": sent}

    return app


def _unique(candidates: list[Candidate]) -> list[Candidate]:
    seen: set[str] = set()
    result = []
    for c in candidates:
        code = c.article_code.strip().upper()
        if code not in seen:
            seen.add(code)
            result.append(c.model_copy(update={"article_code": code}))
    return result


def ensure_loopback(settings: Settings) -> None:
    """The service only listens on localhost unless explicitly allowed (images never leave the register otherwise)."""
    host = "127.0.0.1" if settings.host == "localhost" else settings.host
    try:
        loopback = ipaddress.ip_address(host).is_loopback
    except ValueError:
        loopback = False
    if not loopback and not settings.allow_remote:
        raise SystemExit(
            f"Refus d'écouter sur {settings.host} : seul 127.0.0.1 est autorisé (VISION_ALLOW_REMOTE=true pour forcer)."
        )


def run() -> None:
    import uvicorn

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    current = SettingsProvider().get()
    ensure_loopback(current)
    uvicorn.run(create_app(), host=current.host, port=current.port, log_level="info")


if __name__ == "__main__":
    run()
