"""Gemini key resolution: environment variable (development) or a DPAPI-protected file (registers, Windows).

On a register the installer writes ``gemini.key`` encrypted with DPAPI, machine scope: only processes of that
machine can decrypt it, the key never appears in clear in a file, the registry or the service configuration.
"""

from __future__ import annotations

import sys
from functools import lru_cache
from pathlib import Path

from app.config import Settings


class SecretUnavailableError(RuntimeError):
    pass


def gemini_key(settings: Settings) -> str | None:
    if settings.gemini_api_key is not None and settings.gemini_api_key.get_secret_value():
        return settings.gemini_api_key.get_secret_value()
    if settings.gemini_api_key_file:
        path = Path(settings.gemini_api_key_file)
        try:
            return _read_protected(str(path), path.stat().st_mtime)
        except (OSError, SecretUnavailableError):
            return None
    return None


@lru_cache(maxsize=4)
def _read_protected(path: str, mtime: float) -> str:  # mtime: re-read after a key rotation
    return unprotect(Path(path).read_bytes()).decode("utf-8").strip()


def unprotect(blob: bytes) -> bytes:
    """Windows DPAPI ``CryptUnprotectData`` (the blob was produced by ``ProtectedData.Protect(..., LocalMachine)``)."""
    if sys.platform != "win32":
        raise SecretUnavailableError("DPAPI n'existe que sous Windows")
    import ctypes
    from ctypes import wintypes

    class DataBlob(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

    buffer = ctypes.create_string_buffer(blob, len(blob))
    blob_in = DataBlob(len(blob), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_char)))
    blob_out = DataBlob()
    crypt32 = ctypes.windll.crypt32  # type: ignore[attr-defined]
    kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
    cryptprotect_ui_forbidden = 0x1
    if not crypt32.CryptUnprotectData(
        ctypes.byref(blob_in), None, None, None, None, cryptprotect_ui_forbidden, ctypes.byref(blob_out)
    ):
        raise SecretUnavailableError("Clé Gemini illisible (DPAPI)")
    try:
        return ctypes.string_at(blob_out.pbData, blob_out.cbData)
    finally:
        kernel32.LocalFree(blob_out.pbData)
