# Entraînement du détecteur de plateaux (YOLO)

Machine d'entraînement (poste avec GPU de préférence ; CPU possible pour de petits modèles), jamais une caisse.

```bash
cd vision
uv sync --extra training                     # PyTorch + Ultralytics (plusieurs Go)

# 1. Rassembler les datasets des caisses (dossiers data/dataset, ou copie du conteneur central)
azcopy copy "https://<compte>.blob.core.windows.net/dataset?<SAS lecture>" ./collect --recursive   # exemple

# 2. Images à annoter (une unité vendue n'a pas été détectée) : pré-étiquettes "CODE cx cy w h"
uv run python -m training annotate ./collect/* --out ./to-annotate
#    corriger/compléter les .txt (CVAT, Label Studio ou éditeur), puis les placer dans ./annotations

# 3. Dataset YOLO (classes du modèle précédent conservées)
uv run python -m training build ./collect/* --annotations ./annotations --classes ./previous/classes.json --out ./ds

# 4. Entraînement, validation, export ONNX, manifeste (refus si mAP50 < 0,5)
uv run python -m training train ./ds --out ./models --base yolo11n.pt --epochs 100 --imgsz 640 --device 0
```

Résultat : `./models/<version>/model.onnx` + `manifest.json` → back-office, page **Modèles vision** : importer, publier,
puis l'affecter aux sites (provider `yolo` ou `hybrid`). Les caisses le téléchargent et l'installent seules.

- Une ligne corrigée par la caissière garde la boîte dessinée par le modèle avec le bon article ; une unité non
  détectée rend l'image « à annoter » (exclue tant qu'elle n'est pas annotée).
- Découpage entraînement/validation déterministe (par identifiant de reconnaissance).
- `python -m training synthetic --out ./demo --count 200` produit des plateaux synthétiques pour essayer la chaîne.
- Tests : `uv run pytest -m training` (entraînement réel sur CPU, ~3 min).

**Licence** : Ultralytics est sous AGPL-3.0 ; selon Ultralytics, les modèles entraînés en héritent. Un usage
commercial fermé suppose une licence Ultralytics Enterprise (voir `docs/assumptions.md`). L'inférence en caisse
utilise ONNX Runtime (MIT), sans code Ultralytics.
