"""python -m training <command> — see vision/training/README.md."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m training", description="Entraînement du détecteur de plateaux")
    commands = parser.add_subparsers(dest="command", required=True)

    build = commands.add_parser("build", help="dataset YOLO à partir des enregistrements des caisses")
    build.add_argument("sources", nargs="+", type=Path, help="dossiers dataset (copie de data/dataset des caisses)")
    build.add_argument("--out", type=Path, required=True)
    build.add_argument("--annotations", type=Path, help="dossier <id>.txt (CODE cx cy w h) des images annotées")
    build.add_argument("--classes", type=Path, help="classes.json du modèle précédent (indices conservés)")
    build.add_argument("--val-ratio", type=float, default=0.15)

    annotate = commands.add_parser("annotate", help="exporte les images à annoter avec pré-étiquettes")
    annotate.add_argument("sources", nargs="+", type=Path)
    annotate.add_argument("--out", type=Path, required=True)

    train = commands.add_parser("train", help="entraîne, valide, exporte en ONNX et empaquette")
    train.add_argument("dataset", type=Path)
    train.add_argument("--out", type=Path, required=True)
    train.add_argument("--base", default="yolo11n.pt", help="poids de départ (yolo11n.pt, yolo11s.pt...) ou .yaml")
    train.add_argument("--epochs", type=int, default=100)
    train.add_argument("--imgsz", type=int, default=640)
    train.add_argument("--batch", type=int, default=16)
    train.add_argument("--device", default="cpu", help="cpu, 0 (GPU)...")
    train.add_argument("--version")
    train.add_argument("--min-map50", type=float, default=0.5, help="seuil qualité ; 0 pour désactiver")
    train.add_argument("--patience", type=int, default=30)

    synthetic = commands.add_parser("synthetic", help="plateaux synthétiques (démonstration, tests)")
    synthetic.add_argument("--out", type=Path, required=True)
    synthetic.add_argument("--count", type=int, default=100)
    synthetic.add_argument("--seed", type=int, default=0)

    args = parser.parse_args(argv)
    if args.command == "build":
        from training.dataset import build as run_build

        report = run_build(args.sources, args.out, args.annotations, args.classes, args.val_ratio)
        print(json.dumps(report.to_dict(), indent=1, ensure_ascii=False))
    elif args.command == "annotate":
        from training.dataset import export_for_annotation

        print(f"{export_for_annotation(args.sources, args.out)} image(s) à annoter dans {args.out}")
    elif args.command == "train":
        from training.train import QualityGateError
        from training.train import train as run_train

        try:
            package = run_train(args.dataset, args.out, args.base, args.epochs, args.imgsz, args.batch, args.device,
                                args.version, args.min_map50 or None, args.patience)  # fmt: skip
        except QualityGateError as ex:
            print(ex, file=sys.stderr)
            return 2
        print(f"Modèle prêt : {package} (à importer dans le back-office, page Modèles vision)")
    elif args.command == "synthetic":
        from training.synthetic import generate

        print(f"{len(generate(args.out, args.count, args.seed))} plateaux synthétiques dans {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
