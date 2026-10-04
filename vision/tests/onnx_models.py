"""Tiny ONNX models with the YOLO detection output layout ``[1, 4 + classes, anchors]`` and a fixed answer."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper


def yolo_like_model(
    classes: list[str], boxes: list[tuple[float, float, float, float, dict[str, float]]], size: int = 320
) -> bytes:
    """``boxes``: (cx, cy, w, h) in network input pixels + {class: score}. Other anchors score 0."""
    anchors = max(8, len(boxes))
    output = np.zeros((1, 4 + len(classes), anchors), dtype=np.float32)
    for i, (cx, cy, w, h, scores) in enumerate(boxes):
        output[0, :4, i] = (cx, cy, w, h)
        for code, score in scores.items():
            output[0, 4 + classes.index(code), i] = score
    constant = numpy_helper.from_array(output, "answer")
    zero = numpy_helper.from_array(np.zeros((1,), dtype=np.float32), "zero")
    graph = helper.make_graph(
        [
            helper.make_node("ReduceMean", ["images"], ["mean"], keepdims=0),
            helper.make_node("Mul", ["mean", "zero"], ["nothing"]),
            helper.make_node("Add", ["answer", "nothing"], ["output0"]),
        ],
        "yolo_like",
        [helper.make_tensor_value_info("images", TensorProto.FLOAT, [1, 3, size, size])],
        [helper.make_tensor_value_info("output0", TensorProto.FLOAT, [1, 4 + len(classes), anchors])],
        initializer=[constant, zero],
    )
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    model.ir_version = 8
    onnx.checker.check_model(model)
    return model.SerializeToString()


def manifest_for(version: str, classes: list[str], data: bytes, size: int = 320) -> dict:
    return {
        "version": version,
        "classes": classes,
        "imgsz": size,
        "sha256": hashlib.sha256(data).hexdigest(),
        "size_bytes": len(data),
        "metrics": {"map50": 0.9},
    }


def write_package(folder: Path, version: str, classes: list[str], boxes, size: int = 320) -> tuple[Path, Path]:
    folder.mkdir(parents=True, exist_ok=True)
    data = yolo_like_model(classes, boxes, size)
    (folder / "model.onnx").write_bytes(data)
    (folder / "manifest.json").write_text(json.dumps(manifest_for(version, classes, data, size)), encoding="utf-8")
    return folder / "model.onnx", folder / "manifest.json"
