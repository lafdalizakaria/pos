# Service vision (reconnaissance des plateaux)

Service FastAPI local à chaque caisse (`127.0.0.1:8765`). La caisse lui envoie la photo du plateau et les articles du
menu du jour ; il renvoie les articles reconnus. Il ne bloque jamais une vente : la caisse abandonne après 6 s.

## Contrat

| Méthode | Corps | Réponse |
|---|---|---|
| `POST /recognize` | multipart : `image` (JPEG, ≤ 8 Mo), `candidates` (JSON `[{article_code, label, category?, visual_description?, reference_photos?: [base64]}]`), `register_id?` | `{recognition_id, items: [{article_code, confidence, bbox: [x_min, y_min, x_max, y_max], bbox_yolo, alternatives: [{article_code, confidence}]}], provider, latency_ms, image_width, image_height, rejected_codes}` |
| `POST /feedback` | `{recognition_id, ticket_id?, lines: [{article_code, quantity, source, prediction_index?}]}` | `{stored, labels, needs_annotation}` |
| `GET /health` | — | `{status, provider, provider_ready, detail, dataset_items}` |
| `POST /dataset/upload` | — | `{uploaded}` (si `VISION_DATASET_UPLOAD_URL`) |
| `GET /models` · `PUT /models/{version}` | multipart `model` (ONNX) + `manifest` (JSON) | modèles installés ; installation (empreinte et forme de sortie contrôlées) |
| `PUT /runtime` | `{provider, model_version, hybrid_min_confidence}` | réglages du site poussés par la caisse → `/health` |

Erreurs : `{"detail": {"code", "message"}}` — 400 `invalid_image`, 413 `image_too_large`, 422 `invalid_candidates` /
`no_candidates`, 502 `invalid_provider_answer` / `provider_error`, 503 `provider_unavailable`, 504 `timeout`.
Les codes renvoyés par un modèle hors de la liste des candidats sont **écartés** (`rejected_codes`). Les boîtes sont
en pixels de l'image envoyée par la caisse.

## Providers (choisis par site dans le back-office ; `VISION_CONFIG_FILE` force une valeur localement)

| Provider | Usage |
|---|---|
| `mock` | Tests et démonstrations : réponse déterministe par image (1 à 3 articles, confiances 0,95 / 0,78 / 0,45 couvrant les trois seuils), ou exacte via `VISION_MOCK_SCENARIOS_DIR/<sha256>.json` |
| `gemini` | Google Gen AI : sortie JSON contrainte par schéma (`article_code` = enum des candidats), température 0,1, image ramenée à 1024 px, photos de référence jointes, `box_2d` 0-1000 converties en pixels puis YOLO |
| `yolo` | Modèle local (ONNX Runtime, CPU, sans Internet) entraîné sur le dataset collecté (`training/`) ; seuls les articles du menu du jour peuvent sortir |
| `hybrid` | YOLO, puis Gemini seulement si YOLO hésite ou si le menu contient un article inconnu du modèle ; fusion des réponses par boîte |

## Dataset

Chaque reconnaissance : `data/dataset/<date>/<id>.jpg` + `.json` (candidats, prédictions, lignes validées, labels
YOLO dérivés, besoin d'annotation). Une image où un visage est détecté n'est **jamais** enregistrée (seules les
métadonnées le sont). Rotation à `VISION_DATASET_MAX_ITEMS`. Envoi central optionnel : PUT vers une URL signée
(`VISION_DATASET_UPLOAD_URL`, ex. conteneur Azure Blob avec SAS en écriture seule).

## Développement

```bash
cd vision
uv sync                     # Python 3.12, dépendances verrouillées
uv run ruff check . && uv run ruff format --check .
uv run pytest -q            # aucun réseau ni clé requis (provider mock, faux client Gemini)
uv run python -m app.main   # http://127.0.0.1:8765/docs
```

Installation sur un poste caisse : `deploy/vision/README.md`.

Entraînement d'un modèle : `training/README.md`.
