"""Local recognition service of a register (FastAPI, listens on 127.0.0.1 only).

POST /recognize  multipart: ``image`` (JPEG) + ``candidates`` (JSON list) [+ ``register_id``]
                 [+ file parts ``reference_<ARTICLE_CODE>``: reference photos, resized here]
GET  /camera/capture  JPEG of the tray area (fixed exposure, cropped: never a face)
POST /feedback   JSON: lines validated by the cashier
GET  /health
POST /dataset/upload  pushes validated dataset items to the central container (if configured)
GET  /models, PUT /models/{version}  installed YOLO models; installation of a model pushed by the register
PUT  /runtime    provider + model chosen in the back-office for the site (pushed by the register)
"""

from __future__ import annotations

import asyncio
import base64
import ipaddress
import json
import logging
import time
import uuid
from typing import Annotated

from fastapi import BackgroundTasks, FastAPI, File, Form, HTTPException, Request, Response, UploadFile
from pydantic import TypeAdapter, ValidationError

from app.camera import Camera, CameraUnavailableError
from app.config import Settings, SettingsProvider
from app.dataset import DatasetStore
from app.images import InvalidImageError, contains_face, prepare
from app.model_store import ModelInstallError, ModelManifest, ModelStore
from app.models import Candidate, FeedbackRequest, FeedbackResponse, HealthResponse, RecognizeResponse, RuntimeRequest
from app.providers.base import ProviderResult, ProviderUnavailableError
from app.providers.registry import ProviderRegistry
from app.recognition import finalize

log = logging.getLogger("vision")
_candidates_adapter = TypeAdapter(list[Candidate])
MAX_IMAGE_BYTES = 8 * 1024 * 1024


