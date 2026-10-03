from __future__ import annotations

import base64
import json
from types import SimpleNamespace

import pytest

from app.images import prepare
from app.models import Candidate
from app.providers.base import ProviderUnavailableError
from app.providers.gemini import GeminiProvider, build_schema, parse_response
from tests.conftest import CANDIDATES, make_jpeg


class FakeModels:
    def __init__(self, text: str):
        self.text = text
        self.requests: list[dict] = []

    async def generate_content(self, **kwargs):
        self.requests.append(kwargs)
        return SimpleNamespace(text=self.text)


def fake_client(text: str):
    models = FakeModels(text)
    return SimpleNamespace(aio=SimpleNamespace(models=models)), models


@pytest.fixture
def gemini_settings(settings):
    return settings.model_copy(update={"provider": "gemini", "gemini_api_key": "x"})


def candidates(with_photo: bool = False) -> list[Candidate]:
    result = [Candidate.model_validate(c) for c in CANDIDATES]
    if with_photo:
        photo = base64.b64encode(make_jpeg(64, 64)).decode()
        result[0] = result[0].model_copy(update={"reference_photos": [photo, photo, photo]})
    return result


def test_schema_restricts_codes_to_the_candidates():
    schema = build_schema(["A", "B"])
    item = schema["properties"]["items"]["items"]
    assert item["properties"]["article_code"]["enum"] == ["A", "B"]
    assert item["properties"]["alternatives"]["items"]["properties"]["article_code"]["enum"] == ["A", "B"]
    assert set(item["required"]) == {"article_code", "confidence", "box_2d"}


def test_request_uses_schema_low_temperature_and_resized_image(gemini_settings):
    from pydantic import SecretStr

    settings = gemini_settings.model_copy(update={"gemini_api_key": SecretStr("k")})
    image = prepare(make_jpeg(3000, 2000), settings.image_max_side)
    assert max(image.width, image.height) == 1024
    contents, config = GeminiProvider(lambda s: None).build_request(image, candidates(with_photo=True), settings)
    assert config.temperature == 0.1
    assert config.response_mime_type == "application/json"
    assert config.response_json_schema["properties"]["items"]["items"]["properties"]["article_code"]["enum"] == [
        "PLT-COUS",
        "PLT-POUL",
        "DES-FRUIT",
        "BOI-EAU",
    ]
    assert config.thinking_config.thinking_budget == 0
    assert "PLT-COUS : Couscous viande (Plat) — semoule, légumes" in contents[0]
    images = [c for c in contents if not isinstance(c, str)]
    assert len(images) == 1 + 2  # tray + at most 2 reference photos per candidate


async def test_recognize_converts_gemini_boxes(gemini_settings):
    from pydantic import SecretStr

    settings = gemini_settings.model_copy(update={"gemini_api_key": SecretStr("k")})
    answer = {
        "items": [
            {
                "article_code": "PLT-COUS",
                "confidence": 0.82,
                "box_2d": [0, 0, 500, 1000],
                "alternatives": [{"article_code": "PLT-POUL", "confidence": 0.15}],
            }
        ]
    }
    client, models = fake_client(json.dumps(answer))
    provider = GeminiProvider(lambda s: client)
    image = prepare(make_jpeg(1024, 768))
    detections = await provider.recognize(image, candidates(), settings)
    assert models.requests[0]["model"] == "gemini-2.5-flash"
    assert detections[0].article_code == "PLT-COUS"
    assert detections[0].bbox == (0, 0, 1024, 384)
    assert detections[0].alternatives == [("PLT-POUL", 0.15)]


async def test_recognize_without_key_is_unavailable(settings):
    with pytest.raises(ProviderUnavailableError):
        await GeminiProvider(lambda s: None).recognize(prepare(make_jpeg()), candidates(), settings)


def test_parse_response_rejects_invalid_json_and_skips_bad_items():
    with pytest.raises(json.JSONDecodeError):
        parse_response("not json", 100, 100)
    assert parse_response(None, 100, 100) == []
    detections = parse_response(
        json.dumps({"items": [{"confidence": 1}, "x", {"article_code": "A", "box_2d": [1, 2]}]}), 100, 100
    )
    assert len(detections) == 1
    assert detections[0].bbox is None


def test_key_from_a_dpapi_file(settings, tmp_path, monkeypatch):
    from app import secrets

    blob = tmp_path / "gemini.key"
    blob.write_bytes(b"encrypted")
    monkeypatch.setattr(secrets, "unprotect", lambda data: b"key-from-dpapi\n" if data == b"encrypted" else b"")
    secrets._read_protected.cache_clear()
    current = settings.model_copy(update={"gemini_api_key": None, "gemini_api_key_file": str(blob)})
    assert secrets.gemini_key(current) == "key-from-dpapi"
    assert GeminiProvider(lambda s: None).readiness(current) == (True, None)

    missing = settings.model_copy(update={"gemini_api_key": None, "gemini_api_key_file": str(tmp_path / "absent.key")})
    ready, detail = GeminiProvider(lambda s: None).readiness(missing)
    assert not ready and "VISION_GEMINI_API_KEY_FILE" in detail


def test_dpapi_is_windows_only():
    import sys

    from app.secrets import SecretUnavailableError, unprotect

    if sys.platform != "win32":
        with pytest.raises(SecretUnavailableError):
            unprotect(b"x")
