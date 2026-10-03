from __future__ import annotations

import cv2
import numpy as np
import pytest

from app.camera import Camera, CameraUnavailableError, crop, parse_crop
from tests.conftest import client_for


class FakeDevice:
    def __init__(self, frame: np.ndarray | None, opened: bool = True):
        self.frame = frame
        self.opened = opened
        self.grabs = 0
        self.released = False

    def isOpened(self):  # noqa: N802 - OpenCV API
        return self.opened

    def grab(self):
        self.grabs += 1
        return True

    def read(self):
        return (self.frame is not None), self.frame

    def release(self):
        self.released = True


def frame(width=1920, height=1080):
    return np.full((height, width, 3), 128, dtype=np.uint8)


def test_capture_crops_to_the_tray_area(settings):
    device = FakeDevice(frame())
    camera = Camera(lambda s: device)
    jpeg, width, height = camera.capture_jpeg(settings.model_copy(update={"camera_crop": "400,300,1000,700"}))
    assert (width, height) == (1000, 700)
    decoded = cv2.imdecode(np.frombuffer(jpeg, np.uint8), cv2.IMREAD_COLOR)
    assert decoded.shape[:2] == (700, 1000)
    assert device.grabs == settings.camera_skip_frames


def test_unavailable_camera(settings):
    with pytest.raises(CameraUnavailableError):
        Camera(lambda s: FakeDevice(None, opened=False)).capture_jpeg(settings)
    device = FakeDevice(None)
    with pytest.raises(CameraUnavailableError):
        Camera(lambda s: device).capture_jpeg(settings)
    assert device.released


def test_crop_helpers():
    assert parse_crop(None) is None
    assert parse_crop("1,2,3,4") == (1, 2, 3, 4)
    with pytest.raises(ValueError):
        parse_crop("1,2,0,4")
    assert crop(frame(100, 100), (50, 50, 100, 100)).shape[:2] == (50, 50)
    with pytest.raises(CameraUnavailableError):
        crop(frame(100, 100), (200, 0, 10, 10))


def test_capture_endpoint(settings):
    from fastapi.testclient import TestClient

    from app.config import SettingsProvider
    from app.main import create_app

    client = TestClient(create_app(SettingsProvider(settings), camera=Camera(lambda s: FakeDevice(frame(640, 480)))))
    response = client.get("/camera/capture")
    assert response.status_code == 200
    assert response.headers["content-type"] == "image/jpeg"
    assert response.headers["x-image-width"] == "640"

    broken = TestClient(create_app(SettingsProvider(settings), camera=Camera(lambda s: FakeDevice(None, opened=False))))
    response = broken.get("/camera/capture")
    assert response.status_code == 503
    assert response.json()["detail"]["code"] == "camera_unavailable"
    assert client_for(settings).get("/health").status_code == 200
