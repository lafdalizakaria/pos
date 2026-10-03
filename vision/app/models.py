"""Public contract of the service (mirrored by the register's C# client)."""

from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, Field


class Candidate(BaseModel):
    """An article of today's menu: the only codes the service may return."""

    article_code: str = Field(min_length=1, max_length=32)
    label: str = Field(min_length=1, max_length=150)
    category: str | None = None
    visual_description: str | None = Field(default=None, max_length=1000)
    #: Optional reference photos (base64 JPEG) helping to tell similar dishes apart.
    reference_photos: list[str] = Field(default_factory=list, max_length=4)


class Alternative(BaseModel):
    article_code: str
    confidence: float = Field(ge=0, le=1)


class RecognizedItem(BaseModel):
    article_code: str
    confidence: float = Field(ge=0, le=1)
    #: [x_min, y_min, x_max, y_max] in pixels of the image sent by the register (None when the model gave no box).
    bbox: list[int] | None = Field(default=None, min_length=4, max_length=4)
    #: YOLO (x_center, y_center, width, height), normalised 0-1.
    bbox_yolo: list[float] | None = None
    alternatives: list[Alternative] = Field(default_factory=list)


class RecognizeResponse(BaseModel):
    recognition_id: str
    items: list[RecognizedItem]
    provider: str
    latency_ms: int
    image_width: int
    image_height: int
    #: Codes proposed by the model but absent from the candidates (dropped).
    rejected_codes: list[str] = Field(default_factory=list)


class ValidatedLine(BaseModel):
    article_code: str
    quantity: int = Field(ge=1, le=50)
    source: Literal["VisionAuto", "VisionConfirmed", "VisionCorrected", "Manual"]
    #: Index in ``RecognizeResponse.items`` this line comes from (to keep its box when corrected).
    prediction_index: int | None = None


class FeedbackRequest(BaseModel):
    recognition_id: str = Field(min_length=8, max_length=64)
    ticket_id: str | None = None
    lines: list[ValidatedLine]


class FeedbackResponse(BaseModel):
    recognition_id: str
    stored: bool
    labels: int
    needs_annotation: bool


class HealthResponse(BaseModel):
    status: Literal["ok", "degraded"]
    provider: str
    provider_ready: bool
    detail: str | None = None
    dataset_items: int
