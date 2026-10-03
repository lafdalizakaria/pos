"""Tray camera, read by the vision service (OpenCV is already here; the register stays free of native imaging code).

Exposure and white balance are fixed for stable images; the frame is cropped to the tray area configured for the
register (``VISION_CAMERA_CROP=x,y,w,h``) so that no face can be captured.
"""

from __future__ import annotations

import threading
from collections.abc import Callable
from typing import Any

import cv2
import numpy as np

from app.config import Settings


class CameraUnavailableError(RuntimeError):
    pass


def parse_crop(value: str | None) -> tuple[int, int, int, int] | None:
    if not value:
        return None
    parts = [int(p) for p in value.split(",")]
    if len(parts) != 4 or parts[2] <= 0 or parts[3] <= 0 or parts[0] < 0 or parts[1] < 0:
        raise ValueError("VISION_CAMERA_CROP doit valoir x,y,largeur,hauteur")
    return parts[0], parts[1], parts[2], parts[3]


def crop(frame: np.ndarray, region: tuple[int, int, int, int] | None) -> np.ndarray:
    if region is None:
        return frame
    x, y, w, h = region
    height, width = frame.shape[:2]
    if x >= width or y >= height:
        raise CameraUnavailableError("Zone de recadrage hors de l'image")
    return frame[y : min(y + h, height), x : min(x + w, width)]


def _open_device(settings: Settings) -> Any:
    capture = cv2.VideoCapture(settings.camera_index)
    capture.set(cv2.CAP_PROP_FRAME_WIDTH, settings.camera_width)
    capture.set(cv2.CAP_PROP_FRAME_HEIGHT, settings.camera_height)
    if settings.camera_exposure is not None:
        capture.set(cv2.CAP_PROP_AUTO_EXPOSURE, 0.25)  # manual mode (DirectShow / V4L2 convention)
        capture.set(cv2.CAP_PROP_EXPOSURE, settings.camera_exposure)
    if settings.camera_white_balance is not None:
        capture.set(cv2.CAP_PROP_AUTO_WB, 0)
        capture.set(cv2.CAP_PROP_WB_TEMPERATURE, settings.camera_white_balance)
    return capture


class Camera:
    """Keeps the device open between captures (opening a USB camera takes ~1 s)."""

    def __init__(self, opener: Callable[[Settings], Any] = _open_device) -> None:
        self._opener = opener
        self._device: Any = None
        self._lock = threading.Lock()

    def capture_jpeg(self, settings: Settings) -> tuple[bytes, int, int]:
        with self._lock:
            if self._device is None or not self._device.isOpened():
                self._device = self._opener(settings)
                if not self._device.isOpened():
                    self._device = None
                    raise CameraUnavailableError(f"Caméra {settings.camera_index} introuvable")
            # Drop buffered frames: the image must show the tray now, not a second ago.
            for _ in range(settings.camera_skip_frames):
                self._device.grab()
            ok, frame = self._device.read()
            if not ok or frame is None:
                self._device.release()
                self._device = None
                raise CameraUnavailableError("Lecture de la caméra impossible")
        image = crop(frame, parse_crop(settings.camera_crop))
        ok, encoded = cv2.imencode(".jpg", image, [cv2.IMWRITE_JPEG_QUALITY, settings.jpeg_quality])
        if not ok:
            raise CameraUnavailableError("Encodage JPEG impossible")
        return encoded.tobytes(), image.shape[1], image.shape[0]

    def close(self) -> None:
        with self._lock:
            if self._device is not None:
                self._device.release()
                self._device = None
