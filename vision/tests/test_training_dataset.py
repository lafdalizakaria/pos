from __future__ import annotations

import json

import pytest

from training.__main__ import main
from training.dataset import build, export_for_annotation, read_annotation
from training.synthetic import generate


def test_build_uses_validated_records_and_keeps_class_indices(tmp_path):
    source = tmp_path / "register-1"
    ids = generate(source, 20, needs_annotation_every=5)
    # A record never validated (sale abandoned) and one without image (face guard era / dataset disabled).
    folder = next(source.iterdir())
    (folder / "nosale.json").write_text(json.dumps({"recognition_id": "nosale", "image": "nosale.jpg", "validated_lines": None}))
    (folder / "nosale.jpg").write_bytes((folder / f"{ids[1]}.jpg").read_bytes())
    (folder / "noimage.json").write_text(json.dumps({"recognition_id": "noimage", "image": None, "validated_lines": []}))
    previous = tmp_path / "classes.json"
    previous.write_text(json.dumps(["OLD-ART", "FRU-SAI"]))

    report = build([source], tmp_path / "ds", classes_file=previous)
    assert report.classes[:2] == ["OLD-ART", "FRU-SAI"], "indices of the previous model are kept"
    assert set(report.classes) == {"OLD-ART", "FRU-SAI", "CSC-VND", "EAU-50"}
    assert report.excluded == {"needs_annotation": 4, "not_validated": 1, "no_image": 1}
    assert report.train + report.val == 16 and report.val >= 1
    labels = list((tmp_path / "ds" / "labels").glob("*/*.txt"))
    assert len(labels) == 16
    first = labels[0].read_text().split("\n")[0].split()
    assert 0 <= int(first[0]) < 4 and len(first) == 5
    data = (tmp_path / "ds" / "data.yaml").read_text()
    assert 'names: ["OLD-ART", "FRU-SAI", "CSC-VND", "EAU-50"]' in data

    build([source], tmp_path / "ds2", classes_file=previous)
    assert sorted(p.name for p in (tmp_path / "ds2" / "images" / "val").iterdir()) == sorted(
        p.name for p in (tmp_path / "ds" / "images" / "val").iterdir()
    ), "deterministic split"


def test_annotation_round_trip(tmp_path):
    source = tmp_path / "register-1"
    generate(source, 10, needs_annotation_every=5)
    out = tmp_path / "to-annotate"
    assert export_for_annotation([source], out) == 2
    pre = sorted(out.glob("*.txt"))
    annotation = next(p for p in pre if not p.name.endswith(".sold.txt"))
    assert read_annotation(annotation), "pre-labels are in the annotation format"
    assert "Vendu" in annotation.with_name(annotation.stem + ".sold.txt").read_text()

    report = build([source], tmp_path / "ds", annotations=out)
    assert report.excluded == {}
    assert report.train + report.val == 10


def test_invalid_annotations_and_empty_sources(tmp_path):
    bad = tmp_path / "x.txt"
    bad.write_text("CSC-VND 0.5 0.5 1.5 0.2\n")
    with pytest.raises(ValueError, match="hors de"):
        read_annotation(bad)
    bad.write_text("CSC-VND 0.5\n")
    with pytest.raises(ValueError, match="attendu"):
        read_annotation(bad)
    with pytest.raises(ValueError, match="Aucun"):
        build([tmp_path / "empty"], tmp_path / "ds")


def test_command_line(tmp_path, capsys):
    assert main(["synthetic", "--out", str(tmp_path / "src"), "--count", "6"]) == 0
    assert main(["build", str(tmp_path / "src"), "--out", str(tmp_path / "ds")]) == 0
    assert '"train"' in capsys.readouterr().out
