"""Turns the registers' recognition records into a YOLO dataset.

Input: one or more dataset folders as written by the vision service (``<date>/<id>.jpg`` + ``<id>.json``), e.g.
copies of the registers' ``data/dataset`` or of the central container. A record is used when the cashier validated
the sale and every sold unit has a box:
* labels derived from the feedback (a corrected article keeps the box the model drew);
* records flagged ``needs_annotation`` (a unit the model did not see) are used only once an annotator has supplied
  ``<annotations>/<id>.txt`` (one ``ARTICLE_CODE cx cy w h`` line per item, YOLO-normalised).

Class indices are stable: an existing ``classes.json`` is extended, never reordered, so that a new model keeps the
indices of the previous one.
"""

from __future__ import annotations

import hashlib
import json
import shutil
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class Sample:
    id: str
    image: Path
    labels: list[tuple[str, float, float, float, float]]


@dataclass
class BuildReport:
    train: int = 0
    val: int = 0
    classes: list[str] = field(default_factory=list)
    instances: dict[str, int] = field(default_factory=dict)
    excluded: dict[str, int] = field(default_factory=dict)

    def to_dict(self) -> dict:
        return {
            "train": self.train,
            "val": self.val,
            "classes": self.classes,
            "instances": self.instances,
            "excluded": self.excluded,
        }


def read_annotation(path: Path) -> list[tuple[str, float, float, float, float]]:
    labels = []
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        if not line.strip():
            continue
        parts = line.split()
        if len(parts) != 5:
            raise ValueError(f"{path}:{number}: 'CODE cx cy w h' attendu")
        code, values = parts[0].upper(), [float(v) for v in parts[1:]]
        if not all(0 <= v <= 1 for v in values) or values[2] <= 0 or values[3] <= 0:
            raise ValueError(f"{path}:{number}: coordonnées hors de [0, 1]")
        labels.append((code, *values))
    return labels


def collect(sources: list[Path], annotations: Path | None = None) -> tuple[list[Sample], Counter]:
    samples: dict[str, Sample] = {}
    excluded: Counter = Counter()
    for source in sources:
        for record_path in sorted(source.glob("*/*.json")):
            try:
                record = json.loads(record_path.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                excluded["unreadable"] += 1
                continue
            rid = record.get("recognition_id") or record_path.stem
            image = record_path.with_suffix(".jpg")
            override = annotations / f"{rid}.txt" if annotations else None
            if not image.exists() or not record.get("image"):
                excluded["no_image"] += 1
            elif override is not None and override.exists():
                samples[rid] = Sample(rid, image, read_annotation(override))
            elif record.get("validated_lines") is None:
                excluded["not_validated"] += 1
            elif record.get("needs_annotation"):
                excluded["needs_annotation"] += 1
            elif not record.get("labels"):
                excluded["no_label"] += 1
            else:
                labels = [(lb["article_code"].upper(), *[float(v) for v in lb["bbox_yolo"]]) for lb in record["labels"]]
                samples[rid] = Sample(rid, image, labels)
    return list(samples.values()), excluded


def build(
    sources: list[Path], output: Path, annotations: Path | None = None, classes_file: Path | None = None, val_ratio: float = 0.15
) -> BuildReport:
    samples, excluded = collect(sources, annotations)
    if not samples:
        raise ValueError("Aucun élément exploitable : vérifier les dossiers sources et les annotations.")
    classes = json.loads(classes_file.read_text(encoding="utf-8")) if classes_file and classes_file.exists() else []
    for code in sorted({label[0] for s in samples for label in s.labels} - set(classes)):
        classes.append(code)
    index = {code: i for i, code in enumerate(classes)}

    if output.exists():
        shutil.rmtree(output)
    report = BuildReport(classes=classes, excluded=dict(excluded))
    # Deterministic split by identifier: an image stays in the same split from one build to the next.
    validation = {s.id for s in samples if int(hashlib.sha256(s.id.encode()).hexdigest()[:8], 16) % 1000 < val_ratio * 1000}
    if not validation and len(samples) > 1:
        validation = {samples[-1].id}
    instances: Counter = Counter()
    for sample in samples:
        split = "val" if sample.id in validation else "train"
        (output / "images" / split).mkdir(parents=True, exist_ok=True)
        (output / "labels" / split).mkdir(parents=True, exist_ok=True)
        shutil.copyfile(sample.image, output / "images" / split / f"{sample.id}.jpg")
        lines = [f"{index[code]} {cx:.6f} {cy:.6f} {w:.6f} {h:.6f}" for code, cx, cy, w, h in sample.labels]
        (output / "labels" / split / f"{sample.id}.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
        instances.update(code for code, *_ in sample.labels)
        setattr(report, split, getattr(report, split) + 1)
    report.instances = dict(sorted(instances.items()))

    names = ", ".join(json.dumps(c) for c in classes)
    (output / "data.yaml").write_text(
        f"path: {json.dumps(str(output.resolve()))}\ntrain: images/train\nval: images/val\nnames: [{names}]\n", encoding="utf-8"
    )
    (output / "classes.json").write_text(json.dumps(classes, indent=1), encoding="utf-8")
    (output / "report.json").write_text(json.dumps(report.to_dict(), indent=1), encoding="utf-8")
    return report


def export_for_annotation(sources: list[Path], output: Path) -> int:
    """Copies the records to annotate with pre-labels (``CODE cx cy w h``) for CVAT / Label Studio or a text editor."""
    output.mkdir(parents=True, exist_ok=True)
    count = 0
    for source in sources:
        for record_path in sorted(source.glob("*/*.json")):
            record = json.loads(record_path.read_text(encoding="utf-8"))
            image = record_path.with_suffix(".jpg")
            if not record.get("needs_annotation") or not image.exists():
                continue
            rid = record.get("recognition_id") or record_path.stem
            shutil.copyfile(image, output / f"{rid}.jpg")
            pre = [f"{lb['article_code']} " + " ".join(f"{v:.6f}" for v in lb["bbox_yolo"]) for lb in record.get("labels", [])]
            sold = ", ".join(f"{line['quantity']} x {line['article_code']}" for line in record.get("validated_lines") or [])
            (output / f"{rid}.txt").write_text("\n".join(pre) + ("\n" if pre else ""), encoding="utf-8")
            (output / f"{rid}.sold.txt").write_text(f"Vendu : {sold}\n", encoding="utf-8")
            count += 1
    return count