def create_app(
    settings: SettingsProvider | None = None, registry: ProviderRegistry | None = None, camera: Camera | None = None
) -> FastAPI:
    settings = settings or SettingsProvider()
    registry = registry or ProviderRegistry()
    camera = camera or Camera()
    app = FastAPI(title="Newrest POS — service vision", version="0.4.0")

    def dataset(current: Settings) -> DatasetStore:
        return DatasetStore(current.dataset_dir, current.dataset_max_items)

    def health_of(current: Settings) -> HealthResponse:
        ready, detail = registry.get(current).readiness(current)
        return HealthResponse(
            status="ok" if ready else "degraded",
            provider=current.provider,
            provider_ready=ready,
            detail=detail,
            dataset_items=dataset(current).count() if current.dataset_enabled else 0,
            model_version=current.yolo_model_version,
            models_installed=[m.version for m in ModelStore(current.models_dir).installed()],
            local_override=settings.local_override_keys() & {"provider", "yolo_model_version"} != set(),
        )

    @app.get("/health", response_model=HealthResponse)
    async def health() -> HealthResponse:
        return await asyncio.to_thread(health_of, settings.get())

    @app.get("/models")
    async def list_models() -> dict:
        current = settings.get()
        return {
            "active": current.yolo_model_version,
            "installed": [m.model_dump() for m in ModelStore(current.models_dir).installed()],
        }

    @app.put("/models/{version}")
    async def install_model(
        version: str,
        model: Annotated[UploadFile, File(description="model.onnx")],
        manifest: Annotated[str, Form(description="manifest.json produit par l'entraînement")],
        response: Response,
    ) -> dict:
        current = settings.get()
        try:
            parsed = ModelManifest.model_validate_json(manifest)
        except ValidationError as ex:
            raise HTTPException(422, detail={"code": "invalid_manifest", "message": str(ex.errors()[:3])}) from ex
        if parsed.version != version:
            raise HTTPException(422, detail={"code": "invalid_manifest", "message": "Version du manifeste différente de l'URL."})

        async def chunks():
            while chunk := await model.read(1024 * 1024):
                yield chunk

        data = [c async for c in chunks()]
        try:
            created = await asyncio.to_thread(ModelStore(current.models_dir).install, parsed, data)
        except ModelInstallError as ex:
            raise HTTPException(
                409 if ex.code == "version_conflict" else 422, detail={"code": ex.code, "message": str(ex)}
            ) from ex
        response.status_code = 201 if created else 200
        return {"version": version, "installed": created}

    @app.put("/runtime", response_model=HealthResponse)
    async def set_runtime(request: RuntimeRequest) -> HealthResponse:
        current = settings.get()
        store = ModelStore(current.models_dir)
        if request.provider in ("yolo", "hybrid") and request.model_version is None:
            raise HTTPException(422, detail={"code": "model_required", "message": "Le mode YOLO/hybride exige un modèle."})
        if request.model_version is not None and store.get(request.model_version) is None:
            raise HTTPException(409, detail={"code": "model_not_installed", "message": f"Modèle {request.model_version} absent."})
        values: dict = {"provider": request.provider, "yolo_model_version": request.model_version}
        if request.hybrid_min_confidence is not None:
            values["hybrid_min_confidence"] = request.hybrid_min_confidence
        previous = current.yolo_model_version
        updated = await asyncio.to_thread(settings.write_runtime, values)
        protect = {v for v in (updated.yolo_model_version, previous) if v}
        removed = await asyncio.to_thread(store.prune, updated.models_kept, protect)
        if removed:
            log.info("Old models removed: %s", removed)
        log.info("Runtime set by the register: provider=%s model=%s", updated.provider, updated.yolo_model_version)
        return await asyncio.to_thread(health_of, updated)

    @app.post("/recognize", response_model=RecognizeResponse)
    async def recognize(
        request: Request,
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
        parsed = await _attach_reference_photos(request, parsed, current)

        data = await image.read(MAX_IMAGE_BYTES + 1)
        if len(data) > MAX_IMAGE_BYTES:
            raise HTTPException(413, detail={"code": "image_too_large", "message": "Image > 8 Mo."})
        try:
            prepared = prepare(data, current.image_max_side, current.jpeg_quality)
        except InvalidImageError as ex:
            raise HTTPException(400, detail={"code": "invalid_image", "message": str(ex)}) from ex
        if current.face_guard and await asyncio.to_thread(contains_face, prepared.pixels):
            # Privacy: an image showing a face is neither sent to a model nor stored. The camera crop must be fixed.
            log.warning("Face detected in the tray image: refused (check VISION_CAMERA_CROP)")
            raise HTTPException(
                422, detail={"code": "face_detected", "message": "Visage détecté : recadrer la caméra sur le plateau."}
            )

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
        label = provider.name
        if isinstance(raw, ProviderResult):
            raw, label = raw.detections, raw.label
        latency = round((time.perf_counter() - started) * 1000)

        items, rejected = finalize(raw, parsed, prepared)
        if rejected:
            log.info("Rejected %d code(s) outside today's candidates: %s", len(rejected), rejected)
        response = RecognizeResponse(
            recognition_id=uuid.uuid4().hex,
            items=items,
            provider=label,
            latency_ms=latency,
            image_width=prepared.original_width,
            image_height=prepared.original_height,
            rejected_codes=rejected,
        )
        if current.dataset_enabled:
            try:
                dataset(current).save(response, data, parsed, register_id)
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

    @app.get("/camera/capture", response_class=Response, responses={200: {"content": {"image/jpeg": {}}}})
    async def capture() -> Response:
        current = settings.get()
        try:
            jpeg, width, height = await asyncio.to_thread(camera.capture_jpeg, current)
        except (CameraUnavailableError, ValueError) as ex:
            raise HTTPException(503, detail={"code": "camera_unavailable", "message": str(ex)}) from ex
        return Response(jpeg, media_type="image/jpeg", headers={"X-Image-Width": str(width), "X-Image-Height": str(height)})

    @app.post("/dataset/upload")
    async def upload() -> dict[str, int]:
        current = settings.get()
        if current.dataset_upload_url is None:
            raise HTTPException(409, detail={"code": "upload_not_configured", "message": "VISION_DATASET_UPLOAD_URL absente."})
        sent = await asyncio.to_thread(dataset(current).upload_pending, current.dataset_upload_url.get_secret_value())
        return {"uploaded": sent}

    return app


async def _attach_reference_photos(request: Request, candidates: list[Candidate], settings: Settings) -> list[Candidate]:
    """Reference photos come as file parts ``reference_<code>`` (form fields are limited to 1 MB); resized to 512 px."""
    form = await request.form()
    photos: dict[str, list[str]] = {}
    total = 0
    for key, value in form.multi_items():
        if not key.startswith("reference_") or isinstance(value, str) or total >= settings.max_reference_photos_total:
            continue
        code = key.removeprefix("reference_").strip().upper()
        if len(photos.get(code, [])) >= settings.max_reference_photos_per_candidate:
            continue
        try:
            prepared = prepare(await value.read(MAX_IMAGE_BYTES), settings.reference_photo_max_side, 85)
        except InvalidImageError:
            continue
        photos.setdefault(code, []).append(base64.b64encode(prepared.jpeg).decode("ascii"))
        total += 1
    return [
        c.model_copy(update={"reference_photos": [*c.reference_photos, *photos[c.article_code]]})
        if c.article_code in photos
        else c
        for c in candidates
    ]


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
