"""Gemini (Google Gen AI SDK) provider.

* JSON output constrained by a schema; ``article_code`` is an enum of today's candidates.
* Boxes are requested as ``box_2d = [ymin, xmin, ymax, xmax]`` normalised to 0-1000 (Gemini convention) and
  converted to pixels of the resized image (then to the register's image size and YOLO by ``finalize``).
* Low temperature, image resized (1024 px by default), reference photos of the day attached when provided.
* Confidences are the model's own estimate: not calibrated. Phase 5 calibrates them with the cashiers' feedback.
"""

from __future__ import annotations

import base64
import json
from collections.abc import Callable
from typing import Any

from google import genai
from google.genai import types

from app import geometry
from app.config import Settings
from app.images import PreparedImage
from app.models import Candidate
from app.providers.base import ProviderUnavailableError, RawDetection

PROMPT = """Tu es le système de reconnaissance d'une caisse de restaurant d'entreprise au Maroc.
La photo montre un plateau vu du dessus. Identifie chaque article présent (plat, entrée, dessert, boisson, pain...),
uniquement parmi la liste des articles du menu du jour ci-dessous (codes autorisés).
Règles :
- un élément physique = une entrée (deux pains = deux entrées) ;
- n'invente aucun article : si un objet ne correspond à aucun article de la liste, ignore-le (couverts, serviettes, plateau) ;
- distingue soigneusement les plats proches (par exemple couscous viande / couscous poulet) d'après la description visuelle ;
- box_2d = [ymin, xmin, ymax, xmax] normalisés de 0 à 1000 ;
- confidence entre 0 et 1, honnête : baisse-la si tu hésites et donne le second choix dans alternatives.

Articles du menu du jour :
{candidates}
"""


def build_schema(codes: list[str]) -> dict[str, Any]:
    code = {"type": "string", "enum": codes}
    return {
        "type": "object",
        "properties": {
            "items": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "article_code": code,
                        "confidence": {"type": "number", "minimum": 0, "maximum": 1},
                        "box_2d": {"type": "array", "items": {"type": "integer"}, "minItems": 4, "maxItems": 4},
                        "alternatives": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "properties": {
                                    "article_code": code,
                                    "confidence": {"type": "number", "minimum": 0, "maximum": 1},
                                },
                                "required": ["article_code", "confidence"],
                            },
                        },
                    },
                    "required": ["article_code", "confidence", "box_2d"],
                },
            }
        },
        "required": ["items"],
    }


def describe(candidates: list[Candidate]) -> str:
    lines = []
    for c in candidates:
        line = f"- {c.article_code} : {c.label}"
        if c.category:
            line += f" ({c.category})"
        if c.visual_description:
            line += f" — {c.visual_description}"
        lines.append(line)
    return "\n".join(lines)


class GeminiProvider:
    name = "gemini"

    def __init__(self, client_factory: Callable[[Settings], Any] | None = None) -> None:
        self._client_factory = client_factory or _default_client
        self._clients: dict[str, Any] = {}

    def readiness(self, settings: Settings) -> tuple[bool, str | None]:
        if settings.gemini_api_key is None or not settings.gemini_api_key.get_secret_value():
            return False, "GEMINI_API_KEY manquante"
        return True, None

    def build_request(
        self, image: PreparedImage, candidates: list[Candidate], settings: Settings
    ) -> tuple[list[Any], types.GenerateContentConfig]:
        contents: list[Any] = [
            PROMPT.format(candidates=describe(candidates)),
            types.Part.from_bytes(data=image.jpeg, mime_type="image/jpeg"),
        ]
        for candidate in candidates:
            for photo in candidate.reference_photos[: settings.max_reference_photos_per_candidate]:
                contents.append(f"Photo de référence de {candidate.article_code} ({candidate.label}) :")
                contents.append(types.Part.from_bytes(data=base64.b64decode(photo), mime_type="image/jpeg"))
        config = types.GenerateContentConfig(
            temperature=settings.gemini_temperature,
            response_mime_type="application/json",
            response_json_schema=build_schema([c.article_code for c in candidates]),
            http_options=types.HttpOptions(timeout=int(settings.request_timeout_s * 1000)),
            thinking_config=types.ThinkingConfig(thinking_budget=settings.gemini_thinking_budget)
            if settings.gemini_thinking_budget is not None
            else None,
        )
        return contents, config

    async def recognize(self, image: PreparedImage, candidates: list[Candidate], settings: Settings) -> list[RawDetection]:
        ready, detail = self.readiness(settings)
        if not ready:
            raise ProviderUnavailableError(detail)
        contents, config = self.build_request(image, candidates, settings)
        client = self._client(settings)
        response = await client.aio.models.generate_content(model=settings.gemini_model, contents=contents, config=config)
        return parse_response(response.text, image.width, image.height)

    def _client(self, settings: Settings) -> Any:
        key = settings.gemini_api_key.get_secret_value() if settings.gemini_api_key else ""
        if key not in self._clients:
            self._clients = {key: self._client_factory(settings)}
        return self._clients[key]


def parse_response(text: str | None, width: int, height: int) -> list[RawDetection]:
    """Parses the JSON answer; invalid JSON is an error, invalid items are skipped."""
    if not text:
        return []
    payload = json.loads(text)
    detections = []
    for item in payload.get("items", []) if isinstance(payload, dict) else []:
        if not isinstance(item, dict) or "article_code" not in item:
            continue
        detections.append(
            RawDetection(
                article_code=str(item["article_code"]),
                confidence=float(item.get("confidence", 0) or 0),
                bbox=geometry.gemini_to_pixels(item.get("box_2d"), width, height),
                alternatives=[
                    (str(a.get("article_code", "")), float(a.get("confidence", 0) or 0))
                    for a in item.get("alternatives", []) or []
                    if isinstance(a, dict)
                ],
            )
        )
    return detections


def _default_client(settings: Settings) -> Any:
    return genai.Client(api_key=settings.gemini_api_key.get_secret_value() if settings.gemini_api_key else None)
