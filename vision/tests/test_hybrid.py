from __future__ import annotations

import asyncio

from app.images import prepare
from app.models import Candidate
from app.providers.base import ProviderResult, RawDetection
from app.providers.hybrid import HybridProvider, iou, merge
from tests.conftest import CANDIDATES, make_jpeg


class FakeYolo:
    def __init__(self, detections, known, ready=True):
        self.detections, self.known, self.ready = detections, known, ready

    def readiness(self, settings):
        return self.ready, "modèle v1"

    def known_codes(self, settings):
        return set(self.known)

    async def recognize(self, image, candidates, settings):
        return ProviderResult(self.detections, "yolo:v1")


class FakeGemini:
    def __init__(self, detections=None, delay=0.0, error=None, ready=True):
        self.detections, self.delay, self.error, self.ready = detections or [], delay, error, ready
        self.calls = 0

    def readiness(self, settings):
        return self.ready, None if self.ready else "pas de clé"

    async def recognize(self, image, candidates, settings):
        self.calls += 1
        await asyncio.sleep(self.delay)
        if self.error:
            raise self.error
        return self.detections


ALL = [c["article_code"] for c in CANDIDATES]
PLATE = (0, 0, 100, 100)
GLASS = (200, 200, 260, 260)


def candidates():
    return [Candidate.model_validate(c) for c in CANDIDATES]


async def run(provider, settings):
    return await provider.recognize(prepare(make_jpeg()), candidates(), settings)


def test_iou():
    assert iou(PLATE, PLATE) == 1.0
    assert iou(PLATE, GLASS) == 0.0
    assert iou(PLATE, None) == 0.0
    assert round(iou((0, 0, 100, 100), (50, 0, 150, 100)), 3) == 0.333


async def test_confident_yolo_answers_alone(settings):
    gemini = FakeGemini()
    provider = HybridProvider(FakeYolo([RawDetection("PLT-COUS", 0.93, PLATE)], ALL), gemini)
    result = await run(provider, settings)
    assert result.label == "hybrid:v1"
    assert gemini.calls == 0


async def test_unsure_yolo_asks_gemini_and_merges(settings):
    yolo = FakeYolo([RawDetection("PLT-COUS", 0.95, PLATE), RawDetection("DES-FRUIT", 0.40, GLASS)], ALL)
    gemini = FakeGemini([RawDetection("PLT-POUL", 0.80, (2, 2, 98, 98)), RawDetection("BOI-EAU", 0.85, (205, 198, 258, 262)),
                         RawDetection("DES-FRUIT", 0.7, (400, 400, 450, 450))])  # fmt: skip
    result = await run(HybridProvider(yolo, gemini), settings)
    assert result.label == "hybrid:v1+gemini"
    by_code = {d.article_code: d for d in result.detections}
    assert set(by_code) == {"PLT-COUS", "BOI-EAU", "DES-FRUIT"}
    assert by_code["PLT-COUS"].alternatives[0] == ("PLT-POUL", 0.80), "confident YOLO kept, Gemini's opinion as alternative"
    assert by_code["BOI-EAU"].bbox == GLASS and by_code["BOI-EAU"].alternatives[0] == ("DES-FRUIT", 0.40)
    assert by_code["DES-FRUIT"].bbox == (400, 400, 450, 450), "seen by Gemini only"


async def test_new_article_on_the_menu_triggers_gemini(settings):
    yolo = FakeYolo([RawDetection("PLT-COUS", 0.97, PLATE)], ["PLT-COUS", "PLT-POUL"])
    gemini = FakeGemini([RawDetection("PLT-COUS", 0.9, PLATE), RawDetection("BOI-EAU", 0.9, GLASS)])
    result = await run(HybridProvider(yolo, gemini), settings)
    assert [d.article_code for d in result.detections] == ["PLT-COUS", "BOI-EAU"]
    assert result.detections[0].confidence == 0.97
    off = settings.model_copy(update={"hybrid_ask_gemini_for_unknown_articles": False})
    gemini.calls = 0
    await run(HybridProvider(yolo, gemini), off)
    assert gemini.calls == 0


async def test_gemini_failure_or_slowness_keeps_the_yolo_answer(settings):
    yolo = FakeYolo([RawDetection("PLT-COUS", 0.5, PLATE)], ALL)
    failing = await run(HybridProvider(yolo, FakeGemini(error=ConnectionError("quota"))), settings)
    assert failing.label == "hybrid:v1+gemini!" and [d.article_code for d in failing.detections] == ["PLT-COUS"]
    quick = settings.model_copy(update={"request_timeout_s": 1.0})
    slow = await run(HybridProvider(yolo, FakeGemini([RawDetection("BOI-EAU", 0.9, GLASS)], delay=5)), quick)
    assert slow.label == "hybrid:v1+gemini!"
    no_key = await run(HybridProvider(yolo, FakeGemini(ready=False)), settings)
    assert no_key.label == "hybrid:v1"


async def test_without_model_hybrid_is_gemini(settings):
    result = await run(HybridProvider(FakeYolo([], [], ready=False), FakeGemini([RawDetection("BOI-EAU", 0.9, GLASS)])), settings)
    assert result.label == "hybrid:gemini" and result.detections[0].article_code == "BOI-EAU"
    provider = HybridProvider(FakeYolo([], [], ready=False), FakeGemini(ready=False))
    assert provider.readiness(settings)[0] is False


def test_merge_same_code_keeps_the_best_confidence():
    merged = merge([RawDetection("A", 0.5, PLATE)], [RawDetection("A", 0.8, PLATE)], 0.6)
    assert merged == [RawDetection("A", 0.8, PLATE, [])]
