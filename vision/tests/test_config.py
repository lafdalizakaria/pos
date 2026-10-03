from __future__ import annotations

import json
import os

import pytest

from app.config import Settings, SettingsProvider
from app.main import ensure_loopback
from tests.conftest import client_for, post_recognize


def test_provider_can_be_switched_without_restart(settings, tmp_path):
    override = tmp_path / "vision.json"
    provider = SettingsProvider(settings, str(override))
    assert provider.get().provider == "mock"

    override.write_text(json.dumps({"provider": "gemini", "gemini_api_key": "secret"}), encoding="utf-8")
    current = provider.get()
    assert current.provider == "gemini"
    assert current.gemini_api_key.get_secret_value() == "secret"

    override.write_text(json.dumps({"provider": "yolo"}), encoding="utf-8")
    stat = override.stat()
    os.utime(override, (stat.st_atime, stat.st_mtime + 5))
    assert provider.get().provider == "yolo"


def test_running_service_follows_the_override_file(settings, tmp_path):
    from fastapi.testclient import TestClient

    from app.main import create_app

    override = tmp_path / "vision.json"
    client = TestClient(create_app(SettingsProvider(settings, str(override))))
    assert client.get("/health").json()["provider"] == "mock"
    override.write_text(json.dumps({"provider": "gemini"}), encoding="utf-8")
    assert client.get("/health").json()["provider"] == "gemini"
    assert post_recognize(client).status_code == 503


def test_only_loopback_is_allowed_by_default():
    ensure_loopback(Settings(host="127.0.0.1"))
    ensure_loopback(Settings(host="localhost"))
    ensure_loopback(Settings(host="::1"))
    with pytest.raises(SystemExit):
        ensure_loopback(Settings(host="0.0.0.0"))  # noqa: S104
    ensure_loopback(Settings(host="0.0.0.0", allow_remote=True))  # noqa: S104


def test_secrets_are_not_printed(settings):
    settings = settings.model_copy(update={"gemini_api_key": None})
    assert "secret" not in repr(Settings(GEMINI_API_KEY="secret"))
    assert client_for(settings).get("/health").status_code == 200


def test_empty_variables_mean_not_configured(monkeypatch):
    monkeypatch.setenv("VISION_DATASET_UPLOAD_URL", "")
    monkeypatch.setenv("GEMINI_API_KEY", " ")
    current = Settings()
    assert current.dataset_upload_url is None
    assert current.gemini_api_key is None


def test_an_invalid_override_keeps_the_last_valid_settings(settings, tmp_path):
    override = tmp_path / "vision.json"
    override.write_text(json.dumps({"provider": "gemini"}), encoding="utf-8")
    provider = SettingsProvider(settings, str(override))
    assert provider.get().provider == "gemini"
    override.write_text(json.dumps({"provider": "teapot"}), encoding="utf-8")
    stat = override.stat()
    os.utime(override, (stat.st_atime, stat.st_mtime + 5))
    assert provider.get().provider == "gemini"
    override.write_text("{not json", encoding="utf-8")
    os.utime(override, (stat.st_atime, stat.st_mtime + 10))
    assert provider.get().provider == "gemini"
